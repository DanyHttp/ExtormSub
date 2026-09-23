using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ExtormSub.App.Overlay;

/// <summary>
/// Subtitle text drawn from geometry so it can have an outline. Handles RTL, wrapping, line height and
/// a line limit. Two overflow strategies: <see cref="KeepTail"/> drops leading words (live English that
/// keeps growing), otherwise the font shrinks up to 20 % before the end is trimmed (translations).
/// The element itself stays LeftToRight; <see cref="TextDirection"/> only affects shaping, because WPF
/// mirrors custom rendering in RTL elements.
/// </summary>
public sealed class OutlinedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = Register(nameof(Text), "", FrameworkPropertyMetadataOptions.AffectsMeasure);
    public static readonly DependencyProperty FontFamilyProperty = Register(nameof(FontFamily), new FontFamily("Segoe UI"), FrameworkPropertyMetadataOptions.AffectsMeasure);
    public static readonly DependencyProperty FontSizeProperty = Register(nameof(FontSize), 24.0, FrameworkPropertyMetadataOptions.AffectsMeasure);
    public static readonly DependencyProperty FontWeightProperty = Register(nameof(FontWeight), FontWeights.SemiBold, FrameworkPropertyMetadataOptions.AffectsMeasure);
    public static readonly DependencyProperty FillProperty = Register<Brush>(nameof(Fill), Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty StrokeProperty = Register<Brush>(nameof(Stroke), Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty StrokeThicknessProperty = Register(nameof(StrokeThickness), 0.0, FrameworkPropertyMetadataOptions.AffectsMeasure);
    public static readonly DependencyProperty TextDirectionProperty = Register(nameof(TextDirection), FlowDirection.LeftToRight, FrameworkPropertyMetadataOptions.AffectsMeasure);
    public static readonly DependencyProperty MaxLinesProperty = Register(nameof(MaxLines), 2, FrameworkPropertyMetadataOptions.AffectsMeasure);
    public static readonly DependencyProperty LineSpacingProperty = Register(nameof(LineSpacing), 1.2, FrameworkPropertyMetadataOptions.AffectsMeasure);
    public static readonly DependencyProperty KeepTailProperty = Register(nameof(KeepTail), false, FrameworkPropertyMetadataOptions.AffectsMeasure);

    private FormattedText? _formatted;
    private Geometry? _geometry;

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public FlowDirection TextDirection { get => (FlowDirection)GetValue(TextDirectionProperty); set => SetValue(TextDirectionProperty, value); }
    public int MaxLines { get => (int)GetValue(MaxLinesProperty); set => SetValue(MaxLinesProperty, value); }
    public double LineSpacing { get => (double)GetValue(LineSpacingProperty); set => SetValue(LineSpacingProperty, value); }
    public bool KeepTail { get => (bool)GetValue(KeepTailProperty); set => SetValue(KeepTailProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        _formatted = null;
        _geometry = null;
        var text = Text ?? "";
        if (text.Length == 0) return new Size(0, 0);

        double pad = StrokeThickness;
        double maxWidth = double.IsInfinity(available.Width) ? 1200 : Math.Max(40, available.Width - 2 * pad);
        int maxLines = Math.Max(1, MaxLines);

        FormattedText ft;
        if (KeepTail)
        {
            ft = Build(text, FontSize, maxWidth, null);
            if (Overflows(ft, FontSize, maxLines)) ft = Build(TailThatFits(text, maxWidth, maxLines), FontSize, maxWidth, null);
        }
        else
        {
            ft = Build(text, FontSize, maxWidth, null);
            foreach (var scale in new[] { 0.9, 0.8 })
            {
                if (!Overflows(ft, ft.Height > 0 ? ft.LineHeight : FontSize, maxLines)) break;
                ft = Build(text, FontSize * scale, maxWidth, null);
            }
            if (Overflows(ft, ft.LineHeight, maxLines)) ft = Build(text, ft.LineHeight / LineSpacing, maxWidth, maxLines);
        }

        // Shrink-wrap to the widest line so the background box hugs the text. Same greedy wrap at this width.
        double width = Math.Min(maxWidth, Math.Ceiling(ft.Width) + 1);
        ft.MaxTextWidth = width;
        _formatted = ft;
        return new Size(width + 2 * pad, ft.Height + 2 * pad);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_formatted is null) return;
        _geometry ??= _formatted.BuildGeometry(new Point(StrokeThickness, StrokeThickness));
        if (StrokeThickness > 0)
        {
            // Centered stroke twice as wide, then fill on top: an outline of StrokeThickness outside the glyphs.
            var pen = new Pen(Stroke, StrokeThickness * 2) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, _geometry);
        }
        dc.DrawGeometry(Fill, null, _geometry);
    }

    private FormattedText Build(string text, double size, double maxWidth, int? maxLines)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, TextDirection,
            new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), size, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            TextAlignment = TextAlignment.Center,
            LineHeight = size * LineSpacing,
        };
        if (maxLines is { } n)
        {
            ft.MaxLineCount = n;
            ft.Trimming = TextTrimming.WordEllipsis;
        }
        return ft;
    }

    private static bool Overflows(FormattedText ft, double lineHeight, int maxLines) => ft.Height > lineHeight * maxLines + 0.5;

    /// <summary>Largest suffix (whole words) that fits, found by binary search over the start word.</summary>
    private string TailThatFits(string text, double maxWidth, int maxLines)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int lo = 1, hi = words.Length - 1, best = words.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var candidate = "… " + string.Join(' ', words[mid..]);
            if (Overflows(Build(candidate, FontSize, maxWidth, null), FontSize * LineSpacing, maxLines)) lo = mid + 1;
            else { best = mid; hi = mid - 1; }
        }
        return "… " + string.Join(' ', words[best..]);
    }

    private static DependencyProperty Register<T>(string name, T defaultValue, FrameworkPropertyMetadataOptions options) =>
        DependencyProperty.Register(name, typeof(T), typeof(OutlinedText), new FrameworkPropertyMetadata(defaultValue, options));
}
