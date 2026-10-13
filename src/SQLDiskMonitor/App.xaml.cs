using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SQLDiskMonitor.Data;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services;
using SQLDiskMonitor.ViewModels;

namespace SQLDiskMonitor;

public partial class App : Application
{
    private const string InstanceName = "SQLDiskMonitor.SingleInstance.v2";
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private TrayIcon? _tray;
    private MainViewModel? _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToList();
        string? Arg(string name) { var i = args.IndexOf(name); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }
        var screenshot = Arg("--screenshot");

        // One collector per machine: a second launch just brings the running window forward.
        if (screenshot == null)
        {
            _mutex = new Mutex(true, InstanceName, out bool first);
            if (!first)
            {
                try { EventWaitHandle.OpenExisting(InstanceName + ".show").Set(); } catch { /* other instance still starting */ }
                Shutdown();
                return;
            }
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".show");
            ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);
        }

        DispatcherUnhandledException += OnUnhandled;

        var settings = AppSettings.Load();
        if (Arg("--theme") is { } t && Enum.TryParse<ThemePreference>(t, true, out var tp)) settings.Theme = tp;
        ThemeService.Apply(settings.Theme);

        Store store;
        try
        {
            store = new Store(Arg("--db"));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the history database:\n\n{ex.Message}", "SQL Disk Monitor", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _vm = new MainViewModel(store, settings);
        var window = new MainWindow(_vm);
        MainWindow = window;

        if (screenshot != null)
        {
            _ = ScreenshotMode.RunAsync(window, _vm, screenshot, Arg("--page"), Arg("--range"));
            return;
        }

        _tray = new TrayIcon(ShowMainWindow, () => { _vm.Collector.StopAll(); Shutdown(); });
        window.Closed += (_, _) => { _vm.Collector.StopAll(); Shutdown(); };

        bool minimized = args.Contains("--minimized") || settings.StartMinimized;
        if (!minimized || !settings.MinimizeToTray) window.Show();
        if (minimized && !settings.MinimizeToTray) window.WindowState = WindowState.Minimized;

        _vm.StartMonitoring();
        if (settings.CheckForUpdates) _ = _vm.CheckForUpdatesAsync(manual: false);
    }

    private void ShowMainWindow()
    {
        if (MainWindow == null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "SQL Disk Monitor — unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _showSignal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
