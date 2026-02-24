using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace SQLDiskMonitor;

public partial class LoadSessionDialog : Window
{
    public SessionData? LoadedSession { get; private set; }
    public DateTime? FilterFrom { get; private set; }
    public DateTime? FilterTo { get; private set; }

    private SessionData? _preview;

    public LoadSessionDialog() => InitializeComponent();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON Session|*.json|All Files|*.*",
            Title = "Open SQL Disk Monitor Session"
        };
        if (dlg.ShowDialog(this) != true) return;

        txtFilePath.Text = dlg.FileName;
        try
        {
            var json = File.ReadAllText(dlg.FileName);
            _preview = JsonSerializer.Deserialize<SessionData>(json);
            if (_preview?.Captures == null || _preview.Captures.Count == 0)
            {
                MessageBox.Show("No capture data found in file.", "Load", MessageBoxButton.OK, MessageBoxImage.Warning);
                _preview = null;
                btnLoad.IsEnabled = false;
                return;
            }

            lblServer.Text = _preview.Server;
            lblCaptures.Text = _preview.Captures.Count.ToString();
            lblInterval.Text = $"{_preview.IntervalSeconds}s";

            var first = _preview.Captures[0].Timestamp;
            var last = _preview.Captures[^1].Timestamp;
            lblFirst.Text = first.ToString("yyyy-MM-dd HH:mm:ss");
            lblLast.Text = last.ToString("yyyy-MM-dd HH:mm:ss");

            txtFrom.Text = first.ToString("yyyy-MM-dd HH:mm:ss");
            txtTo.Text = last.ToString("yyyy-MM-dd HH:mm:ss");

            btnLoad.IsEnabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to read session file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _preview = null;
            btnLoad.IsEnabled = false;
        }
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        if (_preview == null) return;

        const string fmt = "yyyy-MM-dd HH:mm:ss";
        if (!string.IsNullOrWhiteSpace(txtFrom.Text) &&
            DateTime.TryParseExact(txtFrom.Text.Trim(), fmt, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var from))
            FilterFrom = from;

        if (!string.IsNullOrWhiteSpace(txtTo.Text) &&
            DateTime.TryParseExact(txtTo.Text.Trim(), fmt, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var to))
            FilterTo = to;

        var filtered = new SessionData
        {
            Version = _preview.Version,
            Application = _preview.Application,
            Server = _preview.Server,
            CapturedAt = _preview.CapturedAt,
            IntervalSeconds = _preview.IntervalSeconds,
            Captures = _preview.Captures
                .Where(c => (!FilterFrom.HasValue || c.Timestamp >= FilterFrom.Value) &&
                            (!FilterTo.HasValue || c.Timestamp <= FilterTo.Value))
                .ToList()
        };

        if (filtered.Captures.Count == 0)
        {
            MessageBox.Show("No captures match the specified time range.", "Filter",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadedSession = filtered;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
