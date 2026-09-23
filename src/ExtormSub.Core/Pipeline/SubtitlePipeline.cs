using System.Collections.Concurrent;
using System.Diagnostics;
using ExtormSub.Core.ASR;
using ExtormSub.Core.Audio;
using ExtormSub.Core.Diagnostics;
using ExtormSub.Core.History;
using ExtormSub.Core.Subtitles;
using ExtormSub.Core.Text;
using ExtormSub.Core.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExtormSub.Core.Pipeline;

public sealed record PipelineOptions
{
    public VadOptions Vad { get; init; } = new();
    public bool Partials { get; init; } = true;
    public TimeSpan StabilityDebounce { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan RingBuffer { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>ASR backlog kept in memory. Beyond it utterances spill to <see cref="SpoolDirectory"/> (or are dropped).</summary>
    public TimeSpan MaxAsrBacklog { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Null disables disk spooling: the oldest queued speech is dropped instead.</summary>
    public string? SpoolDirectory { get; init; }
    public TimeSpan MaxSpool { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Speech recognised later than this after it ended goes to history only, not to the screen.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan HeadTimeout { get; init; } = TimeSpan.FromSeconds(4);
    public float NoSpeechThreshold { get; init; } = 0.6f;
    public int ContextSize { get; init; } = 4;
    public Glossary Glossary { get; init; } = Glossary.Empty;
}

/// <summary>English text for the live line. Empty final text means "this utterance had no speech".</summary>
public sealed record TranscriptUpdate(long Seq, string Text, bool IsFinal);

/// <summary>
/// A subtitle released in order. Translation is null when disabled, failed or abandoned.
/// Stale subtitles (recognised long after the speech, while catching up) are for history, not the screen.
/// </summary>
public sealed record SubtitleUpdate(long Seq, string Original, string? Translation, bool IsFinal, bool TranslationFailed, bool IsStale = false);

public sealed record SessionInfo(string Id, DateTimeOffset StartedAt, string DeviceName);

public enum PipelineState { Stopped, Listening, Stopping }

/// <summary>
/// Orchestrates capture → ring → convert/resample → VAD/segment → ASR → stabilize → translate → sequence.
/// Owns two dedicated threads (DSP, ASR) and a 250 ms housekeeping timer. Raises events from those
/// threads; UI subscribers must marshal. Knows only interfaces, so it runs identically with fakes in tests.
/// </summary>
public sealed class SubtitlePipeline : IAsyncDisposable
{
    private sealed record AsrJob(long Seq, float[] Audio, bool Final, TimeSpan Start, TimeSpan End, long EndedTimestamp);

    private sealed class SegmentInfo
    {
        public TimeSpan Start, End;
        public string? FinalText, Language;
        public float? Confidence;
        public int? AsrMs, TranslationMs;
        public long EndedTimestamp;
        public bool Stale;
    }

    private sealed class CaptureChain(IAudioSource source, AudioRingBuffer ring, PcmConverter converter, StreamResampler resampler)
    {
        public IAudioSource Source { get; } = source;
        public AudioRingBuffer Ring { get; } = ring;
        public PcmConverter Converter { get; } = converter;
        public StreamResampler Resampler { get; } = resampler;
        public void OnData(ReadOnlySpan<byte> data) => Ring.Write(data);
    }

    private readonly Func<string?, IAudioSource> _sourceFactory;
    private readonly Func<IVoiceActivityDetector> _vadFactory;
    private readonly PipelineMetrics _metrics;
    private readonly ILogger _log;

    private readonly object _chainGate = new();
    private readonly object _dispatchGate = new();
    private readonly ConcurrentDictionary<long, SegmentInfo> _segments = new();
    private UtteranceSpool _finals = new(null, TimeSpan.FromSeconds(30), TimeSpan.Zero);
    private SemaphoreSlim _asrSignal = new(0);
    private AsrJob? _partialSlot;
    private long _lastFinalSeq;
    private IASRProvider _asr = null!;

    private volatile CaptureChain? _chain;
    private PipelineOptions _options = new();
    private IVoiceActivityDetector? _vad;
    private VadSegmenter? _segmenter;
    private TranscriptStabilizer? _stabilizer;
    private SubtitleSequencer? _sequencer;
    private TranslationQueue? _translation;
    private TranslationContextManager? _context;
    private CancellationTokenSource? _cts;
    private Thread? _dspThread, _asrThread;
    private Timer? _timer;
    private long _startTimestamp;
    private SessionInfo? _session;

    public SubtitlePipeline(
        Func<string?, IAudioSource> sourceFactory,
        Func<IVoiceActivityDetector> vadFactory,
        PipelineMetrics metrics,
        ILogger<SubtitlePipeline>? log = null)
    {
        _sourceFactory = sourceFactory;
        _vadFactory = vadFactory;
        _metrics = metrics;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    public PipelineState State { get; private set; } = PipelineState.Stopped;
    public SessionInfo? Session => _session;
    public string? CurrentDeviceId => _chain?.Source.DeviceId;
    public string? CurrentDeviceName => _chain?.Source.DeviceName;

    public event Action<SessionInfo>? SessionStarted;
    public event Action<SessionInfo>? SessionEnded;
    public event Action<TranscriptUpdate>? TranscriptUpdated;
    public event Action<SubtitleUpdate>? SubtitleReady;
    public event Action<SegmentRecord>? SegmentCompleted;
    /// <summary>True when speech starts, false when the utterance ends.</summary>
    public event Action<bool>? SpeechActivity;
    /// <summary>The capture device stopped unexpectedly (removed, invalidated). Caller decides the fallback.</summary>
    public event Action<Exception?>? CaptureLost;
    public event Action<string>? Error;

    private TimeSpan Clock => Stopwatch.GetElapsedTime(_startTimestamp);

    /// <summary>
    /// Starts listening. <paramref name="asr"/> must already be initialized. <paramref name="translation"/> is
    /// optional; without it subtitles are English-only. Throws if the audio device cannot be opened.
    /// </summary>
    public void Start(PipelineOptions options, string? deviceId, IASRProvider asr, TranslationQueue? translation)
    {
        if (State != PipelineState.Stopped) throw new InvalidOperationException("Pipeline is already running.");
        if (!asr.IsReady) throw new InvalidOperationException("Speech recognition is not initialized.");

        _asr = asr;
        _options = options;
        _startTimestamp = Stopwatch.GetTimestamp();
        _metrics.ResetSession();
        _finals.Dispose();
        _finals = new UtteranceSpool(options.SpoolDirectory, options.MaxAsrBacklog, options.MaxSpool);
        _segments.Clear();
        _partialSlot = null;
        _lastFinalSeq = 0;
        _asrSignal = new SemaphoreSlim(0);

        _vad = _vadFactory();
        _metrics.VadName = _vad.Name;
        _segmenter = new VadSegmenter(options.Vad);
        _stabilizer = new TranscriptStabilizer(options.StabilityDebounce);
        _sequencer = new SubtitleSequencer(options.HeadTimeout);
        _context = new TranslationContextManager(options.ContextSize);
        AttachTranslation(translation);
        _metrics.AsrBackend = _asr.BackendDescription;
        _metrics.AsrModel = _asr.Current?.ModelId ?? "—";

        _cts = new CancellationTokenSource();
        try
        {
            OpenCapture(deviceId);
        }
        catch
        {
            Cleanup();
            throw;
        }

        _session = new SessionInfo(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, _chain?.Source.DeviceName ?? "");
        State = PipelineState.Listening;
        var ct = _cts.Token;
        _dspThread = new Thread(() => Guard("DSP", () => DspLoop(ct))) { Name = "ExtormSub DSP", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _asrThread = new Thread(() => Guard("ASR", () => AsrLoop(ct))) { Name = "ExtormSub ASR", IsBackground = true };
        _dspThread.Start();
        _asrThread.Start();
        _timer = new Timer(_ => Guard("Timer", Housekeeping), null, 250, 250);
        _log.LogInformation("Listening on {Device} ({Format}), VAD {Vad}, ASR {Asr}, translation {Translation}",
            _chain?.Source.DeviceName, _chain?.Source.Format, _vad.Name, _asr.BackendDescription, _metrics.TranslationProvider);
        SessionStarted?.Invoke(_session);
    }

    /// <summary>
    /// Replaces the translation queue while listening (e.g. the model or API key changed), without touching
    /// capture or ASR. Requests in the old queue are cancelled; their segments fall back to English.
    /// </summary>
    public void SetTranslation(TranslationQueue? translation)
    {
        lock (_dispatchGate)
        {
            DetachTranslation();
            AttachTranslation(translation);
        }
        _log.LogInformation("Translation switched to {Provider}", _metrics.TranslationProvider);
    }

    private void AttachTranslation(TranslationQueue? translation)
    {
        _translation = translation;
        if (translation is not null)
        {
            translation.Completed += OnTranslationCompleted;
            translation.HealthChanged += OnTranslationHealth;
            _metrics.TranslationProvider = translation.ProviderName;
            _metrics.TranslationHealth = "Ready";
        }
        else
        {
            _metrics.TranslationProvider = "Off";
            _metrics.TranslationHealth = "Off";
        }
    }

    private void DetachTranslation()
    {
        if (_translation is null) return;
        _translation.Completed -= OnTranslationCompleted;
        _translation.HealthChanged -= OnTranslationHealth;
        _translation.CancelAll();
        _translation = null;
    }

    public async Task StopAsync()
    {
        if (State != PipelineState.Listening) return;
        State = PipelineState.Stopping;
        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;
        CloseCapture();
        await Task.Run(() =>
        {
            _dspThread?.Join(TimeSpan.FromSeconds(3));
            // ASR may be mid-inference (whisper cannot be interrupted); give it time to return.
            _asrThread?.Join(TimeSpan.FromSeconds(10));
        }).ConfigureAwait(false);
        var session = _session;
        Cleanup();
        State = PipelineState.Stopped;
        _log.LogInformation("Stopped listening");
        if (session is not null) SessionEnded?.Invoke(session);
    }

    /// <summary>Re-opens capture on another device (or the default when null) without ending the session.</summary>
    public void SwitchDevice(string? deviceId)
    {
        if (State != PipelineState.Listening) return;
        lock (_chainGate)
        {
            CloseCapture();
            OpenCapture(deviceId);
        }
        _log.LogInformation("Capture switched to {Device}", _chain?.Source.DeviceName);
    }

    private void OpenCapture(string? deviceId)
    {
        var source = _sourceFactory(deviceId);
        try
        {
            source.Start();
            var format = source.Format;
            var chain = new CaptureChain(
                source,
                AudioRingBuffer.ForDuration(format, _options.RingBuffer),
                new PcmConverter(format),
                new StreamResampler(format.SampleRate, AudioFormat.AsrSampleRate));
            source.DataAvailable += chain.OnData;
            source.Stopped += OnSourceStopped;
            _chain = chain;
            _metrics.DeviceName = source.DeviceName;
            _metrics.AudioFormat = format.ToString();
            _metrics.RingCapacityBytes = chain.Ring.Capacity;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private void CloseCapture()
    {
        var chain = _chain;
        if (chain is null) return;
        _chain = null;
        chain.Source.Stopped -= OnSourceStopped;
        chain.Source.DataAvailable -= chain.OnData;
        try { chain.Source.Stop(); } catch (Exception ex) { _log.LogDebug(ex, "Stopping capture failed"); }
        chain.Source.Dispose();
    }

    private void OnSourceStopped(object? sender, Exception? ex)
    {
        if (State != PipelineState.Listening || !ReferenceEquals(sender, _chain?.Source)) return;
        _log.LogWarning(ex, "Capture device stopped unexpectedly");
        CaptureLost?.Invoke(ex);
    }

    // ───────────────────────────── DSP thread ─────────────────────────────

    private void DspLoop(CancellationToken ct)
    {
        var vad = _vad!;
        var seg = _segmenter!;
        var frame = new float[vad.FrameSize];
        int frameFill = 0;
        CaptureChain? chain = null;
        byte[] bytes = [];
        float[] mono = [], resampled = [];
        long lastAudio = Stopwatch.GetTimestamp();
        var idleGap = TimeSpan.FromMilliseconds(100);

        while (!ct.IsCancellationRequested)
        {
            var current = _chain;
            if (current is null)
            {
                // Between devices: keep the stream clock moving.
                ct.WaitHandle.WaitOne(idleGap);
                AdvanceSilence(ref lastAudio, idleGap);
                continue;
            }
            if (!ReferenceEquals(current, chain))
            {
                chain = current;
                var f = chain.Source.Format;
                int chunk = Math.Max(f.BlockAlign, f.BytesPerSecond / 20 / f.BlockAlign * f.BlockAlign); // 50 ms
                bytes = new byte[chunk];
                mono = new float[chunk / f.BlockAlign];
                resampled = new float[chain.Resampler.MaxOutput(mono.Length)];
            }

            try { chain.Ring.WaitForData(idleGap, ct); }
            catch (OperationCanceledException) { break; }

            bool any = false;
            int n;
            while ((n = chain.Ring.Read(bytes)) > 0)
            {
                any = true;
                int m = chain.Converter.ConvertToMono(bytes.AsSpan(0, n), mono);
                int r = chain.Resampler.Process(mono.AsSpan(0, m), resampled);
                for (int i = 0; i < r;)
                {
                    int take = Math.Min(r - i, frame.Length - frameFill);
                    resampled.AsSpan(i, take).CopyTo(frame.AsSpan(frameFill));
                    frameFill += take;
                    i += take;
                    if (frameFill == frame.Length)
                    {
                        ProcessFrame(vad, seg, frame);
                        frameFill = 0;
                    }
                }
            }

            _metrics.RingFillBytes = chain.Ring.Count;
            _metrics.DroppedAudioBytes = chain.Ring.DroppedBytes;
            if (any) lastAudio = Stopwatch.GetTimestamp();
            else AdvanceSilence(ref lastAudio, idleGap);
        }

        void AdvanceSilence(ref long last, TimeSpan minGap)
        {
            // Loopback delivers no packets while nothing plays. Advance time with synthetic silence so
            // open utterances still close and timestamps track the wall clock.
            var gap = Stopwatch.GetElapsedTime(last);
            if (gap < minGap) return;
            int samples = (int)(gap.TotalSeconds * AudioFormat.AsrSampleRate);
            if (seg.IsActive)
            {
                frame.AsSpan(frameFill).Clear();
                ProcessFrame(vad, seg, frame);
                samples -= frame.Length - frameFill;
                frameFill = 0;
                Array.Clear(frame);
                while (samples >= frame.Length && seg.IsActive)
                {
                    ProcessFrame(vad, seg, frame);
                    samples -= frame.Length;
                }
            }
            if (!seg.IsActive && samples > 0) seg.AdvanceIdle(samples);
            last = Stopwatch.GetTimestamp();
        }
    }

    private void ProcessFrame(IVoiceActivityDetector vad, VadSegmenter seg, float[] frame)
    {
        var events = seg.Process(frame, vad.Process(frame));
        if (events == VadEvents.None) return;

        if ((events & VadEvents.Ended) != 0 && seg.LastEnded is { } u)
        {
            var endedAt = Stopwatch.GetTimestamp();
            if (_segments.TryGetValue(u.Seq, out var info))
            {
                info.End = u.End;
                info.EndedTimestamp = endedAt;
            }
            EnqueueFinal(new AsrJob(u.Seq, u.Audio, true, u.Start, u.End, endedAt));
            if ((events & VadEvents.Started) == 0) SpeechActivity?.Invoke(false);
        }
        if ((events & VadEvents.Started) != 0)
        {
            _segments[seg.CurrentSeq] = new SegmentInfo { Start = seg.CurrentStart };
            _sequencer!.Open(seg.CurrentSeq);
            SpeechActivity?.Invoke(true);
        }
        if ((events & VadEvents.PartialDue) != 0 && _options.Partials)
        {
            var job = new AsrJob(seg.CurrentSeq, seg.CurrentAudio.ToArray(), false, seg.CurrentStart, seg.StreamTime, 0);
            Interlocked.Exchange(ref _partialSlot, job); // latest wins
            _asrSignal.Release();
        }
    }

    private void EnqueueFinal(AsrJob job)
    {
        IReadOnlyList<long> dropped;
        try
        {
            dropped = _finals.Enqueue(new SpooledUtterance(job.Seq, job.Audio, job.Start, job.End, job.EndedTimestamp));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk full or spool folder unavailable: this utterance is lost, capture continues.
            _log.LogError(ex, "Could not spool utterance #{Seq} to disk", job.Seq);
            dropped = [job.Seq];
        }
        foreach (var seq in dropped)
        {
            Interlocked.Increment(ref _metrics.DroppedUtterances);
            _log.LogWarning("ASR backlog limit reached: dropped utterance #{Seq}", seq);
            _segments.TryRemove(seq, out _);
            lock (_dispatchGate) Dispatch(_sequencer!.Close(seq, Clock));
        }
        UpdateBacklogMetrics();
        _asrSignal.Release();
    }

    private void UpdateBacklogMetrics()
    {
        _metrics.AsrQueueDepth = _finals.Count;
        _metrics.AsrBacklogSeconds = _finals.MemorySeconds + _finals.DiskSeconds;
        _metrics.SpooledSeconds = _finals.DiskSeconds;
    }

    // ───────────────────────────── ASR thread ─────────────────────────────

    private void AsrLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { _asrSignal.Wait(ct); }
            catch (OperationCanceledException) { break; }

            AsrJob? job;
            if (_finals.TryDequeue(out var final))
            {
                job = new AsrJob(final.Seq, final.Audio, true, final.Start, final.End, final.EndedTimestamp);
                UpdateBacklogMetrics();
            }
            else
            {
                job = Interlocked.Exchange(ref _partialSlot, null);
            }
            if (job is null) continue;

            if (!job.Final && (job.Seq <= Interlocked.Read(ref _lastFinalSeq) || !_finals.IsEmpty))
            {
                Interlocked.Increment(ref _metrics.PartialsSkipped);
                continue;
            }
            RunAsr(job, ct);
        }
    }

    private void RunAsr(AsrJob job, CancellationToken ct)
    {
        AsrResult result;
        try
        {
            result = _asr.TranscribeAsync(job.Audio, job.Audio.Length, ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Transcription of #{Seq} failed", job.Seq);
            Error?.Invoke($"Speech recognition failed: {ex.Message}");
            if (job.Final) FinishEmpty(job);
            return;
        }

        var text = TranscriptFilter.Clean(result.Text, result.NoSpeechProbability, _options.NoSpeechThreshold);
        text = _options.Glossary.ApplyToTranscript(text);
        var now = Clock;

        if (!job.Final)
        {
            _metrics.LastPartialAsrMs = result.ProcessingTime.TotalMilliseconds;
            var decision = _stabilizer!.OnPartial(job.Seq, text, now);
            if (decision is null || text.Length == 0) return;
            _sequencer!.NoteOriginal(job.Seq, text);
            TranscriptUpdated?.Invoke(new TranscriptUpdate(job.Seq, text, false));
            if (decision.Value.Translate) SubmitTranslation(job.Seq, text, isFinal: false);
            return;
        }

        Interlocked.Exchange(ref _lastFinalSeq, job.Seq);
        Interlocked.Increment(ref _metrics.UtterancesProcessed);
        _metrics.RecordAsr(result.ProcessingTime.TotalMilliseconds);
        var sinceSpeechEnd = Stopwatch.GetElapsedTime(job.EndedTimestamp);
        bool stale = sinceSpeechEnd > _options.StaleAfter;
        if (!stale) _metrics.RecordSpeechToText(sinceSpeechEnd.TotalMilliseconds);
        _stabilizer!.OnFinal(job.Seq, text, now);
        if (!stale) TranscriptUpdated?.Invoke(new TranscriptUpdate(job.Seq, text, true));

        if (text.Length == 0)
        {
            FinishEmpty(job);
            return;
        }

        var info = _segments.GetOrAdd(job.Seq, _ => new SegmentInfo { Start = job.Start });
        info.End = job.End;
        info.EndedTimestamp = job.EndedTimestamp;
        info.FinalText = text;
        info.Language = result.Language;
        info.Confidence = result.Confidence;
        info.AsrMs = (int)result.ProcessingTime.TotalMilliseconds;
        info.Stale = stale;

        _sequencer!.NoteOriginal(job.Seq, text);
        if (_translation is not null)
        {
            SubmitTranslation(job.Seq, text, isFinal: true);
        }
        else
        {
            lock (_dispatchGate)
                Dispatch(_sequencer.Offer(new SequencedSubtitle(job.Seq, text, null, true, null), now));
        }
    }

    private void FinishEmpty(AsrJob job)
    {
        _segments.TryRemove(job.Seq, out _);
        lock (_dispatchGate) Dispatch(_sequencer!.Close(job.Seq, Clock));
    }

    private void SubmitTranslation(long seq, string text, bool isFinal)
    {
        _translation!.Submit(new TranslationJob(seq, text, isFinal, _context!.Snapshot(), _options.Glossary.FindIn(text)));
        _metrics.TranslationPending = _translation.Pending;
        _metrics.TranslationInFlight = _translation.InFlight;
    }

    // ─────────────────────── translation completions / timer ───────────────────────

    private void OnTranslationCompleted(TranslationOutcome o)
    {
        if (State != PipelineState.Listening) return;
        if (_segments.TryGetValue(o.Seq, out var info) && !o.FromCache && o.Translation is not null)
            info.TranslationMs = (int)o.Latency.TotalMilliseconds;
        if (o.Translation is not null && !o.FromCache) _metrics.RecordTranslation(o.Latency.TotalMilliseconds);
        if (o.Error is not null) Interlocked.Increment(ref _metrics.TranslationFailures);

        lock (_dispatchGate)
            Dispatch(_sequencer!.Offer(new SequencedSubtitle(o.Seq, o.SourceText, o.Translation, o.IsFinal, o.Error), Clock));
    }

    private void OnTranslationHealth(TranslationHealth health, string? message)
    {
        _metrics.TranslationHealth = health.ToString();
        if (health is TranslationHealth.AuthError or TranslationHealth.ConfigError or TranslationHealth.Offline && message is not null)
            Error?.Invoke($"Translation {health}: {message}");
    }

    private void Housekeeping()
    {
        if (State != PipelineState.Listening) return;
        lock (_dispatchGate) Dispatch(_sequencer!.Poll(Clock));
        if (_translation is not null)
        {
            _metrics.TranslationPending = _translation.Pending;
            _metrics.TranslationInFlight = _translation.InFlight;
        }
    }

    /// <summary>Must be called under <see cref="_dispatchGate"/> so released subtitles are raised in order.</summary>
    private void Dispatch(SequencerOutput output)
    {
        foreach (var seq in output.Abandoned)
        {
            _translation?.Cancel(seq);
            _log.LogInformation("Translation for #{Seq} abandoned: it blocked newer subtitles for {Timeout} ms",
                seq, _options.HeadTimeout.TotalMilliseconds);
        }

        foreach (var s in output.Display)
        {
            bool failed = s.Error is not null || s.Abandoned;
            bool stale = _segments.TryGetValue(s.Seq, out var current) && current.Stale;
            Safe(() => SubtitleReady?.Invoke(new SubtitleUpdate(s.Seq, s.Original, s.Translation, s.IsFinal, failed, stale)));
            if (!s.IsFinal) continue;

            if (s.Translation is not null) _context!.Add(s.Original, s.Translation);
            if (!_segments.TryRemove(s.Seq, out var info)) info = new SegmentInfo();
            if (info.EndedTimestamp != 0 && s.Translation is not null && !info.Stale)
                _metrics.RecordTotal(Stopwatch.GetElapsedTime(info.EndedTimestamp).TotalMilliseconds);

            if (_session is { } session)
            {
                var record = new SegmentRecord(session.Id, s.Seq, DateTimeOffset.Now, info.Start, info.End,
                    info.FinalText ?? s.Original, s.Translation, info.Language, info.Confidence, info.AsrMs, info.TranslationMs);
                Safe(() => SegmentCompleted?.Invoke(record));
            }
        }
    }

    private void Cleanup()
    {
        DetachTranslation();
        _finals.Clear();
        _vad?.Dispose();
        _vad = null;
        _cts?.Dispose();
        _cts = null;
        _dspThread = _asrThread = null;
        _session = null;
    }

    private void Guard(string name, Action body)
    {
        try { body(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Worker} worker crashed", name);
            Error?.Invoke($"{name} worker failed: {ex.Message}");
        }
    }

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { _log.LogError(ex, "Pipeline event handler failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _finals.Dispose();
    }
}
