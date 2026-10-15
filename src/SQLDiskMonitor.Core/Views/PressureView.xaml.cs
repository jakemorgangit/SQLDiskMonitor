using System.Windows.Controls;

namespace SQLDiskMonitor.Views;

public partial class PressureView : UserControl
{
    public PressureView()
    {
        Hosting.HostTheme.ApplyTo(this);
        InitializeComponent();
    }
}
