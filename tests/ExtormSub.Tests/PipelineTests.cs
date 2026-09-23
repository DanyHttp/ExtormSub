using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ExtormSub.Core.ASR;
using ExtormSub.Core.Audio;
using ExtormSub.Core.Diagnostics;
using ExtormSub.Core.History;
using ExtormSub.Core.Pipeline;
using ExtormSub.Core.Translation;

namespace ExtormSub.Tests;

internal sealed class FakeAudioSource : IAudioSource
{
    public AudioFormat Format { get; } = new(48000, 2, SampleEncoding.Float32);
    public string DeviceId => "fake";
    public string DeviceName => "Fake speakers";
    public event AudioDataHandler? DataAvailable;
    public event EventHandler<Exception?>? Stopped;
    public bool Started { get; private set; }
    public void Start() => Started = true;
    public void Stop() => Started = false;
    public void Dispose() { }
    public void Lose() => Stopped?.Invoke(this, new IOException("unplugged"));

    /// <summary>Pushes mono samples as 48 kHz stereo float, in 10 ms packets like WASAPI.</summary>
    public void Push(float[] mono)
    {
        var stereo = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++) stereo[2 * i] = stereo[2 * i + 1] = mono[i];
        var bytes = MemoryMarshal.AsBytes(stereo.AsSpan());
        for (int off = 0; off < bytes.Length; off += 3840)
            DataAvailable?.Invoke(bytes.Slice(off, Math.Min(3840, bytes.Length - off)));
    }

    public static float[] Tone(double seconds) =>
        Enumerable.Range(0, (int)(48000 * seconds)).Select(i => 0.5f * MathF.Sin(2 * MathF.PI * 220 * i / 48000f)).ToArray();

    public static float[] Silence(double seconds) => new float[(int)(48000 * seconds)];
}

/// <summary>Text depends on audio length so each utterance is distinguishable.</summary>
internal sealed class FakeAsr : IASRProvider
{
    public ConcurrentQueue<(int Samples, TimeSpan At)> Calls { get; } = new();
    public string Name => "Fake";
    public string BackendDescription => "fake · cpu";
    public bool IsReady => true;
    public AsrOptions? Current { get; } = new() { ModelPath = "x", ModelId = "fake" };
    public Task InitializeAsync(AsrOptions options, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Simulated inference time (the ASR worker blocks, like real whisper).</summary>
    public int DelayMs { get; set; }

    public Task<AsrResult> TranscribeAsync(float[] samples, int count, CancellationToken ct)
    {
        if (DelayMs > 0) Thread.Sleep(DelayMs);
        Calls.Enqueue((count, DateTime.UtcNow.TimeOfDay));
        int deciseconds = (int)Math.Round(count / 1600.0);
        return Task.FromResult(new AsrResult($"speech of {deciseconds} tenths", "en", 0.9f, 0.01f, TimeSpan.FromMilliseconds(5)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class SubtitlePipelineTests
{
    private static PipelineOptions Options => new()
    {
        Vad = new VadOptions { MinSpeechMs = 96, SilenceTimeoutMs = 300, PreRollMs = 0, PostRollMs = 0, PartialIntervalMs = 5000 },
        RingBuffer = TimeSpan.FromSeconds(30),
        HeadTimeout = TimeSpan.FromSeconds(10),
    };

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "timed out waiting for pipeline");
    }

    [Fact]
    public async Task Speech_flows_to_ordered_translated_subtitles_and_history_records()
    {
        var source = new FakeAudioSource();
        var translator = new FakeTranslator();
        // The first utterance translates slowest, so raw completion order is 2, 3, 1.
        translator.DelayMs = t => t.Contains("10 tenths") ? 400 : 20;
        using var queue = new TranslationQueue(translator, new TranslationCache(100), new TranslationQueueOptions { MaxRequestsPerMinute = 0 });
        await using var pipeline = new SubtitlePipeline(_ => source, () => new EnergyVad(), new PipelineMetrics());

        var subtitles = new ConcurrentQueue<SubtitleUpdate>();
        var records = new ConcurrentQueue<SegmentRecord>();
        var english = new ConcurrentQueue<TranscriptUpdate>();
        pipeline.SubtitleReady += subtitles.Enqueue;
        pipeline.SegmentCompleted += records.Enqueue;
        pipeline.TranscriptUpdated += english.Enqueue;

        pipeline.Start(Options, null, new FakeAsr(), queue);
        Assert.True(source.Started);

        source.Push(FakeAudioSource.Silence(0.5));
        source.Push(FakeAudioSource.Tone(1.0));
        source.Push(FakeAudioSource.Silence(0.6));
        source.Push(FakeAudioSource.Tone(1.5));
        source.Push(FakeAudioSource.Silence(0.6));
        source.Push(FakeAudioSource.Tone(2.0));
        source.Push(FakeAudioSource.Silence(0.6));

        await WaitUntil(() => records.Count == 3);
        await pipeline.StopAsync();

        var finals = subtitles.Where(s => s.IsFinal).ToList();
        Assert.Equal([1L, 2L, 3L], finals.Select(s => s.Seq));
        Assert.All(finals, s => Assert.Equal("FA:" + s.Original, s.Translation));
        Assert.Contains("10 tenths", finals[0].Original);
        Assert.Contains("15 tenths", finals[1].Original);
        Assert.Contains("20 tenths", finals[2].Original);

        var recs = records.OrderBy(r => r.Seq).ToList();
        Assert.InRange(recs[0].Start.TotalSeconds, 0.45, 0.6);   // tone starts at 0.5 s
        Assert.InRange(recs[1].Start.TotalSeconds, 2.05, 2.2);   // 0.5 + 1.0 + 0.6
        Assert.All(recs, r => Assert.True(r.End > r.Start));
        Assert.Equal(3, english.Count(e => e.IsFinal));
    }

    [Fact]
    public async Task Open_utterance_closes_when_loopback_goes_quiet_with_no_packets()
    {
        var source = new FakeAudioSource();
        await using var pipeline = new SubtitlePipeline(_ => source, () => new EnergyVad(), new PipelineMetrics());
        var subtitles = new ConcurrentQueue<SubtitleUpdate>();
        pipeline.SubtitleReady += subtitles.Enqueue;

        pipeline.Start(Options, null, new FakeAsr(), translation: null);
        source.Push(FakeAudioSource.Tone(1.0)); // then nothing at all, like a paused video

        await WaitUntil(() => !subtitles.IsEmpty, 3000);
        var s = Assert.Single(subtitles);
        Assert.Null(s.Translation); // translation disabled → English-only subtitle
        Assert.True(s.IsFinal);
    }

    [Fact]
    public async Task Translation_failure_still_releases_english_in_order()
    {
        var source = new FakeAudioSource();
        var translator = new FakeTranslator { Fail = (_, _) => new TranslationException(TranslationErrorKind.Auth, "bad key") };
        using var queue = new TranslationQueue(translator, new TranslationCache(10), new TranslationQueueOptions { MaxRequestsPerMinute = 0 });
        await using var pipeline = new SubtitlePipeline(_ => source, () => new EnergyVad(), new PipelineMetrics());
        var subtitles = new ConcurrentQueue<SubtitleUpdate>();
        var errors = new ConcurrentQueue<string>();
        pipeline.SubtitleReady += subtitles.Enqueue;
        pipeline.Error += errors.Enqueue;

        pipeline.Start(Options, null, new FakeAsr(), queue);
        source.Push(FakeAudioSource.Tone(1.0));
        source.Push(FakeAudioSource.Silence(0.6));

        await WaitUntil(() => !subtitles.IsEmpty);
        var s = Assert.Single(subtitles);
        Assert.True(s.TranslationFailed);
        Assert.Null(s.Translation);
        Assert.Contains(errors, e => e.Contains("AuthError"));
    }

    [Fact]
    public async Task Device_loss_is_reported_and_switching_keeps_the_session()
    {
        var first = new FakeAudioSource();
        var second = new FakeAudioSource();
        var sources = new Queue<FakeAudioSource>([first, second]);
        await using var pipeline = new SubtitlePipeline(_ => sources.Dequeue(), () => new EnergyVad(), new PipelineMetrics());
        Exception? lost = null;
        pipeline.CaptureLost += e => lost = e;

        pipeline.Start(Options, null, new FakeAsr(), null);
        var session = pipeline.Session;
        first.Lose();
        Assert.IsType<IOException>(lost);

        pipeline.SwitchDevice(null);
        Assert.True(second.Started);
        Assert.False(first.Started);
        Assert.Same(session, pipeline.Session);
    }
}
