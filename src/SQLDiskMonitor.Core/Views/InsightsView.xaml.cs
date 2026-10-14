using System.Windows.Controls;

namespace SQLDiskMonitor.Views;

public partial class InsightsView : UserControl
{
    public InsightsView()
    {
        Hosting.HostTheme.ApplyTo(this);
        InitializeComponent();
    }
}
