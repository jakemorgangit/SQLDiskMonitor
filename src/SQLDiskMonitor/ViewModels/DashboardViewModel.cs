using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services;

namespace SQLDiskMonitor.ViewModels;

public partial class DiskCardViewModel : ObservableObject
{
    public required DiskConfig Config { get; init; }
    public string Volume => Config.Volume;
    public string Label => string.IsNullOrWhiteSpace(Config.Label) ? "Volume" : Config.Label;
    public string LunText => Config.Lun is { } l ? $"LUN {l}" : "";
    public string Tier => Config.Tier;
    public string Names => Config.NamesText;

    [ObservableProperty] private double _pressure;
    [ObservableProperty] private double _iops;
    [ObservableProperty] private double _mbps;
    [ObservableProperty] private double _readLatency;
    [ObservableProperty] private double _writeLatency;
    [ObservableProperty] private double _freeGb;
    [ObservableProperty] private double _totalGb;
    [ObservableProperty] private double _pending;
    [ObservableProperty] private IReadOnlyList<double> _spark = [];
    [ObservableProperty] private string _filesSummary = "";
    [ObservableProperty] private string _peakText = "";
    [ObservableProperty] private string _spaceTrend = "";

    public double UsedGb => TotalGb - FreeGb;
    public double WorstLatency => Math.Max(ReadLatency, WriteLatency);
    partial void OnReadLatencyChanged(double value) => OnPropertyChanged(nameof(WorstLatency));
    partial void OnWriteLatencyChanged(double value) => OnPropertyChanged(nameof(WorstLatency));
    public double UsedPct => TotalGb > 0 ? UsedGb / TotalGb * 100 : 0;
    public string IopsText => Config.MaxIops > 0 ? $"{Iops:N0} / {Config.MaxIops:N0}" : $"{Iops:N0}";
    public string MbpsText => Config.MaxMbps > 0 ? $"{Mbps:N1} / {Config.MaxMbps:N0}" : $"{Mbps:N1}";
    public string SpaceText => TotalGb > 0 ? $"{FreeGb:N0} GB free of {TotalGb:N0} GB" : "";

    partial void OnIopsChanged(double value) => OnPropertyChanged(nameof(IopsText));
    partial void OnMbpsChanged(double value) => OnPropertyChanged(nameof(MbpsText));
    partial void OnFreeGbChanged(double value) { OnPropertyChanged(nameof(UsedPct)); OnPropertyChanged(nameof(SpaceText)); OnPropertyChanged(nameof(UsedGb)); }
    partial void OnTotalGbChanged(double value) { OnPropertyChanged(nameof(UsedPct)); OnPropertyChanged(nameof(SpaceText)); OnPropertyChanged(nameof(UsedGb)); }
}

public partial class DashboardViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public override string Title => "Overview";
    public override string Glyph => "";
    public override string Subtitle => Server == null ? "" : $"{Server.Name}" + (string.IsNullOrEmpty(Server.HostName) ? "" : $" · host {Server.HostName}");
    public override bool UsesTimeRange => false;

    public ObservableCollection<DiskCardViewModel> Cards { get; } = [];
    public ObservableCollection<Finding> TopFindings { get; } = [];

    [ObservableProperty] private bool _hasServer;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private double _serverPressure;
    [ObservableProperty] private string _worstDisk = "";
    [ObservableProperty] private double _totalIops;
    [ObservableProperty] private double _totalMbps;
    [ObservableProperty] private double _worstLatency;
    [ObservableProperty] private int _disksAtRisk;
    [ObservableProperty] private double _monthlyCost;
    [ObservableProperty] private string _lastSampleText = "";
    [ObservableProperty] private int _databaseCount;
    [ObservableProperty] private int _fileCount;

    public IReadOnlyList<string> Layouts { get; } = ["Compact", "Normal", "List"];

    public string CardLayout
    {
        get => Cfg.DashboardLayout;
        set
        {
            if (value == null || value == Cfg.DashboardLayout) return;
            Cfg.DashboardLayout = value;
            Cfg.Save();
            OnPropertyChanged();
        }
    }

    public bool CostsEnabled => Cfg.CostsEnabled;
    public string Currency => Cfg.CurrencySymbol;

    public override async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(CostsEnabled));
        OnPropertyChanged(nameof(Currency));
        HasServer = Server != null;
        if (Server == null) { Cards.Clear(); TopFindings.Clear(); return; }

        var server = Server;

        var gen = ++Generation;
        var interval = Cfg.SampleIntervalSeconds;
        var (disks, files, recent, analysis) = await Task.Run(() =>
        {
            var disks = Store.GetDisks(server.Id);
            var files = Store.GetFiles(server.Id);
            var recent = Shell.Collector.Recent(server.Id).ToList();
            if (recent.Count == 0)
                recent = Store.GetVolumeSeries(server.Id, DateTime.Now.AddMinutes(-30), DateTime.Now, interval);
            var analysis = Analytics.Analyse(Store, server.Id, DateTime.Now.AddHours(-24), DateTime.Now, Cfg);
            return (disks, files, recent, analysis);
        });
        if (server != Server || gen != Generation) return;
        Cards.Clear();
        TopFindings.Clear();

        DatabaseCount = files.Select(f => f.DatabaseId).Distinct().Count();
        FileCount = files.Count;
        MonthlyCost = disks.Sum(d => d.MonthlyCost);

        foreach (var d in disks)
        {
            var volFiles = files.Where(f => f.Volume.Equals(d.Volume, StringComparison.OrdinalIgnoreCase)).ToList();
            if (volFiles.Count == 0 && !recent.Any(r => r.Volume == d.Volume)) continue;
            var stats = analysis.Disks.FirstOrDefault(s => s.Volume == d.Volume);
            Cards.Add(new DiskCardViewModel
            {
                Config = d,
                FilesSummary = $"{volFiles.Count} files · {volFiles.Select(f => f.DatabaseId).Distinct().Count()} databases · {volFiles.Sum(f => f.SizeMb) / 1024:N1} GB",
                PeakText = stats is { PeakHour: >= 0 } ? $"24h peak {stats.PeakPressure:N0} · busiest {stats.PeakHour:00}:00" : "",
                SpaceTrend = analysis.Space.Volumes.FirstOrDefault(v => v.Volume == d.Volume) is { GrowthGbPerDay: not null } sv
                    ? sv.DaysToFull != null ? $"{sv.GrowthText} · full in {sv.DaysToFullText}" : "Not growing" : "",
            });
        }
        ApplyRecent(recent);

        foreach (var f in analysis.Findings.Where(f => f.Severity >= Severity.Medium).Take(5)) TopFindings.Add(f);
        if (TopFindings.Count == 0)
            foreach (var f in analysis.Findings.Take(3)) TopFindings.Add(f);
    }

    public override void OnSample(SampleBatch batch) => ApplyRecent(Shell.Collector.Recent(batch.ServerId));

    private void ApplyRecent(IReadOnlyList<VolumeSample> recent)
    {
        HasData = recent.Count > 0;
        foreach (var card in Cards)
        {
            var series = recent.Where(r => r.Volume == card.Volume).OrderBy(r => r.Timestamp).ToList();
            if (series.Count == 0) continue;
            var last = series[^1];
            card.Pressure = last.Pressure;
            card.Iops = last.TotalIops;
            card.Mbps = last.TotalMbps;
            card.ReadLatency = last.ReadLatencyMs;
            card.WriteLatency = last.WriteLatencyMs;
            card.Pending = last.PendingIo;
            card.FreeGb = last.FreeGb;
            card.TotalGb = last.TotalGb;
            card.Spark = series.Select(s => s.Pressure).ToList();
        }

        if (Cards.Count > 0)
        {
            var worst = Cards.MaxBy(c => c.Pressure)!;
            ServerPressure = worst.Pressure;
            WorstDisk = worst.Volume;
            TotalIops = Cards.Sum(c => c.Iops);
            TotalMbps = Cards.Sum(c => c.Mbps);
            WorstLatency = Cards.Max(c => Math.Max(c.ReadLatency, c.WriteLatency));
            DisksAtRisk = Cards.Count(c => c.Pressure >= Pressure.High);
        }
        var latest = recent.Count > 0 ? recent.Max(r => r.Timestamp) : (DateTime?)null;
        LastSampleText = latest is { } t ? $"Last sample {t:HH:mm:ss}" : "No samples yet";
    }

    [RelayCommand]
    private void OpenDisk(DiskCardViewModel card) => Shell.NavigateToDisk(card.Volume);
}
