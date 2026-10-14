using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLDiskMonitor.Models;
using static SQLDiskMonitor.ViewModels.MetricsViewModel;

namespace SQLDiskMonitor.ViewModels;

public class FileRow
{
    public required DbFile File { get; init; }
    public string Database => File.DatabaseName;
    public string Logical => File.LogicalName;
    public string Type => File.TypeDesc;
    public string Volume => File.Volume;
    public string Disk { get; init; } = "";
    public string Lun { get; init; } = "";
    public double SizeGb => File.SizeMb / 1024;
    public double? UsedGb => File.UsedMb / 1024;
    public double? UsedPct => File.UsedPct;
    public string Growth => File.GrowthDisplay;
    public string MaxSize => File.MaxSizeDisplay;
    public string Path => File.PhysicalName;
    public double ReadIops { get; init; }
    public double WriteIops { get; init; }
    public double Mbps { get; init; }
    public double ReadLatency { get; init; }
    public double WriteLatency { get; init; }
    public double StallShare { get; init; }
    public double DiskIoShare { get; init; }
}

public partial class FilesViewModel : PageViewModel
{
    public override string Title => "Files";
    public override string Glyph => "";
    public override string Subtitle => "Every database file, the disk it lives on and the IO it drives";

    public ObservableCollection<FileRow> Rows { get; } = [];
    public ObservableCollection<string> VolumeFilter { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _selectedVolume = "All disks";
    [ObservableProperty] private string _totals = "";

    public FilesViewModel(MainViewModel shell) : base(shell)
    {
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is FileRow r
            && (SelectedVolume == "All disks" || r.Volume == SelectedVolume)
            && (string.IsNullOrWhiteSpace(Search) || r.Database.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || r.Logical.Contains(Search, StringComparison.OrdinalIgnoreCase) || r.Path.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnSelectedVolumeChanged(string value) => View.Refresh();

    public override async Task RefreshAsync()
    {
        if (Server == null) { Rows.Clear(); return; }
        var server = Server;
        var gen = ++Generation;
        IsBusy = true;
        var (files, stats, disks) = await Task.Run(() =>
            (Store.GetFiles(server.Id), Store.GetFileStats(server.Id, Shell.RangeFrom, Shell.RangeTo), Store.GetDisks(server.Id)));
        IsBusy = false;
        if (server != Server || gen != Generation) return;

        var diskMap = disks.ToDictionary(d => d.Volume, StringComparer.OrdinalIgnoreCase);
        var statMap = stats.ToDictionary(s => (s.DatabaseId, s.FileId));
        double totalStall = stats.Sum(s => s.StallMsPerSec);
        var volIops = files.GroupBy(f => f.Volume).ToDictionary(g => g.Key,
            g => g.Sum(f => statMap.TryGetValue((f.DatabaseId, f.FileId), out var s) ? s.ReadIops + s.WriteIops : 0));

        Rows.Clear();
        foreach (var f in files)
        {
            statMap.TryGetValue((f.DatabaseId, f.FileId), out var s);
            diskMap.TryGetValue(f.Volume, out var d);
            double iops = (s?.ReadIops ?? 0) + (s?.WriteIops ?? 0);
            Rows.Add(new FileRow
            {
                File = f,
                Disk = d?.Label ?? "",
                Lun = d?.Lun?.ToString() ?? "",
                ReadIops = s?.ReadIops ?? 0,
                WriteIops = s?.WriteIops ?? 0,
                Mbps = (s?.ReadMbps ?? 0) + (s?.WriteMbps ?? 0),
                ReadLatency = s?.ReadLatency ?? 0,
                WriteLatency = s?.WriteLatency ?? 0,
                StallShare = totalStall > 0 ? (s?.StallMsPerSec ?? 0) / totalStall : 0,
                DiskIoShare = volIops.GetValueOrDefault(f.Volume) > 0 ? iops / volIops[f.Volume] : 0,
            });
        }

        var keep = SelectedVolume;
        VolumeFilter.Clear();
        VolumeFilter.Add("All disks");
        foreach (var v in files.Select(f => f.Volume).Distinct().Order()) VolumeFilter.Add(v);
        SelectedVolume = VolumeFilter.Contains(keep) ? keep : "All disks";
        Totals = $"{files.Count} files · {files.Select(f => f.DatabaseId).Distinct().Count()} databases · {files.Sum(f => f.SizeMb) / 1024:N1} GB allocated";
        View.Refresh();
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"{Sanitise(Server?.Name ?? "server")}_files_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder("database,logical_name,type,volume,disk_label,lun,size_gb,growth,max_size,avg_read_iops,avg_write_iops,avg_mbps,read_latency_ms,write_latency_ms,io_stall_share,disk_io_share,path\n");
        foreach (var r in View.Cast<FileRow>())
            sb.AppendLine(string.Join(",", Csv(r.Database), Csv(r.Logical), r.Type, Csv(r.Volume), Csv(r.Disk), r.Lun, F(r.SizeGb), Csv(r.Growth), Csv(r.MaxSize),
                F(r.ReadIops), F(r.WriteIops), F(r.Mbps), F(r.ReadLatency), F(r.WriteLatency), F(r.StallShare), F(r.DiskIoShare), Csv(r.Path)));
        File.WriteAllText(dlg.FileName, sb.ToString());
        Shell.ShowToast("Exported file list");
    }
}
