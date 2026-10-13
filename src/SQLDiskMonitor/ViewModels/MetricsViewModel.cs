using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OxyPlot;
using SQLDiskMonitor.Controls;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services;

namespace SQLDiskMonitor.ViewModels;

public partial class SeriesOption : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public int ColorIndex { get; init; }
    public System.Windows.Media.Brush Brush => new System.Windows.Media.SolidColorBrush(Palette.Series(ColorIndex));
    [ObservableProperty] private bool _isSelected = true;
}

public partial class MetricsViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public override string Title => "Metrics";
    public override string Glyph => "";
    public override string Subtitle => "IOPS, throughput, latency and pressure over time";

    public ObservableCollection<SeriesOption> DiskOptions { get; } = [];
    public IReadOnlyList<string> GroupModes { get; } = ["Disk", "Database"];

    [ObservableProperty] private string _groupBy = "Disk";
    [ObservableProperty] private bool _splitReadWrite;
    [ObservableProperty] private bool _showLimits = true;
    [ObservableProperty] private bool _showNotes = true;

    [ObservableProperty] private PlotModel? _iopsModel;
    [ObservableProperty] private PlotModel? _throughputModel;
    [ObservableProperty] private PlotModel? _readLatencyModel;
    [ObservableProperty] private PlotModel? _writeLatencyModel;
    [ObservableProperty] private PlotModel? _pressureModel;
    [ObservableProperty] private PlotModel? _queueModel;
    [ObservableProperty] private string _summary = "";

    private List<VolumeSample> _series = [];
    private long _optionsFor = -1;
    private DateTime _lastLiveRefresh;

    partial void OnGroupByChanged(string value) => _ = RefreshAsync();
    partial void OnSplitReadWriteChanged(bool value) => _ = RefreshAsync();
    partial void OnShowLimitsChanged(bool value) => _ = RefreshAsync();
    partial void OnShowNotesChanged(bool value) => _ = RefreshAsync();

    private int BucketSeconds => (int)Math.Max(Cfg.SampleIntervalSeconds, Shell.SelectedRange.Span.TotalSeconds / 600);

    public override async Task RefreshAsync()
    {
        if (Server == null) return;
        var server = Server;
        var gen = ++Generation;
        var from = Shell.RangeFrom;
        var to = Shell.RangeTo;
        var bucket = BucketSeconds;
        IsBusy = true;

        var (series, disks, notes, dbSeries) = await Task.Run(() =>
        {
            var series = Store.GetVolumeSeries(server.Id, from, to, bucket);
            var disks = Store.GetDisks(server.Id);
            var notes = Store.GetNotes(server.Id);
            var db = GroupBy == "Database" ? Store.GetDatabaseSeries(server.Id, from, to, bucket, null) : [];
            return (series, disks, notes, db);
        });
        IsBusy = false;
        if (server != Server || gen != Generation) return;
        _series = series;

        if (_optionsFor != server.Id || DiskOptions.Count != disks.Count)
        {
            var deselected = _optionsFor == server.Id ? DiskOptions.Where(o => !o.IsSelected).Select(o => o.Key).ToHashSet() : [];
            _optionsFor = server.Id;
            foreach (var o in DiskOptions) o.PropertyChanged -= OptionChanged;
            DiskOptions.Clear();
            int i = 0;
            foreach (var d in disks)
            {
                var o = new SeriesOption { Key = d.Volume, Label = d.DisplayName, ColorIndex = i++, IsSelected = !deselected.Contains(d.Volume) };
                o.PropertyChanged += OptionChanged;
                DiskOptions.Add(o);
            }
        }

        var selected = DiskOptions.Where(o => o.IsSelected).ToList();
        var diskMap = disks.ToDictionary(d => d.Volume);
        var span = to - from;

        PlotModel Make(string title, string unit, double? max = null) { var m = Charts.TimeChart(title, unit, max); Charts.SetSpan(m, span); return m; }
        var iops = Make("IOPS", "IO/s");
        var mbps = Make("Throughput", "MB/s");
        var rlat = Make("Read latency", "ms");
        var wlat = Make("Write latency", "ms");
        var press = Make("Disk pressure score", "score", 100);
        var queue = Make("Pending IO requests", "requests");
        Charts.AddPressureBands(press);

        if (GroupBy == "Database")
        {
            var dbs = dbSeries.GroupBy(p => p.DatabaseName).OrderByDescending(g => g.Sum(p => p.Iops)).ToList();
            int ci = 0;
            foreach (var g in dbs.Take(10))
            {
                var pts = g.OrderBy(p => p.Time).ToList();
                iops.Series.Add(Charts.Line(g.Key, ci, pts.Select(p => (p.Time, p.Iops)), "IO/s"));
                mbps.Series.Add(Charts.Line(g.Key, ci, pts.Select(p => (p.Time, p.Mbps)), "MB/s"));
                rlat.Series.Add(Charts.Line(g.Key, ci, pts.Select(p => (p.Time, p.ReadLatency)), "ms"));
                wlat.Series.Add(Charts.Line(g.Key, ci, pts.Select(p => (p.Time, p.WriteLatency)), "ms"));
                ci++;
            }
        }
        else
        {
            foreach (var o in selected)
            {
                var pts = series.Where(s => s.Volume == o.Key).OrderBy(s => s.Timestamp).ToList();
                if (SplitReadWrite)
                {
                    iops.Series.Add(Charts.Line($"{o.Key} read", o.ColorIndex, pts.Select(p => (p.Timestamp, p.ReadIops)), "IO/s"));
                    iops.Series.Add(Dashed(Charts.Line($"{o.Key} write", o.ColorIndex, pts.Select(p => (p.Timestamp, p.WriteIops)), "IO/s")));
                    mbps.Series.Add(Charts.Line($"{o.Key} read", o.ColorIndex, pts.Select(p => (p.Timestamp, p.ReadMbps)), "MB/s"));
                    mbps.Series.Add(Dashed(Charts.Line($"{o.Key} write", o.ColorIndex, pts.Select(p => (p.Timestamp, p.WriteMbps)), "MB/s")));
                }
                else
                {
                    iops.Series.Add(Charts.Line(o.Key, o.ColorIndex, pts.Select(p => (p.Timestamp, p.TotalIops)), "IO/s"));
                    mbps.Series.Add(Charts.Line(o.Key, o.ColorIndex, pts.Select(p => (p.Timestamp, p.TotalMbps)), "MB/s"));
                }
                rlat.Series.Add(Charts.Line(o.Key, o.ColorIndex, pts.Select(p => (p.Timestamp, p.ReadLatencyMs)), "ms"));
                wlat.Series.Add(Charts.Line(o.Key, o.ColorIndex, pts.Select(p => (p.Timestamp, p.WriteLatencyMs)), "ms"));
                queue.Series.Add(Charts.Line(o.Key, o.ColorIndex, pts.Select(p => (p.Timestamp, p.PendingIo)), "requests"));

                if (ShowLimits && diskMap.TryGetValue(o.Key, out var d))
                {
                    var c = Charts.Series(o.ColorIndex);
                    if (d.MaxIops > 0) iops.Annotations.Add(Charts.Limit(d.MaxIops, $"{o.Key} max {d.MaxIops:N0}", c));
                    if (d.BurstIops > d.MaxIops) iops.Annotations.Add(Charts.Limit(d.BurstIops, $"{o.Key} burst {d.BurstIops:N0}", c, LineStyle.Dot));
                    if (d.MaxMbps > 0) mbps.Annotations.Add(Charts.Limit(d.MaxMbps, $"{o.Key} max {d.MaxMbps:N0}", c));
                    if (d.BurstMbps > d.MaxMbps) mbps.Annotations.Add(Charts.Limit(d.BurstMbps, $"{o.Key} burst {d.BurstMbps:N0}", c, LineStyle.Dot));
                }
            }
        }

        foreach (var o in selected)
        {
            var pts = series.Where(s => s.Volume == o.Key).OrderBy(s => s.Timestamp).ToList();
            press.Series.Add(Charts.Line(o.Key, o.ColorIndex, pts.Select(p => (p.Timestamp, p.Pressure)), ""));
        }

        if (ShowNotes)
        {
            var sel = selected.Select(s => s.Key).ToHashSet();
            var relevant = notes.Where(n => sel.Contains(n.Volume)).ToList();
            foreach (var m in new[] { iops, mbps, rlat, wlat, press, queue }) Charts.AddNotes(m, relevant, from, to, labels: m == iops);
        }

        IopsModel = iops;
        ThroughputModel = mbps;
        ReadLatencyModel = rlat;
        WriteLatencyModel = wlat;
        PressureModel = press;
        QueueModel = queue;

        var count = series.Select(s => s.Timestamp).Distinct().Count();
        Summary = count == 0
            ? "No samples in this range yet. Start collecting from the top bar."
            : $"{count:N0} points · {(bucket > Cfg.SampleIntervalSeconds ? $"averaged per {FormatBucket(bucket)}" : "raw samples")}";
    }

    private static OxyPlot.Series.LineSeries Dashed(OxyPlot.Series.LineSeries s)
    {
        s.LineStyle = LineStyle.Dash;
        return s;
    }

    private static string FormatBucket(int s) => s >= 3600 ? $"{s / 3600.0:0.#} h" : s >= 60 ? $"{s / 60.0:0.#} min" : $"{s} s";

    private bool _bulkChange;

    private void OptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_bulkChange) _ = RefreshAsync();
    }

    /// <summary>Changes several pills at once with a single redraw.</summary>
    private void SetSelection(Func<SeriesOption, bool> selected)
    {
        _bulkChange = true;
        try { foreach (var o in DiskOptions) o.IsSelected = selected(o); }
        finally { _bulkChange = false; }
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void Isolate(SeriesOption option) => SetSelection(o => o == option);

    [RelayCommand]
    private void HideDisk(SeriesOption option) => option.IsSelected = false;

    [RelayCommand]
    private void ShowAll() => SetSelection(_ => true);

    [RelayCommand]
    private void InvertSelection() => SetSelection(o => !o.IsSelected);

    public override void OnSample(SampleBatch batch)
    {
        // Live redraw for short ranges; long ranges barely move per sample.
        if (Shell.SelectedRange.Span > TimeSpan.FromHours(6)) return;
        if (DateTime.Now - _lastLiveRefresh < TimeSpan.FromSeconds(4)) return;
        _lastLiveRefresh = DateTime.Now;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (_series.Count == 0) { Shell.ShowToast("Nothing to export"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"{Sanitise(Server?.Name ?? "server")}_metrics_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder("timestamp,volume,read_iops,write_iops,read_mbps,write_mbps,read_latency_ms,write_latency_ms,pending_io,pressure,capacity_pct,latency_score,free_gb,total_gb\n");
        foreach (var s in _series.OrderBy(s => s.Timestamp).ThenBy(s => s.Volume))
            sb.AppendLine(string.Join(",", s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), Csv(s.Volume), F(s.ReadIops), F(s.WriteIops), F(s.ReadMbps), F(s.WriteMbps),
                F(s.ReadLatencyMs), F(s.WriteLatencyMs), F(s.PendingIo), F(s.Pressure), F(s.CapacityPct), F(s.LatencyScore), F(s.FreeGb), F(s.TotalGb)));
        File.WriteAllText(dlg.FileName, sb.ToString());
        Shell.ShowToast($"Exported {_series.Count:N0} rows");
    }

    internal static string F(double d) => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    internal static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    internal static string Sanitise(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '\\' ? '_' : c));
}
