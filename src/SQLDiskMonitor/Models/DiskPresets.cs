namespace SQLDiskMonitor.Models;

public record DiskPreset(string Name, string Family, double SizeGb, double Iops, double Mbps, double BurstIops, double BurstMbps)
{
    public string Summary => Iops > 0
        ? $"{Iops:N0} IOPS · {Mbps:N0} MB/s" + (BurstIops > Iops ? $" · burst {BurstIops:N0}/{BurstMbps:N0}" : "")
        : "Custom limits";
    public override string ToString() => $"{Name}  ({Summary})";
}

/// <summary>
/// Published per-disk performance targets for common cloud disk types. Prices are deliberately not included:
/// they vary by region, agreement and currency, so cost is entered per disk.
/// </summary>
public static class DiskPresets
{
    public static readonly IReadOnlyList<DiskPreset> All =
    [
        new("Custom", "Custom", 0, 0, 0, 0, 0),

        // Azure Premium SSD (v1). P20 and below have credit-based burst to 3,500 IOPS / 170 MB/s.
        new("Azure P1", "Azure Premium SSD", 4, 120, 25, 3500, 170),
        new("Azure P2", "Azure Premium SSD", 8, 120, 25, 3500, 170),
        new("Azure P3", "Azure Premium SSD", 16, 120, 25, 3500, 170),
        new("Azure P4", "Azure Premium SSD", 32, 120, 25, 3500, 170),
        new("Azure P6", "Azure Premium SSD", 64, 240, 50, 3500, 170),
        new("Azure P10", "Azure Premium SSD", 128, 500, 100, 3500, 170),
        new("Azure P15", "Azure Premium SSD", 256, 1100, 125, 3500, 170),
        new("Azure P20", "Azure Premium SSD", 512, 2300, 150, 3500, 170),
        new("Azure P30", "Azure Premium SSD", 1024, 5000, 200, 0, 0),
        new("Azure P40", "Azure Premium SSD", 2048, 7500, 250, 0, 0),
        new("Azure P50", "Azure Premium SSD", 4096, 7500, 250, 0, 0),
        new("Azure P60", "Azure Premium SSD", 8192, 16000, 500, 0, 0),
        new("Azure P70", "Azure Premium SSD", 16384, 18000, 750, 0, 0),
        new("Azure P80", "Azure Premium SSD", 32767, 20000, 900, 0, 0),

        // Azure Premium SSD v2 / Ultra: performance is provisioned independently of size.
        new("Azure Premium SSD v2 (baseline)", "Azure Premium SSD v2", 0, 3000, 125, 0, 0),
        new("Azure Ultra Disk", "Azure Ultra Disk", 0, 0, 0, 0, 0),

        // AWS EBS
        new("AWS gp3 (baseline)", "AWS EBS", 0, 3000, 125, 0, 0),
        new("AWS gp3 (max)", "AWS EBS", 0, 16000, 1000, 0, 0),
        new("AWS io2 Block Express", "AWS EBS", 0, 0, 0, 0, 0),
        new("AWS st1 (throughput HDD)", "AWS EBS", 0, 500, 500, 0, 0),

        // Google Cloud
        new("GCP Hyperdisk Balanced (baseline)", "GCP", 0, 3000, 140, 0, 0),

        // On-premises / generic
        new("SAN / array LUN", "On-premises", 0, 0, 0, 0, 0),
        new("Local NVMe SSD", "On-premises", 0, 0, 0, 0, 0),
    ];
}
