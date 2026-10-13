using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLDiskMonitor.Data;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services.Azure;

namespace SQLDiskMonitor.ViewModels;

public partial class DiskMatchRow : ObservableObject
{
    public required AzureDiskInfo Disk { get; init; }
    [ObservableProperty] private string _volume = AzureSyncViewModel.NotUsed;
    [ObservableProperty] private string _reason = "";
}

/// <summary>Sign in, choose the VM behind a SQL Server, confirm which Azure disk backs which volume, and apply.</summary>
public partial class AzureSyncViewModel : ObservableObject
{
    public const string NotUsed = "(not used)";

    private readonly Store _store;
    private readonly AppSettings _cfg;
    private readonly ServerConnection _server;
    private readonly IReadOnlyList<DiskConfig> _volumes;
    private readonly IReadOnlyDictionary<string, double> _volumeSizes;
    private AzureClient _client;
    private AzureVmDetail? _detail;

    public ObservableCollection<AzureVm> Vms { get; } = [];
    public ICollectionView VmView { get; }
    public ObservableCollection<DiskMatchRow> Rows { get; } = [];
    public List<string> VolumeOptions { get; }

    [ObservableProperty] private string _step = "Vm";          // Vm | Map
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _account = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private AzureVm? _selectedVm;
    [ObservableProperty] private string _resourceId = "";
    [ObservableProperty] private string _vmSummary = "";
    [ObservableProperty] private bool _setCosts;

    public string Currency => _cfg.CurrencySymbol;
    public string ServerName => _server.Name;
    public string HostName => _server.HostName;
    public bool IsVmStep => Step == "Vm";
    public bool IsMapStep => Step == "Map";
    public int ChangedVolumes { get; private set; }
    public event Action<bool>? Finished;

    partial void OnStepChanged(string value) { OnPropertyChanged(nameof(IsVmStep)); OnPropertyChanged(nameof(IsMapStep)); }
    partial void OnSearchChanged(string value) => VmView.Refresh();
    partial void OnSelectedVmChanged(AzureVm? value) { if (value != null) ResourceId = value.Id; }

    public AzureSyncViewModel(Store store, AppSettings cfg, ServerConnection server, IReadOnlyList<DiskConfig> volumes, IReadOnlyDictionary<string, double> volumeSizes)
    {
        _store = store; _cfg = cfg; _server = server; _volumes = volumes; _volumeSizes = volumeSizes;
        _client = new AzureClient(cfg);
        _setCosts = cfg.CostsEnabled;
        VolumeOptions = [NotUsed, .. volumes.Select(v => v.Volume)];
        ResourceId = server.Azure?.VmId ?? "";
        Account = cfg.AzureAuth == "AzureCli" ? "Using your Azure CLI sign-in (az login)" : AzureClient.SignedInAccount() is { } a ? $"Signed in as {a}" : "Not signed in";
        VmView = CollectionViewSource.GetDefaultView(Vms);
        VmView.Filter = o => o is AzureVm vm && (string.IsNullOrWhiteSpace(Search)
            || vm.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || vm.ComputerName.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || vm.ResourceGroup.Contains(Search, StringComparison.OrdinalIgnoreCase) || vm.SubscriptionName.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RunAsync(string busyText, Func<Task> action)
    {
        IsBusy = true;
        Status = busyText;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Status = ex is global::Azure.Identity.AuthenticationFailedException or global::Azure.Identity.CredentialUnavailableException
                ? $"Sign-in failed: {AzureClient.FriendlyError(ex)}"
                : AzureClient.FriendlyError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task SignIn() => RunAsync("Signing in to Azure…", async () =>
    {
        _client = new AzureClient(_cfg);
        var who = await _client.SignInAsync();
        Account = $"Signed in as {who}";
        Status = "";
        await LoadVmsCore();
    });

    [RelayCommand]
    private Task LoadVms() => RunAsync("Finding VMs you can read…", LoadVmsCore);

    private async Task LoadVmsCore()
    {
        var vms = await _client.SearchVmsAsync();
        Vms.Clear();
        foreach (var vm in vms) Vms.Add(vm);
        // Suggest the VM whose Windows computer name matches the SQL Server host.
        var host = _server.HostName;
        SelectedVm = Vms.FirstOrDefault(v => v.Id.Equals(_server.Azure?.VmId, StringComparison.OrdinalIgnoreCase))
                     ?? (string.IsNullOrEmpty(host) ? null
                         : Vms.FirstOrDefault(v => v.ComputerName.Equals(host, StringComparison.OrdinalIgnoreCase))
                           ?? Vms.FirstOrDefault(v => v.Name.Equals(host, StringComparison.OrdinalIgnoreCase)));
        Status = vms.Count == 0 ? "No VMs found. Check the account has Reader on the VM's subscription or resource group."
            : SelectedVm != null && !string.IsNullOrEmpty(host) ? $"{vms.Count} VMs found. {SelectedVm.Name} matches the SQL Server host {host}."
            : $"{vms.Count} VMs found.";
    }

    [RelayCommand]
    private Task LoadDisks() => RunAsync("Reading the VM's disks, limits and prices…", async () =>
    {
        var id = ResourceId.Trim();
        if (!id.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
        {
            // Accept a VM name as well as a full resource ID.
            if (Vms.Count == 0) await LoadVmsCore();
            var byName = Vms.Where(v => v.Name.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count != 1) throw new InvalidOperationException(byName.Count == 0
                ? $"No VM called '{id}'. Paste the full resource ID, or pick from the list."
                : $"{byName.Count} VMs are called '{id}'. Pick the right one from the list.");
            id = byName[0].Id;
        }

        var detail = await _client.GetVmDetailAsync(id);
        ShowMatches(detail, await AzureSync.BuildInfosAsync(detail, AzureClient.CurrencyCode(_cfg.CurrencySymbol)));
    });

    /// <summary>Shows the disk-to-volume matching step for a VM.</summary>
    public void ShowMatches(AzureVmDetail detail, IReadOnlyList<AzureDiskInfo> infos)
    {
        _detail = detail;
        Rows.Clear();
        foreach (var m in AzureSync.Suggest(infos, _volumes, _volumeSizes))
            Rows.Add(new DiskMatchRow { Disk = m.Disk, Volume = m.Volume ?? NotUsed, Reason = m.Reason });

        var vm = _detail.Vm;
        VmSummary = $"{vm.Name} · {vm.VmSize} · {vm.Location}\n" +
                    $"VM disk limit (uncached): {(_detail.VmMaxIops is { } i ? $"{i:N0} IOPS · {_detail.VmMaxMbps:N0} MB/s" : "unknown")} · " +
                    $"{_detail.DataDisksAttached}{(_detail.MaxDataDisks is { } md ? $" of {md}" : "")} data disks";
        Status = Rows.Any(r => r.Volume == NotUsed && !r.Disk.IsOsDisk)
            ? "Check the Volume column. Disks without a confident match are set to (not used). Choose the volume each one backs; give every disk in a storage pool the same volume."
            : "Check the matches, then Apply.";
        Step = "Map";
    }

    [RelayCommand]
    private void Back() => Step = "Vm";

    [RelayCommand]
    private void Apply()
    {
        if (_detail == null) return;
        int changed = 0;
        foreach (var group in Rows.Where(r => r.Volume != NotUsed).GroupBy(r => r.Volume))
        {
            var volume = _volumes.First(v => v.Volume == group.Key);
            var note = AzureSync.Apply(volume, group.Select(r => r.Disk).ToList(), SetCosts);
            _store.SaveDisk(volume);
            if (note != null) _store.AddNote(new DiskNote { ServerId = _server.Id, Volume = volume.Volume, Timestamp = DateTime.Now, Text = note });
            changed++;
        }

        var vm = _detail.Vm;
        _server.Azure = new AzureVmLink
        {
            VmId = vm.Id, Name = vm.Name, ResourceGroup = vm.ResourceGroup, SubscriptionId = vm.SubscriptionId, SubscriptionName = vm.SubscriptionName,
            Location = vm.Location, ComputerName = vm.ComputerName, VmSize = vm.VmSize, MaxIops = _detail.VmMaxIops, MaxMbps = _detail.VmMaxMbps,
            MaxDataDisks = _detail.MaxDataDisks, DataDisksAttached = _detail.DataDisksAttached, SyncedAt = DateTime.Now
        };
        _store.SaveServer(_server);
        ChangedVolumes = changed;
        Finished?.Invoke(true);
    }
}
