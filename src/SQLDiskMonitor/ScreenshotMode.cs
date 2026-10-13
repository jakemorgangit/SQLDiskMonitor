using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SQLDiskMonitor.ViewModels;

namespace SQLDiskMonitor;

/// <summary>
/// Renders pages to PNG without user interaction (used for README screenshots and UI checks):
///   SQLDiskMonitor.exe --screenshot out.png [--page Pressure|all] [--theme light] [--range 7d] [--db path]
/// </summary>
public static class ScreenshotMode
{
    public static async Task RunAsync(MainWindow window, MainViewModel vm, string path, string? page, string? range)
    {
        try
        {
            window.Width = 1480;
            window.Height = 940;
            window.ShowInTaskbar = false;
            window.Show();
            if (vm.Servers.Count == 0) await vm.AddDemoServerCommand.ExecuteAsync(null);
            if (range != null) vm.SelectedRange = vm.TimeRanges.FirstOrDefault(r => r.Label == range) ?? vm.SelectedRange;

            var pages = page?.Equals("all", StringComparison.OrdinalIgnoreCase) == true
                ? vm.Pages.Where(p => p.IsVisible).ToList()
                : [vm.Pages.FirstOrDefault(p => p.Title.Equals(page ?? "Overview", StringComparison.OrdinalIgnoreCase)) ?? vm.Dashboard];

            foreach (var p in pages)
            {
                if (p == vm.Disks) vm.Disks.PendingVolume = "D:\\";
                vm.CurrentPage = p;
                await p.RefreshAsync();
                await Task.Delay(1500);
                var file = pages.Count == 1 ? path
                    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $"{Path.GetFileNameWithoutExtension(path)}-{p.Title.ToLowerInvariant()}.png");
                Save(window, file);
                if (p == vm.Dashboard && pages.Count > 1)
                {
                    foreach (var layout in new[] { "Compact", "List" })
                    {
                        vm.Dashboard.CardLayout = layout;
                        await Task.Delay(800);
                        Save(window, file.Replace("-overview.png", $"-overview-{layout.ToLowerInvariant()}.png"));
                    }
                    vm.Dashboard.CardLayout = "Normal";
                }
                if (p == vm.Disks && pages.Count > 1)
                {
                    vm.Disks.ViewMode = "Inventory";
                    await Task.Delay(800);
                    Save(window, file.Replace("-disks.png", "-inventory.png"));
                    vm.Disks.ViewMode = "Details";
                }
            }
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

    private static void Save(Window w, string file)
    {
        var root = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(w);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("B.Bg"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
