using System.IO;
using System.Text.Json;

namespace SQLDiskMonitor.Models;

public enum ThemePreference { System, Light, Dark }

public class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.Dark;
    public int SampleIntervalSeconds { get; set; } = 15;
    public int RetentionDays { get; set; } = 30;
    public int FileRetentionDays { get; set; } = 7;

    public bool CostsEnabled { get; set; }
    public string CurrencySymbol { get; set; } = "£";

    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public string SkippedVersion { get; set; } = "";

    // Latency thresholds (ms). Data = reads/writes against data files, Log = writes to log files.
    public double DataLatencyGood { get; set; } = 10;
    public double DataLatencyWarn { get; set; } = 20;
    public double DataLatencyCritical { get; set; } = 50;
    public double LogLatencyGood { get; set; } = 2;
    public double LogLatencyWarn { get; set; } = 5;
    public double LogLatencyCritical { get; set; } = 20;

    /// <summary>Below this many IOs/sec latency is ignored for pressure: a handful of slow IOs is noise, not pressure.</summary>
    public double MinIopsForLatency { get; set; } = 5;
    /// <summary>Capacity % at which a disk is considered saturated.</summary>
    public double SaturationPct { get; set; } = 90;

    public long LastServerId { get; set; }
    /// <summary>Overview disk card layout: Compact, Normal or List.</summary>
    public string DashboardLayout { get; set; } = "Normal";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string DataFolder
    {
        get
        {
            // SQLDISKMONITOR_DATA lets tests and screenshots run against a throwaway folder.
            var dir = Environment.GetEnvironmentVariable("SQLDISKMONITOR_DATA") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQLDiskMonitor");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt settings fall back to defaults */ }
        return new AppSettings();
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
}
