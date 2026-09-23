namespace ExtormSub.Core.Audio;

/// <summary>Frame-level voice activity detector over 16 kHz mono float audio.</summary>
public interface IVoiceActivityDetector : IDisposable
{
    string Name { get; }

    /// <summary>Samples per frame this detector expects (512 for Silero v5 at 16 kHz).</summary>
    int FrameSize { get; }

    /// <summary>Speech probability 0..1 for exactly <see cref="FrameSize"/> samples.</summary>
    float Process(ReadOnlySpan<float> frame);

    void Reset();
}

/// <summary>RMS-based fallback used when the Silero model is unavailable. Maps -55..-30 dBFS onto 0..1.</summary>
public sealed class EnergyVad : IVoiceActivityDetector
{
    public string Name => "Energy";
    public int FrameSize => 512;

    public float Process(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        double rms = Math.Sqrt(sum / Math.Max(1, frame.Length));
        double db = 20 * Math.Log10(rms + 1e-9);
        return (float)Math.Clamp((db + 55) / 25, 0, 1);
    }

    public void Reset() { }
    public void Dispose() { }
}

public sealed record VadOptions
{
    public float Threshold { get; init; } = 0.5f;
    public int MinSpeechMs { get; init; } = 250;
    public int SilenceTimeoutMs { get; init; } = 500;
    public int PreRollMs { get; init; } = 300;
    public int PostRollMs { get; init; } = 150;
    public int MaxUtteranceMs { get; init; } = 10_000;
    public int PartialIntervalMs { get; init; } = 800;
    public int MinPartialMs { get; init; } = 1_000;

    /// <summary>Silero-style hysteresis: speech continues while probability stays above this.</summary>
    public float ReleaseThreshold => Math.Max(0.01f, Threshold - 0.15f);
}

[Flags]
public enum VadEvents
{
    None = 0,
    Started = 1,
    PartialDue = 2,
    Ended = 4,
}

/// <summary>A finished utterance. <see cref="Audio"/> is owned by the receiver.</summary>
public sealed record Utterance(long Seq, float[] Audio, TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Turns per-frame speech probabilities into utterances with pre-roll, post-roll, minimum speech,
/// silence timeout, forced splits and partial-transcription cadence. Pure state machine; single-threaded.
/// </summary>
public sealed class VadSegmenter
{
    public const int SampleRate = AudioFormat.AsrSampleRate;

    private readonly VadOptions _o;
    private readonly float[] _preRoll;
    private int _preRollPos;
    private int _preRollCount;

    private float[] _utterance = new float[SampleRate * 4];
    private int _length;
    private long _startSample;
    private int _voicedSamples;
    private int _trailingSilence;
    private int _samplesSincePartial;
    private bool _active;     // collecting audio (candidate or confirmed)
    private bool _confirmed;  // passed MinSpeech, has a seq
    private long _nextSeq;
    private long _streamSamples;

    public VadSegmenter(VadOptions options, long firstSeq = 1)
    {
        _o = options;
        _preRoll = new float[Math.Max(1, Ms(options.PreRollMs))];
        _nextSeq = firstSeq;
    }

    public long CurrentSeq { get; private set; }
    public bool IsActive => _active;
    public bool InSpeech => _confirmed;
    public TimeSpan StreamTime => Time(_streamSamples);
    public TimeSpan CurrentStart => Time(_startSample);

    /// <summary>The last utterance closed by an <see cref="VadEvents.Ended"/> event.</summary>
    public Utterance? LastEnded { get; private set; }

    /// <summary>Audio collected so far for the current confirmed utterance (valid until the next call).</summary>
    public ReadOnlySpan<float> CurrentAudio => _utterance.AsSpan(0, _length);

    public VadEvents Process(ReadOnlySpan<float> frame, float probability)
    {
        _streamSamples += frame.Length;
        var events = VadEvents.None;

        if (!_active)
        {
            if (probability >= _o.Threshold)
            {
                _active = true;
                _length = 0;
                _voicedSamples = 0;
                _trailingSilence = 0;
                _samplesSincePartial = 0;
                _startSample = _streamSamples - frame.Length - _preRollCount;
                AppendPreRoll();
            }
            else
            {
                PushPreRoll(frame);
                return events;
            }
        }

        Append(frame);
        if (probability >= _o.ReleaseThreshold)
        {
            _trailingSilence = 0;
            if (probability >= _o.Threshold) _voicedSamples += frame.Length;
        }
        else
        {
            _trailingSilence += frame.Length;
        }

        if (!_confirmed && _voicedSamples >= Ms(_o.MinSpeechMs))
        {
            _confirmed = true;
            CurrentSeq = _nextSeq++;
            events |= VadEvents.Started;
        }

        if (_trailingSilence >= Ms(_o.SilenceTimeoutMs))
        {
            if (_confirmed)
            {
                // Keep PostRoll of the trailing silence, drop the rest.
                int keep = _length - _trailingSilence + Math.Min(_trailingSilence, Ms(_o.PostRollMs));
                events |= End(keep);
            }
            else
            {
                Reset(keepPreRollFromTail: true);
            }
            return events;
        }

        if (_confirmed)
        {
            if (_length >= Ms(_o.MaxUtteranceMs))
            {
                events |= End(_length);
                // Speech continues: open the next utterance immediately, no pre-roll (it was already sent).
                _active = true;
                _confirmed = true;
                CurrentSeq = _nextSeq++;
                _startSample = _streamSamples;
                _length = 0;
                _voicedSamples = 0;
                _samplesSincePartial = 0;
                events |= VadEvents.Started;
                return events;
            }

            _samplesSincePartial += frame.Length;
            if (_samplesSincePartial >= Ms(_o.PartialIntervalMs) && _length >= Ms(_o.MinPartialMs))
            {
                _samplesSincePartial = 0;
                events |= VadEvents.PartialDue;
            }
        }
        return events;
    }

    /// <summary>Advances the stream clock over a gap with no audio (loopback delivers nothing while silent).</summary>
    public void AdvanceIdle(int samples)
    {
        if (_active) throw new InvalidOperationException("Feed real silence frames while an utterance is open.");
        _streamSamples += samples;
        _preRollCount = 0;
    }

    /// <summary>Forces the open utterance (if any) to end, e.g. when listening stops.</summary>
    public VadEvents Flush()
    {
        if (!_active) return VadEvents.None;
        if (_confirmed) return End(_length - _trailingSilence + Math.Min(_trailingSilence, Ms(_o.PostRollMs)));
        Reset(keepPreRollFromTail: false);
        return VadEvents.None;
    }

    private VadEvents End(int keepSamples)
    {
        keepSamples = Math.Clamp(keepSamples, 0, _length);
        var audio = _utterance.AsSpan(0, keepSamples).ToArray();
        LastEnded = new Utterance(CurrentSeq, audio, Time(_startSample), Time(_startSample + keepSamples));
        Reset(keepPreRollFromTail: true);
        return VadEvents.Ended;
    }

    private void Reset(bool keepPreRollFromTail)
    {
        _preRollCount = 0;
        _preRollPos = 0;
        if (keepPreRollFromTail && _length > 0)
            PushPreRoll(_utterance.AsSpan(Math.Max(0, _length - _preRoll.Length), Math.Min(_length, _preRoll.Length)));
        _active = false;
        _confirmed = false;
        _length = 0;
        _voicedSamples = 0;
        _trailingSilence = 0;
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        if (_length + samples.Length > _utterance.Length)
            Array.Resize(ref _utterance, Math.Max(_utterance.Length * 2, _length + samples.Length));
        samples.CopyTo(_utterance.AsSpan(_length));
        _length += samples.Length;
    }

    private void PushPreRoll(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            _preRoll[_preRollPos] = s;
            _preRollPos = (_preRollPos + 1) % _preRoll.Length;
        }
        _preRollCount = Math.Min(_preRoll.Length, _preRollCount + samples.Length);
    }

    private void AppendPreRoll()
    {
        int start = (_preRollPos - _preRollCount + _preRoll.Length) % _preRoll.Length;
        int first = Math.Min(_preRollCount, _preRoll.Length - start);
        Append(_preRoll.AsSpan(start, first));
        Append(_preRoll.AsSpan(0, _preRollCount - first));
        _preRollCount = 0;
    }

    private static int Ms(int ms) => ms * SampleRate / 1000;
    private static TimeSpan Time(long samples) => TimeSpan.FromSeconds((double)Math.Max(0, samples) / SampleRate);
}
