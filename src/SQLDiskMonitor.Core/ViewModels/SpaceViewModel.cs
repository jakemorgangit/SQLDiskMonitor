using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using SQLDiskMonitor.Controls;
using SQLDiskMonitor.Services;

namespace SQLDiskMonitor.ViewModels;

public class GrowingFile
{
    public string Database { get; init; } = "";
    public string File { get; init; } = "";
    public string Type { get; init; } = "";
    public string Volume { get; init; } = "";
    public double SizeMb { get; init; }
    public double? UsedMb { get; init; }
    public double? UsedPct { get; init; }
    public double MbPerDay { get; init; }
    public double? UsedMbPerDay { get; init; }
    public int Events { get; init; }
    public string Growth { get; init; } = "";
}

public partial class SpaceViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public override string Title => "Space";
    public override string Glyph => "";
    public override string Subtitle => "Capacity, used and free space, and how fast it's growing";

    public ObservableCollection<VolumeSpace> Volumes { get; } = [];
    public ObservableCollection<DatabaseSpace> Databases { get; } = [];
    public ObservableCollection<GrowingFile> GrowingFiles { get; } = [];

    [ObservableProperty] private double _totalGb;
    [ObservableProperty] private double _usedGb;
    [ObservableProperty] private double _freeGb;
    [ObservableProperty] private double _usedPct;
    [ObservableProperty] private string _growthText = "—";
    [ObservableProperty] private string _forecastText = "No volume is growing";
    [ObservableProperty] private string _forecastDetail = "";
    [ObservableProperty] private PlotModel? _usedPctModel;
    [ObservableProperty] private PlotModel? _usedGbModel;
    [ObservableProperty] private bool _hasFileSpace;

    public override async Task RefreshAsync()
    {
        if (Server == null) return;
        var server = Server;
        var gen = ++Generation;
        var from = Shell.RangeFrom;
        var to = Shell.RangeTo;
        IsBusy = true;
        var (space, files) = await Task.Run(() => (SpaceAnalytics.Analyse(Store, server.Id, from, to), Store.GetFiles(server.Id)));
        IsBusy = false;
        if (server != Server || gen != Generation) return;

        TotalGb = space.TotalGb;
        UsedGb = space.UsedGb;
        FreeGb = space.FreeGb;
        UsedPct = TotalGb > 0 ? UsedGb / TotalGb * 100 : 0;
        GrowthText = space.GrowthGbPerDay is { } g ? $"{(g >= 0 ? "+" : "")}{g:N2} GB/day" : "Needs 2h+ of data";
        if (space.FirstToFill is { } first)
        {
            ForecastText = first.DaysToFullText;
            ForecastDetail = $"{first.Volume} {first.Label} fills first at {first.GrowthText}";
        }
        else
        {
            ForecastText = "Not growing";
            ForecastDetail = "No volume shows a rising trend in this period";
        }

        Volumes.Clear();
        foreach (var v in space.Volumes) Volumes.Add(v);
        Databases.Clear();
        foreach (var d in space.Databases) Databases.Add(d);

        GrowingFiles.Clear();
        HasFileSpace = files.Any(f => f.UsedMb != null);
        foreach (var f in files
                     .Select(f => (f, g: space.Files.GetValueOrDefault((f.DatabaseId, f.FileId))))
                     .OrderByDescending(x => x.g?.MbPerDay ?? 0).ThenByDescending(x => x.f.SizeMb).Take(25))
        {
            GrowingFiles.Add(new GrowingFile
            {
                Database = f.f.DatabaseName, File = f.f.LogicalName, Type = f.f.TypeDesc, Volume = f.f.Volume,
                SizeMb = f.f.SizeMb, UsedMb = f.f.UsedMb, UsedPct = f.f.UsedPct, Growth = f.f.GrowthDisplay,
                MbPerDay = f.g?.MbPerDay ?? 0, UsedMbPerDay = f.g?.UsedMbPerDay, Events = f.g?.Events ?? 0
            });
        }

        var span = to - from;
        var pct = Charts.TimeChart("Used space", "% of volume", 100, "0");
        var gb = Charts.TimeChart("Used space", "GB", null, "#,0");
        Charts.SetSpan(pct, span);
        Charts.SetSpan(gb, span);
        int i = 0;
        foreach (var v in space.Volumes)
        {
            pct.Series.Add(Charts.Line(v.Volume, i, v.Series.Select(p => (p.T, p.TotalGb > 0 ? p.UsedGb / p.TotalGb * 100 : 0)), "%"));
            gb.Series.Add(Charts.Line(v.Volume, i, v.Series.Select(p => (p.T, p.UsedGb)), "GB"));
            i++;
        }
        pct.Annotations.Add(Charts.Limit(90, "90%", Charts.Theme("Crit", 160)));
        UsedPctModel = pct;
        UsedGbModel = gb;
    }
}
