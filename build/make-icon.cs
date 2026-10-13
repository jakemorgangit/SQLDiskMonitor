#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property PublishTrimmed=false

// Draws the SQL Disk Monitor app icon (blue rounded square, white drive) at every Windows icon size and writes a
// multi-resolution .ico plus a 256px PNG preview.   Usage: dotnet run build/make-icon.cs -- <out.ico>
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var output = args.Length > 0 ? args[0] : "app.ico";
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
var frames = sizes.Select(s => (Size: s, Png: Render(s))).ToList();

using (var fs = File.Create(output))
using (var w = new BinaryWriter(fs))
{
    w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
    int offset = 6 + 16 * frames.Count;
    foreach (var (size, png) in frames)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
        w.Write(png.Length); w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in frames) w.Write(png);
}
File.WriteAllBytes(Path.ChangeExtension(output, ".png"), frames[^1].Png);
Console.WriteLine($"Wrote {output} ({frames.Count} sizes)");

static byte[] Render(int size)
{
    // Designed on a 256 grid; small sizes drop detail and snap to whole pixels so they stay crisp.
    double k = size / 256.0;
    bool tiny = size <= 24;
    var v = new DrawingVisual();
    using (var dc = v.RenderOpen())
    {
        dc.PushTransform(new ScaleTransform(k, k));

        var bg = new LinearGradientBrush(Color.FromRgb(0x5A, 0xAE, 0xFF), Color.FromRgb(0x1A, 0x63, 0xE0), new Point(0, 0), new Point(1, 1));
        double inset = tiny ? 0 : 8;
        dc.DrawRoundedRectangle(bg, null, new Rect(inset, inset, 256 - 2 * inset, 256 - 2 * inset), tiny ? 44 : 54, tiny ? 44 : 54);

        // Soft top highlight for depth on larger sizes.
        if (!tiny)
        {
            var shine = new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0.5, 0), new Point(0.5, 0.55));
            dc.DrawRoundedRectangle(shine, null, new Rect(inset, inset, 256 - 2 * inset, 128), 54, 54);
        }

        // The drive: white body, an activity light and a slot.
        var body = tiny ? new Rect(32, 84, 192, 92) : new Rect(42, 92, 172, 78);
        dc.DrawRoundedRectangle(Brushes.White, null, body, tiny ? 22 : 18, tiny ? 22 : 18);
        var accent = new SolidColorBrush(Color.FromRgb(0x1A, 0x63, 0xE0));
        double cy = body.Top + body.Height / 2;
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2E, 0xC2, 0x5A)), null, new Point(body.Right - (tiny ? 34 : 30), cy), tiny ? 16 : 11, tiny ? 16 : 11);
        if (!tiny)
        {
            dc.DrawRoundedRectangle(accent, null, new Rect(body.Left + 22, cy - 6, 70, 12), 6, 6);
            // A second, fainter drive line suggests storage depth.
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), null, new Rect(58, 184, 140, 14), 7, 7);
        }
        dc.Pop();
    }

    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    RenderOptions.SetEdgeMode(v, tiny ? EdgeMode.Aliased : EdgeMode.Unspecified);
    bmp.Render(v);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}
