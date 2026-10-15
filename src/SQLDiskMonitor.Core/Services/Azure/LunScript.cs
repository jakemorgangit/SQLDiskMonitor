using System.IO;
using System.Windows;

namespace SQLDiskMonitor.Services.Azure;

/// <summary>
/// The PowerShell script (tools/Get-DiskLuns.ps1) that maps Windows volumes to Azure LUNs on the VM itself, for when
/// remote WMI is blocked and Azure access is read-only. Its CSV goes straight into Disks › Import CSV.
/// </summary>
public static class LunScript
{
    public static string Text()
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/SQLDiskMonitor.Core;component/Assets/Get-DiskLuns.ps1"));
        using var reader = new StreamReader(res!.Stream);
        return reader.ReadToEnd();
    }

    public static void CopyWithInstructions(Window? owner)
    {
        Clipboard.SetText(Text());
        const string message =
            "The LUN mapping script is on the clipboard.\n\n" +
            "1. On the SQL Server VM, open PowerShell (no admin rights needed) and paste the script.\n" +
            "2. It saves <computer>-disk-luns.csv to that user's Desktop. Copy the file to this PC.\n" +
            "3. Here, choose Disks › Import CSV and pick the file. It fills in each volume's LUN, or the pool LUNs for storage pools.\n" +
            "4. Run Sync from Azure again. Every disk now matches by LUN.\n\n" +
            "Import only fills empty fields, so clear any LUN you typed by hand that might be wrong first.";
        if (owner != null) MessageBox.Show(owner, message, "Map volumes to LUNs", MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(message, "Map volumes to LUNs", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
