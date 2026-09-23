using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ExtormSub.App.UI.Pages;

public partial class GeneralPage : UserControl { public GeneralPage() => InitializeComponent(); }
public partial class AudioPage : UserControl { public AudioPage() => InitializeComponent(); }
public partial class SpeechPage : UserControl { public SpeechPage() => InitializeComponent(); }
public partial class AppearancePage : UserControl { public AppearancePage() => InitializeComponent(); }
public partial class HotkeysPage : UserControl { public HotkeysPage() => InitializeComponent(); }
public partial class HistoryPage : UserControl { public HistoryPage() => InitializeComponent(); }
public partial class AdvancedPage : UserControl { public AdvancedPage() => InitializeComponent(); }
public partial class AboutPage : UserControl { public AboutPage() => InitializeComponent(); }

public partial class DiagnosticsPage : UserControl
{
    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as SettingsViewModel)?.Diagnostics.Start();
        Unloaded += (_, _) => (DataContext as SettingsViewModel)?.Diagnostics.Stop();
    }
}

public partial class TranslationPage : UserControl
{
    // PasswordBox.Password is deliberately not bindable; save it after typing pauses.
    private readonly DispatcherTimer _keyTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public TranslationPage()
    {
        InitializeComponent();
        _keyTimer.Tick += (_, _) =>
        {
            _keyTimer.Stop();
            (DataContext as SettingsViewModel)?.SetApiKey(KeyBox.Password);
            KeyBox.Clear(); // don't keep the plaintext in the control longer than needed
        };
        Unloaded += (_, _) => { if (_keyTimer.IsEnabled) { _keyTimer.Stop(); (DataContext as SettingsViewModel)?.SetApiKey(KeyBox.Password); } };
    }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _keyTimer.Stop();
        if (KeyBox.Password.Length > 0) _keyTimer.Start();
    }
}
