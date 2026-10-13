using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Services.Reports;

/// <summary>
/// Prints an HTML file to PDF with WebView2 (the Edge engine built into Windows), so the PDF matches the HTML report exactly.
/// </summary>
public static class PdfExporter
{
    public static async Task SaveAsync(string htmlPath, string pdfPath)
    {
        // WebView2 needs a window; this one sits off-screen and never activates.
        var host = new Window
        {
            Width = 1100, Height = 900, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize
        };
        var view = new WebView2();
        host.Content = view;
        host.Show();
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppSettings.DataFolder, "webview2"));
            await view.EnsureCoreWebView2Async(env);

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.CoreWebView2.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            view.CoreWebView2.Navigate(new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri);
            if (!await loaded.Task) throw new InvalidOperationException("The report page didn't load for PDF conversion.");

            var settings = view.CoreWebView2.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            settings.MarginTop = settings.MarginBottom = 0.45;   // inches
            settings.MarginLeft = settings.MarginRight = 0.4;
            if (!await view.CoreWebView2.PrintToPdfAsync(pdfPath, settings))
                throw new InvalidOperationException("WebView2 couldn't write the PDF.");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new InvalidOperationException("PDF export needs the Microsoft Edge WebView2 Runtime, which is part of Windows 10/11. Install it from Microsoft, or save as HTML instead.");
        }
        finally
        {
            view.Dispose();
            host.Close();
        }
    }
}
