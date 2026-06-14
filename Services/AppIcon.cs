using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace VideoCaptureCardViewer.Services;

/// <summary>
/// Draws the app icon (same mark as the website favicon: dark rounded square + SMPTE colour bars +
/// a green "REC" dot) and assembles a multi-size Windows .ico. Generated via <c>--make-icon</c>.
/// </summary>
public static class AppIcon
{
    private static readonly Color Bg = Color.Parse("#090A0C");
    private static readonly Color[] Bars =
    {
        Color.Parse("#D6D6D6"), Color.Parse("#E3E30F"), Color.Parse("#16D6D6"),
        Color.Parse("#18C018"), Color.Parse("#D61FD6"),
    };
    private static readonly Color Dot = Color.Parse("#2BF5A0");

    /// <summary>Loads the embedded icon as a window/taskbar icon (best-effort).</summary>
    public static Avalonia.Controls.WindowIcon? LoadWindowIcon()
    {
        try
        {
            var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://VideoCaptureCardViewer/icon.ico"));
            return new Avalonia.Controls.WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    public static RenderTargetBitmap Render(int size)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using var ctx = rtb.CreateDrawingContext();
        double f = size / 16.0;

        ctx.DrawRectangle(new SolidColorBrush(Bg), null, new RoundedRect(new Rect(0, 0, size, size), 3 * f));

        double x0 = 2 * f, top = 3 * f, barH = 7 * f, totalW = 12 * f, barW = totalW / Bars.Length;
        for (int i = 0; i < Bars.Length; i++)
            ctx.FillRectangle(new SolidColorBrush(Bars[i]), new Rect(x0 + i * barW, top, barW + 0.6, barH));

        ctx.DrawEllipse(new SolidColorBrush(Dot), null, new Point(8 * f, 12.4 * f), 2.1 * f, 2.1 * f);
        return rtb;
    }

    public static byte[] BuildIco(params int[] sizes)
    {
        var imgs = new List<(int Size, byte[] Png)>();
        foreach (var s in sizes)
        {
            using var rtb = Render(s);
            using var png = new MemoryStream();
            rtb.Save(png);
            imgs.Add((s, png.ToArray()));
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0);            // reserved
        w.Write((short)1);            // type = icon
        w.Write((short)imgs.Count);   // image count

        int offset = 6 + 16 * imgs.Count;
        foreach (var (size, png) in imgs)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); // width  (0 => 256)
            w.Write((byte)(size >= 256 ? 0 : size)); // height
            w.Write((byte)0);   // palette count
            w.Write((byte)0);   // reserved
            w.Write((short)1);  // colour planes
            w.Write((short)32); // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in imgs)
            w.Write(png);

        w.Flush();
        return ms.ToArray();
    }
}
