using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ExtormSub.App.Infrastructure;
using ExtormSub.Core.Pipeline;
using ExtormSub.Core.Settings;
using Microsoft.Extensions.Logging;
using Screen = System.Windows.Forms.Screen;

namespace ExtormSub.App.Overlay;

/// <summary>
/// Owns the overlay window: applies appearance, positions it on the chosen monitor, switches between
/// locked (click-through) and edit mode, and renders pipeline events. Pipeline events arrive on worker
/// threads and are marshalled with Dispatcher.BeginInvoke; everything else runs on the UI thread.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private const string SampleOriginal = "This is how your subtitles will look.";
    private const string SampleTranslation = "زیرنویس‌های شما این‌گونه نمایش داده می‌شوند.";

    private readonly SettingsStore _store;
    private readonly ILogger _log;
    private readonly Dispatcher _ui;
    private readonly OverlayWindow _window;
    private readonly DispatcherTimer _hideTimer;
    private OverlaySettings _s;
    private bool _showOriginalWhenUnavailable;
    private bool _pauseWhenSilent;
    private bool _partials;
    private string _original = "";
    private string _translation = "";
    private long _originalSeq;
    private Native.RECT _appliedBounds;

    public OverlayController(SettingsStore store, ILogger<OverlayController> log)
    {
        _store = store;
        _log = log;
        _ui = Dispatcher.CurrentDispatcher;
        _window = new OverlayWindow();
        _window.LockRequested += () => SetLocked(true);
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); FadeOut(); };

        var s = store.Current;
        _s = s.Overlay;
        ApplyBehaviour(s);
        _window.Handle.ToString(); // create the HWND so styles and position apply before first paint
        ApplyAppearance();
        ApplyBounds();
        _window.SetClickThrough(true);

        store.Changed += (_, updated) => _ui.BeginInvoke(() => OnSettingsChanged(updated));
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public bool IsLocked { get; private set; } = true;
    public bool IsVisible => _window.IsVisible;

    public event Action? StateChanged;

    public void Attach(SubtitlePipeline pipeline)
    {
        pipeline.TranscriptUpdated += u => _ui.BeginInvoke(() => OnTranscript(u));
        pipeline.SubtitleReady += s => _ui.BeginInvoke(() => OnSubtitle(s));
        pipeline.SessionEnded += _ => _ui.BeginInvoke(Clear);
    }

    public void ShowInitial()
    {
        if (_s.Visible) _window.Show();
    }

    // ───────────────────────────── modes ─────────────────────────────

    public void ToggleLock() => SetLocked(!IsLocked);

    public void SetLocked(bool locked)
    {
        if (!locked && !_window.IsVisible) SetVisible(true);
        IsLocked = locked;
        _window.SetClickThrough(locked);
        if (locked)
        {
            SaveBoundsIfMoved();
            Render();
        }
        else
        {
            RenderSampleIfEmpty();
        }
        _window.BringToTop();
        StateChanged?.Invoke();
    }

    public void ToggleVisible() => SetVisible(!_window.IsVisible);

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            _window.Show();
            ApplyBounds();
            _window.BringToTop();
        }
        else
        {
            if (!IsLocked) SetLocked(true);
            _window.Hide();
        }
        if (_s.Visible != visible) _store.Update(s => s.Overlay.Visible = visible);
        StateChanged?.Invoke();
    }

    /// <summary>Always recovers control: visible, interactive, on-screen, on top.</summary>
    public void EmergencyRestore()
    {
        _window.Show();
        Native.GetWindowRect(_window.Handle, out var r);
        bool onScreen = Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(
            new System.Drawing.Rectangle(r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top))));
        if (!onScreen)
        {
            _store.Update(s => { s.Overlay.Position = OverlayPosition.BottomCenter; s.Overlay.Monitor = null; });
            _s = _store.Current.Overlay;
            ApplyBounds();
        }
        SetLocked(false);
        _log.LogInformation("Overlay restored by emergency hotkey");
    }

    // ───────────────────────────── rendering ─────────────────────────────

    private void OnTranscript(TranscriptUpdate u)
    {
        if (u.Seq < _originalSeq) return;
        if (!u.IsFinal && !_partials) return;
        if (u.IsFinal && u.Text.Length == 0)
        {
            // Utterance turned out to be noise: remove its partial.
            if (u.Seq == _originalSeq) _original = "";
        }
        else
        {
            _original = u.Text;
        }
        _originalSeq = u.Seq;
        Render();
        Touch();
    }

    private void OnSubtitle(SubtitleUpdate s)
    {
        if (s.IsStale) return; // recognised while catching up on a backlog: history only
        if (s.Translation is not null)
        {
            _translation = s.Translation;
        }
        else if (s.IsFinal)
        {
            // Translation disabled, failed or abandoned.
            _translation = _showOriginalWhenUnavailable && _s.DisplayMode == DisplayMode.TranslationOnly ? s.Original : "";
            if (_s.DisplayMode == DisplayMode.OriginalOnly || _s.DisplayMode == DisplayMode.OriginalAndTranslation)
            {
                if (s.Seq >= _originalSeq) { _original = s.Original; _originalSeq = s.Seq; }
            }
        }
        _window.StatusDot.Visibility = s.TranslationFailed && !_showOriginalWhenUnavailable ? Visibility.Visible : Visibility.Collapsed;
        Render();
        Touch();
    }

    private void Render()
    {
        if (!IsLocked && _original.Length == 0 && _translation.Length == 0)
        {
            RenderSampleIfEmpty();
            return;
        }
        SetTexts(_original, _translation);
    }

    private void RenderSampleIfEmpty()
    {
        if (_original.Length == 0 && _translation.Length == 0) SetTexts(SampleOriginal, SampleTranslation);
    }

    private void SetTexts(string original, string translation)
    {
        bool showOriginal = _s.DisplayMode != DisplayMode.TranslationOnly;
        bool showTranslation = _s.DisplayMode != DisplayMode.OriginalOnly;
        _window.OriginalLine.Text = showOriginal ? original : "";
        _window.TranslationLine.Text = showTranslation ? translation : "";
        _window.TranslationLine.TextDirection = IsRtl(translation) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _window.OriginalLine.Visibility = showOriginal && original.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _window.TranslationLine.Visibility = showTranslation && translation.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool any = _window.OriginalLine.Visibility == Visibility.Visible || _window.TranslationLine.Visibility == Visibility.Visible;
        _window.Block.BeginAnimation(UIElement.OpacityProperty, null);
        _window.Block.Opacity = 1;
        _window.Block.Visibility = any ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Restarts the "subtitle duration" countdown after new text.</summary>
    private void Touch()
    {
        _hideTimer.Stop();
        if (!_pauseWhenSilent) return;
        _hideTimer.Interval = TimeSpan.FromSeconds(_s.SubtitleDurationSeconds);
        _hideTimer.Start();
    }

    private void FadeOut()
    {
        if (!IsLocked) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Math.Max(1, _s.FadeDurationMs)));
        fade.Completed += (_, _) =>
        {
            if (_window.Block.Opacity > 0.01) return; // new text arrived during the fade
            _original = _translation = "";
            SetTexts("", "");
        };
        _window.Block.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void Clear()
    {
        _original = _translation = "";
        _originalSeq = 0;
        _window.StatusDot.Visibility = Visibility.Collapsed;
        Render();
    }

    private static bool IsRtl(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ') return true;
            if (char.IsLetter(ch)) return false;
        }
        return false;
    }

    // ───────────────────────────── appearance & position ─────────────────────────────

    private void OnSettingsChanged(AppSettings updated)
    {
        var old = _s;
        _s = updated.Overlay;
        ApplyBehaviour(updated);
        ApplyAppearance();
        bool layoutChanged = old.Position != _s.Position || old.Monitor != _s.Monitor || old.MaxWidth != _s.MaxWidth ||
                             old.BottomMargin != _s.BottomMargin || old.MaxLines != _s.MaxLines ||
                             old.TranslationFontSize != _s.TranslationFontSize || old.OriginalFontSize != _s.OriginalFontSize ||
                             old.LineSpacing != _s.LineSpacing || old.CustomBounds != _s.CustomBounds;
        if (layoutChanged && IsLocked) ApplyBounds();
        if (_s.Visible != _window.IsVisible) { if (_s.Visible) _window.Show(); else _window.Hide(); }
        Render();
    }

    private void ApplyBehaviour(AppSettings s)
    {
        _showOriginalWhenUnavailable = s.Translation.ShowOriginalWhenUnavailable;
        _pauseWhenSilent = s.General.PauseWhenSilent;
        _partials = s.Asr.ShowPartials;
    }

    private void ApplyAppearance()
    {
        var w = _window;
        var weight = _s.FontWeight switch
        {
            TextWeight.Normal => FontWeights.Normal,
            TextWeight.Medium => FontWeights.Medium,
            TextWeight.Bold => FontWeights.Bold,
            _ => FontWeights.SemiBold,
        };
        Configure(w.OriginalLine, _s.OriginalFont, _s.OriginalFontSize, _s.OriginalColor, weight == FontWeights.Bold ? FontWeights.SemiBold : weight);
        Configure(w.TranslationLine, _s.TranslationFont, _s.TranslationFontSize, _s.TranslationColor, weight);
        w.OriginalLine.MaxLines = Math.Min(2, _s.MaxLines);
        w.TranslationLine.MaxLines = _s.MaxLines;
        w.OriginalLine.Margin = new Thickness(0, 0, 0, _s.OriginalFontSize * 0.2);

        var bg = Parse(_s.BackgroundColor, Colors.Black);
        w.Block.Background = _s.BackgroundOpacity <= 0.001 ? Brushes.Transparent : Frozen(new SolidColorBrush(bg) { Opacity = _s.BackgroundOpacity });
        w.Block.MaxWidth = _s.MaxWidth;
        w.TextStack.Opacity = _s.TextOpacity;
        w.TextStack.Effect = _s.Shadow ? new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1.5, Opacity = 0.85, Color = Colors.Black, Direction = 270 } : null;

        bool top = _s.Position == OverlayPosition.TopCenter;
        w.Block.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        w.Block.HorizontalAlignment = _s.Position switch
        {
            OverlayPosition.BottomLeft => HorizontalAlignment.Left,
            OverlayPosition.BottomRight => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };

        void Configure(OutlinedText t, string font, double size, string color, FontWeight fw)
        {
            t.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(font) ? "Segoe UI" : font);
            t.FontSize = size;
            t.FontWeight = fw;
            t.LineSpacing = _s.LineSpacing;
            t.Fill = Frozen(new SolidColorBrush(Parse(color, Colors.White)));
            t.Stroke = Frozen(new SolidColorBrush(Parse(_s.OutlineColor, Colors.Black)));
            t.StrokeThickness = _s.Outline ? _s.OutlineThickness * size / 34.0 : 0;
        }
    }

    /// <summary>Places the overlay band in physical pixels on the chosen monitor (mixed-DPI safe).</summary>
    private void ApplyBounds()
    {
        var screen = (_s.Monitor is { } name ? Screen.AllScreens.FirstOrDefault(sc => sc.DeviceName == name) : null) ?? Screen.PrimaryScreen!;
        var wa = screen.WorkingArea;
        double scale = Native.ScaleAt(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);

        int x, y, width, height;
        if (_s.Position == OverlayPosition.Custom && _s.CustomBounds is { } c && IsOnAnyScreen(c))
        {
            (x, y, width, height) = (c.X, c.Y, c.Width, c.Height);
        }
        else
        {
            // English (≤2 lines) + translation (MaxLines) + block padding/margins + outline/shadow headroom.
            double dipHeight = _s.OriginalFontSize * _s.LineSpacing * 2 + _s.TranslationFontSize * _s.LineSpacing * _s.MaxLines + 90;
            width = (int)Math.Min((_s.MaxWidth + 60) * scale, wa.Width - 20);
            height = (int)Math.Min(dipHeight * scale, wa.Height / 2);
            int margin = (int)(_s.BottomMargin * scale);
            int side = (int)(24 * scale);
            x = _s.Position switch
            {
                OverlayPosition.BottomLeft => wa.Left + side,
                OverlayPosition.BottomRight => wa.Right - width - side,
                _ => wa.Left + (wa.Width - width) / 2,
            };
            y = _s.Position == OverlayPosition.TopCenter ? wa.Top + margin : wa.Bottom - margin - height;
        }

        // Move first (WPF handles the DPI change of the new monitor), then size in that monitor's pixels.
        var h = _window.Handle;
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, width, height, Native.SWP_NOACTIVATE);
        Native.GetWindowRect(h, out _appliedBounds);
    }

    private void SaveBoundsIfMoved()
    {
        Native.GetWindowRect(_window.Handle, out var r);
        if (r.Left == _appliedBounds.Left && r.Top == _appliedBounds.Top && r.Right == _appliedBounds.Right && r.Bottom == _appliedBounds.Bottom)
            return;
        _appliedBounds = r;
        var monitor = Screen.FromHandle(_window.Handle).DeviceName;
        _store.Update(s =>
        {
            if (!s.General.RememberOverlayPosition) return;
            s.Overlay.Position = OverlayPosition.Custom;
            s.Overlay.CustomBounds = new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            if (s.General.RememberMonitor) s.Overlay.Monitor = monitor;
        });
        _log.LogInformation("Overlay moved to {X},{Y} {W}x{H} on {Monitor}", r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, monitor);
    }

    private static bool IsOnAnyScreen(PixelRect c) =>
        Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new System.Drawing.Rectangle(c.X, c.Y, c.Width, c.Height)));

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _ui.BeginInvoke(() => { if (IsLocked) ApplyBounds(); });

    private static Color Parse(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (Exception ex) when (ex is FormatException or NotSupportedException) { return fallback; }
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _hideTimer.Stop();
        _window.Close();
    }
}
