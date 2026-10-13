using SQLDiskMonitor.Data;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Services;

public class DiskStats
{
    public string Volume { get; init; } = "";
    public DiskConfig? Config { get; init; }
    public int Samples { get; init; }
    public double AvgIops { get; init; }
    public double P95Iops { get; init; }
    public double PeakIops { get; init; }
    public double AvgMbps { get; init; }
    public double P95Mbps { get; init; }
    public double PeakMbps { get; init; }
    public double AvgReadLatency { get; init; }
    public double AvgWriteLatency { get; init; }
    public double P95ReadLatency { get; init; }
    public double P95WriteLatency { get; init; }
    public double AvgPressure { get; init; }
    public double P95Pressure { get; init; }
    public double PeakPressure { get; init; }
    public double SaturatedPct { get; init; }
    public double BurstingPct { get; init; }
    public int PeakHour { get; init; } = -1;
    public double TotalGb { get; init; }
    public double FreeGb { get; init; }
    public double IopsUtilP95 => Config is { MaxIops: > 0 } ? P95Iops / Config.MaxIops * 100 : 0;
    public double MbpsUtilP95 => Config is { MaxMbps: > 0 } ? P95Mbps / Config.MaxMbps * 100 : 0;
    public double FreePct => TotalGb > 0 ? FreeGb / TotalGb * 100 : 0;
}

public class AnalysisResult
{
    public List<DiskStats> Disks { get; init; } = [];
    public List<Finding> Findings { get; init; } = [];
    public List<Store.FileStat> FileStats { get; init; } = [];
    public List<Store.WaitStat> Waits { get; init; } = [];
    public List<Store.HourCell> Hourly { get; init; } = [];
    public SpaceAnalysis Space { get; init; } = new();
}

/// <summary>Turns stored samples into per-disk statistics and ranked bottleneck findings.</summary>
public static class Analytics
{
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var idx = Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }

    public static AnalysisResult Analyse(Store store, long serverId, DateTime from, DateTime to, AppSettings cfg)
    {
        var disks = store.GetDisks(serverId).ToDictionary(d => d.Volume, StringComparer.OrdinalIgnoreCase);
        var files = store.GetFiles(serverId);
        var samples = store.GetVolumeSeries(serverId, from, to, 60);
        var hourly = store.GetHourlyPressure(serverId, from, to);
        var fileStats = store.GetFileStats(serverId, from, to);
        var waits = store.GetWaitStats(serverId, from, to);

        var stats = new List<DiskStats>();
        foreach (var g in samples.GroupBy(s => s.Volume))
        {
            var list = g.ToList();
            disks.TryGetValue(g.Key, out var d);
            var iops = list.Select(s => s.TotalIops).Order().ToList();
            var mbps = list.Select(s => s.TotalMbps).Order().ToList();
            var press = list.Select(s => s.Pressure).Order().ToList();
            var rl = list.Where(s => s.ReadIops >= cfg.MinIopsForLatency).Select(s => s.ReadLatencyMs).Order().ToList();
            var wl = list.Where(s => s.WriteIops >= cfg.MinIopsForLatency).Select(s => s.WriteLatencyMs).Order().ToList();
            double readW = list.Sum(s => s.ReadIops), writeW = list.Sum(s => s.WriteIops);

            int bursting = 0;
            if (d is { MaxIops: > 0 })
                bursting = list.Count(s => s.TotalIops > d.MaxIops || (d.MaxMbps > 0 && s.TotalMbps > d.MaxMbps));

            var peakHour = hourly.Where(h => h.Volume == g.Key).GroupBy(h => h.Hour)
                .Select(h => (Hour: h.Key, P: h.Sum(x => x.AvgPressure * x.Samples) / Math.Max(1, h.Sum(x => x.Samples))))
                .OrderByDescending(h => h.P).FirstOrDefault();

            var last = list[^1];
            stats.Add(new DiskStats
            {
                Volume = g.Key, Config = d, Samples = list.Count,
                AvgIops = iops.Average(), P95Iops = Percentile(iops, 95), PeakIops = iops[^1],
                AvgMbps = mbps.Average(), P95Mbps = Percentile(mbps, 95), PeakMbps = mbps[^1],
                AvgReadLatency = readW > 0 ? list.Sum(s => s.ReadLatencyMs * s.ReadIops) / readW : 0,
                AvgWriteLatency = writeW > 0 ? list.Sum(s => s.WriteLatencyMs * s.WriteIops) / writeW : 0,
                P95ReadLatency = Percentile(rl, 95), P95WriteLatency = Percentile(wl, 95),
                AvgPressure = press.Average(), P95Pressure = Percentile(press, 95), PeakPressure = press[^1],
                SaturatedPct = 100.0 * list.Count(s => s.CapacityPct >= cfg.SaturationPct) / list.Count,
                BurstingPct = 100.0 * bursting / list.Count,
                PeakHour = peakHour.P > 0 ? peakHour.Hour : -1,
                TotalGb = last.TotalGb, FreeGb = last.FreeGb
            });
        }

        var result = new AnalysisResult
        {
            Disks = stats.OrderBy(s => s.Volume).ToList(),
            FileStats = fileStats,
            Waits = waits,
            Hourly = hourly,
            Space = SpaceAnalytics.Analyse(store, serverId, from, to)
        };
        result.Findings.AddRange(BuildFindings(result, disks, files, cfg));
        result.Findings.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return result;
    }

    private static string Hour(int h) => h < 0 ? "" : $"{h:00}:00–{(h + 1) % 24:00}:00";

    private static IEnumerable<Finding> BuildFindings(AnalysisResult r, Dictionary<string, DiskConfig> disks, List<DbFile> files, AppSettings cfg)
    {
        var filesByVol = files.GroupBy(f => f.Volume, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var s in r.Disks)
        {
            var d = s.Config;
            var name = d?.DisplayName ?? s.Volume;
            var volFiles = filesByVol.GetValueOrDefault(s.Volume) ?? [];
            var volStats = r.FileStats.Where(fs => volFiles.Any(f => f.DatabaseId == fs.DatabaseId && f.FileId == fs.FileId)).ToList();
            double logW = volStats.Where(fs => volFiles.Any(f => f.IsLog && f.DatabaseId == fs.DatabaseId && f.FileId == fs.FileId)).Sum(fs => fs.WriteIops);
            double allW = volStats.Sum(fs => fs.WriteIops);
            // A volume counts as a log volume when most of its writes are log writes.
            bool hostsLog = allW > 0 ? logW * 2 > allW : volFiles.Any(f => f.IsLog);
            bool hostsData = volFiles.Any(f => !f.IsLog);

            // 1. Throughput / IOPS saturation
            if (d is { HasLimits: true } && s.SaturatedPct >= 1)
            {
                var sev = s.SaturatedPct >= 15 ? Severity.Critical : s.SaturatedPct >= 5 ? Severity.High : Severity.Medium;
                var which = s.Config!.MaxIops > 0 && s.PeakIops / s.Config.MaxIops >= (s.Config.MaxMbps > 0 ? s.PeakMbps / s.Config.MaxMbps : 0) ? "IOPS" : "throughput";
                yield return new Finding
                {
                    Severity = sev, Category = "Capacity", Volume = s.Volume,
                    Title = $"{name} is at its {which} limit {s.SaturatedPct:N1}% of the time",
                    Detail = $"Peak {s.PeakIops:N0} IOPS / {s.PeakMbps:N0} MB/s against provisioned {d.MaxIops:N0} IOPS / {d.MaxMbps:N0} MB/s. " +
                             (s.PeakHour >= 0 ? $"Pressure peaks {Hour(s.PeakHour)}." : ""),
                    Recommendation = "Once a disk hits its cap, IOs queue and latency climbs. Move hot files to another disk, raise provisioned performance, " +
                                     "or use VM-level caching. Also check the VM's own uncached IOPS/MB/s limit, which can cap sooner than the disk."
                };
            }

            // 2. Living on burst credits
            if (d is { BurstIops: > 0 } && s.BurstingPct >= 5)
            {
                yield return new Finding
                {
                    Severity = s.BurstingPct >= 25 ? Severity.High : Severity.Medium, Category = "Capacity", Volume = s.Volume,
                    Title = $"{name} runs above its baseline {s.BurstingPct:N0}% of the time (burst)",
                    Detail = $"Baseline {d.MaxIops:N0} IOPS / {d.MaxMbps:N0} MB/s, burst {d.BurstIops:N0} / {d.BurstMbps:N0}. Peak {s.PeakIops:N0} IOPS / {s.PeakMbps:N0} MB/s.",
                    Recommendation = "Credit-based bursting runs out (typically after ~30 minutes at full burst). When it does, the disk drops to baseline and latency spikes. Size for the sustained workload."
                };
            }

            // 3. Read latency on data volumes
            if (hostsData && s.AvgReadLatency > cfg.DataLatencyWarn && s.AvgIops >= cfg.MinIopsForLatency)
            {
                yield return new Finding
                {
                    Severity = s.AvgReadLatency > cfg.DataLatencyCritical ? Severity.High : Severity.Medium, Category = "Latency", Volume = s.Volume,
                    Title = $"Read latency on {name} averages {s.AvgReadLatency:N1} ms",
                    Detail = $"p95 {s.P95ReadLatency:N1} ms against a {cfg.DataLatencyWarn:N0} ms warning threshold.",
                    Recommendation = "Look for scans driving physical reads (missing indexes, memory pressure / low page life expectancy) before buying faster storage."
                };
            }

            // 4. Log write latency
            if (hostsLog && s.AvgWriteLatency > cfg.LogLatencyWarn && s.AvgIops >= cfg.MinIopsForLatency)
            {
                yield return new Finding
                {
                    Severity = s.AvgWriteLatency > cfg.LogLatencyCritical ? Severity.High : Severity.Medium, Category = "Latency", Volume = s.Volume,
                    Title = $"Write latency on log volume {name} averages {s.AvgWriteLatency:N1} ms",
                    Detail = $"p95 {s.P95WriteLatency:N1} ms against a {cfg.LogLatencyWarn:N0} ms target. Every commit waits for log writes.",
                    Recommendation = "Keep transaction logs on low-latency storage with host caching off, away from data files, and review small, chatty transactions."
                };
            }

            // 5. Free space (optional; effective free space counts unused space inside the data files)
            var space = r.Space.Volumes.FirstOrDefault(v => v.Volume == s.Volume);
            double freePct = cfg.HealthFreePct(space, s.FreePct);
            if (cfg.SpaceInHealth && s.TotalGb > 0 && freePct < cfg.SpaceWarnPct)
            {
                bool effective = cfg.CountFileFreeSpace && space != null;
                yield return new Finding
                {
                    Severity = freePct < cfg.SpaceWarnPct / 3 ? Severity.Critical : freePct < cfg.SpaceWarnPct / 1.5 ? Severity.High : Severity.Low,
                    Category = "Space", Volume = s.Volume,
                    Title = effective
                        ? $"{name} has {freePct:N1}% headroom left ({space!.EffectiveFreeGb:N0} of {s.TotalGb:N0} GB)"
                        : $"{name} has {freePct:N1}% free ({s.FreeGb:N0} of {s.TotalGb:N0} GB)",
                    Detail = effective
                        ? $"{space!.FreeGb:N0} GB free on the volume plus {space.FreeInFilesGb ?? 0:N0} GB unused inside its {volFiles.Count} database files."
                        : $"{volFiles.Count} database files on this volume.",
                    Recommendation = "Plan growth before autogrowth fails. Check the size of the next growth increment against free space."
                };
            }

            // 6. Over-provisioned
            if (d is { HasLimits: true } && s.Samples >= 60 && s.P95Pressure < 25 && Math.Max(s.IopsUtilP95, s.MbpsUtilP95) < 20)
            {
                var cost = cfg.CostsEnabled && d.MonthlyCost > 0 ? $" Costs {cfg.CurrencySymbol}{d.MonthlyCost:N2}/month." : "";
                var smaller = DiskPresets.All
                    .Where(p => p.Family == PresetFamily(d.Tier) && p.Iops > 0 && p.Iops >= s.P95Iops * 1.5 && p.Mbps >= s.P95Mbps * 1.5 && p.Iops < d.MaxIops && (p.SizeGb == 0 || p.SizeGb >= s.TotalGb - s.FreeGb))
                    .OrderBy(p => p.Iops).FirstOrDefault();
                yield return new Finding
                {
                    Severity = Severity.Info, Category = "Cost", Volume = s.Volume,
                    Title = $"{name} uses under 20% of its provisioned performance",
                    Detail = $"p95 {s.P95Iops:N0} IOPS ({s.IopsUtilP95:N0}%) and {s.P95Mbps:N0} MB/s ({s.MbpsUtilP95:N0}%).{cost}",
                    Recommendation = smaller != null
                        ? $"{smaller.Name} ({smaller.Summary}) would still give 50% headroom over observed p95, if capacity allows."
                        : "Consider a smaller performance tier, provided the observed period includes month-end and maintenance windows."
                };
            }

            // 7. Unconfigured disk
            if (d is not { HasLimits: true } && s.AvgIops > 1)
            {
                yield return new Finding
                {
                    Severity = Severity.Info, Category = "Configuration", Volume = s.Volume,
                    Title = $"No performance limits set for {name}",
                    Detail = "Pressure on this disk is based on latency only.",
                    Recommendation = "Set max IOPS and MB/s on the Disks page (or pick a preset) to enable capacity, burst and right-sizing analysis."
                };
            }

            // 8. Hot file dominates the volume
            var volFileStats = volStats;
            double volStall = volFileStats.Sum(f => f.StallMsPerSec);
            var hot = volFileStats.OrderByDescending(f => f.StallMsPerSec).FirstOrDefault();
            if (hot != null && volFileStats.Count > 1 && volStall > 50 && hot.StallMsPerSec / volStall > 0.6 && s.P95Pressure >= Pressure.Elevated)
            {
                var hf = volFiles.First(f => f.DatabaseId == hot.DatabaseId && f.FileId == hot.FileId);
                yield return new Finding
                {
                    Severity = Severity.Medium, Category = "Hotspot", Volume = s.Volume,
                    Title = $"{hf.DatabaseName}.{hf.LogicalName} accounts for {hot.StallMsPerSec / volStall:P0} of IO wait on {name}",
                    Detail = $"{hot.ReadIops + hot.WriteIops:N0} avg IOPS, read {hot.ReadLatency:N1} ms, write {hot.WriteLatency:N1} ms.",
                    Recommendation = "Moving this file to its own disk (or spreading it across files on several disks) isolates its load from everything else on the volume."
                };
            }
        }

        // Capacity forecast. With file free space counted, it follows data growth inside the files against effective headroom.
        foreach (var v in r.Space.Volumes)
        {
            if (!cfg.SpaceInHealth) break;
            bool effective = cfg.CountFileFreeSpace;
            if ((effective ? v.EffectiveDaysToFull : v.DaysToFull) is not { } days || days >= 120) continue;
            yield return new Finding
            {
                Severity = days < 7 ? Severity.Critical : days < 30 ? Severity.High : Severity.Medium, Category = "Space", Volume = v.Volume,
                Title = $"{(disks.GetValueOrDefault(v.Volume)?.DisplayName ?? v.Volume)} is on course to fill in about {days:N0} days",
                Detail = effective
                    ? $"Data inside the files is growing {v.DataGrowthText}, with {v.EffectiveFreeGb:N0} GB of headroom ({v.FreeGb:N0} GB on the volume + {v.FreeInFilesGb ?? 0:N0} GB inside files)."
                    : $"Used space is growing {v.GrowthText} with {v.FreeGb:N0} GB free ({v.FreePct:N1}%).",
                Recommendation = "Extend the disk or move growing files before then. The Space page shows which databases are growing."
            };
        }

        // Frequent autogrowth
        var frequent = r.Space.Files.Values.Where(g => g.Days >= 0.5 && g.Events / g.Days > 4).ToList();
        if (frequent.Count > 0)
        {
            string Name(FileGrowth g) => files.FirstOrDefault(f => f.DatabaseId == g.DatabaseId && f.FileId == g.FileId) is { } f
                ? $"{f.DatabaseName}.{f.LogicalName}" : $"{g.DatabaseId}:{g.FileId}";
            yield return new Finding
            {
                Severity = Severity.Low, Category = "Files",
                Title = $"{frequent.Count} file(s) autogrow more than 4 times a day",
                Detail = string.Join(", ", frequent.Take(6).Select(g => $"{Name(g)} ({g.Events / g.Days:N0}/day)")),
                Recommendation = "Each growth stalls writes while the file extends, and log files can't use instant file initialisation. Pre-size the file or use a larger growth increment."
            };
        }

        // 9. Data and log of the same user database share a volume
        var mixed = files.Where(f => f.DatabaseId > 4).GroupBy(f => f.DatabaseName)
            .Where(g => g.Any(f => f.IsLog) && g.Any(f => !f.IsLog) &&
                        g.Where(f => f.IsLog).Any(l => g.Any(d => !d.IsLog && d.Volume.Equals(l.Volume, StringComparison.OrdinalIgnoreCase))))
            .Select(g => g.Key).ToList();
        if (mixed.Count > 0)
        {
            yield return new Finding
            {
                Severity = Severity.Low, Category = "Layout",
                Title = $"Data and log files share a volume for {mixed.Count} database(s)",
                Detail = string.Join(", ", mixed.Take(10)) + (mixed.Count > 10 ? $" and {mixed.Count - 10} more" : ""),
                Recommendation = "Log writes are sequential and latency-sensitive. Mixing them with random data-file IO on one disk hurts commit latency."
            };
        }

        // 10. tempdb sharing with user databases
        var tempVols = files.Where(f => f.IsTempDb).Select(f => f.Volume).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sharing = tempVols.Where(v => files.Any(f => f.DatabaseId > 4 && f.Volume.Equals(v, StringComparison.OrdinalIgnoreCase))).ToList();
        if (sharing.Count > 0)
        {
            yield return new Finding
            {
                Severity = Severity.Low, Category = "Layout",
                Title = "tempdb shares a volume with user databases",
                Detail = string.Join(", ", sharing),
                Recommendation = "Put tempdb on its own disk, ideally the VM's local/ephemeral SSD, so sorts, spills and version store traffic don't compete with user data."
            };
        }

        // 11. Percent autogrowth / unlimited-tiny growth
        var pctGrowth = files.Where(f => f.IsPercentGrowth && f.Growth > 0).ToList();
        if (pctGrowth.Count > 0)
        {
            yield return new Finding
            {
                Severity = Severity.Low, Category = "Files",
                Title = $"{pctGrowth.Count} file(s) use percentage autogrowth",
                Detail = string.Join(", ", pctGrowth.Take(8).Select(f => $"{f.DatabaseName}.{f.LogicalName} ({f.Growth}%)")),
                Recommendation = "Percentage growth gets larger as files grow, so growth events take longer and log files fragment into many VLFs. Use a fixed MB increment."
            };
        }

        // 12. IO waits
        var writelog = r.Waits.FirstOrDefault(w => w.WaitType == "WRITELOG");
        if (writelog is { AvgMsPerWait: > 0 } && writelog.AvgMsPerWait > cfg.LogLatencyWarn && writelog.AvgWaitMsPerSec > 50)
        {
            yield return new Finding
            {
                Severity = writelog.AvgMsPerWait > cfg.LogLatencyCritical ? Severity.High : Severity.Medium, Category = "Waits",
                Title = $"WRITELOG waits average {writelog.AvgMsPerWait:N1} ms",
                Detail = $"{writelog.AvgWaitMsPerSec / 1000:N2} sessions on average are waiting for log flushes.",
                Recommendation = "Commit throughput is limited by log write latency. Check the log volume's latency and whether it's near its IOPS limit."
            };
        }
        var pageio = r.Waits.Where(w => w.WaitType.StartsWith("PAGEIOLATCH")).ToList();
        double pageioMs = pageio.Sum(w => w.AvgWaitMsPerSec);
        if (pageioMs > 500)
        {
            yield return new Finding
            {
                Severity = pageioMs > 5000 ? Severity.High : Severity.Medium, Category = "Waits",
                Title = $"Queries spend {pageioMs / 1000:N1} s/s waiting on physical data-page reads (PAGEIOLATCH)",
                Detail = string.Join(", ", pageio.Select(w => $"{w.WaitType} {w.AvgMsPerWait:N1} ms/wait")),
                Recommendation = "High PAGEIOLATCH is usually memory pressure or large scans first, and storage speed second. Check buffer pool size and the top read queries."
            };
        }
    }

    private static string PresetFamily(string tier) =>
        DiskPresets.All.FirstOrDefault(p => p.Name.Equals(tier, StringComparison.OrdinalIgnoreCase))?.Family ?? "";
}
