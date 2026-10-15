using System.Windows;
using Microsoft.Win32;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Services;

/// <summary>
/// Applies light/dark: WPF's Fluent ThemeMode for standard controls plus our own palette dictionary.
/// When another app hosts SQL Disk Monitor the host owns the theme (see Hosting.HostTheme) and this never touches
/// Application.ThemeMode or Application.Resources.
/// </summary>
public static class ThemeService
{
    public static event Action? Changed;
    public static bool IsDark { get; private set; } = true;

    /// <summary>True when a hosting app controls the theme.</summary>
    public static bool HostControlled { get; internal set; }

    public static void Apply(ThemePreference pref)
    {
        if (HostControlled) return;
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
        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/SQLDiskMonitor.Core;component/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        if (existing != null) dicts[dicts.IndexOf(existing)] = next;
        else dicts.Add(next);

        Changed?.Invoke();
    }

    /// <summary>Called by Hosting.HostTheme after the host's palette has been mapped.</summary>
    internal static void SetHostTheme(bool dark)
    {
        IsDark = dark;
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
