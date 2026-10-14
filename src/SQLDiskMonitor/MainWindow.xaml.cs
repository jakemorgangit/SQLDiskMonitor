using System.Windows;
using SQLDiskMonitor.ViewModels;
using SQLDiskMonitor.Views;

namespace SQLDiskMonitor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        Content = new DiskMonitorView(vm);
        StateChanged += (_, _) =>
        {
            // Minimised windows go to the notification area so collection carries on out of the way.
            if (WindowState == WindowState.Minimized && _vm.Settings.MinimizeToTray) Hide();
        };
    }
}
