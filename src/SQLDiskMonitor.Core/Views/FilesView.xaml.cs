using System.Windows.Controls;

namespace SQLDiskMonitor.Views;

public partial class FilesView : UserControl
{
    public FilesView()
    {
        Hosting.HostTheme.ApplyTo(this);
        InitializeComponent();
    }
}
