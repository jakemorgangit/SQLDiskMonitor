using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Services;

/// <summary>
/// Disk Pressure Score (0–100). Not a hardware health metric: it shows how hard SQL Server is pushing a volume.
///
///   Capacity  = max(IOPS / max IOPS, MB/s / max MB/s) × 100   (only when limits are configured; capped at 100)
///   Latency   = read and write latency mapped onto a 0–100 curve using the good / warn / critical thresholds
///               (log volumes use the stricter log-write thresholds). Ignored when there are too few IOs to be meaningful.
///   Pressure  = max(Capacity, Latency)
///
/// Either running out of provisioned throughput or IOs queuing up (latency) is pressure, whichever is worse.
/// </summary>
public static class Pressure
{
    public const double Elevated = 40, High = 70, Critical = 90;

    public static string Band(double score) => score switch
    {
        >= Critical => "Critical",
        >= High => "High",
        >= Elevated => "Elevated",
        _ => "Healthy"
    };

    public static double LatencyCurve(double ms, double good, double warn, double crit)
    {
        if (ms <= 0) return 0;
        if (ms <= good) return 30 * ms / good;
        if (ms <= warn) return 30 + 40 * (ms - good) / (warn - good);
        if (ms <= crit) return 70 + 25 * (ms - warn) / (crit - warn);
        return Math.Min(100, 95 + 5 * (ms - crit) / crit);
    }

    public static void Score(VolumeSample s, DiskConfig? disk, bool hostsLog, AppSettings cfg)
    {
        double capacity = 0;
        if (disk is { MaxIops: > 0 }) capacity = Math.Max(capacity, s.TotalIops / disk.MaxIops);
        if (disk is { MaxMbps: > 0 }) capacity = Math.Max(capacity, s.TotalMbps / disk.MaxMbps);
        s.CapacityPct = Math.Min(100, capacity * 100);

        double lat = 0;
        if (s.ReadIops >= cfg.MinIopsForLatency)
            lat = Math.Max(lat, LatencyCurve(s.ReadLatencyMs, cfg.DataLatencyGood, cfg.DataLatencyWarn, cfg.DataLatencyCritical));
        if (s.WriteIops >= cfg.MinIopsForLatency)
        {
            lat = Math.Max(lat, hostsLog
                ? LatencyCurve(s.WriteLatencyMs, cfg.LogLatencyGood, cfg.LogLatencyWarn, cfg.LogLatencyCritical)
                : LatencyCurve(s.WriteLatencyMs, cfg.DataLatencyGood, cfg.DataLatencyWarn, cfg.DataLatencyCritical));
        }
        s.LatencyScore = lat;
        s.Pressure = Math.Round(Math.Max(s.CapacityPct, s.LatencyScore), 1);
    }
}
