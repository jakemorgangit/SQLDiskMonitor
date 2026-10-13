using SQLDiskMonitor.Data;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Services;

public class VolumeSpace
{
    public string Volume { get; init; } = "";
    public string Label { get; init; } = "";
    public int? Lun { get; init; }
    public double TotalGb { get; init; }
    public double FreeGb { get; init; }
    public double UsedGb => TotalGb - FreeGb;
    public double FreePct => TotalGb > 0 ? FreeGb / TotalGb * 100 : 0;
    public double UsedPct => 100 - FreePct;
    /// <summary>Linear trend of used space over the period; null when the period is too short to say.</summary>
    public double? GrowthGbPerDay { get; init; }
    public double? DaysToFull => GrowthGbPerDay is > 0.01 ? FreeGb / GrowthGbPerDay : null;
    public double AllocatedGb { get; init; }
    public double? UsedInFilesGb { get; init; }
    /// <summary>Space allocated to database files but not yet used inside them.</summary>
    public double? FreeInFilesGb => UsedInFilesGb is { } u ? Math.Max(0, AllocatedGb - u) : null;
    /// <summary>Volume free space plus unused space inside the database files on it.</summary>
    public double EffectiveFreeGb => FreeGb + (FreeInFilesGb ?? 0);
    public double EffectiveFreePct => TotalGb > 0 ? EffectiveFreeGb / TotalGb * 100 : 0;
    public double EffectiveUsedPct => 100 - EffectiveFreePct;
    /// <summary>Growth of data actually written inside the files (not file autogrowth); null when unknown.</summary>
    public double? DataGrowthGbPerDay { get; init; }
    public double? EffectiveDaysToFull => DataGrowthGbPerDay is > 0.01 ? EffectiveFreeGb / DataGrowthGbPerDay : null;
    public string DataGrowthText => DataGrowthGbPerDay is { } g ? $"{(g >= 0 ? "+" : "")}{g:N2} GB/day" : "—";
    public string EffectiveDaysToFullText => EffectiveDaysToFull is { } d ? d > 3650 ? "> 10 years" : $"{d:N0} days" : "Not growing";
    public int FileCount { get; init; }
    public List<(DateTime T, double UsedGb, double TotalGb)> Series { get; init; } = [];
    public string GrowthText => GrowthGbPerDay is { } g ? $"{(g >= 0 ? "+" : "")}{g:N2} GB/day" : "—";
    public string DaysToFullText => DaysToFull is { } d ? d > 3650 ? "> 10 years" : $"{d:N0} days" : "Not growing";
}

public class FileGrowth
{
    public int DatabaseId { get; init; }
    public int FileId { get; init; }
    public double StartMb { get; init; }
    public double EndMb { get; init; }
    public double GrowthMb => EndMb - StartMb;
    public int Events { get; init; }
    public double? UsedStartMb { get; init; }
    public double? UsedEndMb { get; init; }
    public double Days { get; init; }
    public double MbPerDay => Days > 0 ? GrowthMb / Days : 0;
    public double? UsedMbPerDay => Days > 0 && UsedStartMb is { } a && UsedEndMb is { } b ? (b - a) / Days : null;
}

public class DatabaseSpace
{
    public string Database { get; init; } = "";
    public int Files { get; init; }
    public double DataGb { get; init; }
    public double LogGb { get; init; }
    public double AllocatedGb => DataGb + LogGb;
    public double? UsedGb { get; init; }
    public double? FreeInFilesGb => UsedGb is { } u ? Math.Max(0, AllocatedGb - u) : null;
    public double? UsedPct => UsedGb is { } u && AllocatedGb > 0 ? u / AllocatedGb : null;
    public double AllocatedMbPerDay { get; init; }
    public double? UsedMbPerDay { get; init; }
    public int GrowthEvents { get; init; }
    public string Volumes { get; init; } = "";
}

public class SpaceAnalysis
{
    public List<VolumeSpace> Volumes { get; init; } = [];
    public List<DatabaseSpace> Databases { get; init; } = [];
    public Dictionary<(int Db, int File), FileGrowth> Files { get; init; } = [];
    public double TotalGb => Volumes.Sum(v => v.TotalGb);
    public double UsedGb => Volumes.Sum(v => v.UsedGb);
    public double FreeGb => Volumes.Sum(v => v.FreeGb);
    public double? GrowthGbPerDay => Volumes.Any(v => v.GrowthGbPerDay != null) ? Volumes.Sum(v => v.GrowthGbPerDay ?? 0) : null;
    public VolumeSpace? FirstToFill => Volumes.Where(v => v.DaysToFull != null).MinBy(v => v.DaysToFull);
}

/// <summary>Capacity, used space and growth: volumes from the IO samples, files from the 15-minute space snapshots.</summary>
public static class SpaceAnalytics
{
    public static SpaceAnalysis Analyse(Store store, long serverId, DateTime from, DateTime to)
    {
        var disks = store.GetDisks(serverId).ToDictionary(d => d.Volume, StringComparer.OrdinalIgnoreCase);
        var files = store.GetFiles(serverId);
        int bucket = (int)Math.Max(300, (to - from).TotalSeconds / 400);
        var series = store.GetVolumeSeries(serverId, from, to, bucket);
        var history = store.GetFileSpaceHistory(serverId, from, to);

        var growth = new Dictionary<(int, int), FileGrowth>();
        foreach (var g in history.GroupBy(h => (h.DatabaseId, h.FileId)))
        {
            var pts = g.OrderBy(p => p.Time).ToList();
            int events = 0;
            for (int i = 1; i < pts.Count; i++) if (pts[i].SizeMb > pts[i - 1].SizeMb + 0.01) events++;
            var firstUsed = pts.FirstOrDefault(p => p.UsedMb != null);
            var lastUsed = pts.LastOrDefault(p => p.UsedMb != null);
            growth[g.Key] = new FileGrowth
            {
                DatabaseId = g.Key.DatabaseId, FileId = g.Key.FileId, StartMb = pts[0].SizeMb, EndMb = pts[^1].SizeMb, Events = events,
                UsedStartMb = firstUsed?.UsedMb, UsedEndMb = lastUsed?.UsedMb,
                Days = (pts[^1].Time - pts[0].Time).TotalDays
            };
        }

        var volumes = new List<VolumeSpace>();
        foreach (var g in series.GroupBy(s => s.Volume).OrderBy(g => g.Key))
        {
            var pts = g.OrderBy(s => s.Timestamp).Where(s => s.TotalGb > 0).ToList();
            if (pts.Count == 0) continue;
            var last = pts[^1];
            disks.TryGetValue(g.Key, out var d);
            var volFiles = files.Where(f => f.Volume.Equals(g.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            // Log usage cycles up and down with backups and checkpoints, so only data files count towards growth.
            var dataRates = volFiles.Where(f => !f.IsLog)
                .Select(f => growth.GetValueOrDefault((f.DatabaseId, f.FileId))?.UsedMbPerDay).OfType<double>().ToList();
            volumes.Add(new VolumeSpace
            {
                Volume = g.Key, Label = d?.Label ?? "", Lun = d?.Lun,
                TotalGb = last.TotalGb, FreeGb = last.FreeGb,
                GrowthGbPerDay = Slope(pts.Select(p => (p.Timestamp, p.TotalGb - p.FreeGb)).ToList()),
                AllocatedGb = volFiles.Sum(f => f.SizeMb) / 1024,
                UsedInFilesGb = volFiles.Any(f => f.UsedMb != null) ? volFiles.Sum(f => f.UsedMb ?? f.SizeMb) / 1024 : null,
                DataGrowthGbPerDay = dataRates.Count > 0 ? dataRates.Sum() / 1024 : null,
                FileCount = volFiles.Count,
                Series = pts.Select(p => (p.Timestamp, p.TotalGb - p.FreeGb, p.TotalGb)).ToList()
            });
        }

        var databases = files.GroupBy(f => f.DatabaseName).Select(g =>
        {
            var fg = g.Select(f => growth.GetValueOrDefault((f.DatabaseId, f.FileId))).OfType<FileGrowth>().ToList();
            var usedRates = fg.Where(x => x.UsedMbPerDay != null).Select(x => x.UsedMbPerDay!.Value).ToList();
            return new DatabaseSpace
            {
                Database = g.Key, Files = g.Count(),
                DataGb = g.Where(f => !f.IsLog).Sum(f => f.SizeMb) / 1024,
                LogGb = g.Where(f => f.IsLog).Sum(f => f.SizeMb) / 1024,
                UsedGb = g.Any(f => f.UsedMb != null) ? g.Sum(f => f.UsedMb ?? f.SizeMb) / 1024 : null,
                AllocatedMbPerDay = fg.Sum(x => x.MbPerDay),
                UsedMbPerDay = usedRates.Count > 0 ? usedRates.Sum() : null,
                GrowthEvents = fg.Sum(x => x.Events),
                Volumes = string.Join(", ", g.Select(f => f.Volume).Distinct().Order())
            };
        }).OrderByDescending(d => d.AllocatedGb).ToList();

        return new SpaceAnalysis { Volumes = volumes, Databases = databases, Files = growth };
    }

    /// <summary>Least-squares slope in GB per day; null when there is less than two hours of data.</summary>
    public static double? Slope(IReadOnlyList<(DateTime T, double V)> pts)
    {
        if (pts.Count < 3 || (pts[^1].T - pts[0].T).TotalHours < 2) return null;
        var t0 = pts[0].T;
        double n = pts.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (t, v) in pts)
        {
            double x = (t - t0).TotalDays;
            sx += x; sy += v; sxx += x * x; sxy += x * v;
        }
        double den = n * sxx - sx * sx;
        return den == 0 ? null : (n * sxy - sx * sy) / den;
    }
}
