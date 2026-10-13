using System.Windows;
using Microsoft.Win32;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Services;

/// <summary>Applies light/dark: WPF's Fluent ThemeMode for standard controls plus our own palette dictionary.</summary>
public static class ThemeService
{
    public static event Action? Changed;
    public static bool IsDark { get; private set; } = true;

    public static void Apply(ThemePreference pref)
    {
        bool dark = pref switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemUsesDark()
        };
        IsDark = dark;

        var app = Application.Current;
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;

        var dicts = app.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/Dark.xaml") == true ||
                                                 d.Source?.OriginalString.Contains("Themes/Light.xaml") == true);
        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/SQLDiskMonitor;component/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        if (existing != null) dicts[dicts.IndexOf(existing)] = next;
        else dicts.Add(next);

        Changed?.Invoke();
    }

    private static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return true;
        }
    }
}
