using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLDiskMonitor.Services;

namespace SQLDiskMonitor.ViewModels;

public class DiskCostRow
{
    public string Volume { get; init; } = "";
    public string Label { get; init; } = "";
    public string Tier { get; init; } = "";
    public double Monthly { get; init; }
    public double Annual => Monthly * 12;
    public double UsedGb { get; init; }
    public double CostPerUsedGb => UsedGb > 0 ? Monthly / UsedGb : 0;
    public double P95Util { get; init; }
    public double IdleCost => Monthly * Math.Clamp(1 - P95Util / 100, 0, 1);
    public double CostPerMillionIo { get; init; }
    public bool HasLimits { get; init; }
    public double Share { get; init; }
}

public class DatabaseCostRow
{
    public string Database { get; init; } = "";
    public int Files { get; init; }
    public double SizeGb { get; init; }
    public double SizeShare { get; init; }
    public double IoShare { get; init; }
    public double ByCapacity { get; init; }
    public double ByIo { get; init; }
    public double Allocated { get; set; }
    public double BarShare { get; set; }
}

public partial class CostsViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public override string Title => "Costs";
    public override string Glyph => "";
    public override string Subtitle => "What the storage costs, what it's used for and what sits idle";

    public ObservableCollection<DiskCostRow> DiskRows { get; } = [];
    public ObservableCollection<DatabaseCostRow> DatabaseRows { get; } = [];
    public IReadOnlyList<string> AllocationModes { get; } = ["By capacity", "By IO"];

    [ObservableProperty] private string _allocation = "By capacity";
    [ObservableProperty] private double _totalMonthly;
    [ObservableProperty] private double _idleMonthly;
    [ObservableProperty] private double _avgUtil;
    [ObservableProperty] private bool _hasCosts;

    public string Currency => Cfg.CurrencySymbol;

    partial void OnAllocationChanged(string value) => ApplyAllocation();

    public override async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(Currency));
        if (Server == null) return;
        var server = Server;
        var gen = ++Generation;
        IsBusy = true;
        var (analysis, files) = await Task.Run(() =>
            (Analytics.Analyse(Store, server.Id, Shell.RangeFrom, Shell.RangeTo, Cfg), Store.GetFiles(server.Id)));
        var disks = Store.GetDisks(server.Id);
        IsBusy = false;
        if (server != Server || gen != Generation) return;

        DiskRows.Clear();
        double total = disks.Sum(d => d.MonthlyCost);
        foreach (var d in disks.OrderByDescending(d => d.MonthlyCost))
        {
            var s = analysis.Disks.FirstOrDefault(x => x.Volume == d.Volume);
            double util = s == null ? 0 : Math.Max(s.IopsUtilP95, s.MbpsUtilP95);
            // Average IOs/sec extrapolated to a 30-day month.
            double monthlyIos = (s?.AvgIops ?? 0) * 86400 * 30;
            DiskRows.Add(new DiskCostRow
            {
                Volume = d.Volume, Label = d.Label, Tier = d.Tier, Monthly = d.MonthlyCost,
                UsedGb = s != null ? s.TotalGb - s.FreeGb : 0, P95Util = util, HasLimits = d.HasLimits,
                CostPerMillionIo = monthlyIos > 0 ? d.MonthlyCost / (monthlyIos / 1e6) : 0,
                Share = total > 0 ? d.MonthlyCost / total : 0
            });
        }
        TotalMonthly = total;
        IdleMonthly = DiskRows.Where(r => r.HasLimits).Sum(r => r.IdleCost);
        var limited = DiskRows.Where(r => r.HasLimits && r.Monthly > 0).ToList();
        AvgUtil = limited.Count > 0 ? limited.Sum(r => r.P95Util * r.Monthly) / limited.Sum(r => r.Monthly) : 0;
        HasCosts = total > 0;

        // Each disk's cost is split across the databases on it, by allocated size or by observed IO.
        var stat = analysis.FileStats.ToDictionary(s => (s.DatabaseId, s.FileId));
        var costByVol = disks.ToDictionary(d => d.Volume, d => d.MonthlyCost, StringComparer.OrdinalIgnoreCase);
        var volSize = files.GroupBy(f => f.Volume).ToDictionary(g => g.Key, g => g.Sum(f => f.SizeMb));
        double Io(Models.DbFile f) => stat.TryGetValue((f.DatabaseId, f.FileId), out var s) ? s.ReadIops + s.WriteIops : 0;
        var volIo = files.GroupBy(f => f.Volume).ToDictionary(g => g.Key, g => g.Sum(Io));
        double totalSize = files.Sum(f => f.SizeMb), totalIo = files.Sum(Io);

        DatabaseRows.Clear();
        foreach (var g in files.GroupBy(f => f.DatabaseName))
        {
            double byCap = 0, byIo = 0;
            foreach (var f in g)
            {
                var cost = costByVol.GetValueOrDefault(f.Volume);
                if (volSize.GetValueOrDefault(f.Volume) > 0) byCap += cost * f.SizeMb / volSize[f.Volume];
                if (volIo.GetValueOrDefault(f.Volume) > 0) byIo += cost * Io(f) / volIo[f.Volume];
            }
            DatabaseRows.Add(new DatabaseCostRow
            {
                Database = g.Key, Files = g.Count(), SizeGb = g.Sum(f => f.SizeMb) / 1024,
                SizeShare = totalSize > 0 ? g.Sum(f => f.SizeMb) / totalSize : 0,
                IoShare = totalIo > 0 ? g.Sum(Io) / totalIo : 0,
                ByCapacity = byCap, ByIo = byIo
            });
        }
        ApplyAllocation();
    }

    private void ApplyAllocation()
    {
        var rows = DatabaseRows.ToList();
        foreach (var r in rows) r.Allocated = Allocation == "By IO" ? r.ByIo : r.ByCapacity;
        double max = rows.Count > 0 ? rows.Max(r => r.Allocated) : 0;
        foreach (var r in rows) r.BarShare = max > 0 ? r.Allocated / max : 0;
        DatabaseRows.Clear();
        foreach (var r in rows.OrderByDescending(r => r.Allocated)) DatabaseRows.Add(r);
    }
}
