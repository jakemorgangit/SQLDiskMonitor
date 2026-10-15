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
        OnPropertyChanged(nameof(AzureVmText));
        if (Server == null) { DiskList.Clear(); return; }
        var server = Server;
        var gen = ++Generation;
        var keep = PendingVolume ?? SelectedDisk?.Volume;

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
        // Cleared only once applied, so an overlapping refresh can't lose the requested disk.
        PendingVolume = null;
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
    private void CopyLunScript() => Services.Azure.LunScript.CopyWithInstructions(System.Windows.Application.Current.MainWindow);

    [RelayCommand]
    private async Task SyncAzure()
    {
        if (Server == null || DiskList.Count == 0) { Shell.ShowToast("No disks yet. Collect from the server first."); return; }
        if (Server.IsDemo) { Shell.ShowToast("Azure sync isn't available for the demo server."); return; }
        if (IsDirty) SaveAll();

        // Current volume sizes from recent samples help match Azure disks to volumes by size.
        var server = Server;
        var sizes = await Task.Run(() => Store.GetVolumeSeries(server.Id, DateTime.Now.AddDays(-2), DateTime.Now, 3600)
            .GroupBy(s => s.Volume).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Timestamp).Last().TotalGb));
        foreach (var d in DiskList) if (!sizes.ContainsKey(d.Volume) && d.SizeGb > 0) sizes[d.Volume] = d.SizeGb;

        var vm = new AzureSyncViewModel(Store, Cfg, server, DiskList, sizes);
        var win = new Views.AzureSyncWindow(vm) { Owner = System.Windows.Application.Current.MainWindow };
        if (win.ShowDialog() == true)
        {
            Shell.ShowToast($"Updated {vm.ChangedVolumes} volume(s) from Azure VM {server.Azure?.Name}");
            await RefreshAsync();
        }
    }

    public string AzureVmText => Server?.Azure is { } a
        ? $"{a.Name} · {a.VmSize} · VM disk limit {a.CapText} · {a.SlotsText} · synced {a.SyncedAt:dd MMM HH:mm}" : "";

    [RelayCommand]
    private void OpenInPortal(Services.Azure.AzureDiskInfo disk) =>
        MainViewModel.OpenUrl($"https://portal.azure.com/#@/resource{disk.Id}");

    [RelayCommand]
    private void SaveCsvTemplate()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            Title = "Save disk settings template",
            FileName = $"{MetricsViewModel.Sanitise(Server?.Name ?? "server")}_disk_settings_template.csv"
        };
        if (dlg.ShowDialog() != true) return;
        System.IO.File.WriteAllText(dlg.FileName, DiskCsvImport.BuildTemplate(DiskList), new System.Text.UTF8Encoding(true));
        Shell.ShowToast("Template saved. Fill in the blanks, then use Import CSV.");
    }

    [RelayCommand]
    private void ImportCsv()
    {
        if (Server == null || DiskList.Count == 0) { Shell.ShowToast("No disks to import into yet"); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "CSV files (*.csv;*.txt)|*.csv;*.txt|All files (*.*)|*.*", Title = "Import disk settings" };
        if (dlg.ShowDialog() != true) return;

        string text;
        try
        {
            // Allow reading a file that is still open in Excel.
            using var fs = new System.IO.FileStream(dlg.FileName, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            using var reader = new System.IO.StreamReader(fs);
            text = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not read the file:\n\n{ex.Message}", "Import disk settings", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var r = DiskCsvImport.Apply(text, DiskList);
        foreach (var d in r.Changed) Store.SaveDisk(d);
        IsDirty = false;
        OnSelectedDiskChanged(SelectedDisk);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(r.FieldsFilled > 0
            ? $"Filled {r.FieldsFilled} empty field(s) on {r.Changed.Count} disk(s). Existing values were left unchanged."
            : r.RowsMatched > 0 ? "Matched rows found, but every field they contain is already filled in. Nothing changed." : "No rows matched a disk on this server.");
        if (r.Details.Count > 0) { sb.AppendLine(); foreach (var line in r.Details) sb.AppendLine("  " + line); }
        if (r.UnmatchedRows.Count > 0) { sb.AppendLine(); sb.AppendLine($"No matching disk for: {string.Join(", ", r.UnmatchedRows)}"); }
        if (r.RecognisedColumns.Count == 0) { sb.AppendLine(); sb.AppendLine("No recognised columns. Use headers such as Label, Drive name, Disk name, LUN, Tier, Max IOPS, Max MB/s, Size GB, Tags, Notes."); }
        else if (r.IgnoredColumns.Count > 0) { sb.AppendLine(); sb.AppendLine($"Ignored columns: {string.Join(", ", r.IgnoredColumns)}"); }
        System.Windows.MessageBox.Show(sb.ToString(), "Import disk settings", System.Windows.MessageBoxButton.OK,
            r.FieldsFilled > 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
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
        var sb = new System.Text.StringBuilder("server,volume,label,drive_name,disk_name,lun,disk_number,device_model,tier,max_iops,max_mbps,burst_iops,burst_mbps,size_gb,monthly_cost,tags,notes\n");
        foreach (var d in DiskList)
            sb.AppendLine(string.Join(",", C(Server?.Name ?? ""), C(d.Volume), C(d.Label), C(d.DriveName), C(d.DiskName), d.Lun, d.DiskNumber, C(d.DeviceModel), C(d.Tier), F(d.MaxIops), F(d.MaxMbps),
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
            DetectStatus = $"WMI query failed: {ex.Message} Remote WMI (usually local admin) isn't available to {host} from here. Use Copy LUN script for the server instead, or enter LUNs by hand.";
        }
    }
}
