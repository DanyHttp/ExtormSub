using System.Windows;
using System.Windows.Threading;
using ExtormSub.App.Hotkeys;
using ExtormSub.App.Infrastructure;
using ExtormSub.App.Overlay;
using ExtormSub.App.Services;
using ExtormSub.App.Tray;
using ExtormSub.App.UI;
using ExtormSub.Core.History;
using ExtormSub.Core.Settings;
using ExtormSub.Infrastructure.ASR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;

namespace ExtormSub.App;

/// <summary>
/// Tray application lifetime: single instance, composition, wiring between services and UI,
/// global exception guards and ordered shutdown. The app lives in the tray; windows come and go.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\ExtormSub.SingleInstance";
    private const string ShowEventName = @"Local\ExtormSub.ShowSettings";
    private const string ExitEventName = @"Local\ExtormSub.Exit";
    private const string ExitArg = "--exit"; // used by installers/updaters to close the running instance cleanly

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal, _exitSignal;
    private RegisteredWaitHandle? _showWait, _exitWait;
    private IHost? _host;
    private ILogger? _log;
    private ListeningController? _controller;
    private OverlayController? _overlay;
    private TrayIcon? _tray;
    private HotkeyService? _hotkeys;
    private UiShell? _shell;
    private DateTime _lastErrorNotice;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (firstInstance && e.Args.Contains(ExitArg))
        {
            Shutdown(); // nothing to exit
            return;
        }
        if (!firstInstance)
        {
            // Already running: ask that instance to show its settings window (or exit) and quit.
            var name = e.Args.Contains(ExitArg) ? ExitEventName : ShowEventName;
            try { EventWaitHandle.OpenExisting(name).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        try
        {
            await StartAsync(e.Args);
        }
        catch (Exception ex)
        {
            _log?.LogCritical(ex, "Startup failed");
            MessageBox.Show($"ExtormSub could not start:\n\n{ex.Message}", "ExtormSub", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartAsync(string[] args)
    {
        _host = Bootstrap.Build();
        var sp = _host.Services;
        _log = sp.GetRequiredService<ILogger<App>>();
        RegisterExceptionGuards();
        await _host.StartAsync();

        var settings = sp.GetRequiredService<SettingsStore>();
        var levelSwitch = sp.GetRequiredService<LoggingLevelSwitch>();
        levelSwitch.MinimumLevel = Bootstrap.ParseLevel(settings.Current.Advanced.LogLevel);
        _log.LogInformation("ExtormSub {Version} starting (settings: {Path})", typeof(App).Assembly.GetName().Version, settings.FilePath);

        try { await sp.GetRequiredService<IHistoryStore>().InitializeAsync(); }
        catch (Exception ex) { _log.LogError(ex, "History database unavailable; history will not be saved"); }

        _controller = sp.GetRequiredService<ListeningController>();
        _shell = sp.GetRequiredService<UiShell>();
        _tray = sp.GetRequiredService<TrayIcon>();
        var models = sp.GetRequiredService<ModelLibrary>();

        try
        {
            _overlay = sp.GetRequiredService<OverlayController>();
            _overlay.Attach(_controller.Pipeline);
            _overlay.ShowInitial();
            _overlay.StateChanged += UpdateTray;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Overlay creation failed");
            _tray.Notify(new Notice("Overlay unavailable", "The subtitle overlay could not be created. See the log for details.", NoticeLevel.Error));
        }

        _tray.ToggleListening += () => _ = _controller.ToggleAsync();
        _tray.ShowSettings += page => _shell.ShowSettings(page);
        _tray.ShowHistory += () => _shell.ShowHistory();
        _tray.ToggleLock += () => _overlay?.ToggleLock();
        _tray.ToggleOverlay += () => _overlay?.ToggleVisible();
        _tray.ExitRequested += () => _ = ExitAsync();

        _controller.StateChanged += () => Dispatcher.BeginInvoke(UpdateTray);
        _controller.Notification += n => Dispatcher.BeginInvoke(() => _tray.Notify(n));
        _controller.SetupNeeded += () => Dispatcher.BeginInvoke(() => _shell.ShowSettings("Speech Recognition"));
        models.Notification += n => _tray.Notify(n);
        var updates = sp.GetRequiredService<UpdateController>();
        updates.Notification += n => _tray.Notify(n);

        _hotkeys = sp.GetRequiredService<HotkeyService>();
        _hotkeys.Pressed += OnHotkey;
        ApplyHotkeys(settings.Current);
        ApplyStartup(settings.Current);
        settings.Changed += (old, updated) => Dispatcher.BeginInvoke(() =>
        {
            if (Json(old.Hotkeys) != Json(updated.Hotkeys)) ApplyHotkeys(updated);
            if (old.General.StartWithWindows != updated.General.StartWithWindows) ApplyStartup(updated);
            levelSwitch.MinimumLevel = Bootstrap.ParseLevel(updated.Advanced.LogLevel);
        });

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(() => _shell.ShowSettings()), null, Timeout.Infinite, executeOnlyOnce: false);
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal,
            (_, _) => Dispatcher.BeginInvoke(() => _ = ExitAsync()), null, Timeout.Infinite, executeOnlyOnce: true);

        var current = settings.Current;
        bool firstRun = !current.General.FirstRunCompleted;
        if (firstRun) settings.Update(s => s.General.FirstRunCompleted = true);
        bool modelMissing = current.Asr.Engine == AsrEngines.FasterWhisper
            ? !_controller.FasterWhisperEnvironment.IsInstalled
            : _controller.Resolve(current).Model is { } m && !ModelDownloader.IsDownloaded(_controller.ModelsDirectory, m);
        bool launchedMinimized = args.Contains(StartupRegistration.MinimizedArg);

        if (firstRun || modelMissing) _shell.ShowSettings(modelMissing ? "Speech Recognition" : "General");
        else if (!current.General.StartMinimized && !launchedMinimized) _shell.ShowSettings();
        if (firstRun) _tray.Notify(new Notice("ExtormSub is in your tray", "Press Ctrl+Alt+S to start subtitles. Right-click the tray icon for options.", NoticeLevel.Info));

        UpdateTray();
        if (current.General.StartListeningOnLaunch && !modelMissing) _ = _controller.StartAsync();
        _ = updates.RunAsync();
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleListening: _ = _controller!.ToggleAsync(); break;
            case HotkeyAction.ToggleLock: _overlay?.ToggleLock(); break;
            case HotkeyAction.ToggleVisibility: _overlay?.ToggleVisible(); break;
            case HotkeyAction.EmergencyUnlock: _overlay?.EmergencyRestore(); break;
        }
    }

    private void ApplyHotkeys(AppSettings s)
    {
        var errors = _hotkeys!.Apply(new Dictionary<HotkeyAction, string>
        {
            [HotkeyAction.ToggleListening] = s.Hotkeys.ToggleListening,
            [HotkeyAction.ToggleLock] = s.Hotkeys.ToggleLock,
            [HotkeyAction.ToggleVisibility] = s.Hotkeys.ToggleVisibility,
            [HotkeyAction.EmergencyUnlock] = s.Hotkeys.EmergencyUnlock,
        });
        _shell!.PublishHotkeyErrors(errors);
        if (errors.Count > 0)
            _tray!.Notify(new Notice("Shortcut unavailable", string.Join("\n", errors.Values), NoticeLevel.Warning));
    }

    private void ApplyStartup(AppSettings s)
    {
        try { StartupRegistration.Apply(s.General.StartWithWindows); }
        catch (Exception ex) { _log?.LogWarning(ex, "Could not update the Windows startup entry"); }
    }

    private void UpdateTray()
    {
        if (_tray is null || _controller is null) return;
        _tray.Update(_controller.State, _controller.StatusText, _overlay?.IsLocked ?? true, _overlay?.IsVisible ?? false);
    }

    private void RegisterExceptionGuards()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Unhandled UI exception");
            e.Handled = true; // keep the tray app alive
            if (DateTime.UtcNow - _lastErrorNotice > TimeSpan.FromSeconds(30) && _tray is not null)
            {
                _lastErrorNotice = DateTime.UtcNow;
                _tray.Notify(new Notice("Something went wrong", e.Exception.Message, NoticeLevel.Error));
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            _log?.LogCritical(e.ExceptionObject as Exception, "Fatal unhandled exception (terminating: {Terminating})", e.IsTerminating);
            Serilog.Log.CloseAndFlush();
        };
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _log?.LogInformation("Exiting");
        try
        {
            _shell?.CloseAll();
            _hotkeys?.Dispose();
            if (_controller is not null) await _controller.DisposeAsync();
            _overlay?.Dispose();
            _tray?.Dispose();
            // UI objects were disposed above, on the UI thread and in order. The container is not disposed:
            // it would dispose them again from a pool thread, and the process is exiting anyway.
            if (_host is not null) await _host.StopAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Error during shutdown");
        }
        finally
        {
            _showWait?.Unregister(null);
            _exitWait?.Unregister(null);
            _showSignal?.Dispose();
            _exitSignal?.Dispose();
            Serilog.Log.CloseAndFlush();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            Shutdown();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows logoff/shutdown: stop capture and flush history quickly.
        _ = ExitAsync();
        base.OnSessionEnding(e);
    }

    private static string Json<T>(T v) => System.Text.Json.JsonSerializer.Serialize(v);
}
