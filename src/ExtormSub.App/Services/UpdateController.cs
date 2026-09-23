using System.Reflection;
using System.Windows.Input;
using ExtormSub.App.UI;
using ExtormSub.Core.Settings;
using ExtormSub.Infrastructure;
using ExtormSub.Infrastructure.ASR;
using ExtormSub.Infrastructure.Updates;
using Microsoft.Extensions.Logging;

namespace ExtormSub.App.Services;

/// <summary>
/// App-lifetime update state: checks shortly after start and then daily (when enabled), and on demand from About.
/// The manifest URL is baked in at build time (UpdateManifestUrl in ExtormSub.App.csproj); without one, updates are off.
/// Lives on the UI thread.
/// </summary>
public sealed class UpdateController : ObservableObject
{
    private readonly UpdateChecker? _checker;
    private readonly SettingsStore _settings;
    private readonly AppPaths _paths;
    private readonly ILogger _log;
    private UpdateInfo? _available;
    private string _status;
    private bool _busy, _downloading;
    private double _progress;
    private Version? _notified;
    private CancellationTokenSource? _cts;

    public UpdateController(HttpClient http, SettingsStore settings, AppPaths paths, ILogger<UpdateController> log)
    {
        _settings = settings;
        _paths = paths;
        _log = log;
        var url = typeof(UpdateController).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateManifestUrl")?.Value;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) _checker = new UpdateChecker(http, uri);
        _status = _checker is null ? "Updates are not configured for this build." : $"ExtormSub {CurrentText}";

        Check = new RelayCommand(async () => await CheckAsync(manual: true), _ => _checker is not null && !_busy);
        Install = new RelayCommand(async () => await InstallAsync(), _ => _available is not null && !_busy);
        Cancel = new RelayCommand(() => _cts?.Cancel(), _ => _downloading);
    }

    public static Version Current { get; } =
        UpdateChecker.Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 1, 0));
    private static string CurrentText => Current.ToString(3);

    public event Action<Notice>? Notification;

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsAvailable => _available is not null;
    public bool IsDownloading { get => _downloading; private set => Set(ref _downloading, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public ICommand Check { get; }
    public ICommand Install { get; }
    public ICommand Cancel { get; }

    /// <summary>Background loop for the app's lifetime.</summary>
    public async Task RunAsync()
    {
        if (_checker is null) return;
        await Task.Delay(TimeSpan.FromSeconds(30));
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            if (_settings.Current.General.CheckForUpdates) await CheckAsync(manual: false);
        } while (await timer.WaitForNextTickAsync());
    }

    private async Task CheckAsync(bool manual)
    {
        if (_checker is null || _busy) return;
        _busy = true;
        if (manual) Status = "Checking for updates…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _available = await _checker.CheckAsync(Current, cts.Token);
            Raise(nameof(IsAvailable));
            if (_available is null)
            {
                Status = $"ExtormSub {CurrentText} is up to date.";
                try { if (Directory.Exists(_paths.UpdatesDirectory)) Directory.Delete(_paths.UpdatesDirectory, true); }
                catch (IOException) { } // an installer that is still running
                return;
            }
            var v = _available.Version.ToString(3);
            Status = $"ExtormSub {v} is available (you have {CurrentText}).";
            if (!manual && _notified != _available.Version)
            {
                _notified = _available.Version;
                Notification?.Invoke(new Notice("Update available", $"ExtormSub {v} is ready to install. Click to open.", NoticeLevel.Info, "About"));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or IOException)
        {
            _log.LogWarning(ex, "Update check failed");
            if (manual) Status = "Could not check for updates: " + (ex is OperationCanceledException ? "the server did not answer." : ex.Message);
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task InstallAsync()
    {
        if (_checker is null || _available is not { } info || _busy) return;
        _busy = true;
        IsDownloading = true;
        _cts = new CancellationTokenSource();
        var v = info.Version.ToString(3);
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress = (p.Fraction ?? 0) * 100;
                Status = p.Total is { } t ? $"Downloading ExtormSub {v}… {p.Received >> 20} of {t >> 20} MB" : $"Downloading ExtormSub {v}…";
            });
            var installer = await _checker.DownloadAsync(info, _paths.UpdatesDirectory, Environment.ProcessPath!, progress, _cts.Token);
            _log.LogInformation("Starting update installer {Installer}", installer);
            UpdateChecker.Launch(installer);
            Status = $"Installing ExtormSub {v}. ExtormSub will close and reopen.";
        }
        catch (OperationCanceledException)
        {
            Status = "Download paused. Press Install to resume.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            _log.LogWarning(ex, "Update download failed");
            Status = "Update failed: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsDownloading = false;
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
