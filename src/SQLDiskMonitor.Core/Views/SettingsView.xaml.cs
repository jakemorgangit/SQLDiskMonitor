using System.Windows.Controls;

namespace SQLDiskMonitor.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        Hosting.HostTheme.ApplyTo(this);
        InitializeComponent();
    }
}
