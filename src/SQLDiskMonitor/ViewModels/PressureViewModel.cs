using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using SQLDiskMonitor.Controls;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services;

namespace SQLDiskMonitor.ViewModels;

public record HeatCell(double Value, Brush Fill, Brush TextBrush, string Text, string Tip);
public record HeatRow(string Label, string Sublabel, IReadOnlyList<HeatCell> Cells);

public partial class PressureViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public override string Title => "Pressure";
    public override string Glyph => "";
    public override string Subtitle => "When and where each disk is under pressure";

    public ObservableCollection<HeatRow> HourRows { get; } = [];
    public ObservableCollection<HeatRow> WeekRows { get; } = [];
    public ObservableCollection<DiskStats> Stats { get; } = [];
    public ObservableCollection<string> Volumes { get; } = [];
    public IReadOnlyList<string> Hours { get; } = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList();
    public IReadOnlyList<string> Modes { get; } = ["Average", "Peak"];

    [ObservableProperty] private string _mode = "Average";
    [ObservableProperty] private string? _selectedVolume;
    [ObservableProperty] private PlotModel? _timeline;
    [ObservableProperty] private bool _hasData;

    private List<Data.Store.HourCell> _cells = [];
    private Dictionary<string, DiskConfig> _disks = [];

    partial void OnModeChanged(string value) => BuildHeatmaps();
    partial void OnSelectedVolumeChanged(string? value) => BuildWeek();

    public override async Task RefreshAsync()
    {
        if (Server == null) return;
        var server = Server;
        var gen = ++Generation;
        var from = Shell.RangeFrom;
        var to = Shell.RangeTo;
        IsBusy = true;
        var bucket = (int)Math.Max(Cfg.SampleIntervalSeconds, (to - from).TotalSeconds / 500);
        var (analysis, series, disks) = await Task.Run(() =>
            (Analytics.Analyse(Store, server.Id, from, to, Cfg), Store.GetVolumeSeries(server.Id, from, to, bucket), Store.GetDisks(server.Id)));
        IsBusy = false;
        if (server != Server || gen != Generation) return;

        _cells = analysis.Hourly;
        _disks = disks.ToDictionary(d => d.Volume);
        HasData = _cells.Count > 0;

        Stats.Clear();
        foreach (var s in analysis.Disks.OrderByDescending(s => s.P95Pressure)) Stats.Add(s);

        var keep = SelectedVolume;
        Volumes.Clear();
        foreach (var v in _cells.Select(c => c.Volume).Distinct().Order()) Volumes.Add(v);
        BuildHeatmaps();
        SelectedVolume = Volumes.Contains(keep ?? "") ? keep : Stats.FirstOrDefault()?.Volume ?? Volumes.FirstOrDefault();
        BuildWeek();

        var m = Charts.TimeChart("Pressure over time", "score", 100);
        Charts.SetSpan(m, to - from);
        Charts.AddPressureBands(m);
        int i = 0;
        foreach (var g in series.GroupBy(s => s.Volume).OrderBy(g => g.Key))
            m.Series.Add(Charts.Line(g.Key, i++, g.OrderBy(s => s.Timestamp).Select(s => (s.Timestamp, s.Pressure)), ""));
        Timeline = m;
    }

    private double Pick(Data.Store.HourCell c) => Mode == "Peak" ? c.MaxPressure : c.AvgPressure;

    private void BuildHeatmaps()
    {
        HourRows.Clear();
        foreach (var vol in Volumes)
        {
            var byHour = _cells.Where(c => c.Volume == vol).GroupBy(c => c.Hour).ToDictionary(g => g.Key, g => g.ToList());
            var cells = Enumerable.Range(0, 24).Select(h =>
            {
                if (!byHour.TryGetValue(h, out var list)) return Empty($"{vol} {h:00}:00 — no data");
                int n = list.Sum(c => c.Samples);
                double v = Mode == "Peak" ? list.Max(c => c.MaxPressure) : list.Sum(c => c.AvgPressure * c.Samples) / Math.Max(1, n);
                double iops = list.Sum(c => c.AvgIops * c.Samples) / Math.Max(1, n);
                double lat = list.Sum(c => c.AvgLatency * c.Samples) / Math.Max(1, n);
                return Cell(v, $"{vol}  {h:00}:00–{(h + 1) % 24:00}:00\n{Mode} pressure {v:N0} ({Pressure.Band(v)})\nAvg {iops:N0} IOPS · {lat:N1} ms\n{n:N0} samples");
            }).ToList();
            _disks.TryGetValue(vol, out var d);
            HourRows.Add(new HeatRow(vol, d?.Label ?? "", cells));
        }
    }

    private static readonly string[] Days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    private void BuildWeek()
    {
        WeekRows.Clear();
        if (SelectedVolume == null) return;
        var vol = _cells.Where(c => c.Volume == SelectedVolume).ToList();
        foreach (var dow in new[] { 1, 2, 3, 4, 5, 6, 0 })
        {
            var cells = Enumerable.Range(0, 24).Select(h =>
            {
                var c = vol.FirstOrDefault(x => x.DayOfWeek == dow && x.Hour == h);
                if (c == null) return Empty($"{Days[dow]} {h:00}:00 — no data");
                var v = Pick(c);
                return Cell(v, $"{Days[dow]} {h:00}:00–{(h + 1) % 24:00}:00\n{Mode} pressure {v:N0} ({Pressure.Band(v)})\nAvg {c.AvgIops:N0} IOPS · {c.AvgLatency:N1} ms");
            }).ToList();
            WeekRows.Add(new HeatRow(Days[dow], "", cells));
        }
    }

    private static HeatCell Empty(string tip) => new(-1, Palette.Brush("Surface2"), Palette.Brush("TextSubtle"), "", tip);

    private static HeatCell Cell(double v, string tip)
    {
        var c = Palette.ColorForPressure(v);
        // Low scores fade towards the background so hot spots stand out.
        byte alpha = (byte)Math.Clamp(55 + v * 2.0, 55, 255);
        var fill = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        fill.Freeze();
        var text = v >= 55 ? Palette.Brush("OnAccent") : Palette.Brush("Text");
        if (!ThemeService.IsDark) text = v >= 55 ? Brushes.White : Palette.Brush("Text");
        return new HeatCell(v, fill, text, v.ToString("0"), tip);
    }
}
