using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ExtormSub.App.Hotkeys;

namespace ExtormSub.App.UI;

/// <summary>Titled settings row; see the SettingRow style in Theme.xaml.</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow));

    static SettingRow() => DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingRow), new FrameworkPropertyMetadata(typeof(SettingRow)));

    public string? Header { get => (string?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}

/// <summary>Slider with an inline value readout (see Theme.xaml). <see cref="Format"/> is a composite format string.</summary>
public sealed class ValueSlider : System.Windows.Controls.Slider
{
    public static readonly DependencyProperty FormatProperty =
        DependencyProperty.Register(nameof(Format), typeof(string), typeof(ValueSlider), new PropertyMetadata("{0:0}"));

    static ValueSlider() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ValueSlider), new FrameworkPropertyMetadata(typeof(ValueSlider)));

    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
}

/// <summary>Hex colour input with a live swatch.</summary>
public sealed class ColorBox : TextBox
{
    static ColorBox() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ColorBox), new FrameworkPropertyMetadata(typeof(ColorBox)));
}

/// <summary>Formats a value with the format string passed as the second binding.</summary>
public sealed class MultiFormatConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && values[1] is string f ? string.Format(CultureInfo.InvariantCulture, f, values[0]) : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>Captures a key combination: press the shortcut, Backspace/Delete clears it.</summary>
public sealed class HotkeyBox : TextBox
{
    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Width = 190;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        Cursor = Cursors.Hand;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete) { Text = ""; return; }
        if (key is Key.Escape or Key.Tab) { MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin) return;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None) return; // bare keys would hijack typing
        Text = new HotkeyGesture(mods, key).ToString();
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute) : this(_ => execute()) { }
    public RelayCommand(Action execute, Func<object?, bool> canExecute) : this(_ => execute(), canExecute) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>Shows a value with a format, e.g. slider readouts: ConverterParameter="{}{0:0} px".</summary>
public sealed class FormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Format(CultureInfo.InvariantCulture, parameter as string ?? "{0}", value);

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Enum value → readable label ("OriginalAndTranslation" → "Original and translation").</summary>
public sealed class Humanize : IValueConverter
{
    private static readonly Dictionary<string, string> Overrides = new()
    {
        ["Gpu"] = "GPU (Vulkan)", ["Cpu"] = "CPU", ["Auto"] = "Automatic",
        ["OriginalAndTranslation"] = "English + translation", ["TranslationOnly"] = "Translation only", ["OriginalOnly"] = "English only",
        ["Fast"] = "Fast", ["Balanced"] = "Balanced", ["Accurate"] = "Accurate", ["Custom"] = "Custom model",
        ["SemiBold"] = "Semibold",
    };

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value?.ToString() ?? "";
        if (Overrides.TryGetValue(name, out var label)) return label;
        var words = System.Text.RegularExpressions.Regex.Split(name, "(?<=[a-z])(?=[A-Z])");
        return string.Join(" ", words.Select((w, i) => i == 0 ? w : w.ToLowerInvariant()));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Hex colour string → brush, for swatches next to colour inputs.</summary>
public sealed class HexToBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        try { return new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value as string ?? "")); }
        catch (Exception ex) when (ex is FormatException or NotSupportedException) { return System.Windows.Media.Brushes.Transparent; }
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
