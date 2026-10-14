using System.Windows;
using System.Windows.Media;
using SQLDiskMonitor.Services;

namespace SQLDiskMonitor.Hosting;

/// <summary>
/// When hosted, SQL Disk Monitor's palette keys (B.* brushes, C.* colours) are mapped onto the host's palette and merged,
/// with the app's styles and shared resources, into each SQL Disk Monitor element's own resources — never into
/// Application.Resources. Every view and dialog in this library calls <see cref="ApplyTo"/> before InitializeComponent, so
/// StaticResource lookups resolve even for views created by data templates before they join the hosted tree.
/// The standalone app doesn't use this: its App.xaml merges the palette, styles and shared resources itself.
/// </summary>
public static class HostTheme
{
    /// <summary>Palette names used by the app (the part after "B." / "C.").</summary>
    public static readonly string[] Keys =
    [
        "Accent", "AccentSoft", "Bg", "Border", "Crit", "Good", "Grid", "High", "Info", "Nav", "OnAccent",
        "Surface", "Surface2", "SurfaceHover", "Text", "TextMuted", "TextSubtle", "Track", "Warn"
    ];

    private static Func<string, bool, Color?>? _lookup;
    private static string _signature = "";

    /// <summary>The dictionary merged into hosted elements, or null when running standalone.</summary>
    public static ResourceDictionary? Resources { get; private set; }

    /// <summary>The current mapped palette (B.* and C.* keys) while hosted.</summary>
    public static ResourceDictionary? Palette { get; private set; }

    /// <summary>
    /// Sets up hosted resources (first call) and applies the host's current palette. <paramref name="lookup"/> maps a palette
    /// name (see <see cref="Keys"/>) and the dark flag to the host's current colour; null falls back to SQL Disk Monitor's
    /// own colour for the theme. UI thread only.
    /// </summary>
    public static void Initialize(Func<string, bool, Color?> lookup, bool isDark)
    {
        _lookup = lookup;
        if (Resources == null)
        {
            ThemeService.HostControlled = true;
            Palette = BuildPalette(isDark);
            var root = new ResourceDictionary();
            root.MergedDictionaries.Add(Palette);
            root.MergedDictionaries.Add(Load("Styles"));
            root.MergedDictionaries.Add(Load("Shared"));
            root.MergedDictionaries.Add(Load("Hosted"));
            Resources = root;
            _signature = Signature(Palette, isDark);
            ThemeService.SetHostTheme(isDark);
            return;
        }
        Refresh(isDark);
    }

    /// <summary>Re-reads the host's palette after its theme changes; charts and custom-drawn controls redraw.</summary>
    public static void Refresh(bool isDark)
    {
        if (Resources == null || _lookup == null) return;
        var next = BuildPalette(isDark);
        var signature = Signature(next, isDark);
        if (signature == _signature) return;
        _signature = signature;
        // Swapping the merged palette notifies every element that merged Resources, so DynamicResource brushes update.
        Resources.MergedDictionaries[0] = next;
        Palette = next;
        ThemeService.SetHostTheme(isDark);
    }

    /// <summary>Merges the hosted resources into <paramref name="element"/>. Call before InitializeComponent. No-op when standalone.</summary>
    public static void ApplyTo(FrameworkElement element)
    {
        if (Resources != null && !element.Resources.MergedDictionaries.Contains(Resources))
            element.Resources.MergedDictionaries.Add(Resources);
    }

    private static ResourceDictionary BuildPalette(bool isDark)
    {
        var fallback = Load(isDark ? "Dark" : "Light");
        var d = new ResourceDictionary();
        foreach (var k in Keys)
        {
            var c = Safe(k, isDark) ?? (fallback["C." + k] is Color own ? own : Colors.Gray);
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            d["C." + k] = c;
            d["B." + k] = brush;
        }
        return d;
    }

    private static string Signature(ResourceDictionary palette, bool isDark) =>
        isDark + ":" + string.Join(",", Keys.Select(k => palette["C." + k]));

    private static Color? Safe(string key, bool isDark)
    {
        try { return _lookup?.Invoke(key, isDark); }
        catch { return null; }
    }

    private static ResourceDictionary Load(string name) =>
        new() { Source = new Uri($"pack://application:,,,/SQLDiskMonitor.Core;component/Themes/{name}.xaml") };
}
