namespace ExtormSub.Core.ASR;

public enum AsrBackend { Auto, Gpu, Cpu }

public enum AsrPreset { Fast, Balanced, Accurate, Custom }

public sealed record AsrOptions
{
    public required string ModelPath { get; init; }
    public string ModelId { get; init; } = "";
    /// <summary>ISO code ("en") or "auto".</summary>
    public string Language { get; init; } = "en";
    public AsrBackend Backend { get; init; } = AsrBackend.Auto;
    /// <summary>0 = automatic.</summary>
    public int Threads { get; init; }
    /// <summary>Glossary-derived prompt that biases recognition toward known terms.</summary>
    public string? InitialPrompt { get; init; }
    /// <summary>Shrink whisper's audio context for short clips. Faster, slightly less accurate.</summary>
    public bool TrimAudioContext { get; init; }
}

public sealed record AsrResult(
    string Text,
    string? Language,
    float? Confidence,
    float NoSpeechProbability,
    TimeSpan ProcessingTime)
{
    public static AsrResult Empty(TimeSpan elapsed) => new("", null, null, 1f, elapsed);
}

/// <summary>
/// Local speech recognition engine. Implementations load the model once and keep it warm.
/// DisposeAsync unloads the model; the provider can be initialized again afterwards (engine switching).
/// </summary>
public interface IASRProvider : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Human-readable backend, e.g. "whisper.cpp · Vulkan". Empty until initialized.</summary>
    string BackendDescription { get; }

    bool IsReady { get; }

    /// <summary>The options the provider is currently initialized with, or null.</summary>
    AsrOptions? Current { get; }

    /// <summary>Loads the model and warms it up. Safe to call again with new options.</summary>
    Task InitializeAsync(AsrOptions options, CancellationToken ct);

    /// <summary>Transcribes 16 kHz mono samples. Callers serialize calls (one ASR worker).</summary>
    Task<AsrResult> TranscribeAsync(float[] samples, int count, CancellationToken ct);
}

public sealed class AsrInitializationException(string message, Exception? inner = null) : Exception(message, inner);
