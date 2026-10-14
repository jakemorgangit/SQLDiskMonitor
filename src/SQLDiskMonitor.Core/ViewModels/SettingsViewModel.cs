using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services;

namespace SQLDiskMonitor.ViewModels;

public partial class SettingsViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public override string Title => "Settings";
    public override string Glyph => "";
    public override string Subtitle => "Appearance, collection, thresholds, costs and updates";
    public override bool UsesTimeRange => false;

    public IReadOnlyList<int> Intervals { get; } = [5, 10, 15, 30, 60, 120, 300];

    [ObservableProperty] private ThemePreference _theme;
    [ObservableProperty] private int _sampleInterval;
    [ObservableProperty] private int _retentionDays;
    [ObservableProperty] private int _fileRetentionDays;
    [ObservableProperty] private bool _costsEnabled;
    [ObservableProperty] private string _currency = "";
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private double _dataGood;
    [ObservableProperty] private double _dataWarn;
    [ObservableProperty] private double _dataCrit;
    [ObservableProperty] private double _logGood;
    [ObservableProperty] private double _logWarn;
    [ObservableProperty] private double _logCrit;
    [ObservableProperty] private double _minIops;
    [ObservableProperty] private double _saturation;
    [ObservableProperty] private bool _spaceInHealth;
    [ObservableProperty] private bool _countFileFreeSpace;
    [ObservableProperty] private double _spaceWarnPct;
    [ObservableProperty] private string _storageInfo = "";

    public string DataFolder => AppSettings.DataFolder;
    public string VersionText => Shell.VersionText;
    /// <summary>Hosted in another app: theme, start-up, tray, updates and demo data belong to the host and are hidden.</summary>
    public bool IsHosted => Shell.IsHosted;

    public override Task RefreshAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    public void Reload()
    {
        var s = Cfg;
        _loading = true;
        Theme = s.Theme;
        SampleInterval = s.SampleIntervalSeconds;
        RetentionDays = s.RetentionDays;
        FileRetentionDays = s.FileRetentionDays;
        CostsEnabled = s.CostsEnabled;
        Currency = s.CurrencySymbol;
        MinimizeToTray = s.MinimizeToTray;
        StartWithWindows = s.StartWithWindows;
        StartMinimized = s.StartMinimized;
        CheckForUpdates = s.CheckForUpdates;
        DataGood = s.DataLatencyGood; DataWarn = s.DataLatencyWarn; DataCrit = s.DataLatencyCritical;
        LogGood = s.LogLatencyGood; LogWarn = s.LogLatencyWarn; LogCrit = s.LogLatencyCritical;
        MinIops = s.MinIopsForLatency; Saturation = s.SaturationPct;
        SpaceInHealth = s.SpaceInHealth; CountFileFreeSpace = s.CountFileFreeSpace; SpaceWarnPct = s.SpaceWarnPct;
        AzureAuthMode = s.AzureAuth == "AzureCli" ? AzureAuthModes[1] : AzureAuthModes[0];
        AzureTenant = s.AzureTenantId;
        ShowAzureStatus();
        StorageInfo = $"History database: {Store.DatabaseSizeBytes() / 1048576.0:N1} MB";
        _loading = false;
    }

    private bool _loading;

    // Theme applies immediately so it can be previewed.
    partial void OnThemeChanged(ThemePreference value)
    {
        if (_loading || IsHosted) return;
        Cfg.Theme = value;
        Cfg.Save();
        ThemeService.Apply(value);
    }

    partial void OnCostsEnabledChanged(bool value)
    {
        if (_loading) return;
        Cfg.CostsEnabled = value;
        Cfg.Save();
        Shell.Costs.IsVisible = value;
    }

    [RelayCommand]
    private void Save()
    {
        var s = Cfg;
        bool intervalChanged = s.SampleIntervalSeconds != SampleInterval;
        s.SampleIntervalSeconds = Math.Max(1, SampleInterval);
        s.RetentionDays = Math.Clamp(RetentionDays, 1, 3650);
        s.FileRetentionDays = Math.Clamp(FileRetentionDays, 1, s.RetentionDays);
        s.CostsEnabled = CostsEnabled;
        s.CurrencySymbol = string.IsNullOrWhiteSpace(Currency) ? "£" : Currency.Trim();
        if (!IsHosted)
        {
            s.MinimizeToTray = MinimizeToTray;
            s.StartWithWindows = StartWithWindows;
            s.StartMinimized = StartMinimized;
            s.CheckForUpdates = CheckForUpdates;
        }
        s.DataLatencyGood = DataGood; s.DataLatencyWarn = Math.Max(DataWarn, DataGood + 0.1); s.DataLatencyCritical = Math.Max(DataCrit, s.DataLatencyWarn + 0.1);
        s.LogLatencyGood = LogGood; s.LogLatencyWarn = Math.Max(LogWarn, LogGood + 0.1); s.LogLatencyCritical = Math.Max(LogCrit, s.LogLatencyWarn + 0.1);
        s.MinIopsForLatency = Math.Max(0, MinIops);
        s.SaturationPct = Math.Clamp(Saturation, 10, 100);
        s.SpaceInHealth = SpaceInHealth;
        s.CountFileFreeSpace = CountFileFreeSpace;
        s.SpaceWarnPct = Math.Clamp(SpaceWarnPct, 1, 50);
        ApplyAzureSettings();
        s.Save();
        // Never register the host's exe (Environment.ProcessPath) to start with Windows.
        if (!IsHosted) ApplyStartup(s.StartWithWindows);
        Shell.Costs.IsVisible = s.CostsEnabled;
        if (intervalChanged)
            foreach (var srv in Shell.Servers.Where(x => Shell.Collector.IsRunning(x.Id))) Shell.Collector.Restart(srv);
        Reload();
        Shell.ShowToast("Settings saved. Latency thresholds apply to new samples; space alerts apply straight away.");
    }

    private static void ApplyStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key == null) return;
        if (enabled) key.SetValue("SQLDiskMonitor", $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue("SQLDiskMonitor", false);
    }

    // ───────── Azure ─────────
    public IReadOnlyList<string> AzureAuthModes { get; } = ["Browser sign-in", "Azure CLI (az login)"];
    [ObservableProperty] private string _azureAuthMode = "Browser sign-in";
    [ObservableProperty] private string _azureTenant = "";
    [ObservableProperty] private string _azureStatus = "";
    [ObservableProperty] private Services.Azure.AzureClient.AzureTenant? _selectedTenant;
    public System.Collections.ObjectModel.ObservableCollection<Services.Azure.AzureClient.AzureTenant> Tenants { get; } = [];

    partial void OnSelectedTenantChanged(Services.Azure.AzureClient.AzureTenant? value)
    {
        if (value != null) AzureTenant = value.Id;
    }

    private void ApplyAzureSettings()
    {
        Cfg.AzureAuth = AzureAuthMode.StartsWith("Azure CLI") ? "AzureCli" : "Browser";
        Cfg.AzureTenantId = AzureTenant.Trim();
    }

    private void ShowAzureStatus() =>
        AzureStatus = Cfg.AzureAuth == "AzureCli" ? "Uses your az login"
            : Services.Azure.AzureClient.SignedInAccount() is { } a ? $"Signed in as {a}" : "Not signed in";

    [RelayCommand]
    private async Task SignInAzure()
    {
        if (Services.Azure.AzureClient.ValidateTenant(AzureTenant) is { } problem)
        {
            AzureStatus = problem;
            return;
        }
        ApplyAzureSettings();
        Cfg.Save();
        AzureStatus = "Signing in…";
        try
        {
            var client = new Services.Azure.AzureClient(Cfg);
            var who = await client.SignInAsync();
            AzureStatus = $"Signed in as {who}";
            // Offer the account's tenants so nobody has to look up a GUID.
            var tenants = await client.GetTenantsAsync();
            Tenants.Clear();
            foreach (var t in tenants) Tenants.Add(t);
            if (tenants.Count > 1) AzureStatus += $". {tenants.Count} tenants available: pick one above and Save to switch.";
        }
        catch (Exception ex)
        {
            AzureStatus = (Cfg.AzureAuth == "AzureCli" ? "Azure CLI sign-in failed: " : "Sign-in failed: ") + Services.Azure.AzureClient.FriendlyError(ex);
        }
    }

    [RelayCommand]
    private void SignOutAzure()
    {
        Services.Azure.AzureClient.SignOut();
        ShowAzureStatus();
    }

    [RelayCommand]
    private Task CheckUpdatesNow() => IsHosted ? Task.CompletedTask : Shell.CheckForUpdatesAsync(manual: true);

    [RelayCommand]
    private void OpenDataFolder() => Process.Start(new ProcessStartInfo(DataFolder) { UseShellExecute = true });

    [RelayCommand]
    private void OpenRepo() => MainViewModel.OpenUrl(UpdateService.RepoUrl);

    [RelayCommand]
    private void ClearHistory()
    {
        if (Server == null) return;
        if (System.Windows.MessageBox.Show($"Delete all collected samples for {Server.Name}? Disk settings and notes are kept.",
                "Clear history", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        Store.ClearSamples(Server.Id);
        Reload();
        Shell.ShowToast("History cleared");
    }
}
