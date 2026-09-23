using System.Windows;
using ExtormSub.App.Hotkeys;
using ExtormSub.App.Overlay;
using ExtormSub.App.Services;
using ExtormSub.Core.Diagnostics;
using ExtormSub.Core.History;
using ExtormSub.Core.Settings;
using ExtormSub.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ExtormSub.App.UI;

/// <summary>Opens the settings and history windows (one instance each) and applies minimize-to-tray.</summary>
public sealed class UiShell(IServiceProvider services, SettingsStore settings)
{
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private IReadOnlyDictionary<HotkeyAction, string> _hotkeyErrors = new Dictionary<HotkeyAction, string>();
    private SettingsViewModel? _vm;

    public void ShowSettings(string? page = null)
    {
        if (_settingsWindow is null)
        {
            _vm = new SettingsViewModel(
                settings,
                services.GetRequiredService<ISecretStore>(),
                services.GetRequiredService<ListeningController>(),
                services.GetRequiredService<OverlayController>(),
                services.GetRequiredService<ModelLibrary>(),
                services.GetRequiredService<AppPaths>(),
                services.GetRequiredService<PipelineMetrics>(),
                services.GetRequiredService<ExtormSub.Infrastructure.ASR.CudaRuntimePack>(),
                services.GetRequiredService<UpdateController>(),
                () => ShowHistory(),
                () => services.GetRequiredService<IHistoryStore>().ClearAsync());
            _vm.SetHotkeyErrors(_hotkeyErrors);
            _settingsWindow = new SettingsWindow(_vm, page);
            _settingsWindow.Closed += (_, _) => { _settingsWindow = null; _vm = null; };
            _settingsWindow.StateChanged += (_, _) =>
            {
                if (_settingsWindow?.WindowState == WindowState.Minimized && settings.Current.General.MinimizeToTray)
                    _settingsWindow.Hide();
            };
            _settingsWindow.Show();
        }
        else
        {
            if (page is not null) _settingsWindow.Navigate(page);
            _settingsWindow.Show();
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        }
        _settingsWindow.Activate();
    }

    public void ShowHistory()
    {
        if (_historyWindow is null)
        {
            _historyWindow = new HistoryWindow(services.GetRequiredService<HistoryViewModel>());
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Show();
        }
        else if (_historyWindow.WindowState == WindowState.Minimized)
        {
            _historyWindow.WindowState = WindowState.Normal;
        }
        _historyWindow.Activate();
    }

    public void PublishHotkeyErrors(IReadOnlyDictionary<HotkeyAction, string> errors)
    {
        _hotkeyErrors = errors;
        _vm?.SetHotkeyErrors(errors);
    }

    public void CloseAll()
    {
        _settingsWindow?.Close();
        _historyWindow?.Close();
    }
}
