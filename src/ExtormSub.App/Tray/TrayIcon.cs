using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ExtormSub.App.Infrastructure;
using ExtormSub.App.Services;

namespace ExtormSub.App.Tray;

/// <summary>Tray presence: state-coloured icon, status tooltip, dark context menu, balloon notices.</summary>
public sealed class TrayIcon : IDisposable
{
    private static readonly Color MenuBack = Color.FromArgb(28, 28, 31);
    private static readonly Color MenuHover = Color.FromArgb(44, 44, 49);
    private static readonly Color MenuBorder = Color.FromArgb(52, 52, 58);
    private static readonly Color MenuText = Color.FromArgb(236, 236, 240);

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _listen, _lock, _visible;
    private IntPtr _hicon;
    private string? _noticePage;
    private ListeningState _drawnState = (ListeningState)(-1);

    public TrayIcon()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false },
            ShowImageMargin = false,
            BackColor = MenuBack,
            ForeColor = MenuText,
            Font = new Font("Segoe UI", 9.5f),
            Padding = new Padding(2, 4, 2, 4),
        };
        _listen = Item("Start Listening", () => ToggleListening?.Invoke());
        _listen.Font = new Font(menu.Font, FontStyle.Bold);
        _lock = Item("Unlock Overlay", () => ToggleLock?.Invoke());
        _visible = Item("Hide Overlay", () => ToggleOverlay?.Invoke());
        menu.Items.AddRange(
        [
            _listen,
            new ToolStripSeparator(),
            Item("Settings", () => ShowSettings?.Invoke(null)),
            Item("Transcript History", () => ShowHistory?.Invoke()),
            new ToolStripSeparator(),
            _lock,
            _visible,
            new ToolStripSeparator(),
            Item("Exit", () => ExitRequested?.Invoke()),
        ]);

        _icon = new NotifyIcon { ContextMenuStrip = menu, Text = "ExtormSub", Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings?.Invoke(null); };
        _icon.BalloonTipClicked += (_, _) => ShowSettings?.Invoke(_noticePage);
        Update(ListeningState.Idle, "Idle", overlayLocked: true, overlayVisible: true);
    }

    public event Action? ToggleListening;
    /// <summary>Argument: the settings page to open, or null for the last one.</summary>
    public event Action<string?>? ShowSettings;
    public event Action? ShowHistory;
    public event Action? ToggleLock;
    public event Action? ToggleOverlay;
    public event Action? ExitRequested;

    public void Update(ListeningState state, string status, bool overlayLocked, bool overlayVisible)
    {
        _listen.Text = state is ListeningState.Listening or ListeningState.Loading ? "Stop Listening" : "Start Listening";
        _lock.Text = overlayLocked ? "Unlock Overlay" : "Lock Overlay";
        _visible.Text = overlayVisible ? "Hide Overlay" : "Show Overlay";
        var tip = $"ExtormSub — {status}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
        if (state != _drawnState) SetIcon(state);
    }

    public void Notify(Notice notice)
    {
        _noticePage = notice.Page;
        _icon.BalloonTipTitle = notice.Title;
        _icon.BalloonTipText = notice.Message;
        _icon.BalloonTipIcon = notice.Level switch
        {
            NoticeLevel.Error => ToolTipIcon.Error,
            NoticeLevel.Warning => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(5000);
    }

    private void SetIcon(ListeningState state)
    {
        _drawnState = state;
        var accent = state switch
        {
            ListeningState.Listening => Color.FromArgb(76, 141, 246),
            ListeningState.Loading => Color.FromArgb(160, 190, 240),
            ListeningState.Error => Color.FromArgb(245, 158, 11),
            _ => Color.FromArgb(113, 113, 122),
        };
        int size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // The app logo (the .ico picks the frame closest to the tray size), plus a small state dot in the corner.
            using (var logo = LoadLogo(size)) g.DrawIcon(logo, new Rectangle(0, 0, size, size));
            float d = size * 0.42f, x = size - d - 0.5f, y = size - d - 0.5f;
            using (var ring = new SolidBrush(Color.FromArgb(24, 24, 27))) g.FillEllipse(ring, x - 1, y - 1, d + 2, d + 2);
            using (var dot = new SolidBrush(accent)) g.FillEllipse(dot, x, y, d, d);
        }
        var old = _hicon;
        _hicon = bmp.GetHicon();
        _icon.Icon = Icon.FromHandle(_hicon);
        if (old != IntPtr.Zero) Native.DestroyIcon(old);
    }

    private static Icon LoadLogo(int size)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ExtormSub.ico")).Stream;
        return new Icon(stream, new Size(size, size));
    }

    private static ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { ForeColor = MenuText, Padding = new Padding(4, 3, 4, 3) };
        item.Click += (_, _) => onClick();
        return item;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hicon != IntPtr.Zero) Native.DestroyIcon(_hicon);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => MenuBack;
        public override Color MenuBorder => TrayIcon.MenuBorder;
        public override Color MenuItemBorder => MenuHover;
        public override Color MenuItemSelected => MenuHover;
        public override Color MenuItemSelectedGradientBegin => MenuHover;
        public override Color MenuItemSelectedGradientEnd => MenuHover;
        public override Color MenuItemPressedGradientBegin => MenuHover;
        public override Color MenuItemPressedGradientEnd => MenuHover;
        public override Color ImageMarginGradientBegin => MenuBack;
        public override Color ImageMarginGradientMiddle => MenuBack;
        public override Color ImageMarginGradientEnd => MenuBack;
        public override Color SeparatorDark => TrayIcon.MenuBorder;
        public override Color SeparatorLight => TrayIcon.MenuBorder;
    }
}
