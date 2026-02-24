using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace SQLDiskMonitor;

public partial class MainWindow : Window
{
    #region Fields

    private SqlConnection? _connection;
    private DispatcherTimer? _captureTimer;
    private Snapshot? _prevSnapshot;
    private readonly List<DeltaCapture> _deltas = new();
    private readonly Dictionary<string, bool> _hiddenSeries = new();
    private readonly Dictionary<string, bool> _expandedDbs = new();
    private readonly Dictionary<string, int> _dbColorIndex = new();
    private int _nextDbColor;
    private DateTime _sessionStart;
    private int _captureCount;
    private bool _isCapturing;
    private string _connectedServer = "";
    private string _connectedDisplayName = "";
    private bool _suppressFilterEvents;

    private PlotModel[] _plotModels = null!;
    private OxyPlot.Wpf.PlotView[] _plotViews = null!;

    private static readonly string[] ChartTitles =
    [
        "Avg Read Latency (ms)", "Avg Write Latency (ms)",
        "Read IOPS", "Write IOPS",
        "Read Throughput (MB/s)", "Write Throughput (MB/s)"
    ];

    private static readonly IntervalStep[] IntervalSteps =
    [
        new(1, "1s"), new(5, "5s"), new(10, "10s"), new(30, "30s"),
        new(60, "1 min"), new(120, "2 min"), new(180, "3 min"),
        new(240, "4 min"), new(300, "5 min")
    ];

    private static readonly string[] Palette =
    [
        "#61DAFB", "#E06C75", "#98C379", "#E5C07B", "#C678DD", "#56B6C2",
        "#D19A66", "#BE5046", "#ABB2BF", "#528BFF", "#FF6B6B", "#4ECDC4",
        "#F0C674", "#81A2BE", "#CC6666", "#B5BD68", "#8ABEB7", "#DE935F"
    ];

    private const int MaxCaptures = 360;

    private const string SnapshotSql = """
        SET NOCOUNT ON;
        SELECT vfs.database_id, DB_NAME(vfs.database_id) AS database_name, vfs.file_id,
            LEFT(mf.physical_name,1) AS drive_letter, mf.physical_name, mf.type_desc,
            vfs.num_of_reads, vfs.io_stall_read_ms, vfs.num_of_writes, vfs.io_stall_write_ms,
            vfs.num_of_bytes_read, vfs.num_of_bytes_written
        FROM sys.dm_io_virtual_file_stats(NULL,NULL) vfs
        JOIN sys.master_files mf ON mf.database_id=vfs.database_id AND mf.file_id=vfs.file_id
        WHERE vfs.database_id<>32767
        """;

    private const string FilterSql = """
        SET NOCOUNT ON;
        SELECT DISTINCT LEFT(mf.physical_name,1) AS drive_letter
          FROM sys.master_files mf WHERE mf.database_id<>32767 ORDER BY drive_letter;
        SELECT DISTINCT DB_NAME(mf.database_id) AS database_name
          FROM sys.master_files mf WHERE mf.database_id<>32767 ORDER BY database_name;
        """;

    #endregion

    public MainWindow() => InitializeComponent();

    #region Initialization

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _plotViews = [plotReadLat, plotWriteLat, plotReadIops, plotWriteIops, plotReadMbps, plotWriteMbps];
        _plotModels = new PlotModel[6];
        var hoverCtrl = CreateHoverController();
        for (int i = 0; i < 6; i++)
        {
            _plotModels[i] = BuildEmptyModel(ChartTitles[i]);
            _plotViews[i].Model = _plotModels[i];
            _plotViews[i].Controller = hoverCtrl;
        }

        InitializeComboBoxes();
        RefreshServerList();

        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(IntervalSteps[4].Seconds) };
        _captureTimer.Tick += CaptureTimer_Tick;
    }

    private static PlotController CreateHoverController()
    {
        var c = new PlotController();
        c.UnbindMouseDown(OxyMouseButton.Left);
        c.BindMouseEnter(PlotCommands.HoverSnapTrack);
        return c;
    }

    private static PlotModel BuildEmptyModel(string title)
    {
        var m = new PlotModel
        {
            Title = title,
            TitleFontSize = 11,
            TitleFontWeight = 400,
            Background = OxyColor.FromRgb(0x0d, 0x11, 0x17),
            PlotAreaBackground = OxyColor.FromRgb(0x0d, 0x11, 0x17),
            TextColor = OxyColor.FromRgb(0x8b, 0x94, 0x9e),
            TitleColor = OxyColor.FromRgb(0xc9, 0xd1, 0xd9),
            PlotAreaBorderColor = OxyColor.FromRgb(0x21, 0x26, 0x2d),
            IsLegendVisible = false,
            PlotMargins = new OxyThickness(48, 6, 10, 26),
            Padding = new OxyThickness(2)
        };
        m.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Key = "x",
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(0x1c, 0x23, 0x33),
            MinorGridlineStyle = LineStyle.None,
            AxislineColor = OxyColor.FromRgb(0x30, 0x36, 0x3d),
            TicklineColor = OxyColor.FromRgb(0x30, 0x36, 0x3d),
            TextColor = OxyColor.FromRgb(0x58, 0x5e, 0x68),
            FontSize = 9,
            IsZoomEnabled = true,
            IsPanEnabled = true,
            Minimum = 0,
            Maximum = 10
        });
        m.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Key = "y",
            Minimum = 0,
            Maximum = 1,
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(0x1c, 0x23, 0x33),
            MinorGridlineStyle = LineStyle.None,
            AxislineColor = OxyColor.FromRgb(0x30, 0x36, 0x3d),
            TicklineColor = OxyColor.FromRgb(0x30, 0x36, 0x3d),
            TextColor = OxyColor.FromRgb(0x8b, 0x94, 0x9e),
            FontSize = 9,
            IsZoomEnabled = true,
            IsPanEnabled = true
        });
        return m;
    }

    private void InitializeComboBoxes()
    {
        _suppressFilterEvents = true;
        cmbDrive.Items.Add("(All)"); cmbDrive.SelectedIndex = 0;
        cmbDatabase.Items.Add("(All)"); cmbDatabase.SelectedIndex = 0;
        cmbGroupBy.Items.Add("Database");
        cmbGroupBy.Items.Add("Drive");
        cmbGroupBy.Items.Add("File");
        cmbGroupBy.SelectedIndex = 0;
        _suppressFilterEvents = false;
    }

    #endregion

    #region Sidebar / Connection Management

    private void RefreshServerList()
    {
        lstServers.Items.Clear();
        try
        {
            foreach (var entry in CredentialStore.ListAll())
                lstServers.Items.Add(BuildServerCard(entry));
        }
        catch { }
    }

    private StackPanel BuildServerCard(ServerEntry entry)
    {
        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill = entry.ServerAddress == _connectedServer && _connection is { State: ConnectionState.Open }
                ? (Brush)FindResource("GreenBrush") : (Brush)FindResource("DisabledBrush"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var nameText = new TextBlock
        {
            Text = entry.DisplayName, FontWeight = System.Windows.FontWeights.SemiBold,
            FontSize = 12, Foreground = (Brush)FindResource("FgBrightBrush")
        };
        var addrText = new TextBlock
        {
            Text = entry.ServerAddress, FontSize = 10, Foreground = (Brush)FindResource("FgMutedBrush")
        };
        var info = new StackPanel();
        info.Children.Add(nameText); info.Children.Add(addrText);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = entry.DisplayName };
        row.Children.Add(dot); row.Children.Add(info);
        return row;
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AddServerDialog { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        try { CredentialStore.Save(dlg.Result, dlg.ResultPassword); }
        catch (Exception ex) { MessageBox.Show($"Failed to save credentials:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        RefreshServerList();
    }

    private void LstServers_DoubleClick(object sender, MouseButtonEventArgs e) => ConnectToSelectedServer();
    private void ServerConnect_Click(object sender, RoutedEventArgs e) => ConnectToSelectedServer();

    private void ServerEdit_Click(object sender, RoutedEventArgs e)
    {
        var displayName = GetSelectedServerName(); if (displayName == null) return;
        var (entry, pass) = CredentialStore.Load(displayName); if (entry == null) return;
        var dlg = new AddServerDialog { Owner = this, EditEntry = entry };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        try { CredentialStore.Save(dlg.Result, string.IsNullOrEmpty(dlg.ResultPassword) ? pass : dlg.ResultPassword); } catch { }
        RefreshServerList();
    }

    private void ServerDelete_Click(object sender, RoutedEventArgs e)
    {
        var displayName = GetSelectedServerName(); if (displayName == null) return;
        if (MessageBox.Show($"Delete '{displayName}'?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { CredentialStore.Delete(displayName); } catch { }
        if (_connectedDisplayName == displayName && _connection is { State: ConnectionState.Open })
        {
            if (_isCapturing) BtnStop_Click(this, new RoutedEventArgs());
            _connection.Close(); _connection.Dispose(); _connection = null;
            _connectedServer = ""; _connectedDisplayName = "";
            btnStart.IsEnabled = false; btnClear.IsEnabled = false;
            statusConn.Text = "No connection"; statusConn.Foreground = (Brush)FindResource("FgMutedBrush");
        }
        RefreshServerList();
    }

    private string? GetSelectedServerName() => lstServers.SelectedItem is StackPanel p ? p.Tag as string : null;

    private void ConnectToSelectedServer()
    {
        var displayName = GetSelectedServerName(); if (displayName == null) return;
        var (entry, password) = CredentialStore.Load(displayName);
        if (entry == null) { MessageBox.Show("Could not load connection.", "Error"); return; }
        statusConn.Text = $"Connecting to {entry.ServerAddress}…";
        statusConn.Foreground = (Brush)FindResource("YellowBrush");
        Dispatcher.InvokeAsync(() =>
        {
            if (DoConnect(entry, password)) { _connectedDisplayName = displayName; RefreshServerList(); }
        }, DispatcherPriority.Background);
    }

    private bool DoConnect(ServerEntry entry, string password)
    {
        var csb = new SqlConnectionStringBuilder
        {
            DataSource = entry.ServerAddress, ConnectTimeout = entry.Timeout,
            TrustServerCertificate = entry.TrustCertificate, Encrypt = entry.Encrypt,
            ApplicationName = "SQL Disk Monitor v1.0"
        };
        if (entry.WindowsAuth) csb.IntegratedSecurity = true;
        else { csb.UserID = entry.Username; csb.Password = password; }
        Cursor = Cursors.Wait;
        try
        {
            if (_connection is { State: ConnectionState.Open })
            {
                if (_isCapturing) BtnStop_Click(this, new RoutedEventArgs());
                _connection.Close(); _connection.Dispose(); _connection = null;
            }
            _connection = new SqlConnection(csb.ConnectionString);
            _connection.Open();
            var ds = RunQuery(FilterSql);
            _suppressFilterEvents = true;
            cmbDrive.Items.Clear(); cmbDrive.Items.Add("(All)");
            if (ds.Tables.Count > 0) foreach (DataRow r in ds.Tables[0].Rows) cmbDrive.Items.Add(r["drive_letter"]?.ToString() ?? "");
            cmbDrive.SelectedIndex = 0;
            cmbDatabase.Items.Clear(); cmbDatabase.Items.Add("(All)");
            if (ds.Tables.Count > 1) foreach (DataRow r in ds.Tables[1].Rows) cmbDatabase.Items.Add(r["database_name"]?.ToString() ?? "");
            cmbDatabase.SelectedIndex = 0;
            _suppressFilterEvents = false;
            _connectedServer = entry.ServerAddress;
            btnStart.IsEnabled = true; btnClear.IsEnabled = true;
            statusConn.Text = $"Connected: {entry.DisplayName} ({entry.ServerAddress})";
            statusConn.Foreground = (Brush)FindResource("GreenBrush");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Connection failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            statusConn.Text = "Connection failed"; statusConn.Foreground = (Brush)FindResource("RedBrush");
            return false;
        }
        finally { Cursor = Cursors.Arrow; }
    }

    #endregion

    #region SQL

    private DataSet RunQuery(string sql)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql; cmd.CommandTimeout = 15;
        using var da = new SqlDataAdapter(cmd);
        var ds = new DataSet(); da.Fill(ds); return ds;
    }

    private Snapshot TakeSnapshot()
    {
        var ds = RunQuery(SnapshotSql);
        var snap = new Snapshot { Timestamp = DateTime.Now };
        foreach (DataRow r in ds.Tables[0].Rows)
            snap.Rows.Add(new SnapshotRow
            {
                DatabaseId = Convert.ToInt32(r["database_id"]),
                DatabaseName = r["database_name"]?.ToString() ?? "",
                FileId = Convert.ToInt32(r["file_id"]),
                Drive = r["drive_letter"]?.ToString() ?? "",
                Path = r["physical_name"]?.ToString() ?? "",
                TypeDesc = r["type_desc"]?.ToString() ?? "",
                Reads = Convert.ToInt64(r["num_of_reads"]),
                ReadStall = Convert.ToInt64(r["io_stall_read_ms"]),
                Writes = Convert.ToInt64(r["num_of_writes"]),
                WriteStall = Convert.ToInt64(r["io_stall_write_ms"]),
                BytesRead = Convert.ToInt64(r["num_of_bytes_read"]),
                BytesWritten = Convert.ToInt64(r["num_of_bytes_written"])
            });
        return snap;
    }

    private static DeltaCapture ComputeDelta(Snapshot prev, Snapshot curr)
    {
        double elapsed = (curr.Timestamp - prev.Timestamp).TotalSeconds;
        if (elapsed <= 0) elapsed = 1;
        var lookup = prev.Rows.ToDictionary(r => $"{r.DatabaseId}|{r.FileId}");
        var delta = new DeltaCapture { Timestamp = curr.Timestamp, ElapsedSeconds = elapsed };
        foreach (var c in curr.Rows)
        {
            if (!lookup.TryGetValue($"{c.DatabaseId}|{c.FileId}", out var p)) continue;
            long dr = Math.Max(0, c.Reads - p.Reads);
            long drs = Math.Max(0, c.ReadStall - p.ReadStall);
            long dw = Math.Max(0, c.Writes - p.Writes);
            long dws = Math.Max(0, c.WriteStall - p.WriteStall);
            long dbr = Math.Max(0, c.BytesRead - p.BytesRead);
            long dbw = Math.Max(0, c.BytesWritten - p.BytesWritten);
            delta.Rows.Add(new DeltaRow
            {
                DatabaseName = c.DatabaseName, FileId = c.FileId, Drive = c.Drive,
                Path = c.Path, TypeDesc = c.TypeDesc,
                ReadLatency = dr > 0 ? Math.Round((double)drs / dr, 2) : 0,
                WriteLatency = dw > 0 ? Math.Round((double)dws / dw, 2) : 0,
                ReadIops = Math.Round(dr / elapsed, 1),
                WriteIops = Math.Round(dw / elapsed, 1),
                ReadMbps = Math.Round(dbr / (1024.0 * 1024) / elapsed, 2),
                WriteMbps = Math.Round(dbw / (1024.0 * 1024) / elapsed, 2),
                DeltaReads = dr, DeltaReadStall = drs, DeltaWrites = dw, DeltaWriteStall = dws
            });
        }
        return delta;
    }

    #endregion

    #region Capture Timer

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        _prevSnapshot = null; _isCapturing = true; _sessionStart = DateTime.Now;
        btnStart.IsEnabled = false; btnStop.IsEnabled = true;
        lblStatus.Text = "Baseline…"; lblStatus.Foreground = (Brush)FindResource("YellowBrush");
        try
        {
            _prevSnapshot = TakeSnapshot();
            lblStatus.Text = $"Baseline OK, next in {IntervalSteps[(int)sldInterval.Value].Label}…";
            statusCapture.Text = $"Baseline: {_prevSnapshot.Timestamp:HH:mm:ss}";
            statusSession.Text = "Session: 00:00:00";
        }
        catch (Exception ex)
        {
            lblStatus.Text = $"Err: {ex.Message}"; lblStatus.Foreground = (Brush)FindResource("RedBrush");
            btnStart.IsEnabled = true; btnStop.IsEnabled = false; _isCapturing = false; return;
        }
        _captureTimer?.Start();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _captureTimer?.Stop(); _isCapturing = false;
        btnStart.IsEnabled = true; btnStop.IsEnabled = false;
        lblStatus.Text = "Stopped"; lblStatus.Foreground = (Brush)FindResource("FgBrush");
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _deltas.Clear(); _prevSnapshot = null; _captureCount = 0;
        _hiddenSeries.Clear(); _expandedDbs.Clear();
        RebuildCharts();
        lblStatus.Text = "Cleared"; statusCapture.Text = ""; statusSession.Text = "";
    }

    private void CaptureTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (_connection is not { State: ConnectionState.Open })
            {
                BtnStop_Click(this, new RoutedEventArgs());
                lblStatus.Text = "Connection lost"; lblStatus.Foreground = (Brush)FindResource("RedBrush"); return;
            }
            var snap = TakeSnapshot();
            if (_prevSnapshot != null)
            {
                var delta = ComputeDelta(_prevSnapshot, snap);
                _deltas.Add(delta);
                while (_deltas.Count > MaxCaptures) _deltas.RemoveAt(0);
                _captureCount++;
                RebuildCharts();
                int active = delta.Rows.Count(r => r.DeltaReads != 0 || r.DeltaWrites != 0);
                long tdr = delta.Rows.Sum(r => r.DeltaReads), tdw = delta.Rows.Sum(r => r.DeltaWrites);
                var el = DateTime.Now - _sessionStart;
                statusSession.Text = $"Session: {el:hh\\:mm\\:ss}";
                statusCapture.Text = $"Captures: {_captureCount} | Active: {active}/{delta.Rows.Count} | ΔR:{tdr} ΔW:{tdw}";
                lblStatus.Text = active > 0 ? $"Cap: {_captureCount} | IO detected" : $"Cap: {_captureCount} | No physical IO";
            }
            else
                lblStatus.Text = $"Baseline OK, next in {IntervalSteps[(int)sldInterval.Value].Label}…";
            _prevSnapshot = snap;
        }
        catch (Exception ex) { lblStatus.Text = $"Err: {ex.Message}"; lblStatus.Foreground = (Brush)FindResource("RedBrush"); }
    }

    private void SldInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_captureTimer == null) return;
        int idx = (int)sldInterval.Value;
        if (idx < 0 || idx >= IntervalSteps.Length) return;
        var step = IntervalSteps[idx];
        lblInterval.Text = step.Label;
        _captureTimer.Interval = TimeSpan.FromSeconds(Math.Max(step.Seconds, 1));
        if (_isCapturing) lblStatus.Text = $"Interval: {step.Label}";
    }

    #endregion

    #region Chart Rebuild (6 charts — LinearAxis approach)

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (!_suppressFilterEvents) RebuildCharts(); }
    private void GroupBy_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvents) return;
        _hiddenSeries.Clear(); _expandedDbs.Clear(); RebuildCharts();
    }

    private void RebuildCharts()
    {
        if (_plotViews == null) return;

        var groupBy = cmbGroupBy.SelectedItem as string ?? "Database";
        var driveFilter = cmbDrive.SelectedItem as string;
        var dbFilter = cmbDatabase.SelectedItem as string;
        legendPanel.Children.Clear();

        if (_deltas.Count == 0)
        {
            for (int i = 0; i < 6; i++)
            {
                _plotModels[i] = BuildEmptyModel(ChartTitles[i]);
                _plotViews[i].Model = _plotModels[i];
            }
            return;
        }

        DateTime origin = _deltas[0].Timestamp;

        var allKeys = new Dictionary<string, GroupInfo>();
        foreach (var delta in _deltas)
            foreach (var row in FilterRows(delta.Rows, driveFilter, dbFilter))
            {
                var gk = GetGroupKey(row, groupBy);
                if (!allKeys.ContainsKey(gk))
                    allKeys[gk] = new GroupInfo { DatabaseName = row.DatabaseName, TypeDesc = row.TypeDesc, FileId = row.FileId, Drive = row.Drive, Path = row.Path };
            }

        if (allKeys.Count == 0)
        {
            for (int i = 0; i < 6; i++) { _plotModels[i] = BuildEmptyModel(ChartTitles[i]); _plotViews[i].Model = _plotModels[i]; }
            return;
        }

        var dbFileKeys = new Dictionary<string, List<string>>();
        foreach (var (gk, info) in allKeys)
        {
            if (!dbFileKeys.TryGetValue(info.DatabaseName, out var list)) dbFileKeys[info.DatabaseName] = list = [];
            list.Add(gk);
        }
        var colorMap = AssignColors(groupBy, allKeys);

        var perKey = new Dictionary<string, List<(double x, double[] y)>>();
        double[] yMaxes = new double[6];
        double xMax = 0;

        foreach (var delta in _deltas)
        {
            double xVal = (delta.Timestamp - origin).TotalSeconds;
            if (xVal > xMax) xMax = xVal;

            var groups = new Dictionary<string, List<DeltaRow>>();
            foreach (var row in FilterRows(delta.Rows, driveFilter, dbFilter))
            {
                var gk = GetGroupKey(row, groupBy);
                if (!groups.TryGetValue(gk, out var list)) groups[gk] = list = [];
                list.Add(row);
            }

            foreach (var (gk, rows) in groups)
            {
                if (_hiddenSeries.ContainsKey(gk)) continue;
                double[] v =
                [
                    Metric(rows, 0), Metric(rows, 1),
                    Metric(rows, 2), Metric(rows, 3),
                    Metric(rows, 4), Metric(rows, 5)
                ];
                if (!perKey.TryGetValue(gk, out var pts)) perKey[gk] = pts = [];
                pts.Add((xVal, v));
                for (int m = 0; m < 6; m++) if (v[m] > yMaxes[m]) yMaxes[m] = v[m];
            }
        }

        var models = new PlotModel[6];
        for (int i = 0; i < 6; i++)
        {
            var mdl = new PlotModel
            {
                Title = ChartTitles[i],
                TitleFontSize = 11, TitleFontWeight = 400,
                Background = OxyColor.FromRgb(0x0d, 0x11, 0x17),
                PlotAreaBackground = OxyColor.FromRgb(0x0d, 0x11, 0x17),
                TextColor = OxyColor.FromRgb(0x8b, 0x94, 0x9e),
                TitleColor = OxyColor.FromRgb(0xc9, 0xd1, 0xd9),
                PlotAreaBorderColor = OxyColor.FromRgb(0x21, 0x26, 0x2d),
                IsLegendVisible = false,
                PlotMargins = new OxyThickness(48, 6, 10, 26),
                Padding = new OxyThickness(2)
            };

            double xPad = Math.Max(xMax * 0.05, 2);
            var xAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Key = "x",
                Minimum = -xPad,
                Maximum = xMax + xPad,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromRgb(0x1c, 0x23, 0x33),
                MinorGridlineStyle = LineStyle.None,
                AxislineColor = OxyColor.FromRgb(0x30, 0x36, 0x3d),
                TicklineColor = OxyColor.FromRgb(0x30, 0x36, 0x3d),
                TextColor = OxyColor.FromRgb(0x58, 0x5e, 0x68),
                FontSize = 9
            };
            xAxis.LabelFormatter = val =>
            {
                var t = origin.AddSeconds(val);
                return t.ToString("HH:mm:ss");
            };
            mdl.Axes.Add(xAxis);

            double ym = yMaxes[i] > 0 ? yMaxes[i] * 1.15 : 1;
            mdl.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Key = "y",
                Minimum = 0,
                Maximum = ym,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromRgb(0x1c, 0x23, 0x33),
                MinorGridlineStyle = LineStyle.None,
                AxislineColor = OxyColor.FromRgb(0x30, 0x36, 0x3d),
                TicklineColor = OxyColor.FromRgb(0x30, 0x36, 0x3d),
                TextColor = OxyColor.FromRgb(0x8b, 0x94, 0x9e),
                FontSize = 9
            });

            foreach (var gk in allKeys.Keys.OrderBy(k => k))
            {
                bool hidden = _hiddenSeries.ContainsKey(gk);
                var color = colorMap.TryGetValue(gk, out var c) ? c : OxyColors.Gray;

                var fill = OxyColor.FromAColor(50, color);
                var area = new AreaSeries
                {
                    XAxisKey = "x", YAxisKey = "y",
                    Title = gk,
                    Color = hidden ? OxyColors.Transparent : color,
                    Fill = hidden ? OxyColors.Transparent : fill,
                    StrokeThickness = hidden ? 0 : 2,
                    IsVisible = !hidden,
                    MarkerType = hidden ? MarkerType.None : MarkerType.Circle,
                    MarkerSize = 3,
                    MarkerFill = hidden ? OxyColors.Transparent : color,
                    TrackerFormatString = $"{gk}\n{{2:HH:mm:ss}}\n{ChartTitles[i]}: {{4:0.##}}",
                    RenderInLegend = false
                };

                if (!hidden && perKey.TryGetValue(gk, out var pts))
                    foreach (var (x, y) in pts)
                        area.Points.Add(new DataPoint(x, y[i]));

                mdl.Series.Add(area);
            }

            models[i] = mdl;
        }

        for (int i = 0; i < 6; i++)
        {
            _plotModels[i] = models[i];
            _plotViews[i].Model = models[i];
        }

        RebuildLegend(groupBy, allKeys, colorMap, dbFileKeys);
    }

    private static double Metric(List<DeltaRow> rows, int idx) => idx switch
    {
        0 => SumL(rows, r => r.DeltaReads) is > 0 and var tr ? Math.Round(SumL(rows, r => r.DeltaReadStall) / (double)tr, 2) : 0,
        1 => SumL(rows, r => r.DeltaWrites) is > 0 and var tw ? Math.Round(SumL(rows, r => r.DeltaWriteStall) / (double)tw, 2) : 0,
        2 => Math.Round(rows.Sum(r => r.ReadIops), 1),
        3 => Math.Round(rows.Sum(r => r.WriteIops), 1),
        4 => Math.Round(rows.Sum(r => r.ReadMbps), 2),
        5 => Math.Round(rows.Sum(r => r.WriteMbps), 2),
        _ => 0
    };

    private static List<DeltaRow> FilterRows(List<DeltaRow> rows, string? drv, string? db)
    {
        IEnumerable<DeltaRow> r = rows;
        if (!string.IsNullOrEmpty(drv) && drv != "(All)") r = r.Where(x => x.Drive == drv);
        if (!string.IsNullOrEmpty(db) && db != "(All)") r = r.Where(x => x.DatabaseName == db);
        return r.ToList();
    }

    private static string GetGroupKey(DeltaRow row, string g) => g switch
    {
        "Drive" => row.Drive + ":",
        "File" => $"{row.DatabaseName}:{row.FileId}",
        _ => row.DatabaseName
    };

    private static long SumL(List<DeltaRow> rows, Func<DeltaRow, long> sel) { long s = 0; foreach (var r in rows) s += sel(r); return s; }

    #endregion

    #region Color Assignment

    private OxyColor GetDbBaseColor(string db)
    {
        if (!_dbColorIndex.TryGetValue(db, out int idx)) { idx = _nextDbColor++; _dbColorIndex[db] = idx; }
        return OxyColor.Parse(Palette[idx % Palette.Length]);
    }

    private Dictionary<string, OxyColor> AssignColors(string groupBy, Dictionary<string, GroupInfo> allKeys)
    {
        var cm = new Dictionary<string, OxyColor>();
        if (groupBy == "File")
        {
            var byDb = new Dictionary<string, List<string>>();
            foreach (var (gk, info) in allKeys)
            {
                if (!byDb.TryGetValue(info.DatabaseName, out var l)) byDb[info.DatabaseName] = l = [];
                l.Add(gk);
            }
            foreach (var (db, files) in byDb)
            {
                var bc = GetDbBaseColor(db);
                var sorted = files.OrderBy(f => f).ToList();
                for (int i = 0; i < sorted.Count; i++) cm[sorted[i]] = GetShade(bc, i, sorted.Count);
            }
        }
        else
        {
            int ci = 0;
            foreach (var gk in allKeys.Keys.OrderBy(k => k))
            {
                cm[gk] = groupBy == "Database" ? GetDbBaseColor(allKeys[gk].DatabaseName) : OxyColor.Parse(Palette[ci % Palette.Length]);
                ci++;
            }
        }
        return cm;
    }

    private static (double h, double s, double l) ToHsl(OxyColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        double l = (mx + mn) / 2, h = 0, s = 0;
        if (mx != mn)
        {
            double d = mx - mn;
            s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
            if (mx == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
            else if (mx == g) h = ((b - r) / d + 2) / 6;
            else h = ((r - g) / d + 4) / 6;
        }
        return (h, s, l);
    }

    private static double Hue2Rgb(double p, double q, double t)
    {
        if (t < 0) t++; if (t > 1) t--;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static OxyColor HslToOxy(double h, double s, double l)
    {
        if (s == 0) { byte v = (byte)(l * 255); return OxyColor.FromRgb(v, v, v); }
        double q2 = l < 0.5 ? l * (1 + s) : l + s - l * s, p2 = 2 * l - q2;
        return OxyColor.FromRgb(
            (byte)Math.Clamp(Hue2Rgb(p2, q2, h + 1.0 / 3) * 255, 0, 255),
            (byte)Math.Clamp(Hue2Rgb(p2, q2, h) * 255, 0, 255),
            (byte)Math.Clamp(Hue2Rgb(p2, q2, h - 1.0 / 3) * 255, 0, 255));
    }

    private static OxyColor GetShade(OxyColor baseColor, int index, int total)
    {
        if (total <= 1) return baseColor;
        var (h, s, bL) = ToHsl(baseColor);
        double lo = Math.Max(0.20, bL - 0.22), hi = Math.Min(0.82, bL + 0.22);
        double nL = lo + (hi - lo) * ((double)index / Math.Max(total - 1, 1));
        return HslToOxy(h, Math.Min(1.0, s * 1.05), nL);
    }

    #endregion

    #region Legend

    private void RebuildLegend(string groupBy, Dictionary<string, GroupInfo> allKeys,
        Dictionary<string, OxyColor> colorMap, Dictionary<string, List<string>> dbFileKeys)
    {
        legendPanel.Children.Clear();
        if (groupBy == "File")
        {
            var headerWrap = new WrapPanel();
            foreach (var db in dbFileKeys.Keys.OrderBy(d => d))
            {
                var dbBase = GetDbBaseColor(db);
                bool allHidden = dbFileKeys[db].All(k => _hiddenSeries.ContainsKey(k));
                bool expanded = _expandedDbs.ContainsKey(db);
                headerWrap.Children.Add(CreateDbHeader(db, dbBase, allHidden, expanded, dbFileKeys[db]));
            }
            legendPanel.Children.Add(headerWrap);
            foreach (var db in dbFileKeys.Keys.OrderBy(d => d))
            {
                if (!_expandedDbs.ContainsKey(db)) continue;
                var sec = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
                sec.Children.Add(new TextBlock
                {
                    Text = db + ":", FontWeight = System.Windows.FontWeights.SemiBold, FontSize = 11,
                    Foreground = new SolidColorBrush(WpfColor(GetDbBaseColor(db))), Margin = new Thickness(4, 0, 4, 2)
                });
                var fw = new WrapPanel { Margin = new Thickness(16, 0, 0, 0) };
                foreach (var fk in dbFileKeys[db].OrderBy(f => f))
                {
                    var info = allKeys[fk]; var col = colorMap.TryGetValue(fk, out var fc) ? fc : OxyColors.Gray;
                    fw.Children.Add(CreateLegendItem(fk, $"{System.IO.Path.GetFileName(info.Path)} [{info.FileId}]", col, _hiddenSeries.ContainsKey(fk)));
                }
                sec.Children.Add(fw); legendPanel.Children.Add(sec);
            }
        }
        else
        {
            var wrap = new WrapPanel();
            foreach (var gk in allKeys.Keys.OrderBy(k => k))
            {
                var col = colorMap.TryGetValue(gk, out var c) ? c : OxyColors.Gray;
                wrap.Children.Add(CreateLegendItem(gk, gk, col, _hiddenSeries.ContainsKey(gk)));
            }
            legendPanel.Children.Add(wrap);
        }
    }

    private Border CreateDbHeader(string db, OxyColor baseColor, bool allHidden, bool expanded, List<string> fileKeys)
    {
        var wc = WpfColor(baseColor);
        var swatch = new Rectangle { Width = 12, Height = 12, RadiusX = 2, RadiusY = 2, Fill = allHidden ? Brushes.Gray : new SolidColorBrush(wc), Margin = new Thickness(0, 0, 4, 0) };
        var text = new TextBlock
        {
            Text = $"{(expanded ? "▾" : "▸")} {db}", FontWeight = System.Windows.FontWeights.SemiBold, FontSize = 11,
            Foreground = allHidden ? Brushes.Gray : new SolidColorBrush(Color.FromRgb(0xe6, 0xed, 0xf3))
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(swatch); panel.Children.Add(text);
        var border = new Border { Child = panel, Padding = new Thickness(6, 3, 8, 3), Margin = new Thickness(2), CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x23, 0x33)), Cursor = Cursors.Hand };
        border.MouseLeftButtonDown += (s, e) =>
        {
            var pos = e.GetPosition(swatch);
            if (pos.X >= 0 && pos.X <= swatch.ActualWidth + 4 && pos.Y >= 0 && pos.Y <= swatch.ActualHeight + 6)
            {
                bool vis = fileKeys.Any(k => !_hiddenSeries.ContainsKey(k));
                foreach (var k in fileKeys) { if (vis) _hiddenSeries[k] = true; else _hiddenSeries.Remove(k); }
            }
            else { if (_expandedDbs.ContainsKey(db)) _expandedDbs.Remove(db); else _expandedDbs[db] = true; }
            RebuildCharts();
        };
        border.MouseEnter += (s, e) => border.Background = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3d));
        border.MouseLeave += (s, e) => border.Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x23, 0x33));
        return border;
    }

    private Border CreateLegendItem(string key, string label, OxyColor color, bool hidden)
    {
        var wc = WpfColor(color);
        var swatch = new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = hidden ? Brushes.Gray : new SolidColorBrush(wc), Margin = new Thickness(0, 0, 4, 0) };
        var text = new TextBlock { Text = label, FontSize = 10.5, Foreground = hidden ? Brushes.Gray : new SolidColorBrush(Color.FromRgb(0xe6, 0xed, 0xf3)), TextDecorations = hidden ? TextDecorations.Strikethrough : null };
        var panel = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(swatch); panel.Children.Add(text);
        var border = new Border { Child = panel, Padding = new Thickness(5, 2, 7, 2), Margin = new Thickness(2), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand };
        border.MouseLeftButtonDown += (s, e) => { if (_hiddenSeries.ContainsKey(key)) _hiddenSeries.Remove(key); else _hiddenSeries[key] = true; RebuildCharts(); };
        border.MouseEnter += (s, e) => border.Background = new SolidColorBrush(Color.FromArgb(30, wc.R, wc.G, wc.B));
        border.MouseLeave += (s, e) => border.Background = Brushes.Transparent;
        return border;
    }

    private static Color WpfColor(OxyColor c) => Color.FromRgb(c.R, c.G, c.B);

    #endregion

    #region Session Save / Load / Export

    private void SaveSession_Click(object sender, RoutedEventArgs e)
    {
        if (_deltas.Count == 0) { MessageBox.Show("No data.", "Save"); return; }
        var dlg = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"DiskMonitorSession_{DateTime.Now:yyyyMMdd_HHmmss}.json" };
        if (dlg.ShowDialog(this) != true) return;
        var session = new SessionData { Server = _connectedServer, CapturedAt = _sessionStart, IntervalSeconds = IntervalSteps[(int)sldInterval.Value].Seconds, Captures = _deltas.ToList() };
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
        statusCapture.Text = $"Saved: {dlg.FileName}";
    }

    private void LoadSession_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new LoadSessionDialog { Owner = this };
        if (dlg.ShowDialog() != true || dlg.LoadedSession == null) return;
        var s = dlg.LoadedSession;
        _deltas.Clear(); _deltas.AddRange(s.Captures); _captureCount = s.Captures.Count;
        _hiddenSeries.Clear(); _expandedDbs.Clear(); _dbColorIndex.Clear(); _nextDbColor = 0;
        _connectedServer = s.Server;
        _suppressFilterEvents = true;
        cmbDrive.Items.Clear(); cmbDrive.Items.Add("(All)"); cmbDatabase.Items.Clear(); cmbDatabase.Items.Add("(All)");
        var drives = new HashSet<string>(); var dbs = new HashSet<string>();
        foreach (var cap in s.Captures) foreach (var row in cap.Rows) { drives.Add(row.Drive); dbs.Add(row.DatabaseName); }
        foreach (var d in drives.OrderBy(x => x)) cmbDrive.Items.Add(d);
        foreach (var d in dbs.OrderBy(x => x)) cmbDatabase.Items.Add(d);
        cmbDrive.SelectedIndex = 0; cmbDatabase.SelectedIndex = 0;
        _suppressFilterEvents = false;
        statusConn.Text = $"Loaded: {s.Server} ({s.Captures.Count} captures)"; statusConn.Foreground = (Brush)FindResource("AccentBrush");
        statusCapture.Text = $"Captures: {s.Captures.Count}";
        if (s.Captures.Count > 0) { var f = s.Captures[0].Timestamp; var l = s.Captures[^1].Timestamp; statusSession.Text = $"Range: {f:HH:mm:ss} — {l:HH:mm:ss}"; }
        RebuildCharts();
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_deltas.Count == 0) { MessageBox.Show("No data.", "Export"); return; }
        var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"DiskMonitorData_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Database,FileId,Drive,Path,Type,ReadLat_ms,WriteLat_ms,ReadIOPS,WriteIOPS,ReadMBps,WriteMBps");
        foreach (var d in _deltas) foreach (var r in d.Rows)
            sb.AppendLine($"{d.Timestamp:yyyy-MM-dd HH:mm:ss},{r.DatabaseName},{r.FileId},{r.Drive},\"{r.Path}\",{r.TypeDesc},{r.ReadLatency},{r.WriteLatency},{r.ReadIops},{r.WriteIops},{r.ReadMbps},{r.WriteMbps}");
        File.WriteAllText(dlg.FileName, sb.ToString());
        statusCapture.Text = $"Exported {_deltas.Count} captures to CSV";
    }

    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "PNG|*.png", FileName = $"DiskMonitorChart_{DateTime.Now:yyyyMMdd_HHmmss}.png" };
        if (dlg.ShowDialog(this) != true) return;
        using var stream = File.Create(dlg.FileName);
        new OxyPlot.Wpf.PngExporter { Width = (int)chartGrid.ActualWidth, Height = (int)chartGrid.ActualHeight }.Export(_plotModels[0], stream);
        statusCapture.Text = $"Saved: {dlg.FileName}";
    }

    #endregion

    #region Window Events / Keyboard / Menus

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && btnStart.IsEnabled) { BtnStart_Click(this, new RoutedEventArgs()); e.Handled = true; }
        if (e.Key == Key.F6 && btnStop.IsEnabled) { BtnStop_Click(this, new RoutedEventArgs()); e.Handled = true; }
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { SaveSession_Click(this, new RoutedEventArgs()); e.Handled = true; }
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) { LoadSession_Click(this, new RoutedEventArgs()); e.Handled = true; }
        if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control) { DumpDiagnostic(); e.Handled = true; }
    }

    private void DumpDiagnostic()
    {
        if (_connection is not { State: ConnectionState.Open }) { MessageBox.Show("Not connected.", "Diagnostic"); return; }
        try
        {
            var s1 = TakeSnapshot(); System.Threading.Thread.Sleep(1100); var s2 = TakeSnapshot();
            var sb = new StringBuilder();
            sb.AppendLine($"=== Diagnostic @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine($"Server: {_connectedServer}");
            sb.AppendLine($"Snap1: {s1.Timestamp:HH:mm:ss.fff} Rows:{s1.Rows.Count}  Snap2: {s2.Timestamp:HH:mm:ss.fff} Rows:{s2.Rows.Count}");
            sb.AppendLine($"Elapsed: {(s2.Timestamp - s1.Timestamp).TotalSeconds:F3}s\n");
            sb.AppendLine("Database|FileId|Drive|S1_Reads|S2_Reads|ΔR|S1_Writes|S2_Writes|ΔW");
            var lk = s1.Rows.ToDictionary(r => $"{r.DatabaseId}|{r.FileId}");
            int changed = 0;
            foreach (var c in s2.Rows)
            {
                if (!lk.TryGetValue($"{c.DatabaseId}|{c.FileId}", out var p)) continue;
                long dr = c.Reads - p.Reads, dw = c.Writes - p.Writes;
                if (dr != 0 || dw != 0) changed++;
                sb.AppendLine($"{c.DatabaseName}|{c.FileId}|{c.Drive}|{p.Reads}|{c.Reads}|{dr}|{p.Writes}|{c.Writes}|{dw}{(dr != 0 || dw != 0 ? " ***" : "")}");
            }
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"DiskMonitorDiag_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, sb.ToString());
            MessageBox.Show($"Saved: {path}\n\nActive: {changed}/{s2.Rows.Count}\n{(changed == 0 ? "No physical IO. Try DBCC DROPCLEANBUFFERS" : $"{changed} files had IO")}", "Diagnostic");
        }
        catch (Exception ex) { MessageBox.Show($"Failed:\n{ex.Message}", "Error"); }
    }

    private void FileMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Background = (Brush)FindResource("PanelAltBrush"), BorderBrush = (Brush)FindResource("BorderBrush") };
        menu.Items.Add(new MenuItem { Header = "Save Session… (Ctrl+S)", Command = new RelayCmd(() => SaveSession_Click(this, new RoutedEventArgs())) });
        menu.Items.Add(new MenuItem { Header = "Load Session… (Ctrl+O)", Command = new RelayCmd(() => LoadSession_Click(this, new RoutedEventArgs())) });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Export to CSV…", Command = new RelayCmd(() => ExportCsv_Click(this, new RoutedEventArgs())) });
        menu.Items.Add(new MenuItem { Header = "Save Chart Image…", Command = new RelayCmd(() => SaveImage_Click(this, new RoutedEventArgs())) });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Exit", Command = new RelayCmd(Close) });
        menu.PlacementTarget = sender as Button; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; menu.IsOpen = true;
    }

    private void HelpMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Background = (Brush)FindResource("PanelAltBrush"), BorderBrush = (Brush)FindResource("BorderBrush") };
        menu.Items.Add(new MenuItem { Header = "Diagnostic Dump… (Ctrl+D)", Command = new RelayCmd(DumpDiagnostic) });
        menu.PlacementTarget = sender as Button; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; menu.IsOpen = true;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _captureTimer?.Stop();
        if (_connection is { State: ConnectionState.Open }) { _connection.Close(); _connection.Dispose(); }
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e) { _hiddenSeries.Clear(); RebuildCharts(); }
    private void HideAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var m in _plotModels) foreach (var s in m.Series) if (s.Title != null) _hiddenSeries[s.Title] = true;
        RebuildCharts();
    }
    private void ExpandAll_Click(object sender, RoutedEventArgs e) { foreach (var d in _deltas) foreach (var r in d.Rows) _expandedDbs[r.DatabaseName] = true; RebuildCharts(); }
    private void CollapseAll_Click(object sender, RoutedEventArgs e) { _expandedDbs.Clear(); RebuildCharts(); }
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    #endregion
}

internal class RelayCmd(Action execute) : ICommand
{
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
