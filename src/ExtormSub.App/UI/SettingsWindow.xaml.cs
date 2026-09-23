using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using ExtormSub.App.Infrastructure;
using ExtormSub.App.UI.Pages;

namespace ExtormSub.App.UI;

/// <summary>
/// Navigation shell. Edits auto-save: any input change restarts a short debounce, then the working
/// copy is written. Page content lives in UI/Pages; logic lives in <see cref="SettingsViewModel"/>.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly Dictionary<string, (string Subtitle, Func<FrameworkElement> Create)> PageFactory = new()
    {
        ["General"] = ("Startup, tray and overlay behaviour.", () => new GeneralPage()),
        ["Audio"] = ("What ExtormSub listens to: everything your PC plays, or a microphone.", () => new AudioPage()),
        ["Speech Recognition"] = ("Runs locally on this PC. Models are downloaded once and kept loaded.", () => new SpeechPage()),
        ["Translation"] = ("Stable English segments are translated through an OpenAI-compatible API.", () => new TranslationPage()),
        ["Subtitle Appearance"] = ("How the overlay looks on top of your videos and games.", () => new AppearancePage()),
        ["Hotkeys"] = ("System-wide shortcuts. Click a box and press the new combination.", () => new HotkeysPage()),
        ["History"] = ("Every session is saved locally so you can search and export it later.", () => new HistoryPage()),
        ["Diagnostics"] = ("Live measurements of each pipeline stage.", () => new DiagnosticsPage()),
        ["Advanced"] = ("Voice detection and pipeline tuning. Defaults suit most content.", () => new AdvancedPage()),
        ["About"] = ("", () => new AboutPage()),
    };

    private readonly SettingsViewModel _vm;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _loading = true;

    public SettingsWindow(SettingsViewModel vm, string? page = null)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _vm.SaveNow(); };

        AddHandler(TextBoxBase.TextChangedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(Selector.SelectionChangedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedEventHandler(OnEdited));

        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => _loading = false;
        Navigate(page ?? "General");
    }

    public void Navigate(string page)
    {
        Nav.SelectedItem = PageFactory.ContainsKey(page) ? page : "General";
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true; // navigation is not an edit
        if (Nav.SelectedItem is not string name) return;
        if (!_pages.TryGetValue(name, out var view)) _pages[name] = view = PageFactory[name].Create();
        PageTitle.Text = name;
        PageSubtitle.Text = PageFactory[name].Subtitle;
        PageSubtitle.Visibility = PageSubtitle.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PageHost.Content = view;
        Scroller.ScrollToTop();
    }

    private void OnEdited(object sender, RoutedEventArgs e)
    {
        if (_loading || e.OriginalSource is ListBox { Name: "Nav" } || e.OriginalSource is PasswordBox) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            _vm.SaveNow();
        }
        _vm.Dispose();
        base.OnClosed(e);
    }
}
