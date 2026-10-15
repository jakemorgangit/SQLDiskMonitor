using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SQLDiskMonitor.Services;

namespace SQLDiskMonitor.Controls;

public static class Palette
{
    /// <summary>When set (e.g. while rendering a printable report), colours come from this palette instead of the current theme.</summary>
    public static ResourceDictionary? Override { get; set; }

    public static ResourceDictionary LightPalette() =>
        new() { Source = new Uri("pack://application:,,,/SQLDiskMonitor.Core;component/Themes/Light.xaml") };

    public static Brush Brush(string key) => Find("B." + key) as Brush ?? Brushes.Gray;
    public static Color Color(string key) => Find("C." + key) is Color c ? c : Colors.Gray;

    // Report override first, then the hosted palette (when another app hosts us), then the standalone app's resources.
    private static object? Find(string key) =>
        Override?.Contains(key) == true ? Override[key]
        : Hosting.HostTheme.Palette?.Contains(key) == true ? Hosting.HostTheme.Palette[key]
        : Application.Current?.TryFindResource(key);

    public static Brush ForPressure(double p) => Brush(BandKey(p));
    public static Color ColorForPressure(double p) => Color(BandKey(p));

    public static string BandKey(double p) => p switch
    {
        >= Pressure.Critical => "Crit",
        >= Pressure.High => "High",
        >= Pressure.Elevated => "Warn",
        _ => "Good"
    };

    public static readonly string[] SeriesHex =
        ["#4C8DFF", "#2DB37A", "#E0A526", "#D9598C", "#9B6BF2", "#25B5C4", "#EE7D3B", "#8A96A3", "#6BC46D", "#C77DFF"];

    public static Color Series(int i) => (Color)ColorConverter.ConvertFromString(SeriesHex[i % SeriesHex.Length]);
}

/// <summary>Redraws itself when the theme changes, since it renders with palette brushes.</summary>
public abstract class ThemedElement : FrameworkElement
{
    protected ThemedElement()
    {
        Loaded += (_, _) => ThemeService.Changed += InvalidateVisual;
        Unloaded += (_, _) => ThemeService.Changed -= InvalidateVisual;
    }
}

/// <summary>270° arc gauge showing a 0–100 pressure score, coloured by band.</summary>
public class PressureGauge : ThemedElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(PressureGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(PressureGauge),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(PressureGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = size / 2 - Thickness / 2 - 1;

        var track = new Pen(Palette.Brush("Track"), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(center, r, 135, 270));

        double v = Math.Clamp(Value, 0, 100);
        if (v > 0.5)
        {
            var pen = new Pen(Palette.ForPressure(v), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Arc(center, r, 135, 270 * v / 100));
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText(v.ToString("0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            face, size * 0.26, Palette.Brush("Text"), dpi);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2 - size * 0.04));

        var cap = string.IsNullOrEmpty(Caption) ? Pressure.Band(v) : Caption;
        var capText = new FormattedText(cap, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), Math.Max(9, size * 0.095), Palette.ForPressure(v), dpi);
        dc.DrawText(capText, new Point(center.X - capText.Width / 2, center.Y + size * 0.15));
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        sweepDeg = Math.Min(sweepDeg, 359.9);
        Point P(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(startDeg), false, false);
            ctx.ArcTo(P(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Tiny line chart for recent values.</summary>
public class Sparkline : ThemedElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    /// <summary>Fixed top of scale; 0 = auto.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var v = Values;
        if (v == null || v.Count < 2 || ActualWidth <= 0) return;
        double max = Maximum > 0 ? Maximum : Math.Max(1e-6, v.Max() * 1.15);
        double w = ActualWidth, h = ActualHeight - 2;
        var stroke = Stroke ?? Palette.Brush("Accent");

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        Point Pt(int i) => new(i * w / (v.Count - 1), 1 + h - Math.Min(1, v[i] / max) * h);
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(Pt(0), false, false);
            for (int i = 1; i < v.Count; i++) ctx.LineTo(Pt(i), true, true);
        }
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, h + 1), true, true);
            for (int i = 0; i < v.Count; i++) ctx.LineTo(Pt(i), false, true);
            ctx.LineTo(new Point(w, h + 1), false, false);
        }
        var fill = stroke.Clone();
        fill.Opacity = 0.15;
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(stroke, 1.5) { LineJoin = PenLineJoin.Round }, line);
    }
}

/// <summary>Horizontal utilisation bar. Shows a tick at the baseline when a burst limit extends the scale.</summary>
public class MeterBar : ThemedElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BurstProperty = DependencyProperty.Register(nameof(Burst), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(MeterBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Burst { get => (double)GetValue(BurstProperty); set => SetValue(BurstProperty, value); }
    /// <summary>Explicit fill; when null the bar is coloured by utilisation.</summary>
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public MeterBar() { Height = 6; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        double rad = h / 2;
        dc.DrawRoundedRectangle(Palette.Brush("Track"), null, new Rect(0, 0, w, h), rad, rad);
        if (Maximum <= 0)
        {
            return;
        }
        double scale = Math.Max(Maximum, Burst);
        double frac = Math.Clamp(Value / scale, 0, 1);
        double util = Value / Maximum * 100;
        var fill = Fill ?? Palette.ForPressure(util);
        if (frac > 0) dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, Math.Max(h, w * frac), h), rad, rad);
        if (Burst > Maximum)
        {
            double x = w * Maximum / scale;
            dc.DrawRectangle(Palette.Brush("TextMuted"), null, new Rect(x - 0.75, -2, 1.5, h + 4));
        }
    }
}
