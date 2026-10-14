using System.Windows.Controls;

namespace SQLDiskMonitor.Views;

public partial class DisksView : UserControl
{
    public DisksView()
    {
        Hosting.HostTheme.ApplyTo(this);
        InitializeComponent();
    }
}
