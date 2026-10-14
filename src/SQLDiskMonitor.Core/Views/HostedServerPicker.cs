using System.Windows;
using System.Windows.Controls;
using SQLDiskMonitor.Hosting;

namespace SQLDiskMonitor.Views;

/// <summary>Hosted mode: lets the user pick one of the host's connected servers to add to the monitor.</summary>
public static class HostedServerPicker
{
    public static HostedServer? Pick(IReadOnlyList<HostedServer> servers, Window? owner)
    {
        if (servers.Count == 0) return null;

        var list = new ListBox { MinHeight = 160, MaxHeight = 360, Margin = new Thickness(0, 0, 0, 16), DisplayMemberPath = nameof(Row.Text) };
        list.ItemsSource = servers.Select(s => new Row(s)).ToList();
        list.SelectedIndex = 0;

        var ok = new Button { Content = "Monitor", IsDefault = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "SoftButton");

        var heading = new TextBlock { Text = "Monitor another server", Margin = new Thickness(0, 0, 0, 6) };
        heading.SetResourceReference(FrameworkElement.StyleProperty, "H2");
        var hint = new TextBlock { Text = "Servers connected in Blackcat Studio. Collection uses the same credentials and stops when no open tab shows the server.", Margin = new Thickness(0, 0, 0, 12) };
        hint.SetResourceReference(FrameworkElement.StyleProperty, "Muted");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(heading);
        panel.Children.Add(hint);
        panel.Children.Add(list);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = "Monitor another server",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            Content = panel,
        };
        HostTheme.ApplyTo(win);
        win.SetResourceReference(Control.BackgroundProperty, "B.Surface");
        win.SetResourceReference(Control.ForegroundProperty, "B.Text");
        win.SetResourceReference(Control.FontFamilyProperty, "UiFont");

        ok.Click += (_, _) => win.DialogResult = list.SelectedItem != null;
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem != null) win.DialogResult = true; };

        return win.ShowDialog() == true ? (list.SelectedItem as Row)?.Server : null;
    }

    private sealed record Row(HostedServer Server)
    {
        public string Text => Server.IsProduction ? $"{Server.DisplayName}  ·  {Server.Server}  ·  PRODUCTION" : $"{Server.DisplayName}  ·  {Server.Server}";
    }
}
