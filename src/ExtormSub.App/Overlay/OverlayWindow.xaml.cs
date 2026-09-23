using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ExtormSub.App.Infrastructure;

namespace ExtormSub.App.Overlay;

/// <summary>
/// The subtitle layer. Always a tool window (no taskbar/Alt+Tab) that never activates.
/// Locked = click-through (WS_EX_TRANSPARENT). Behaviour lives in <see cref="OverlayController"/>.
/// </summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
            Native.SetExStyle(Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED, Native.WS_EX_APPWINDOW);
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    public event Action? LockRequested;

    public void SetClickThrough(bool locked)
    {
        Native.SetExStyle(Handle, locked ? Native.WS_EX_TRANSPARENT : 0, locked ? 0 : Native.WS_EX_TRANSPARENT);
        Chrome.ResizeBorderThickness = new Thickness(locked ? 0 : 8);
        EditFrame.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Re-asserts z-order above other topmost windows without activating.</summary>
    public void BringToTop() =>
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    private void EditFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void LockButton_Click(object sender, RoutedEventArgs e) => LockRequested?.Invoke();
}
