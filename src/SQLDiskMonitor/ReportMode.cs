using System.IO;
using System.Windows;
using SQLDiskMonitor.Services.Reports;
using SQLDiskMonitor.ViewModels;

namespace SQLDiskMonitor;

/// <summary>
/// Writes a report without showing the app, e.g. from Task Scheduler:
///   SQLDiskMonitor.exe --report "C:\Reports\prod.pdf" [--server "Name"] [--range 7d] [--files]
/// The file extension (.pdf or .html) picks the format. Errors are written next to the output as .error.txt.
/// </summary>
public static class ReportMode
{
    public static async Task RunAsync(MainViewModel vm, string path, string? serverName, string? range, bool includeFiles)
    {
        try
        {
            var server = serverName == null ? vm.SelectedServer
                : vm.Servers.FirstOrDefault(s => s.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException($"No saved server called '{serverName}'.");
            if (server == null) throw new InvalidOperationException("No servers are configured.");
            var span = vm.TimeRanges.FirstOrDefault(r => r.Label == range)?.Span ?? TimeSpan.FromDays(7);
            var full = Path.GetFullPath(path);
            await ReportFiles.WriteAsync(vm, server, DateTime.Now - span, DateTime.Now, new ReportOptions { Files = includeFiles }, full,
                pdf: full.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".error.txt", ex.ToString());
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }
}
