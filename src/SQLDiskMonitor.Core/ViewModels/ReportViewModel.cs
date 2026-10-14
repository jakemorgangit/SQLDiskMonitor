using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services.Reports;

namespace SQLDiskMonitor.ViewModels;

public partial class ReportViewModel(MainViewModel shell) : ObservableObject
{
    public ReportOptions Options { get; } = new();
    public IReadOnlyList<string> Formats { get; } = ["PDF", "HTML"];

    [ObservableProperty] private string _format = "PDF";
    [ObservableProperty] private bool _openAfter = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    public bool CostsEnabled => shell.Settings.CostsEnabled;
    public string ServerName => shell.SelectedServer?.Name ?? "";
    public string PeriodText => $"Last {shell.SelectedRange.Label}: {shell.RangeFrom:d MMM HH:mm} – {shell.RangeTo:d MMM HH:mm}. Change the period with the range selector at the top of the app.";
    public event Action? Done;

    [RelayCommand]
    private async Task Generate()
    {
        var server = shell.SelectedServer;
        if (server == null) return;
        bool pdf = Format == "PDF";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = pdf ? "PDF (*.pdf)|*.pdf" : "HTML (*.html)|*.html",
            FileName = $"{MetricsViewModel.Sanitise(server.Name)}_storage_report_{DateTime.Now:yyyyMMdd_HHmm}.{(pdf ? "pdf" : "html")}"
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        Status = "Building the report…";
        try
        {
            await ReportFiles.WriteAsync(shell, server, shell.RangeFrom, shell.RangeTo, Options, dlg.FileName, pdf);
            if (OpenAfter) Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            shell.ShowToast($"Report saved: {Path.GetFileName(dlg.FileName)}");
            Done?.Invoke();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public static class ReportFiles
{
    /// <summary>Writes the report as HTML, or as PDF via a temporary HTML file.</summary>
    public static async Task WriteAsync(MainViewModel shell, ServerConnection server, DateTime from, DateTime to, ReportOptions options, string path, bool pdf)
    {
        if (!shell.Settings.CostsEnabled) options.Costs = false;
        var html = await ReportBuilder.BuildAsync(shell.Store, server, shell.Settings, from, to, options, Services.UpdateService.CurrentVersion.ToString(3));
        if (!pdf)
        {
            await File.WriteAllTextAsync(path, html);
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), $"sqldiskmonitor-report-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(temp, html);
        try
        {
            await PdfExporter.SaveAsync(temp, path);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}
