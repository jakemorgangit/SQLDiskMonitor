using System.Windows.Controls;

namespace SQLDiskMonitor.Views;

public partial class CostsView : UserControl
{
    public CostsView()
    {
        Hosting.HostTheme.ApplyTo(this);
        InitializeComponent();
    }
}
