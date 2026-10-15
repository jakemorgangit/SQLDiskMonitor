using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using SQLDiskMonitor.Models;
using WColor = System.Windows.Media.Color;

namespace SQLDiskMonitor.Controls;

/// <summary>Builds OxyPlot models styled with the current theme palette.</summary>
public static class Charts
{
    public static OxyColor Oxy(WColor c, byte? alpha = null) => OxyColor.FromArgb(alpha ?? c.A, c.R, c.G, c.B);
    public static OxyColor Theme(string key, byte? alpha = null) => Oxy(Palette.Color(key), alpha);
    public static OxyColor Series(int i, byte? alpha = null) => Oxy(Palette.Series(i), alpha);

    public static PlotModel TimeChart(string title, string unit, double? fixedMax = null, string format = "#,0.##")
    {
        var m = new PlotModel
        {
            Title = title,
            TitleFontSize = 13,
            TitleFontWeight = FontWeights.Normal,
            TitleHorizontalAlignment = TitleHorizontalAlignment.CenteredWithinView,
            TitleColor = Theme("Text"),
            TextColor = Theme("TextMuted"),
            PlotAreaBorderColor = OxyColors.Transparent,
            Background = OxyColors.Transparent,
            DefaultFont = "Segoe UI",
            DefaultFontSize = 11,
            Padding = new OxyThickness(4, 6, 12, 4),
        };
        m.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "HH:mm",
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = Theme("Grid"),
            AxislineColor = Theme("Border"),
            TicklineColor = Theme("Border"),
            TextColor = Theme("TextSubtle"),
            IntervalLength = 70,
        });
        m.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Minimum = 0,
            Maximum = fixedMax ?? double.NaN,
            MinimumPadding = 0,
            MaximumPadding = 0.08,
            Title = unit,
            TitleFontSize = 10.5,
            StringFormat = format,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = Theme("Grid"),
            AxislineColor = OxyColors.Transparent,
            TicklineColor = OxyColors.Transparent,
            TextColor = Theme("TextSubtle"),
            TitleColor = Theme("TextSubtle"),
            IntervalLength = 40,
        });
        m.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.TopLeft,
            LegendPlacement = LegendPlacement.Inside,
            LegendOrientation = LegendOrientation.Horizontal,
            LegendBackground = Theme("Surface", 200),
            LegendTextColor = Theme("TextMuted"),
            LegendFontSize = 10.5,
            LegendItemSpacing = 10,
            LegendPadding = 4,
            LegendSymbolLength = 12,
        });
        return m;
    }

    /// <summary>Adapts the X-axis label format to the span being shown.</summary>
    public static void SetSpan(PlotModel m, TimeSpan span)
    {
        if (m.Axes.FirstOrDefault(a => a is DateTimeAxis) is DateTimeAxis ax)
            ax.StringFormat = span.TotalHours > 30 ? "ddd dd HH:mm" : "HH:mm";
    }

    public static LineSeries Line(string title, int colorIndex, IEnumerable<(DateTime T, double V)> points, string unit, bool fill = false, double thickness = 1.6)
    {
        var s = new LineSeries
        {
            Title = title,
            Color = Series(colorIndex),
            StrokeThickness = thickness,
            LineJoin = LineJoin.Round,
            TrackerFormatString = "{0}\n{2:ddd HH:mm:ss}\n{4:#,0.##} " + unit,
            CanTrackerInterpolatePoints = false,
        };
        foreach (var (t, v) in points) s.Points.Add(new DataPoint(DateTimeAxis.ToDouble(t), v));
        return s;
    }

    public static AreaSeries Area(string title, OxyColor color, IEnumerable<(DateTime T, double V)> points, string unit)
    {
        var s = new AreaSeries
        {
            Title = title,
            Color = color,
            Fill = OxyColor.FromAColor(45, color),
            StrokeThickness = 1.4,
            TrackerFormatString = "{0}\n{2:ddd HH:mm:ss}\n{4:#,0.##} " + unit,
        };
        foreach (var (t, v) in points)
        {
            var x = DateTimeAxis.ToDouble(t);
            s.Points.Add(new DataPoint(x, v));
            s.Points2.Add(new DataPoint(x, 0));
        }
        return s;
    }

    public static LineAnnotation Limit(double y, string text, OxyColor color, LineStyle style = LineStyle.Dash) => new()
    {
        Type = LineAnnotationType.Horizontal,
        Y = y,
        Color = color,
        LineStyle = style,
        StrokeThickness = 1.2,
        Text = text,
        TextColor = color,
        FontSize = 10,
        TextHorizontalAlignment = HorizontalAlignment.Right,
        TextVerticalAlignment = VerticalAlignment.Bottom,
    };

    public static void AddNotes(PlotModel m, IEnumerable<DiskNote> notes, DateTime from, DateTime to, bool labels = true)
    {
        foreach (var n in notes.Where(n => n.Timestamp >= from && n.Timestamp <= to))
        {
            m.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                X = DateTimeAxis.ToDouble(n.Timestamp),
                Color = Theme("Accent", 160),
                LineStyle = LineStyle.Dot,
                StrokeThickness = 1.2,
                Text = labels ? $"{n.Volume} {Trim(n.Text, 40)}" : null,
                ToolTip = $"{n.Timestamp:g}  {n.Volume}  {n.Text}",
                TextColor = Theme("Accent"),
                FontSize = 10,
                TextOrientation = AnnotationTextOrientation.Vertical,
                TextHorizontalAlignment = HorizontalAlignment.Right,
            });
        }
    }

    public static void AddPressureBands(PlotModel m)
    {
        void Band(double from, double to, string key) => m.Annotations.Add(new RectangleAnnotation
        {
            MinimumY = from, MaximumY = to, Fill = Theme(key, 18), Layer = AnnotationLayer.BelowSeries
        });
        Band(Services.Pressure.Elevated, Services.Pressure.High, "Warn");
        Band(Services.Pressure.High, Services.Pressure.Critical, "High");
        Band(Services.Pressure.Critical, 100, "Crit");
    }

    private static string Trim(string s, int n)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= n ? s : s[..n] + "…";
    }
}
