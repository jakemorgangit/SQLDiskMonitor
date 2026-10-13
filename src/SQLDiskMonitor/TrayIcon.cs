using System.Windows;
using Forms = System.Windows.Forms;

namespace SQLDiskMonitor;

/// <summary>Notification-area icon so collection can keep running while the window is hidden.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action show, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open SQL Disk Monitor", null, (_, _) => show());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/SQLDiskMonitor;component/Assets/app.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "SQL Disk Monitor",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => show();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
