using System.Windows.Controls;

namespace SQLDiskMonitor.Views;

public partial class SpaceView : UserControl
{
    public SpaceView()
    {
        Hosting.HostTheme.ApplyTo(this);
        InitializeComponent();
    }
}
