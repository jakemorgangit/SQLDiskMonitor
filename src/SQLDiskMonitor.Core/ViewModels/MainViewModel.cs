using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLDiskMonitor.Data;
using SQLDiskMonitor.Hosting;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services;

namespace SQLDiskMonitor.ViewModels;

public abstract partial class PageViewModel(MainViewModel shell) : ObservableObject
{
    public MainViewModel Shell { get; } = shell;
    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public virtual string Subtitle => "";
    public virtual bool UsesTimeRange => true;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Bumped by each refresh so a slower, older refresh can tell it has been superseded.</summary>
    protected int Generation;

    public virtual Task RefreshAsync() => Task.CompletedTask;
    public virtual void OnSample(SampleBatch batch) { }

    protected ServerConnection? Server => Shell.SelectedServer;
    protected Store Store => Shell.Store;
    protected AppSettings Cfg => Shell.Settings;
}

public partial class MainViewModel : ObservableObject, IDisposable
{
    public Store Store { get; }
    public Collector Collector { get; }
    public AppSettings Settings { get; }

    public ObservableCollection<ServerConnection> Servers { get; } = [];
    public ObservableCollection<PageViewModel> Pages { get; } = [];

    public IReadOnlyList<TimeRange> TimeRanges { get; } =
    [
        new("15m", TimeSpan.FromMinutes(15)), new("1h", TimeSpan.FromHours(1)), new("6h", TimeSpan.FromHours(6)),
        new("24h", TimeSpan.FromHours(24)), new("7d", TimeSpan.FromDays(7)), new("30d", TimeSpan.FromDays(30))
    ];

    [ObservableProperty] private ServerConnection? _selectedServer;
    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private TimeRange _selectedRange;
    [ObservableProperty] private ReleaseInfo? _availableUpdate;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private string _toast = "";

    public DashboardViewModel Dashboard { get; }
    public DisksViewModel Disks { get; }
    public MetricsViewModel Metrics { get; }
    public PressureViewModel PressurePage { get; }
    public SpaceViewModel Space { get; }
    public FilesViewModel Files { get; }
    public InsightsViewModel Insights { get; }
    public CostsViewModel Costs { get; }
    public SettingsViewModel SettingsPage { get; }

    public string VersionText => $"v{UpdateService.CurrentVersion.ToString(3)}";

    /// <summary>The app that hosts this view (for example Blackcat Studio), or null when running standalone.</summary>
    public IDiskMonitorHost? Host { get; }
    /// <summary>Hosted: the host owns connections, credentials, theme, updates and start-up.</summary>
    public bool IsHosted => Host != null;
    /// <summary>Standalone only: hosted connections are edited in the host.</summary>
    public bool CanEditSelectedServer => !IsHosted && SelectedServer?.IsExternal != true;
    public string AddServerToolTip => IsHosted ? "Monitor another server connected in the host" : "Add a SQL Server";

    public DateTime RangeTo => DateTime.Now;
    public DateTime RangeFrom => DateTime.Now - SelectedRange.Span;

    private CancellationTokenSource? _toastCts;

    public MainViewModel(Store store, AppSettings settings) : this(store, settings, null, null) { }

    /// <summary>
    /// <paramref name="collector"/> and <paramref name="host"/> are given when another app hosts SQL Disk Monitor
    /// (see <see cref="DiskMonitorRuntime"/>): the collector is shared between views and only servers attached with
    /// <see cref="AttachHostedServer"/> are listed.
    /// </summary>
    public MainViewModel(Store store, AppSettings settings, Collector? collector, IDiskMonitorHost? host)
    {
        Store = store;
        Settings = settings;
        Host = host;
        Collector = collector ?? new Collector(store, () => Settings);
        _selectedRange = TimeRanges[3];

        Dashboard = new DashboardViewModel(this);
        Disks = new DisksViewModel(this);
        Metrics = new MetricsViewModel(this);
        PressurePage = new PressureViewModel(this);
        Space = new SpaceViewModel(this);
        Files = new FilesViewModel(this);
        Insights = new InsightsViewModel(this);
        Costs = new CostsViewModel(this);
        SettingsPage = new SettingsViewModel(this);
        foreach (var p in new PageViewModel[] { Dashboard, Disks, Metrics, PressurePage, Space, Files, Insights, Costs, SettingsPage })
            Pages.Add(p);
        Costs.IsVisible = settings.CostsEnabled;

        Collector.SampleCollected += OnSampleCollected;
        ThemeService.Changed += OnThemeChanged;

        if (!IsHosted)
        {
            foreach (var s in store.GetServers()) Servers.Add(s);
            SelectedServer = Servers.FirstOrDefault(s => s.Id == settings.LastServerId) ?? Servers.FirstOrDefault();
        }
        CurrentPage = Dashboard;
    }

    private void OnSampleCollected(SampleBatch b) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        if (!_disposed && b.ServerId == SelectedServer?.Id) CurrentPage?.OnSample(b);
    });

    private void OnThemeChanged() => _ = CurrentPage?.RefreshAsync();

    // ───────────────────────── Hosted mode ─────────────────────────

    private readonly List<long> _attached = [];
    private bool _disposed;

    /// <summary>Hosted: shows a server owned by the host, using its saved disk settings and history, and starts collecting.</summary>
    public ServerConnection AttachHostedServer(HostedServer hosted, bool select = true)
    {
        var existing = Servers.FirstOrDefault(s => DiskMonitorRuntime.SameServer(s, hosted.Server));
        if (existing != null)
        {
            if (select) SelectedServer = existing;
            return existing;
        }
        var server = DiskMonitorRuntime.Acquire(hosted);
        _attached.Add(server.Id);
        Servers.Add(server);
        if (select) SelectedServer = server;
        return server;
    }

    /// <summary>Hosted: servers connected in the host that this view isn't showing yet.</summary>
    public IReadOnlyList<HostedServer> AvailableHostedServers()
    {
        if (Host == null) return [];
        try
        {
            return Host.AvailableServers().Where(h => !Servers.Any(s => DiskMonitorRuntime.SameServer(s, h.Server))).ToList();
        }
        catch (Exception ex)
        {
            Host.Log(ex, "SQL Disk Monitor: listing the host's servers failed");
            return [];
        }
    }

    /// <summary>Stops listening for samples and theme changes and, when hosted, releases this view's servers.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Collector.SampleCollected -= OnSampleCollected;
        ThemeService.Changed -= OnThemeChanged;
        _toastCts?.Cancel();
        if (IsHosted)
            foreach (var id in _attached) DiskMonitorRuntime.Release(id);
        _attached.Clear();
    }

    public void StartMonitoring()
    {
        foreach (var s in Servers.Where(s => s.Monitor)) Collector.Start(s);
    }

    partial void OnSelectedServerChanged(ServerConnection? value)
    {
        OnPropertyChanged(nameof(CanEditSelectedServer));
        if (value != null)
        {
            Settings.LastServerId = value.Id;
            Settings.Save();
        }
        _ = CurrentPage?.RefreshAsync();
    }

    partial void OnCurrentPageChanged(PageViewModel? value) => _ = value?.RefreshAsync();
    partial void OnSelectedRangeChanged(TimeRange value) => _ = CurrentPage?.RefreshAsync();

    [RelayCommand]
    private void Navigate(PageViewModel page) => CurrentPage = page;

    public void NavigateToDisk(string volume)
    {
        Disks.PendingVolume = volume;
        CurrentPage = Disks;
    }

    [RelayCommand]
    public void OpenReport()
    {
        if (SelectedServer == null) { ShowToast("Add a server first"); return; }
        new Views.ReportWindow(new ReportViewModel(this)) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    [RelayCommand]
    private Task Refresh() => CurrentPage?.RefreshAsync() ?? Task.CompletedTask;

    // ───────────────────────── Servers ─────────────────────────

    [RelayCommand]
    private void AddServer()
    {
        if (IsHosted)
        {
            AddHostedServer();
            return;
        }
        EditServer(null);
    }

    private void AddHostedServer()
    {
        var available = AvailableHostedServers();
        if (available.Count == 0)
        {
            ShowToast("Connect to another server in the host app first; it then appears here.", 6);
            return;
        }
        var pick = Views.HostedServerPicker.Pick(available, Application.Current?.MainWindow);
        if (pick == null) return;
        var s = AttachHostedServer(pick);
        ShowToast($"Monitoring {s.Name}");
    }

    [RelayCommand]
    public void EditServer(ServerConnection? server)
    {
        // Hosted connections belong to the host: their address and credentials are edited there.
        if (IsHosted || server?.IsExternal == true) return;
        var dlg = new Views.ServerDialog(server) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        if (dlg.Deleted && server != null)
        {
            DeleteServer(server);
            return;
        }

        var s = dlg.Result!;
        Store.SaveServer(s);
        if (dlg.Password != null) CredentialStore.SavePassword(s.Id, s.Username, dlg.Password);

        if (server == null)
        {
            Servers.Add(s);
            SelectedServer = s;
        }
        if (s.Monitor) Collector.Restart(s);
        else Collector.Stop(s.Id);
        ShowToast($"Saved {s.Name}");
        _ = CurrentPage?.RefreshAsync();
    }

    private void DeleteServer(ServerConnection s)
    {
        Collector.Stop(s.Id);
        Store.DeleteServer(s.Id);
        CredentialStore.Delete(s.Id);
        Servers.Remove(s);
        SelectedServer = Servers.FirstOrDefault();
        ShowToast($"Removed {s.Name} and its history");
    }

    [RelayCommand]
    private void ToggleCollect()
    {
        var s = SelectedServer;
        if (s == null) return;
        if (Collector.IsRunning(s.Id))
        {
            Collector.Stop(s.Id);
            s.Monitor = false;
        }
        else
        {
            s.Monitor = true;
            Collector.Start(s);
        }
        Store.SaveServer(s);
    }

    [RelayCommand]
    private async Task AddDemoServer()
    {
        if (IsHosted) return; // the host supplies the connections
        var existing = Servers.FirstOrDefault(s => s.IsDemo);
        if (existing != null)
        {
            SelectedServer = existing;
            return;
        }
        var s = new ServerConnection { Name = "Demo SQL Server", Address = "demo", Monitor = true };
        Store.SaveServer(s);
        var (azureVm, azureDisks) = DemoSource.DemoAzure();
        s.Azure = azureVm;
        Store.SaveServer(s);
        foreach (var d in DemoSource.DemoDisks(s.Id))
        {
            d.AzureDisks = azureDisks.GetValueOrDefault(d.Volume) ?? [];
            Store.SaveDisk(d);
        }
        Store.AddNote(new DiskNote { ServerId = s.Id, Volume = "D:\\", Timestamp = DateTime.Today.AddDays(-2).AddHours(9.5), Text = "Rebuilt Warehouse clustered index" });
        Store.AddNote(new DiskNote { ServerId = s.Id, Volume = "T:\\", Timestamp = DateTime.Today.AddDays(-4).AddHours(14), Text = "Added 3 tempdb data files" });

        ShowToast("Generating 7 days of demo history…", 60);
        Dashboard.IsBusy = true;
        await Task.Run(() => Collector.BackfillDemo(Store, s.Id, Settings, 7));
        Dashboard.IsBusy = false;
        Servers.Add(s);
        SelectedServer = s;
        Collector.Start(s);
        ShowToast("Demo server ready");
    }

    // ───────────────────────── Theme ─────────────────────────

    [RelayCommand]
    private void ToggleTheme()
    {
        if (IsHosted) return; // the host owns the theme
        Settings.Theme = ThemeService.IsDark ? ThemePreference.Light : ThemePreference.Dark;
        Settings.Save();
        ThemeService.Apply(Settings.Theme);
        SettingsPage.Reload();
    }

    // ───────────────────────── Updates ─────────────────────────

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (IsHosted) return; // the host updates itself and everything it hosts
        try
        {
            var r = await UpdateService.CheckAsync();
            if (r != null && (manual || r.Version.ToString(3) != Settings.SkippedVersion))
                AvailableUpdate = r;
            else if (manual)
                ShowToast($"You're on the latest version ({VersionText})");
        }
        catch (Exception ex)
        {
            if (manual) ShowToast($"Update check failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        var r = AvailableUpdate;
        if (r == null || IsHosted) return;
        if (r.MsiUrl == null)
        {
            OpenUrl(r.HtmlUrl);
            return;
        }
        try
        {
            IsUpdating = true;
            var path = await UpdateService.DownloadAsync(r, new Progress<double>(p => UpdateProgress = p));
            Collector.StopAll();
            UpdateService.InstallAndRestart(path);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            IsUpdating = false;
            ShowToast($"Update failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SkipUpdate()
    {
        if (AvailableUpdate != null)
        {
            Settings.SkippedVersion = AvailableUpdate.Version.ToString(3);
            Settings.Save();
        }
        AvailableUpdate = null;
    }

    [RelayCommand]
    private void ViewRelease() => OpenUrl(AvailableUpdate?.HtmlUrl ?? UpdateService.RepoUrl);

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public async void ShowToast(string text, int seconds = 4)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast = text;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
            Toast = "";
        }
        catch (TaskCanceledException) { }
    }
}
