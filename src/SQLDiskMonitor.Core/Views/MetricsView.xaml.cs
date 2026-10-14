using System.Windows.Controls;

namespace SQLDiskMonitor.Views;

public partial class MetricsView : UserControl
{
    public MetricsView()
    {
        Hosting.HostTheme.ApplyTo(this);
        InitializeComponent();
    }
}
