namespace ExtormSub.Core.Audio;

public enum SampleEncoding { Float32, Pcm16, Pcm24, Pcm32 }

public sealed record AudioFormat(int SampleRate, int Channels, SampleEncoding Encoding)
{
    public const int AsrSampleRate = 16_000;

    public int BytesPerSample => Encoding switch
    {
        SampleEncoding.Pcm16 => 2,
        SampleEncoding.Pcm24 => 3,
        _ => 4,
    };

    public int BlockAlign => BytesPerSample * Channels;
    public int BytesPerSecond => BlockAlign * SampleRate;

    public override string ToString() => $"{SampleRate} Hz · {Channels} ch · {Encoding}";
}

/// <summary>Raised on the capture thread. Implementations must copy, never hold, the span.</summary>
public delegate void AudioDataHandler(ReadOnlySpan<byte> data);

/// <summary>A live PCM source (WASAPI loopback today, microphone later).</summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Valid after <see cref="Start"/>.</summary>
    AudioFormat Format { get; }
    string DeviceId { get; }
    string DeviceName { get; }

    event AudioDataHandler? DataAvailable;

    /// <summary>Raised when capture stops. Exception is non-null when the device was lost or failed.</summary>
    event EventHandler<Exception?>? Stopped;

    void Start();
    void Stop();
}
