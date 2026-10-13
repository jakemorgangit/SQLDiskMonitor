using System.Windows;
using SQLDiskMonitor.ViewModels;

namespace SQLDiskMonitor.Views;

public partial class ReportWindow : Window
{
    public ReportWindow(ReportViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Done += () => DialogResult = true;
    }
}
