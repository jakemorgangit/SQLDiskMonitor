using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services;

namespace SQLDiskMonitor.ViewModels;

public partial class DisksViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public override string Title => "Disks";
    public override string Glyph => "";
    public override string Subtitle => "Performance limits, LUNs, costs and notes for each volume";

    public ObservableCollection<DiskConfig> DiskList { get; } = [];
    public ObservableCollection<DiskNote> Notes { get; } = [];
    public ObservableCollection<DbFile> FilesOnDisk { get; } = [];
    public IReadOnlyList<DiskPreset> Presets => DiskPresets.All;

    [ObservableProperty] private DiskConfig? _selectedDisk;
    [ObservableProperty] private DiskPreset? _selectedPreset;
    [ObservableProperty] private string _newNote = "";
    [ObservableProperty] private DiskStats? _observed;
    [ObservableProperty] private string _detectStatus = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _viewMode = "Details";

    public IReadOnlyList<string> ViewModes { get; } = ["Details", "Inventory"];
    public bool IsDetails => ViewMode == "Details";
    partial void OnViewModeChanged(string value) => OnPropertyChanged(nameof(IsDetails));

    public string? PendingVolume { get; set; }
    public bool CostsEnabled => Cfg.CostsEnabled;
    public string Currency => Cfg.CurrencySymbol;
    public string RangeLabel => Shell.SelectedRange.Label;

    private List<DbFile> _files = [];
    private AnalysisResult? _analysis;

    public override async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(CostsEnabled));
        OnPropertyChanged(nameof(Currency));
        OnPropertyChanged(nameof(RangeLabel));
        if (Server == null) { DiskList.Clear(); return; }
        var server = Server;
        var gen = ++Generation;
        var keep = PendingVolume ?? SelectedDisk?.Volume;
        PendingVolume = null;

        var (disks, files, analysis) = await Task.Run(() =>
            (Store.GetDisks(server.Id), Store.GetFiles(server.Id), Analytics.Analyse(Store, server.Id, Shell.RangeFrom, Shell.RangeTo, Cfg)));
        if (server != Server || gen != Generation) return;

        _files = files;
        _analysis = analysis;
        foreach (var d in DiskList) d.PropertyChanged -= DiskEdited;
        DiskList.Clear();
        foreach (var d in disks)
        {
            d.PropertyChanged += DiskEdited;
            DiskList.Add(d);
        }
        SelectedDisk = DiskList.FirstOrDefault(d => d.Volume == keep) ?? DiskList.FirstOrDefault();
        IsDirty = false;
    }

    partial void OnSelectedDiskChanged(DiskConfig? value)
    {
        Notes.Clear();
        FilesOnDisk.Clear();
        Observed = null;
        SelectedPreset = null;
        if (value == null || Server == null) return;
        foreach (var n in Store.GetNotes(Server.Id, value.Volume)) Notes.Add(n);
        foreach (var f in _files.Where(f => f.Volume.Equals(value.Volume, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.DatabaseName).ThenBy(f => f.FileId))
            FilesOnDisk.Add(f);
        Observed = _analysis?.Disks.FirstOrDefault(d => d.Volume == value.Volume);
        SelectedPreset = DiskPresets.All.FirstOrDefault(p => p.Name == value.Tier);
    }

    private void DiskEdited(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => IsDirty = true;

    [RelayCommand]
    private void ApplyPreset()
    {
        if (SelectedDisk == null || SelectedPreset == null) return;
        var p = SelectedPreset;
        SelectedDisk.Tier = p.Name;
        if (p.Iops > 0) SelectedDisk.MaxIops = p.Iops;
        if (p.Mbps > 0) SelectedDisk.MaxMbps = p.Mbps;
        SelectedDisk.BurstIops = p.BurstIops;
        SelectedDisk.BurstMbps = p.BurstMbps;
        if (p.SizeGb > 0) SelectedDisk.SizeGb = p.SizeGb;
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedDisk == null) return;
        Store.SaveDisk(SelectedDisk);
        IsDirty = false;
        Shell.ShowToast($"Saved {SelectedDisk.Volume}");
    }

    [RelayCommand]
    private void SaveAll()
    {
        foreach (var d in DiskList) Store.SaveDisk(d);
        IsDirty = false;
        Shell.ShowToast("Saved all disks");
    }

    [RelayCommand]
    private void ExportInventory()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"{MetricsViewModel.Sanitise(Server?.Name ?? "server")}_disks_{DateTime.Now:yyyyMMdd}.csv"
        };
        if (dlg.ShowDialog() != true) return;
        static string C(string s) => MetricsViewModel.Csv(s);
        static string F(double d) => MetricsViewModel.F(d);
        var sb = new System.Text.StringBuilder("server,volume,label,lun,disk_number,device_model,tier,max_iops,max_mbps,burst_iops,burst_mbps,size_gb,monthly_cost,tags,notes\n");
        foreach (var d in DiskList)
            sb.AppendLine(string.Join(",", C(Server?.Name ?? ""), C(d.Volume), C(d.Label), d.Lun, d.DiskNumber, C(d.DeviceModel), C(d.Tier), F(d.MaxIops), F(d.MaxMbps),
                F(d.BurstIops), F(d.BurstMbps), F(d.SizeGb), F(d.MonthlyCost), C(d.Tags), C(d.Notes.ReplaceLineEndings(" "))));
        System.IO.File.WriteAllText(dlg.FileName, sb.ToString());
        Shell.ShowToast("Exported disk inventory");
    }

    [RelayCommand]
    private void AddNote()
    {
        if (SelectedDisk == null || Server == null || string.IsNullOrWhiteSpace(NewNote)) return;
        var n = new DiskNote { ServerId = Server.Id, Volume = SelectedDisk.Volume, Timestamp = DateTime.Now, Text = NewNote.Trim() };
        Store.AddNote(n);
        Notes.Insert(0, n);
        NewNote = "";
    }

    [RelayCommand]
    private void DeleteNote(DiskNote note)
    {
        Store.DeleteNote(note.Id);
        Notes.Remove(note);
    }

    [RelayCommand]
    private async Task DetectLuns()
    {
        if (Server == null) return;
        if (Server.IsDemo) { DetectStatus = "LUN detection isn't available for the demo server."; return; }
        var host = string.IsNullOrWhiteSpace(Server.HostName) ? Server.Address.Split('\\', ',')[0] : Server.HostName;
        DetectStatus = $"Querying WMI on {host}…";
        try
        {
            var detected = await LunDetector.DetectAsync(host);
            int matched = 0;
            foreach (var d in DiskList)
            {
                // Longest matching access path wins so mount points beat their parent drive letter.
                var hit = detected.Where(x => d.Volume.StartsWith(x.AccessPath, StringComparison.OrdinalIgnoreCase))
                                  .OrderByDescending(x => x.AccessPath.Length).FirstOrDefault();
                if (hit == null) continue;
                d.Lun = hit.Lun;
                d.DiskNumber = hit.DiskNumber;
                d.DeviceModel = hit.Model;
                if (d.SizeGb <= 0) d.SizeGb = Math.Round(hit.SizeGb, 1);
                Store.SaveDisk(d);
                matched++;
            }
            DetectStatus = $"Matched {matched} of {DiskList.Count} volumes to disks on {host}.";
            IsDirty = false;
        }
        catch (Exception ex)
        {
            DetectStatus = $"WMI query failed: {ex.Message} You need remote WMI access (usually local admin) on {host}. LUNs can still be entered manually.";
        }
    }
}
