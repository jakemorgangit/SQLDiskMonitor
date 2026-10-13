using System.Windows;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services;

namespace SQLDiskMonitor.Views;

public partial class ServerDialog : Window
{
    private readonly ServerConnection? _existing;

    public ServerConnection? Result { get; private set; }
    /// <summary>The password typed in, or null to leave the stored one unchanged.</summary>
    public string? Password { get; private set; }
    public bool Deleted { get; private set; }

    public ServerDialog(ServerConnection? existing)
    {
        InitializeComponent();
        _existing = existing;
        AuthBox.SelectedIndex = 0;
        if (existing != null)
        {
            Heading.Text = $"Edit {existing.Name}";
            NameBox.Text = existing.Name;
            AddressBox.Text = existing.Address;
            AuthBox.SelectedIndex = (int)existing.Auth;
            UserBox.Text = existing.Username;
            EncryptBox.IsChecked = existing.Encrypt;
            TrustBox.IsChecked = existing.TrustCertificate;
            MonitorBox.IsChecked = existing.Monitor;
            TimeoutBox.Text = existing.Timeout.ToString();
            PasswordLabel.Text = "Password (blank = keep saved)";
            DeleteButton.Visibility = Visibility.Visible;
        }
        else
        {
            TrustBox.IsChecked = true;
        }
        AuthChanged(this, null!);
        Loaded += (_, _) => (existing == null ? NameBox : AddressBox).Focus();
    }

    private AuthMode Auth => (AuthMode)Math.Max(0, AuthBox.SelectedIndex);

    private void AuthChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CredentialPanel == null) return;
        CredentialPanel.Visibility = Auth is AuthMode.Sql or AuthMode.EntraPassword ? Visibility.Visible : Visibility.Collapsed;
    }

    private ServerConnection? Build(out string error)
    {
        error = "";
        var address = AddressBox.Text.Trim();
        if (address.Length == 0) { error = "Enter a server name."; return null; }
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout < 1) timeout = 10;
        return new ServerConnection
        {
            Id = _existing?.Id ?? 0,
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? address : NameBox.Text.Trim(),
            Address = address,
            Auth = Auth,
            Username = UserBox.Text.Trim(),
            Encrypt = EncryptBox.IsChecked == true,
            TrustCertificate = TrustBox.IsChecked == true,
            Monitor = MonitorBox.IsChecked == true,
            Timeout = timeout,
            HostName = _existing?.HostName ?? "",
            Version = _existing?.Version ?? ""
        };
    }

    private string? TypedPassword() => PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var s = Build(out var error);
        TestResultBorder.Visibility = Visibility.Visible;
        if (s == null) { TestResult.Text = error; return; }
        if (s.IsDemo) { TestResult.Text = "Demo server: synthetic data, no connection needed."; return; }
        TestButton.IsEnabled = false;
        TestResult.Text = "Connecting…";
        try
        {
            var pw = TypedPassword() ?? (_existing != null ? CredentialStore.LoadPassword(_existing.Id) : "");
            TestResult.Text = "✓ " + await SqlSource.TestAsync(s, pw);
        }
        catch (Exception ex)
        {
            TestResult.Text = "✗ " + ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = Build(out var error);
        if (s == null)
        {
            TestResultBorder.Visibility = Visibility.Visible;
            TestResult.Text = error;
            return;
        }
        if (_existing != null)
        {
            // Edit in place so bindings to the existing object update.
            _existing.Name = s.Name; _existing.Address = s.Address; _existing.Auth = s.Auth; _existing.Username = s.Username;
            _existing.Encrypt = s.Encrypt; _existing.TrustCertificate = s.TrustCertificate; _existing.Monitor = s.Monitor; _existing.Timeout = s.Timeout;
            s = _existing;
        }
        Result = s;
        Password = s.Auth is AuthMode.Sql or AuthMode.EntraPassword ? TypedPassword() : null;
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Delete {_existing!.Name}? Its collected history, disk settings and notes are removed too.", "Delete server",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Deleted = true;
        DialogResult = true;
    }
}
