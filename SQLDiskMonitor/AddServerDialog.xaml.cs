using System.Windows;
using Microsoft.Data.SqlClient;

namespace SQLDiskMonitor;

public partial class AddServerDialog : Window
{
    public ServerEntry? Result { get; private set; }
    public string ResultPassword { get; private set; } = "";

    /// <summary>Set to pre-populate for editing an existing entry.</summary>
    public ServerEntry? EditEntry { get; set; }

    public AddServerDialog() => InitializeComponent();

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (EditEntry != null)
        {
            Title = "Edit Server";
            txtDisplayName.Text = EditEntry.DisplayName;
            txtDisplayName.IsEnabled = false;
            txtServerAddr.Text = EditEntry.ServerAddress;
            radWinAuth.IsChecked = EditEntry.WindowsAuth;
            radSqlAuth.IsChecked = !EditEntry.WindowsAuth;
            txtSqlUser.Text = EditEntry.Username;
            chkTrust.IsChecked = EditEntry.TrustCertificate;
            chkEnc.IsChecked = EditEntry.Encrypt;
            txtTimeout.Text = EditEntry.Timeout.ToString();
        }
        txtDisplayName.Focus();
    }

    private void AuthMode_Changed(object sender, RoutedEventArgs e)
    {
        if (txtSqlUser == null || txtSqlPass == null) return;
        bool isSql = radSqlAuth.IsChecked == true;
        txtSqlUser.IsEnabled = isSql;
        txtSqlPass.IsEnabled = isSql;
    }

    private ServerEntry BuildEntry() => new()
    {
        DisplayName = txtDisplayName.Text.Trim(),
        ServerAddress = txtServerAddr.Text.Trim(),
        WindowsAuth = radWinAuth.IsChecked == true,
        Username = txtSqlUser.Text.Trim(),
        TrustCertificate = chkTrust.IsChecked == true,
        Encrypt = chkEnc.IsChecked == true,
        Timeout = int.TryParse(txtTimeout.Text, out int t) && t > 0 ? t : 10
    };

    private SqlConnectionStringBuilder BuildConnectionString(ServerEntry entry, string password)
    {
        var csb = new SqlConnectionStringBuilder
        {
            DataSource = entry.ServerAddress,
            ConnectTimeout = entry.Timeout,
            TrustServerCertificate = entry.TrustCertificate,
            Encrypt = entry.Encrypt,
            ApplicationName = "SQL Disk Monitor v1.0"
        };
        if (entry.WindowsAuth)
            csb.IntegratedSecurity = true;
        else
        {
            csb.UserID = entry.Username;
            csb.Password = password;
        }
        return csb;
    }

    private void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var entry = BuildEntry();
        if (string.IsNullOrEmpty(entry.ServerAddress))
        {
            MessageBox.Show("Enter a server address.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var csb = BuildConnectionString(entry, txtSqlPass.Password);
            using var conn = new SqlConnection(csb.ConnectionString);
            conn.Open();
            MessageBox.Show($"Connected successfully to:\n{conn.DataSource}\nVersion: {conn.ServerVersion}",
                "Test Connection", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Connection failed:\n{ex.Message}", "Test Connection",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Cursor = System.Windows.Input.Cursors.Arrow; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var entry = BuildEntry();
        if (string.IsNullOrEmpty(entry.DisplayName))
        {
            MessageBox.Show("Enter a display name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtDisplayName.Focus();
            return;
        }
        if (string.IsNullOrEmpty(entry.ServerAddress))
        {
            MessageBox.Show("Enter a server address.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtServerAddr.Focus();
            return;
        }
        if (!entry.WindowsAuth && string.IsNullOrEmpty(entry.Username))
        {
            MessageBox.Show("Enter a SQL username.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtSqlUser.Focus();
            return;
        }

        Result = entry;
        ResultPassword = txtSqlPass.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
