using System.Collections.ObjectModel;
using System.Windows.Threading;
using ExtormSub.App.UI;
using ExtormSub.Core.ASR;
using ExtormSub.Core.Settings;
using ExtormSub.Infrastructure.ASR;
using Microsoft.Extensions.Logging;

namespace ExtormSub.App.Services;

public sealed class ModelItemViewModel(WhisperModel model) : ObservableObject
{
    private bool _isDownloaded, _isDownloading, _isActive;
    private double _progress;
    private string _status = "";

    public WhisperModel Model { get; } = model;
    public string Name => Model.DisplayName;
    public string Id => Model.Id;
    public string Details => $"{Model.SizeText} · {(Model.EnglishOnly ? "English" : "Multilingual")} · {Model.Notes}";
    public CancellationTokenSource? Cts { get; set; }

    public bool IsDownloaded { get => _isDownloaded; set { if (Set(ref _isDownloaded, value)) Raise(nameof(CanDownload)); } }
    public bool IsDownloading { get => _isDownloading; set { if (Set(ref _isDownloading, value)) { Raise(nameof(CanDownload)); Raise(nameof(ShowProgress)); } } }
    /// <summary>Progress bar while downloading, or to show how far a paused download got.</summary>
    public bool ShowProgress => IsDownloading || (!IsDownloaded && PartialBytes > 0);
    public bool CanDownload => !IsDownloaded && !IsDownloading;
    public string DownloadLabel => PartialBytes > 0 ? "Resume" : "Download";
    public long PartialBytes { get; set; }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public void NotifyDownloadLabel()
    {
        Raise(nameof(DownloadLabel));
        Raise(nameof(ShowProgress));
    }
    public string Status { get => _status; set => Set(ref _status, value); }
}

/// <summary>
/// App-lifetime model manager state, so downloads continue (and stay visible) when the settings window closes.
/// Lives on the UI thread; progress callbacks are marshalled via Progress&lt;T&gt;.
/// </summary>
public sealed class ModelLibrary
{
    private readonly ModelDownloader _downloader;
    private readonly ListeningController _controller;
    private readonly SettingsStore _settings;
    private readonly ILogger _log;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    public ModelLibrary(HttpClient http, ListeningController controller, SettingsStore settings, ILogger<ModelLibrary> log)
    {
        _downloader = new ModelDownloader(http);
        _controller = controller;
        _settings = settings;
        _log = log;
        Items = new(ModelCatalog.All.Select(m => new ModelItemViewModel(m)));
        Refresh();
        settings.Changed += (_, _) => _ui.BeginInvoke(Refresh);
    }

    public ObservableCollection<ModelItemViewModel> Items { get; }

    public event Action<Notice>? Notification;

    public string Directory => _controller.ModelsDirectory;

    public void Refresh()
    {
        var active = _controller.Resolve(_settings.Current).ModelId;
        foreach (var item in Items)
        {
            item.IsActive = item.Id == active;
            if (item.IsDownloading) continue;
            item.IsDownloaded = ModelDownloader.IsDownloaded(Directory, item.Model);
            item.PartialBytes = item.IsDownloaded ? 0 : ModelDownloader.PartialBytes(Directory, item.Model);
            item.Status = item.IsDownloaded ? "Downloaded · verified SHA-256"
                : item.PartialBytes > 0 ? $"Paused at {item.PartialBytes / 1048576.0:0} of {item.Model.Bytes / 1048576.0:0} MB — resume to continue"
                : "Not downloaded";
            item.Progress = item.IsDownloaded ? 100 : 100.0 * item.PartialBytes / Math.Max(1, item.Model.Bytes);
            item.NotifyDownloadLabel();
        }
    }

    public ModelItemViewModel? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

    public async Task DownloadAsync(ModelItemViewModel item)
    {
        if (item.IsDownloading || item.IsDownloaded) return;
        item.IsDownloading = true;
        item.Cts = new CancellationTokenSource();
        item.Status = "Starting…";
        var progress = new Progress<DownloadProgress>(p =>
        {
            item.Progress = (p.Fraction ?? 0) * 100;
            item.Status = p.Total is { } t
                ? $"{p.Received / 1048576.0:0} / {t / 1048576.0:0} MB · {p.BytesPerSecond / 1048576.0:0.0} MB/s"
                : $"{p.Received / 1048576.0:0} MB";
        });
        try
        {
            await _downloader.DownloadAsync(item.Model, Directory, progress, item.Cts.Token);
            _log.LogInformation("Downloaded model {Model}", item.Id);
            Notification?.Invoke(new Notice("Model ready", $"{item.Name} is downloaded.", NoticeLevel.Info));
            item.IsDownloading = false;
            Refresh();
            // The user tried to listen before the model existed: start now.
            if (item.IsActive && _controller.State == ListeningState.Error) _ = _controller.StartAsync();
        }
        catch (OperationCanceledException)
        {
            item.IsDownloading = false;
            Refresh(); // shows "Paused at … — resume to continue"
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Model download failed for {Model}", item.Id);
            item.IsDownloading = false;
            Refresh();
            item.Status = (ex is ChecksumMismatchException ? "" : "Download failed — ") + ex.Message;
            Notification?.Invoke(new Notice("Model download failed", $"{item.Name}: {ex.Message}", NoticeLevel.Error));
        }
        finally
        {
            item.Cts?.Dispose();
            item.Cts = null;
        }
    }

    public void Cancel(ModelItemViewModel item) => item.Cts?.Cancel();

    public void Delete(ModelItemViewModel item)
    {
        if (item.IsActive && _controller.State is ListeningState.Listening or ListeningState.Loading)
        {
            Notification?.Invoke(new Notice("Model in use", "Stop listening before deleting the active model.", NoticeLevel.Warning));
            return;
        }
        try
        {
            ModelDownloader.Delete(Directory, item.Model);
        }
        catch (IOException ex)
        {
            Notification?.Invoke(new Notice("Could not delete model", ex.Message, NoticeLevel.Error));
        }
        Refresh();
    }
}
