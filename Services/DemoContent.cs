using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace VideoCaptureCardViewer.Services;

/// <summary>
/// Generates synthetic "fake screen content" — a broadcast-style test pattern — used as the
/// no-signal fallback and for marketing screenshots. Must be called on the UI thread (uses
/// <see cref="RenderTargetBitmap"/>).
/// </summary>
public static class DemoContent
{
    private static readonly Color[] s_bars =
    {
        Color.FromRgb(0xC0, 0xC0, 0xC0), // grey/white
        Color.FromRgb(0xC0, 0xC0, 0x00), // yellow
        Color.FromRgb(0x00, 0xC0, 0xC0), // cyan
        Color.FromRgb(0x00, 0xC0, 0x00), // green
        Color.FromRgb(0xC0, 0x00, 0xC0), // magenta
        Color.FromRgb(0xC0, 0x00, 0x00), // red
        Color.FromRgb(0x00, 0x00, 0xC0), // blue
    };

    public static RenderTargetBitmap CreateTestPattern(int width, int height, string caption = "VIDEO CAPTURE · TEST PATTERN")
    {
        var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using var ctx = rtb.CreateDrawingContext();

        double w = width, h = height;

        // ---- Top ~67%: SMPTE-style colour bars ----
        double barsHeight = h * 0.67;
        double barWidth = w / s_bars.Length;
        for (int i = 0; i < s_bars.Length; i++)
        {
            ctx.FillRectangle(new SolidColorBrush(s_bars[i]),
                new Rect(i * barWidth, 0, barWidth + 1, barsHeight));
        }

        // ---- Middle strip: castellation ----
        double stripTop = barsHeight;
        double stripHeight = h * 0.08;
        for (int i = 0; i < s_bars.Length; i++)
        {
            var c = (i % 2 == 0) ? Color.FromRgb(0x10, 0x10, 0x10) : s_bars[s_bars.Length - 1 - i];
            ctx.FillRectangle(new SolidColorBrush(c), new Rect(i * barWidth, stripTop, barWidth + 1, stripHeight));
        }

        // ---- Bottom: dark panel with a gradient pluge + labels ----
        double panelTop = stripTop + stripHeight;
        var panel = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x05, 0x05, 0x08), 0),
                new GradientStop(Color.FromRgb(0x18, 0x18, 0x22), 0.5),
                new GradientStop(Color.FromRgb(0x05, 0x05, 0x08), 1),
            },
        };
        ctx.FillRectangle(panel, new Rect(0, panelTop, w, h - panelTop));

        // Centre crosshair box over the whole frame.
        var thin = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), Math.Max(1, w / 1280));
        ctx.DrawLine(thin, new Point(w / 2, 0), new Point(w / 2, h));
        ctx.DrawLine(thin, new Point(0, h / 2), new Point(w, h / 2));

        // Caption.
        DrawCenteredText(ctx, caption, new Point(w / 2, panelTop + (h - panelTop) * 0.42),
            Math.Max(16, h / 22), Colors.White, bold: true);

        DrawCenteredText(ctx, $"{width} × {height}", new Point(w / 2, panelTop + (h - panelTop) * 0.72),
            Math.Max(12, h / 36), Color.FromRgb(0x9A, 0xD0, 0xFF), bold: false);

        return rtb;
    }

    private static void DrawCenteredText(DrawingContext ctx, string text, Point center, double size, Color color, bool bold)
    {
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, size, new SolidColorBrush(color));
        ctx.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }
}
