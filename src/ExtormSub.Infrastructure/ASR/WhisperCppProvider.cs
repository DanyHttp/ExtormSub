using System.Diagnostics;
using ExtormSub.Core.ASR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace ExtormSub.Infrastructure.ASR;

/// <summary>
/// whisper.cpp through Whisper.net. The model is loaded once, warmed up, and reused for every utterance.
/// GPU (CUDA with the NVIDIA pack, else Vulkan) is tried first when requested; whisper.net falls back to the
/// CPU runtime if no GPU runtime can load. A sentinel file detects native crashes during GPU init so the next start uses CPU.
/// </summary>
public sealed class WhisperCppProvider : IASRProvider
{
    // whisper.cpp returns nothing for inputs shorter than 1 s; pad short utterances with silence.
    private const int MinSamples = 17_600; // 1.1 s

    private static readonly object RuntimeGate = new();
    private static bool _runtimeConfigured;

    private readonly string _sentinelPath;
    private readonly CudaRuntimePack? _cuda;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<SegmentData> _segments = [];
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private float[] _padBuffer = new float[MinSamples];

    public WhisperCppProvider(AppPaths paths, CudaRuntimePack? cuda = null, ILogger<WhisperCppProvider>? log = null)
    {
        _sentinelPath = paths.GpuInitSentinel;
        _cuda = cuda;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    public string Name => "whisper.cpp";
    public string BackendDescription { get; private set; } = "";
    public bool IsReady => _processor is not null;
    public AsrOptions? Current { get; private set; }

    /// <summary>True when the previous run crashed natively while initializing the GPU backend.</summary>
    public bool GpuCrashedLastRun => File.Exists(_sentinelPath);

    public async Task InitializeAsync(AsrOptions options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsReady && Current == options) return;
            await Task.Run(() => Load(options, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Load(AsrOptions options, CancellationToken ct)
    {
        if (!File.Exists(options.ModelPath))
            throw new AsrInitializationException($"Model file not found: {options.ModelPath}. Download it in Settings → Speech Recognition.");

        Unload();
        bool gpu = options.Backend == AsrBackend.Gpu;
        ConfigureRuntimeOnce(gpu);

        var sw = Stopwatch.StartNew();
        if (gpu) File.WriteAllText(_sentinelPath, DateTime.Now.ToString("O"));
        try
        {
            _factory = WhisperFactory.FromPath(options.ModelPath, new WhisperFactoryOptions { UseGpu = gpu });
            var builder = _factory.CreateBuilder()
                .WithThreads(Math.Max(1, options.Threads))
                .WithNoContext()
                .WithSingleSegment()
                .WithProbabilities()
                .WithSegmentEventHandler(_segments.Add);
            builder = options.Language is "auto" or "" ? builder.WithLanguageDetection() : builder.WithLanguage(options.Language);
            if (!string.IsNullOrWhiteSpace(options.InitialPrompt)) builder = builder.WithPrompt(options.InitialPrompt);
            if (options.TrimAudioContext) builder = builder.WithAudioContextSize(768); // ~15 s window instead of 30 s
            _processor = builder.Build();

            ct.ThrowIfCancellationRequested();
            // Warm-up: the first inference allocates GPU buffers / kernels. Do it now, not on the first sentence.
            var warm = new float[16_000 * 2];
            for (int i = 0; i < warm.Length; i++) warm[i] = 0.001f * MathF.Sin(i * 0.05f);
            RunLocked(warm, warm.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AsrInitializationException)
        {
            Unload();
            throw new AsrInitializationException($"Could not load the speech model: {ex.Message}", ex);
        }
        finally
        {
            if (gpu) TryDelete(_sentinelPath);
        }

        Current = options;
        var runtime = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
        BackendDescription = $"whisper.cpp · {runtime}";
        _log.LogInformation("Loaded {Model} on {Runtime} ({Threads} threads) in {Ms} ms",
            options.ModelId, runtime, options.Threads, sw.ElapsedMilliseconds);
        if (gpu && runtime.StartsWith("Cpu", StringComparison.OrdinalIgnoreCase))
            _log.LogWarning("GPU was requested but no GPU runtime loaded; running on CPU");
    }

    /// <summary>
    /// Whisper.net picks a native runtime once per process. Order CUDA 12 (only when the NVIDIA pack is
    /// installed and an NVIDIA driver is present) → Vulkan → CPU (AVX) → CPU (no AVX), so any failure falls
    /// back instead of crashing. Changing backend later needs an app restart.
    /// </summary>
    private void ConfigureRuntimeOnce(bool gpu)
    {
        lock (RuntimeGate)
        {
            if (_runtimeConfigured)
            {
                if (gpu != RuntimeOptions.LoadedLibrary is RuntimeLibrary.Vulkan or RuntimeLibrary.Cuda12)
                    _log.LogInformation("Backend change takes effect after restarting ExtormSub");
                return;
            }
            bool cuda = gpu && _cuda is { IsInstalled: true } && CudaRuntimePack.NvidiaDriverPresent;
            if (cuda) _cuda!.Activate();
            RuntimeOptions.RuntimeLibraryOrder = (gpu, cuda) switch
            {
                (true, true) => [RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
                (true, false) => [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
                _ => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            };
            _log.LogInformation("Native runtime order: {Order}", string.Join(" → ", RuntimeOptions.RuntimeLibraryOrder));
            _runtimeConfigured = true;
        }
    }

    public async Task<AsrResult> TranscribeAsync(float[] samples, int count, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_processor is null) throw new InvalidOperationException("Speech model is not loaded.");
            return RunLocked(samples, count);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AsrResult RunLocked(float[] samples, int count)
    {
        var sw = Stopwatch.StartNew();
        ReadOnlySpan<float> input = samples.AsSpan(0, count);
        if (count < MinSamples)
        {
            Array.Clear(_padBuffer);
            input.CopyTo(_padBuffer);
            input = _padBuffer;
        }

        _segments.Clear();
        _processor!.Process(input);

        if (_segments.Count == 0) return AsrResult.Empty(sw.Elapsed);
        var text = string.Concat(_segments.Select(s => s.Text)).Trim();
        float confidence = _segments.Average(s => s.Probability);
        float noSpeech = _segments.Max(s => s.NoSpeechProbability);
        return new AsrResult(text, _segments[0].Language, confidence, noSpeech, sw.Elapsed);
    }

    private void Unload()
    {
        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
        Current = null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    /// <summary>Clears the crash marker (e.g. after the user explicitly re-enables GPU).</summary>
    public void ClearGpuCrashMarker() => TryDelete(_sentinelPath);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Unload(); }
        finally { _gate.Release(); }
    }
}
