using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLDiskMonitor.Data;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services;

namespace SQLDiskMonitor.ViewModels;

public partial class InsightsViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public override string Title => "Bottlenecks";
    public override string Glyph => "";
    public override string Subtitle => "Ranked findings and recommendations for the selected period";

    public ObservableCollection<Finding> Findings { get; } = [];
    public ObservableCollection<Store.WaitStat> Waits { get; } = [];

    [ObservableProperty] private int _critical;
    [ObservableProperty] private int _high;
    [ObservableProperty] private int _medium;
    [ObservableProperty] private int _other;

    private AnalysisResult? _last;

    public override async Task RefreshAsync()
    {
        if (Server == null) return;
        var server = Server;
        var gen = ++Generation;
        IsBusy = true;
        var a = await Task.Run(() => Analytics.Analyse(Store, server.Id, Shell.RangeFrom, Shell.RangeTo, Cfg));
        IsBusy = false;
        if (server != Server || gen != Generation) return;
        _last = a;

        Findings.Clear();
        foreach (var f in a.Findings) Findings.Add(f);
        Waits.Clear();
        foreach (var w in a.Waits) Waits.Add(w);
        Critical = a.Findings.Count(f => f.Severity == Severity.Critical);
        High = a.Findings.Count(f => f.Severity == Severity.High);
        Medium = a.Findings.Count(f => f.Severity == Severity.Medium);
        Other = a.Findings.Count(f => f.Severity <= Severity.Low);
    }

    [RelayCommand]
    private void ExportReport() => Shell.OpenReport();
}
