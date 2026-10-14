using System.Windows.Controls;
using SQLDiskMonitor.ViewModels;

namespace SQLDiskMonitor.Views;

/// <summary>
/// The whole SQL Disk Monitor UI (navigation, pages, header, toasts) as a control. The standalone app puts it in its
/// MainWindow; a hosting app (Blackcat Studio) puts it in a document tab after calling Hosting.HostTheme.Initialize.
/// </summary>
public partial class DiskMonitorView : UserControl
{
    public MainViewModel ViewModel { get; }

    public DiskMonitorView(MainViewModel vm)
    {
        Hosting.HostTheme.ApplyTo(this);
        InitializeComponent();
        DataContext = ViewModel = vm;
    }
}
