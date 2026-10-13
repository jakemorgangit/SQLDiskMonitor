using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLDiskMonitor.Data;
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

public partial class MainViewModel : ObservableObject
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

    public DateTime RangeTo => DateTime.Now;
    public DateTime RangeFrom => DateTime.Now - SelectedRange.Span;

    private CancellationTokenSource? _toastCts;

    public MainViewModel(Store store, AppSettings settings)
    {
        Store = store;
        Settings = settings;
        Collector = new Collector(store, () => Settings);
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

        Collector.SampleCollected += b => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (b.ServerId == SelectedServer?.Id) CurrentPage?.OnSample(b);
        });
        ThemeService.Changed += () => _ = CurrentPage?.RefreshAsync();

        foreach (var s in store.GetServers()) Servers.Add(s);
        SelectedServer = Servers.FirstOrDefault(s => s.Id == settings.LastServerId) ?? Servers.FirstOrDefault();
        CurrentPage = Dashboard;
    }

    public void StartMonitoring()
    {
        foreach (var s in Servers.Where(s => s.Monitor)) Collector.Start(s);
    }

    partial void OnSelectedServerChanged(ServerConnection? value)
    {
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
    private Task Refresh() => CurrentPage?.RefreshAsync() ?? Task.CompletedTask;

    // ───────────────────────── Servers ─────────────────────────

    [RelayCommand]
    private void AddServer() => EditServer(null);

    [RelayCommand]
    public void EditServer(ServerConnection? server)
    {
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
        var existing = Servers.FirstOrDefault(s => s.IsDemo);
        if (existing != null)
        {
            SelectedServer = existing;
            return;
        }
        var s = new ServerConnection { Name = "Demo SQL Server", Address = "demo", Monitor = true };
        Store.SaveServer(s);
        foreach (var d in DemoSource.DemoDisks(s.Id)) Store.SaveDisk(d);
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
        Settings.Theme = ThemeService.IsDark ? ThemePreference.Light : ThemePreference.Dark;
        Settings.Save();
        ThemeService.Apply(Settings.Theme);
        SettingsPage.Reload();
    }

    // ───────────────────────── Updates ─────────────────────────

    public async Task CheckForUpdatesAsync(bool manual)
    {
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
        if (r == null) return;
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
