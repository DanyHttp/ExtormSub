using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ExtormSub.App.Hotkeys;
using ExtormSub.App.Overlay;
using ExtormSub.App.Services;
using ExtormSub.Core.ASR;
using ExtormSub.Core.Diagnostics;
using ExtormSub.Core.Settings;
using ExtormSub.Core.Text;
using ExtormSub.Core.Translation;
using ExtormSub.Infrastructure;
using ExtormSub.Infrastructure.ASR;
using ExtormSub.Infrastructure.Security;

namespace ExtormSub.App.UI;

public sealed record DeviceOption(string? Id, string Name);

/// <summary>
/// Backs the settings window. Edits a working copy of <see cref="AppSettings"/> (<see cref="S"/>);
/// the window auto-saves it after edits. Values that other parts of the app change (overlay placement)
/// are refreshed from the store so a later save never overwrites them with stale data.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly ISecretStore _secrets;
    private readonly ListeningController _controller;
    private readonly OverlayController _overlay;
    private readonly AppPaths _paths;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private DeviceOption? _selectedDevice;
    private string _testResult = "";
    private bool? _testOk;
    private bool _testing;
    private IReadOnlyDictionary<HotkeyAction, string> _hotkeyErrors = new Dictionary<HotkeyAction, string>();
    private readonly CudaRuntimePack _cuda;
    private readonly FasterWhisperEnvironment _fw;
    private readonly Queue<string> _setupLog = new();
    private CancellationTokenSource? _setupCts, _cudaCts;
    private bool _settingUp, _installingCuda;
    private double _cudaProgress;
    private string _cudaStage = "";

    public SettingsViewModel(SettingsStore store, ISecretStore secrets, ListeningController controller, OverlayController overlay,
        ModelLibrary models, AppPaths paths, PipelineMetrics metrics, CudaRuntimePack cuda, UpdateController updates, Action showHistory, Func<Task> clearHistory)
    {
        _cuda = cuda;
        _fw = controller.FasterWhisperEnvironment;
        _store = store;
        _secrets = secrets;
        _controller = controller;
        _overlay = overlay;
        _paths = paths;
        Models = models;
        Updates = updates;
        S = store.Current;
        Diagnostics = new DiagnosticsViewModel(metrics, controller);

        ToggleListening = new RelayCommand(() => _ = controller.ToggleAsync());
        RefreshDevices = new RelayCommand(LoadDevices);
        EditOverlay = new RelayCommand(() => overlay.SetLocked(false));
        ResetOverlayPosition = new RelayCommand(() => Position = OverlayPosition.BottomCenter);
        Download = new RelayCommand(p => { if (p is ModelItemViewModel m) _ = models.DownloadAsync(m); });
        CancelDownload = new RelayCommand(p => { if (p is ModelItemViewModel m) models.Cancel(m); });
        DeleteModel = new RelayCommand(p =>
        {
            if (p is ModelItemViewModel m && Confirm($"Delete {m.Name} ({m.Model.SizeText})?")) models.Delete(m);
        });
        UseModel = new RelayCommand(p =>
        {
            if (p is not ModelItemViewModel m) return;
            Preset = AsrPreset.Custom;
            S.Asr.Model = m.Id;
            SaveNow();
        });
        BrowseModelDirectory = new RelayCommand(BrowseModels);
        ResetModelDirectory = new RelayCommand(() => { S.Asr.ModelDirectory = null; Raise(nameof(ModelDirectoryText)); SaveNow(); });
        OpenModelsFolder = new RelayCommand(() => OpenFolder(models.Directory));
        RetryGpu = new RelayCommand(() =>
        {
            controller.Asr.ClearGpuCrashMarker();
            Raise(nameof(GpuCrashed));
            MessageBox.Show("GPU will be tried again the next time ExtormSub starts.", "ExtormSub", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        TestTranslation = new RelayCommand(async () => await TestAsync(), _ => !_testing);
        OpenHistory = new RelayCommand(showHistory);
        ClearHistory = new RelayCommand(() => { if (Confirm("Delete all saved subtitle history? This cannot be undone.")) _ = clearHistory(); });
        OpenLogs = new RelayCommand(() => OpenFolder(paths.LogsDirectory));
        OpenData = new RelayCommand(() => OpenFolder(paths.Roaming));
        OpenUrl = new RelayCommand(p => { if (p is string url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); });
        SetupFasterWhisper = new RelayCommand(async () => await SetupFasterWhisperAsync(), _ => !_settingUp);
        CancelSetup = new RelayCommand(() => _setupCts?.Cancel(), _ => _settingUp);
        RemoveFasterWhisper = new RelayCommand(RemoveFasterWhisperEnv, _ => !_settingUp && _fw.IsInstalled);
        BrowsePython = new RelayCommand(() =>
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = "Choose python.exe", Filter = "Python|python.exe" };
            if (d.ShowDialog() == true) { PythonPath = d.FileName; Raise(nameof(PythonPath)); SaveNow(); }
        });
        InstallCuda = new RelayCommand(async () => await InstallCudaAsync(), _ => !_installingCuda && !_cuda.IsInstalled && HasNvidia);
        CancelCuda = new RelayCommand(() => _cudaCts?.Cancel(), _ => _installingCuda);
        RemoveCuda = new RelayCommand(() =>
        {
            if (!Confirm("Remove the NVIDIA acceleration pack? whisper.cpp will use Vulkan after restarting ExtormSub.")) return;
            try { _cuda.Uninstall(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show("The pack is in use. Restart ExtormSub and try again.\n\n" + ex.Message, "ExtormSub");
            }
            RaiseCuda();
        }, _ => !_installingCuda && _cuda.IsInstalled);

        LoadDevices();
        controller.StateChanged += OnControllerChanged;
        store.Changed += OnStoreChanged;
    }

    /// <summary>Working copy bound by the pages.</summary>
    public AppSettings S { get; }

    public ModelLibrary Models { get; }
    public UpdateController Updates { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    public static IReadOnlyList<string> Pages { get; } =
        ["General", "Audio", "Speech Recognition", "Translation", "Subtitle Appearance", "Hotkeys", "History", "Diagnostics", "Advanced", "About"];

    // ─── status ───
    public string StatusText => _controller.StatusText;
    public bool IsListening => _controller.State is ListeningState.Listening or ListeningState.Loading;
    public string ListenLabel => IsListening ? "Stop listening" : "Start listening";
    public Brush StatusBrush => _controller.State switch
    {
        ListeningState.Listening => Brushes.MediumSeaGreen,
        ListeningState.Loading => Brushes.CornflowerBlue,
        ListeningState.Error => Brushes.Orange,
        _ => Brushes.Gray,
    };

    // ─── audio ───
    public ObservableCollection<DeviceOption> Devices { get; } = [];

    public static AudioSourceKind[] SourceKinds { get; } = Enum.GetValues<AudioSourceKind>();

    public AudioSourceKind AudioSource
    {
        get => S.Audio.Source;
        set
        {
            if (S.Audio.Source == value) return;
            S.Audio.Source = value;
            Raise();
            Raise(nameof(DeviceLabel));
            LoadDevices();
        }
    }

    public string DeviceLabel => S.Audio.Source == AudioSourceKind.Microphone ? "Microphone" : "Playback device";

    public DeviceOption? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value) || value is null) return;
            if (S.Audio.Source == AudioSourceKind.Microphone) S.Audio.MicrophoneId = value.Id;
            else S.Audio.DeviceId = value.Id;
        }
    }

    public string CurrentDevice => _controller.Pipeline.CurrentDeviceName is { } n ? $"Capturing: {n}" : "Not capturing";

    // ─── speech ───
    public static AsrPreset[] PresetValues { get; } = Enum.GetValues<AsrPreset>();
    public static AsrBackend[] BackendValues { get; } = Enum.GetValues<AsrBackend>();
    public static string[] Languages { get; } = ["en", "auto", "de", "fr", "es", "it", "ja", "ko", "zh", "ru", "ar", "tr", "fa"];

    public AsrPreset Preset
    {
        get => S.Asr.Preset;
        set { S.Asr.Preset = value; Raise(); Raise(nameof(ResolvedText)); }
    }

    public AsrBackend Backend
    {
        get => S.Asr.Backend;
        set { S.Asr.Backend = value; Raise(); Raise(nameof(ResolvedText)); }
    }

    public static string[] Engines { get; } = [AsrEngines.WhisperCpp, AsrEngines.FasterWhisper];

    public string Engine
    {
        get => S.Asr.Engine;
        set
        {
            S.Asr.Engine = value;
            Raise();
            Raise(nameof(IsFasterWhisper));
            Raise(nameof(ResolvedText));
        }
    }

    public bool IsFasterWhisper => S.Asr.Engine == AsrEngines.FasterWhisper;

    public string ResolvedText
    {
        get
        {
            var (backend, model, id) = _controller.Resolve(S);
            if (IsFasterWhisper)
            {
                bool cuda = backend == AsrBackend.Gpu && CudaRuntimePack.NvidiaDriverPresent;
                return $"Uses faster-whisper {ModelCatalog.FasterWhisperModel(id)} on {(cuda ? "GPU (CUDA, float16)" : "CPU (int8)")}";
            }
            var where = backend != AsrBackend.Gpu ? "CPU" : _cuda.IsInstalled && CudaRuntimePack.NvidiaDriverPresent ? "GPU (CUDA)" : "GPU (Vulkan)";
            return $"Uses {model?.DisplayName ?? id} on {where}";
        }
    }

    // ─── faster-whisper ───
    public string PythonPath { get => S.Asr.PythonPath ?? ""; set => S.Asr.PythonPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }
    public bool FasterWhisperInstalled => _fw.IsInstalled;
    public bool IsSettingUp { get => _settingUp; private set { if (Set(ref _settingUp, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string FasterWhisperStatus => _fw.IsInstalled
        ? $"Installed in a private environment ({_fw.EnvDirectory}). Models download on first use."
        : "Not set up. Setup downloads about 150 MB of Python packages into a private folder; your own Python stays untouched.";
    public string SetupLog { get { lock (_setupLog) return string.Join("\n", _setupLog); } }

    private async Task SetupFasterWhisperAsync()
    {
        IsSettingUp = true;
        lock (_setupLog) _setupLog.Clear();
        _setupCts = new CancellationTokenSource();
        var progress = new Progress<string>(line =>
        {
            lock (_setupLog)
            {
                _setupLog.Enqueue(line);
                while (_setupLog.Count > 12) _setupLog.Dequeue();
            }
            Raise(nameof(SetupLog));
        });
        try
        {
            await _fw.SetupAsync(S.Asr.PythonPath, CudaRuntimePack.NvidiaDriverPresent, progress, _setupCts.Token);
            ((IProgress<string>)progress).Report("✓ faster-whisper is ready. Choose it as the engine above.");
        }
        catch (OperationCanceledException)
        {
            ((IProgress<string>)progress).Report("Setup cancelled.");
        }
        catch (Exception ex)
        {
            ((IProgress<string>)progress).Report("✗ " + ex.Message);
        }
        finally
        {
            _setupCts.Dispose();
            _setupCts = null;
            IsSettingUp = false;
            Raise(nameof(FasterWhisperInstalled));
            Raise(nameof(FasterWhisperStatus));
        }
    }

    private void RemoveFasterWhisperEnv()
    {
        if (IsFasterWhisper && IsListening)
        {
            MessageBox.Show("Stop listening or switch the engine to whisper.cpp first.", "ExtormSub");
            return;
        }
        if (!Confirm("Remove the faster-whisper environment and its downloaded models?")) return;
        try { _fw.Remove(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(ex.Message, "ExtormSub"); }
        Raise(nameof(FasterWhisperInstalled));
        Raise(nameof(FasterWhisperStatus));
    }

    // ─── NVIDIA pack ───
    public bool HasNvidia => _controller.Hardware.Gpus.Any(g => g.Vendor == GpuVendor.Nvidia) || CudaRuntimePack.NvidiaDriverPresent;
    public bool CudaInstalled => _cuda.IsInstalled;
    public bool IsInstallingCuda { get => _installingCuda; private set { if (Set(ref _installingCuda, value)) CommandManager.InvalidateRequerySuggested(); } }
    public double CudaProgress { get => _cudaProgress; private set => Set(ref _cudaProgress, value); }
    public string CudaStatus => !HasNvidia
        ? "No NVIDIA GPU detected. Your GPU is used through Vulkan, which needs no extra download."
        : _cuda.IsInstalled ? "Installed. whisper.cpp uses CUDA on the next start (Processor: Automatic or GPU)."
        : IsInstallingCuda ? _cudaStage
        : $"Faster whisper.cpp on NVIDIA GPUs. {_cuda.DownloadBytes / 1048576.0:0} MB download" +
          (CudaRuntimePack.SystemHasCudaRuntime ? " (your CUDA 12 toolkit is reused)." : " including NVIDIA's CUDA 12 runtime libraries.");

    private async Task InstallCudaAsync()
    {
        IsInstallingCuda = true;
        _cudaCts = new CancellationTokenSource();
        var progress = new Progress<(string Stage, DownloadProgress P)>(x =>
        {
            CudaProgress = (x.P.Fraction ?? 0) * 100;
            _cudaStage = $"{x.Stage} · {x.P.Received / 1048576.0:0} / {(x.P.Total ?? 0) / 1048576.0:0} MB";
            Raise(nameof(CudaStatus));
        });
        try
        {
            await _cuda.InstallAsync(progress, _cudaCts.Token);
            MessageBox.Show("NVIDIA acceleration pack installed. Restart ExtormSub to use CUDA.", "ExtormSub", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not install the NVIDIA pack:\n{ex.Message}\n\nDownloads resume where they stopped.", "ExtormSub",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cudaCts.Dispose();
            _cudaCts = null;
            IsInstallingCuda = false;
            RaiseCuda();
        }
    }

    private void RaiseCuda()
    {
        Raise(nameof(CudaInstalled));
        Raise(nameof(CudaStatus));
        Raise(nameof(ResolvedText));
        CommandManager.InvalidateRequerySuggested();
    }

    public string CpuText => $"{_controller.Hardware.CpuName} · {_controller.Hardware.PhysicalCores} cores";
    public string GpuText => _controller.Hardware.Gpus.Count == 0 ? "No GPU detected"
        : string.Join(", ", _controller.Hardware.Gpus.Select(g => g.Name)) + (_controller.Hardware.HasVulkan ? " · Vulkan" : " · no Vulkan driver");
    public bool GpuCrashed => _controller.Asr.GpuCrashedLastRun;
    public string ModelDirectoryText => Models.Directory;

    // ─── translation ───
    public static IEnumerable<string> ProviderNames { get; } = ProviderPresets.All.Select(p => p.Name);

    public string Provider
    {
        get => S.Translation.Provider;
        set
        {
            if (S.Translation.Provider == value) return;
            S.Translation.Provider = value;
            var preset = ProviderPresets.Find(value);
            S.Translation.BaseUrl = preset.BaseUrl;
            if (preset.DefaultModel.Length > 0) S.Translation.Model = preset.DefaultModel;
            Raise();
            Raise(nameof(BaseUrl));
            Raise(nameof(Model));
            Raise(nameof(HasApiKey));
            Raise(nameof(ApiKeyHint));
        }
    }

    public string BaseUrl { get => S.Translation.BaseUrl; set { S.Translation.BaseUrl = value.Trim(); Raise(); } }
    public string Model { get => S.Translation.Model; set { S.Translation.Model = value.Trim(); Raise(); } }

    public bool HasApiKey => !string.IsNullOrEmpty(_secrets.Get(DpapiSecretStore.ApiKeyName(S.Translation.Provider)));
    public string ApiKeyHint => HasApiKey
        ? "A key is saved, encrypted with Windows DPAPI. Type to replace it."
        : ProviderPresets.Find(S.Translation.Provider).RequiresKey ? "Required. Stored encrypted with Windows DPAPI, never in settings.json." : "Optional for local servers.";

    public void SetApiKey(string? key)
    {
        _secrets.Set(DpapiSecretStore.ApiKeyName(S.Translation.Provider), string.IsNullOrWhiteSpace(key) ? null : key.Trim());
        Raise(nameof(HasApiKey));
        Raise(nameof(ApiKeyHint));
        _controller.ApiKeyChanged();
    }

    public string GlossaryText
    {
        get => Glossary.Format(S.Glossary);
        set => S.Glossary = Glossary.Parse(value);
    }

    public string TestResult { get => _testResult; private set => Set(ref _testResult, value); }
    public bool? TestOk { get => _testOk; private set => Set(ref _testOk, value); }

    // ─── appearance ───
    public static IEnumerable<string> Fonts { get; } = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n).ToList();
    public static DisplayMode[] DisplayModes { get; } = Enum.GetValues<DisplayMode>();
    public static OverlayPosition[] Positions { get; } = Enum.GetValues<OverlayPosition>();
    public static TextWeight[] Weights { get; } = Enum.GetValues<TextWeight>();

    public OverlayPosition Position
    {
        get => S.Overlay.Position;
        set { S.Overlay.Position = value; Raise(); }
    }

    // ─── hotkeys ───
    public string? ToggleListeningError => _hotkeyErrors.GetValueOrDefault(HotkeyAction.ToggleListening);
    public string? ToggleLockError => _hotkeyErrors.GetValueOrDefault(HotkeyAction.ToggleLock);
    public string? ToggleVisibilityError => _hotkeyErrors.GetValueOrDefault(HotkeyAction.ToggleVisibility);
    public string? EmergencyError => _hotkeyErrors.GetValueOrDefault(HotkeyAction.EmergencyUnlock);

    public void SetHotkeyErrors(IReadOnlyDictionary<HotkeyAction, string> errors)
    {
        _hotkeyErrors = errors;
        Raise(nameof(ToggleListeningError));
        Raise(nameof(ToggleLockError));
        Raise(nameof(ToggleVisibilityError));
        Raise(nameof(EmergencyError));
    }

    // ─── advanced / about ───
    public static string[] LogLevels { get; } = ["Debug", "Information", "Warning", "Error"];
    public string Version => UpdateController.Current.ToString(3);
    public string DataPaths => $"Settings: {_paths.SettingsFile}\nModels & history: {_paths.Local}";

    // ─── commands ───
    public ICommand ToggleListening { get; }
    public ICommand RefreshDevices { get; }
    public ICommand EditOverlay { get; }
    public ICommand ResetOverlayPosition { get; }
    public ICommand Download { get; }
    public ICommand CancelDownload { get; }
    public ICommand DeleteModel { get; }
    public ICommand UseModel { get; }
    public ICommand BrowseModelDirectory { get; }
    public ICommand ResetModelDirectory { get; }
    public ICommand OpenModelsFolder { get; }
    public ICommand RetryGpu { get; }
    public ICommand TestTranslation { get; }
    public ICommand OpenHistory { get; }
    public ICommand ClearHistory { get; }
    public ICommand OpenLogs { get; }
    public ICommand OpenData { get; }
    public ICommand OpenUrl { get; }
    public ICommand SetupFasterWhisper { get; }
    public ICommand CancelSetup { get; }
    public ICommand RemoveFasterWhisper { get; }
    public ICommand BrowsePython { get; }
    public ICommand InstallCuda { get; }
    public ICommand CancelCuda { get; }
    public ICommand RemoveCuda { get; }

    /// <summary>Persists the working copy. Called by the window's debounced auto-save.</summary>
    public void SaveNow()
    {
        _store.Replace(S);
        Raise(nameof(ResolvedText));
    }

    private void LoadDevices()
    {
        Devices.Clear();
        var kind = S.Audio.Source;
        var defaultName = _controller.Devices.DefaultDeviceName(kind) ?? "none";
        Devices.Add(new DeviceOption(null, $"Windows default ({defaultName})"));
        foreach (var d in _controller.Devices.ListDevices(kind)) Devices.Add(new DeviceOption(d.Id, d.Name));
        var selectedId = kind == AudioSourceKind.Microphone ? S.Audio.MicrophoneId : S.Audio.DeviceId;
        _selectedDevice = Devices.FirstOrDefault(d => d.Id == selectedId) ?? Devices[0];
        Raise(nameof(SelectedDevice));
        Raise(nameof(CurrentDevice));
    }

    private async Task TestAsync()
    {
        _testing = true;
        TestOk = null;
        TestResult = "Testing…";
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var key = _secrets.Get(DpapiSecretStore.ApiKeyName(S.Translation.Provider));
            var (ok, message) = await _controller.TestTranslationAsync(S.Translation, key);
            TestOk = ok;
            TestResult = message;
        }
        finally
        {
            _testing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void BrowseModels()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where speech models are stored", InitialDirectory = Models.Directory };
        if (dialog.ShowDialog() != true) return;
        S.Asr.ModelDirectory = dialog.FolderName;
        Raise(nameof(ModelDirectoryText));
        SaveNow();
    }

    private void OnControllerChanged() => _ui.BeginInvoke(() =>
    {
        Raise(nameof(StatusText));
        Raise(nameof(IsListening));
        Raise(nameof(ListenLabel));
        Raise(nameof(StatusBrush));
        Raise(nameof(CurrentDevice));
    });

    private void OnStoreChanged(AppSettings old, AppSettings updated) => _ui.BeginInvoke(() =>
    {
        // Placement is owned by the overlay (drag/lock); keep the working copy in sync with it.
        S.Overlay.CustomBounds = updated.Overlay.CustomBounds;
        S.Overlay.Monitor = updated.Overlay.Monitor;
        S.Overlay.Visible = updated.Overlay.Visible;
        if (S.Overlay.Position != updated.Overlay.Position) Position = updated.Overlay.Position;
    });

    private static bool Confirm(string message) =>
        MessageBox.Show(message, "ExtormSub", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public void Dispose()
    {
        _controller.StateChanged -= OnControllerChanged;
        _store.Changed -= OnStoreChanged;
        _setupCts?.Cancel();
        Diagnostics.Stop();
    }
}

public sealed class MetricRow(string label) : ObservableObject
{
    private string _value = "—";
    public string Label { get; } = label;
    public string Value { get => _value; set => Set(ref _value, value); }
}

/// <summary>Live pipeline metrics, sampled once a second while the Diagnostics page is open.</summary>
public sealed class DiagnosticsViewModel
{
    private readonly PipelineMetrics _m;
    private readonly ListeningController _controller;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private DateTime _lastSample;

    public DiagnosticsViewModel(PipelineMetrics metrics, ListeningController controller)
    {
        _m = metrics;
        _controller = controller;
        _timer.Tick += (_, _) => Sample();
        Groups =
        [
            new("Audio", ["Device", "Format", "Ring buffer", "Dropped audio", "VAD"]),
            new("Speech recognition", ["Backend", "Model", "Final latency", "Partial latency", "Speech end → text", "Queue", "Utterances", "Dropped / skipped"]),
            new("Translation", ["Provider", "Health", "Latency", "Speech end → subtitle", "Requests", "Cache", "Failures"]),
            new("System", ["CPU (ExtormSub)", "Memory", "Processor", "GPU"]),
        ];
    }

    public IReadOnlyList<MetricGroup> Groups { get; }

    public void Start()
    {
        _process.Refresh();
        _lastCpu = _process.TotalProcessorTime;
        _lastSample = DateTime.UtcNow;
        Sample();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Sample()
    {
        _process.Refresh();
        var now = DateTime.UtcNow;
        var cpu = _process.TotalProcessorTime;
        double cpuPct = (cpu - _lastCpu).TotalMilliseconds / Math.Max(1, (now - _lastSample).TotalMilliseconds) / Environment.ProcessorCount * 100;
        _lastCpu = cpu;
        _lastSample = now;
        var hw = _controller.Hardware;
        var cache = _controller.Cache;

        Set(0, 0, _m.DeviceName);
        Set(0, 1, _m.AudioFormat);
        Set(0, 2, _m.RingCapacityBytes == 0 ? "—" : $"{_m.RingFillBytes / 1024.0:0} / {_m.RingCapacityBytes / 1024.0:0} KB");
        Set(0, 3, $"{_m.DroppedAudioBytes / 1024.0:0} KB");
        Set(0, 4, _m.VadName);
        Set(1, 0, _m.AsrBackend);
        Set(1, 1, _m.AsrModel);
        Set(1, 2, Ms(_m.LastAsrMs, _m.AvgAsrMs));
        Set(1, 3, _m.LastPartialAsrMs > 0 ? $"{_m.LastPartialAsrMs:0} ms" : "—");
        Set(1, 4, Ms(_m.LastSpeechToTextMs, _m.AvgSpeechToTextMs));
        Set(1, 5, $"{_m.AsrQueueDepth} queued · {_m.AsrBacklogSeconds:0.0} s of audio" + (_m.SpooledSeconds > 0 ? $" ({_m.SpooledSeconds:0.0} s on disk)" : ""));
        Set(1, 6, _m.UtterancesProcessed.ToString());
        Set(1, 7, $"{_m.DroppedUtterances} dropped · {_m.PartialsSkipped} partials skipped");
        Set(2, 0, _m.TranslationProvider);
        Set(2, 1, _m.TranslationHealth);
        Set(2, 2, Ms(_m.LastTranslationMs, _m.AvgTranslationMs));
        Set(2, 3, Ms(_m.LastTotalMs, _m.AvgTotalMs));
        Set(2, 4, $"{_m.TranslationPending} waiting · {_m.TranslationInFlight} in flight");
        Set(2, 5, $"{cache.Count} entries · {cache.Hits} hits · {cache.Misses} misses");
        Set(2, 6, _m.TranslationFailures.ToString());
        Set(3, 0, $"{cpuPct:0.0} %");
        Set(3, 1, $"{_process.WorkingSet64 / 1048576.0:0} MB working set · {_process.PrivateMemorySize64 / 1048576.0:0} MB private");
        Set(3, 2, $"{hw.CpuName} · {hw.PhysicalCores}C/{hw.LogicalCores}T");
        Set(3, 3, hw.Gpus.Count == 0 ? "None" : string.Join(", ", hw.Gpus.Select(g => g.Name)) + (hw.HasVulkan ? " · Vulkan" : ""));
    }

    private void Set(int group, int row, string value) => Groups[group].Rows[row].Value = value;

    private static string Ms(double last, double avg) => last <= 0 ? "—" : $"{last:0} ms  (avg {avg:0} ms)";
}

public sealed class MetricGroup(string title, string[] labels)
{
    public string Title { get; } = title.ToUpperInvariant();
    public IReadOnlyList<MetricRow> Rows { get; } = labels.Select(l => new MetricRow(l)).ToList();
}
