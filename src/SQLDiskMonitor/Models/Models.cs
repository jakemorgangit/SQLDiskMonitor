using CommunityToolkit.Mvvm.ComponentModel;

namespace SQLDiskMonitor.Models;

public enum AuthMode { Windows, Sql, EntraIntegrated, EntraPassword }

/// <summary>A saved SQL Server connection. The password itself lives in Windows Credential Manager.</summary>
public partial class ServerConnection : ObservableObject
{
    public long Id { get; set; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private AuthMode _auth = AuthMode.Windows;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private bool _trustCertificate = true;
    [ObservableProperty] private bool _encrypt;
    [ObservableProperty] private int _timeout = 10;
    [ObservableProperty] private bool _monitor = true;
    [ObservableProperty] private string _hostName = "";
    [ObservableProperty] private string _version = "";

    // Runtime state (not persisted)
    [ObservableProperty] private bool _isCollecting;
    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private DateTime? _lastSample;

    public bool IsDemo => Address.Equals("demo", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

/// <summary>User-supplied characteristics of a volume (disk) on a server.</summary>
public partial class DiskConfig : ObservableObject
{
    public long ServerId { get; set; }
    /// <summary>Volume mount point as reported by sys.dm_os_volume_stats, e.g. "D:\" or "E:\Mounts\Data01\".</summary>
    public string Volume { get; set; } = "";

    [ObservableProperty] private string _label = "";
    /// <summary>Friendly name for the drive, e.g. "SQL data drive".</summary>
    [ObservableProperty] private string _driveName = "";
    /// <summary>Name of the underlying disk, e.g. the Azure managed disk resource name.</summary>
    [ObservableProperty] private string _diskName = "";
    [ObservableProperty] private int? _lun;
    [ObservableProperty] private string _tier = "";
    [ObservableProperty] private double _maxIops;
    [ObservableProperty] private double _maxMbps;
    [ObservableProperty] private double _burstIops;
    [ObservableProperty] private double _burstMbps;
    [ObservableProperty] private double _sizeGb;
    [ObservableProperty] private double _costMonthly;
    [ObservableProperty] private double _costPerGb;
    [ObservableProperty] private double _costPerIops;
    [ObservableProperty] private double _costPerMbps;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private int? _diskNumber;
    [ObservableProperty] private string _deviceModel = "";
    /// <summary>Free-form, comma-separated tags (e.g. "host cache: ReadOnly, SAN: VNX-02").</summary>
    [ObservableProperty] private string _tags = "";

    public IReadOnlyList<string> TagList => Tags.Split([",", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    partial void OnTagsChanged(string value) => OnPropertyChanged(nameof(TagList));

    public bool HasLimits => MaxIops > 0 || MaxMbps > 0;

    public double MonthlyCost =>
        CostMonthly + SizeGb * CostPerGb + MaxIops * CostPerIops + MaxMbps * CostPerMbps;

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Volume : $"{Volume}  {Label}";

    partial void OnCostMonthlyChanged(double value) => OnPropertyChanged(nameof(MonthlyCost));
    partial void OnCostPerGbChanged(double value) => OnPropertyChanged(nameof(MonthlyCost));
    partial void OnCostPerIopsChanged(double value) => OnPropertyChanged(nameof(MonthlyCost));
    partial void OnCostPerMbpsChanged(double value) => OnPropertyChanged(nameof(MonthlyCost));
    partial void OnSizeGbChanged(double value) => OnPropertyChanged(nameof(MonthlyCost));
    partial void OnMaxIopsChanged(double value) { OnPropertyChanged(nameof(MonthlyCost)); OnPropertyChanged(nameof(HasLimits)); }
    partial void OnMaxMbpsChanged(double value) { OnPropertyChanged(nameof(MonthlyCost)); OnPropertyChanged(nameof(HasLimits)); }
    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(DisplayName));
    partial void OnDriveNameChanged(string value) => OnPropertyChanged(nameof(NamesText));
    partial void OnDiskNameChanged(string value) => OnPropertyChanged(nameof(NamesText));

    /// <summary>Drive name and disk name together, for compact display.</summary>
    public string NamesText => string.Join("  ·  ", new[] { DriveName, DiskName }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public class DiskNote
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public string Volume { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>A database file and the volume it lives on.</summary>
public class DbFile
{
    public long ServerId { get; set; }
    public int DatabaseId { get; set; }
    public int FileId { get; set; }
    public string DatabaseName { get; set; } = "";
    public string LogicalName { get; set; } = "";
    public string TypeDesc { get; set; } = "";
    public string PhysicalName { get; set; } = "";
    public string Volume { get; set; } = "";
    public double SizeMb { get; set; }
    public long MaxSizePages { get; set; }
    public long Growth { get; set; }
    public bool IsPercentGrowth { get; set; }
    public DateTime LastSeen { get; set; }
    /// <summary>Space used inside the file (FILEPROPERTY SpaceUsed), when known.</summary>
    public double? UsedMb { get; set; }

    public double? FreeInFileMb => UsedMb is { } u ? Math.Max(0, SizeMb - u) : null;
    public double? UsedPct => UsedMb is { } u && SizeMb > 0 ? u / SizeMb : null;
    public bool IsLog => TypeDesc == "LOG";
    public bool IsTempDb => DatabaseId == 2;
    public string GrowthDisplay => Growth == 0 ? "None" : IsPercentGrowth ? $"{Growth}%" : $"{Growth * 8 / 1024.0:N0} MB";
    public string MaxSizeDisplay => MaxSizePages switch
    {
        -1 => "Unlimited",
        0 => "No growth",
        268435456 => "2 TB (log max)",
        _ => $"{MaxSizePages * 8 / 1024.0:N0} MB"
    };
}

/// <summary>One aggregated observation of a volume over a sample interval.</summary>
public class VolumeSample
{
    public long ServerId { get; set; }
    public string Volume { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public double ElapsedSec { get; set; }
    public double ReadIops { get; set; }
    public double WriteIops { get; set; }
    public double ReadMbps { get; set; }
    public double WriteMbps { get; set; }
    public double ReadLatencyMs { get; set; }
    public double WriteLatencyMs { get; set; }
    public double PendingIo { get; set; }
    public double TotalGb { get; set; }
    public double FreeGb { get; set; }
    public double Pressure { get; set; }
    public double CapacityPct { get; set; }
    public double LatencyScore { get; set; }

    public double TotalIops => ReadIops + WriteIops;
    public double TotalMbps => ReadMbps + WriteMbps;
}

public class FileSample
{
    public long ServerId { get; set; }
    public DateTime Timestamp { get; set; }
    public int DatabaseId { get; set; }
    public int FileId { get; set; }
    public double ReadIops { get; set; }
    public double WriteIops { get; set; }
    public double ReadMbps { get; set; }
    public double WriteMbps { get; set; }
    public double ReadLatencyMs { get; set; }
    public double WriteLatencyMs { get; set; }
    public double StallMsPerSec { get; set; }
}

public class WaitSample
{
    public long ServerId { get; set; }
    public DateTime Timestamp { get; set; }
    public string WaitType { get; set; } = "";
    public double WaitMsPerSec { get; set; }
    public double WaitsPerSec { get; set; }
}

public enum Severity { Info, Low, Medium, High, Critical }

/// <summary>A bottleneck or recommendation produced by the insight engine.</summary>
public class Finding
{
    public Severity Severity { get; set; }
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public string Volume { get; set; } = "";
    public string SeverityText => Severity.ToString().ToUpperInvariant();
}

public record TimeRange(string Label, TimeSpan Span)
{
    public override string ToString() => Label;
}
