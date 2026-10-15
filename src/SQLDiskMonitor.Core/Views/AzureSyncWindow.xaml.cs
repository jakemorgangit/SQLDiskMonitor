using System.Windows;
using SQLDiskMonitor.ViewModels;

namespace SQLDiskMonitor.Views;

public partial class AzureSyncWindow : Window
{
    public AzureSyncWindow(AzureSyncViewModel vm)
    {
        Hosting.HostTheme.ApplyTo(this);
        InitializeComponent();
        DataContext = vm;
        vm.Finished += ok => DialogResult = ok;
        // Go straight to the VM list when already signed in (or using the Azure CLI).
        Loaded += (_, _) =>
        {
            if (vm.Account.StartsWith("Signed in") || vm.Account.StartsWith("Using")) vm.LoadVmsCommand.Execute(null);
        };
    }
}
