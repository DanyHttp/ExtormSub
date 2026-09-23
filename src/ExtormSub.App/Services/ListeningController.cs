using ExtormSub.Core.ASR;
using ExtormSub.Core.Audio;
using ExtormSub.Core.Diagnostics;
using ExtormSub.Core.History;
using ExtormSub.Core.Pipeline;
using ExtormSub.Core.Settings;
using ExtormSub.Core.Text;
using ExtormSub.Core.Translation;
using ExtormSub.Infrastructure;
using ExtormSub.Infrastructure.ASR;
using ExtormSub.Infrastructure.Audio;
using ExtormSub.Infrastructure.Security;
using ExtormSub.Infrastructure.Vad;
using Microsoft.Extensions.Logging;

namespace ExtormSub.App.Services;

public enum ListeningState { Idle, Loading, Listening, Error }

public enum NoticeLevel { Info, Warning, Error }

/// <summary>A tray balloon. <see cref="Page"/> is the settings page that clicking it opens.</summary>
public sealed record Notice(string Title, string Message, NoticeLevel Level, string? Page = null);

/// <summary>
/// Application-level coordinator: turns settings into a running pipeline and keeps it running.
/// Resolves backend/model, loads ASR once, builds the translation queue, follows device changes,
/// and restarts only the parts affected by a settings change. All public methods are thread-safe.
/// </summary>
public sealed class ListeningController : IAsyncDisposable
{
    private readonly SettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly AppPaths _paths;
    private readonly WhisperCppProvider _asr;
    private readonly FasterWhisperProvider _fasterWhisper;
    private readonly FasterWhisperEnvironment _fasterWhisperEnv;
    private readonly AudioDeviceService _devices;
    private readonly SubtitlePipeline _pipeline;
    private readonly HistoryRecorder _history;
    private readonly PipelineMetrics _metrics;
    private readonly HttpClient _http;
    // Loopback endpoints (Ollama, LM Studio) must not go through a system proxy.
    private readonly HttpClient _localHttp = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ILogger _log;
    private readonly ILogger _translationLog;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TranslationCache _cache;
    private TranslationQueue? _queue;
    private bool _deviceFellBack;
    private Timer? _deviceDebounce;
    private bool _gpuCrashNotified;

    public ListeningController(
        SettingsStore settings, ISecretStore secrets, AppPaths paths, WhisperCppProvider asr,
        FasterWhisperProvider fasterWhisper, FasterWhisperEnvironment fasterWhisperEnv, AudioDeviceService devices,
        HistoryRecorder history, PipelineMetrics metrics, HttpClient http, ILoggerFactory loggers)
    {
        _settings = settings;
        _secrets = secrets;
        _paths = paths;
        _asr = asr;
        _fasterWhisper = fasterWhisper;
        _fasterWhisperEnv = fasterWhisperEnv;
        _devices = devices;
        _history = history;
        _metrics = metrics;
        _http = http;
        _log = loggers.CreateLogger<ListeningController>();
        _translationLog = loggers.CreateLogger<TranslationQueue>();
        var vadLog = loggers.CreateLogger("ExtormSub.Vad");
        _pipeline = new SubtitlePipeline(CreateSource, () => CreateVad(_settings.Current.Vad.UseSilero, vadLog), metrics,
            loggers.CreateLogger<SubtitlePipeline>());
        _cache = new TranslationCache(settings.Current.Advanced.TranslationCacheSize);

        _history.Attach(_pipeline);
        _pipeline.CaptureLost += _ => ThreadPool.QueueUserWorkItem(_ => HandleDeviceLost(_pipeline.CurrentDeviceId, "stopped responding"));
        _pipeline.Error += message => Notify("ExtormSub", message, NoticeLevel.Warning);
        _devices.DefaultDeviceChanged += OnDefaultDeviceChanged;
        _devices.DeviceLost += id => HandleDeviceLost(id, "was disconnected");
        _settings.Changed += OnSettingsChanged;
    }

    public ListeningState State { get; private set; } = ListeningState.Idle;
    public string StatusText { get; private set; } = "Idle";
    public HardwareInfo Hardware => HardwareDetector.Detect(_log);
    public SubtitlePipeline Pipeline => _pipeline;
    public WhisperCppProvider Asr => _asr;
    public AudioDeviceService Devices => _devices;
    public TranslationCache Cache => _cache;

    public event Action? StateChanged;
    public event Action<Notice>? Notification;
    /// <summary>Raised when listening cannot start until the user downloads a model or sets up the engine.</summary>
    public event Action? SetupNeeded;

    public string ModelsDirectory => _paths.ModelsDirectory(_settings.Current.Asr.ModelDirectory);

    public FasterWhisperEnvironment FasterWhisperEnvironment => _fasterWhisperEnv;

    /// <summary>Backend and model the current settings resolve to on this machine.</summary>
    public (AsrBackend Backend, WhisperModel? Model, string ModelId) Resolve(AppSettings s)
    {
        var backend = ModelCatalog.ResolveBackend(s.Asr.Backend, Hardware, _asr.GpuCrashedLastRun);
        var id = ModelCatalog.ModelForPreset(s.Asr.Preset, backend == AsrBackend.Gpu, s.Asr.Model);
        return (backend, ModelCatalog.Find(id), id);
    }

    public Task ToggleAsync() => State is ListeningState.Listening or ListeningState.Loading ? StopAsync() : StartAsync();

    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State is ListeningState.Listening) return;
            await StartCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetState(ListeningState.Idle, "Idle");
        }
        finally { _gate.Release(); }
    }

    private async Task StartCoreAsync()
    {
        var s = _settings.Current;
        var (backend, model, modelId) = Resolve(s);

        if (_asr.GpuCrashedLastRun && s.Asr.Backend != AsrBackend.Cpu && !_gpuCrashNotified)
        {
            _gpuCrashNotified = true;
            Notify("GPU disabled", "ExtormSub crashed while starting the GPU last time, so speech recognition runs on the CPU. " +
                                   "You can retry the GPU in Settings → Speech Recognition.", NoticeLevel.Warning);
        }

        bool fasterWhisper = s.Asr.Engine == AsrEngines.FasterWhisper;
        IASRProvider engine = fasterWhisper ? _fasterWhisper : _asr;
        IASRProvider other = fasterWhisper ? _asr : _fasterWhisper;
        if (other.IsReady) await other.DisposeAsync().ConfigureAwait(false); // free the unused engine's memory

        string modelPath;
        if (fasterWhisper)
        {
            if (!_fasterWhisperEnv.IsInstalled)
            {
                SetState(ListeningState.Error, "faster-whisper not set up");
                SetupNeeded?.Invoke();
                Notify("Set up faster-whisper", "Click “Set up faster-whisper” in Settings → Speech Recognition, or switch the engine back to whisper.cpp.", NoticeLevel.Warning);
                return;
            }
            modelPath = ModelCatalog.FasterWhisperModel(modelId); // a model name; the sidecar downloads it on first use
        }
        else
        {
            modelPath = model is null ? Path.Combine(ModelsDirectory, $"ggml-{modelId}.bin") : ModelDownloader.PathFor(ModelsDirectory, model);
            if (!File.Exists(modelPath))
            {
                SetState(ListeningState.Error, "Model not downloaded");
                SetupNeeded?.Invoke();
                Notify("Speech model needed", $"Download “{model?.DisplayName ?? modelId}” in Settings → Speech Recognition to start.", NoticeLevel.Warning);
                return;
            }
        }

        var glossary = new Glossary(s.Glossary);
        var options = new AsrOptions
        {
            ModelPath = modelPath,
            ModelId = modelId,
            Backend = backend,
            Language = s.Asr.Language,
            Threads = s.Asr.Threads > 0 ? s.Asr.Threads : ModelCatalog.DefaultThreads(Hardware),
            InitialPrompt = glossary.AsrPrompt,
            TrimAudioContext = s.Asr.TrimAudioContext,
        };

        SetState(ListeningState.Loading, fasterWhisper && !engine.IsReady ? $"Starting faster-whisper ({modelPath})…" : $"Loading {modelId}…");
        try
        {
            await engine.InitializeAsync(options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AsrInitializationException ex) when (backend == AsrBackend.Gpu)
        {
            _log.LogWarning(ex, "GPU initialization failed; retrying on CPU");
            Notify("GPU unavailable", "The GPU could not be used for speech recognition. Falling back to the CPU.", NoticeLevel.Warning);
            try
            {
                await engine.InitializeAsync(options with { Backend = AsrBackend.Cpu }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AsrInitializationException cpuEx)
            {
                Fail("Speech recognition failed", cpuEx.Message, cpuEx);
                return;
            }
        }
        catch (AsrInitializationException ex)
        {
            Fail("Speech recognition failed", ex.Message, ex);
            return;
        }

        _queue = CreateTranslationQueue(s);
        _history.Enabled = s.History.Enabled;
        _history.AsrModel = fasterWhisper ? $"faster-whisper/{modelPath}" : modelId;
        _history.TranslationModel = _queue is null ? null : $"{s.Translation.Provider}/{s.Translation.Model}";

        var pipelineOptions = new PipelineOptions
        {
            Vad = new VadOptions
            {
                Threshold = s.Vad.SpeechThreshold,
                MinSpeechMs = s.Vad.MinSpeechMs,
                SilenceTimeoutMs = s.Vad.SilenceTimeoutMs,
                PreRollMs = s.Vad.PreRollMs,
                PostRollMs = s.Vad.PostRollMs,
                MaxUtteranceMs = s.Vad.MaxUtteranceSeconds * 1000,
                PartialIntervalMs = s.Asr.PartialIntervalMs,
            },
            Partials = s.Asr.ShowPartials,
            StabilityDebounce = TimeSpan.FromMilliseconds(s.Asr.StabilityDebounceMs),
            RingBuffer = TimeSpan.FromSeconds(s.Audio.RingBufferSeconds),
            MaxAsrBacklog = TimeSpan.FromSeconds(s.Advanced.MaxAsrBacklogSeconds),
            HeadTimeout = TimeSpan.FromMilliseconds(s.Advanced.SequencerHeadTimeoutMs),
            NoSpeechThreshold = s.Advanced.NoSpeechThreshold,
            SpoolDirectory = s.Advanced.SpoolToDisk ? Path.Combine(_paths.Local, "spool") : null,
            MaxSpool = TimeSpan.FromMinutes(s.Advanced.MaxSpoolMinutes),
            StaleAfter = TimeSpan.FromSeconds(s.Advanced.StaleAfterSeconds),
            ContextSize = s.Translation.ContextSize,
            Glossary = glossary,
        };

        try
        {
            _deviceFellBack = false;
            _pipeline.Start(pipelineOptions, DeviceIdFor(s.Audio), engine, _queue);
        }
        catch (Exception ex)
        {
            _queue?.Dispose();
            _queue = null;
            Fail("Cannot capture audio", ex is NoAudioDeviceException or UnauthorizedAccessException ? ex.Message : $"Audio capture failed: {ex.Message}", ex);
            return;
        }

        if (_deviceFellBack)
            Notify("Audio device unavailable", $"The selected {KindName(s.Audio.Source)} is not available. Using the Windows default: {_pipeline.CurrentDeviceName}.", NoticeLevel.Warning);
        SetState(ListeningState.Listening, $"Listening · {_pipeline.CurrentDeviceName}");
    }

    private async Task StopCoreAsync()
    {
        await _pipeline.StopAsync().ConfigureAwait(false);
        _queue?.Dispose();
        _queue = null;
    }

    /// <summary>Factory handed to the pipeline; also records whether we had to fall back to the default device.</summary>
    public IAudioSource CreateSource(string? deviceId)
    {
        var source = _devices.CreateSource(_settings.Current.Audio.Source, deviceId, out bool fellBack);
        _deviceFellBack |= fellBack;
        return source;
    }

    private static string? DeviceIdFor(AudioSettings a) => a.Source == AudioSourceKind.Microphone ? a.MicrophoneId : a.DeviceId;

    private static string KindName(AudioSourceKind kind) => kind == AudioSourceKind.Microphone ? "microphone" : "playback device";

    public static IVoiceActivityDetector CreateVad(bool useSilero, ILogger log)
    {
        if (useSilero)
        {
            try { return new SileroVad(AppPaths.SileroModel); }
            catch (Exception ex) { log.LogWarning(ex, "Silero VAD unavailable; using the energy detector"); }
        }
        return new EnergyVad();
    }

    private TranslationQueue? CreateTranslationQueue(AppSettings s)
    {
        if (!s.Translation.Enabled) return null;
        var preset = ProviderPresets.Find(s.Translation.Provider);
        var key = _secrets.Get(DpapiSecretStore.ApiKeyName(s.Translation.Provider));
        if (string.IsNullOrWhiteSpace(s.Translation.BaseUrl) || string.IsNullOrWhiteSpace(s.Translation.Model))
        {
            Notify("Translation not configured", "Set the translation endpoint and model in Settings → Translation. Showing English only.", NoticeLevel.Warning);
            return null;
        }
        // No API key yet: translation stays off and subtitles are English only, without nagging.
        if (preset.RequiresKey && string.IsNullOrWhiteSpace(key)) return null;
        var provider = new OpenAiCompatibleProvider(HttpFor(s.Translation.BaseUrl), new OpenAiCompatibleConfig
        {
            ProviderName = s.Translation.Provider,
            BaseUrl = s.Translation.BaseUrl,
            Model = s.Translation.Model,
            Temperature = s.Translation.Temperature,
            Timeout = TimeSpan.FromSeconds(s.Translation.TimeoutSeconds),
        }, key);
        return new TranslationQueue(provider, _cache, new TranslationQueueOptions
        {
            MaxConcurrent = s.Translation.MaxConcurrentRequests,
            MaxRequestsPerMinute = s.Translation.MaxRequestsPerMinute,
            MaxRetries = s.Translation.MaxRetries,
            SourceLanguage = s.Translation.SourceLanguage,
            TargetLanguage = s.Translation.TargetLanguage,
        }, _translationLog);
    }

    private HttpClient HttpFor(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback ? _localHttp : _http;

    /// <summary>One-off request used by the "Test connection" button.</summary>
    public async Task<(bool Ok, string Message)> TestTranslationAsync(TranslationSettings t, string? apiKey)
    {
        var provider = new OpenAiCompatibleProvider(HttpFor(t.BaseUrl), new OpenAiCompatibleConfig
        {
            ProviderName = t.Provider, BaseUrl = t.BaseUrl, Model = t.Model, Temperature = t.Temperature,
            Timeout = TimeSpan.FromSeconds(t.TimeoutSeconds),
        }, apiKey);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await provider.TranslateAsync(new TranslationRequest("That's sick, I didn't expect it to work.", [], [],
                t.SourceLanguage, t.TargetLanguage), CancellationToken.None).ConfigureAwait(false);
            return (true, $"{result}   ·   {sw.ElapsedMilliseconds} ms");
        }
        catch (TranslationException ex)
        {
            return (false, ex.Kind switch
            {
                TranslationErrorKind.Auth => "The API key was rejected (401/403).",
                TranslationErrorKind.RateLimited => "Rate limited by the provider (429). Try again shortly.",
                _ => ex.Message,
            });
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            return (false, $"Invalid endpoint: {ex.Message}");
        }
    }

    private void OnDefaultDeviceChanged(AudioSourceKind kind, string? id)
    {
        var audio = _settings.Current.Audio;
        if (kind != audio.Source || DeviceIdFor(audio) is not null) return; // only when following the default of this kind
        // Windows raises this several times per switch; collapse into one restart.
        _deviceDebounce?.Dispose();
        _deviceDebounce = new Timer(_ =>
        {
            if (_pipeline.State != PipelineState.Listening) return;
            if (_pipeline.CurrentDeviceId == id) return;
            TrySwitch(null, $"Now capturing the new default device.");
        }, null, 500, Timeout.Infinite);
    }

    private void HandleDeviceLost(string? id, string what)
    {
        if (_pipeline.State != PipelineState.Listening || id is null || _pipeline.CurrentDeviceId != id) return;
        var name = _pipeline.CurrentDeviceName;
        TrySwitch(null, $"{name} {what}. Switched to the Windows default {KindName(_settings.Current.Audio.Source)}.");
    }

    private void TrySwitch(string? deviceId, string message)
    {
        _gate.Wait();
        try
        {
            _pipeline.SwitchDevice(deviceId);
            SetState(ListeningState.Listening, $"Listening · {_pipeline.CurrentDeviceName}");
            Notify("Audio device changed", message, NoticeLevel.Info);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Device switch failed");
            _ = Task.Run(async () =>
            {
                await StopAsync().ConfigureAwait(false);
                Fail("No audio device", ex is NoAudioDeviceException ? ex.Message : $"Audio capture failed: {ex.Message}", ex);
            });
        }
        finally { _gate.Release(); }
    }

    private void OnSettingsChanged(AppSettings old, AppSettings updated)
    {
        _history.Enabled = updated.History.Enabled;
        if (State is not (ListeningState.Listening or ListeningState.Loading)) return;
        bool restart = Json(old.Audio) != Json(updated.Audio) || Json(old.Vad) != Json(updated.Vad) ||
                       Json(old.Asr) != Json(updated.Asr) || Json(old.Glossary) != Json(updated.Glossary) ||
                       Json(old.Advanced) != Json(updated.Advanced);
        if (!restart)
        {
            // Translation-only edits (provider, model, key) swap the queue; capture and ASR keep running.
            if (Json(old.Translation) != Json(updated.Translation)) ReplaceTranslation(updated);
            return;
        }
        _log.LogInformation("Pipeline settings changed; restarting listening");
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCoreAsync().ConfigureAwait(false);
                await StartCoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { Fail("Restart failed", ex.Message, ex); }
            finally { _gate.Release(); }
        });
    }

    /// <summary>Re-applies translation settings after the API key changed (keys are not part of settings).</summary>
    public void ApiKeyChanged()
    {
        if (State == ListeningState.Listening) ReplaceTranslation(_settings.Current);
    }

    private void ReplaceTranslation(AppSettings s)
    {
        _gate.Wait();
        try
        {
            if (_pipeline.State != PipelineState.Listening) return;
            var old = _queue;
            _queue = CreateTranslationQueue(s);
            _pipeline.SetTranslation(_queue);
            old?.Dispose();
            _history.TranslationModel = _queue is null ? null : $"{s.Translation.Provider}/{s.Translation.Model}";
        }
        finally { _gate.Release(); }
    }

    private static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);

    private void Fail(string title, string message, Exception ex)
    {
        _log.LogError(ex, "{Title}: {Message}", title, message);
        SetState(ListeningState.Error, title);
        Notify(title, message, NoticeLevel.Error);
    }

    private void SetState(ListeningState state, string text)
    {
        State = state;
        StatusText = text;
        StateChanged?.Invoke();
    }

    private void Notify(string title, string message, NoticeLevel level) => Notification?.Invoke(new Notice(title, message, level));

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        _deviceDebounce?.Dispose();
        await StopCoreAsync().ConfigureAwait(false);
        await _history.DisposeAsync().ConfigureAwait(false);
        await _asr.DisposeAsync().ConfigureAwait(false);
        await _fasterWhisper.DisposeAsync().ConfigureAwait(false);
        _devices.Dispose();
    }
}
