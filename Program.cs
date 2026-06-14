using System.Threading;
using Avalonia;
using FlashCap;
using VideoCaptureCardViewer.Services;

namespace VideoCaptureCardViewer;

internal static class Program
{
    private static Mutex? s_singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        // Diagnostic: dump all capture devices + their formats to a file, then exit.
        if (args.Any(a => string.Equals(a, "--list", StringComparison.OrdinalIgnoreCase)))
        {
            DumpDevices();
            return;
        }

        // Headless screenshot mode: render marketing PNGs and exit (no window, no single-instance guard).
        int idx = Array.FindIndex(args, a => string.Equals(a, "--screenshots", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            var outDir = (idx + 1 < args.Length && !args[idx + 1].StartsWith('-')) ? args[idx + 1] : "screenshots";
            BuildAvaloniaApp().SetupWithoutStarting();
            ScreenshotRunner.RenderAll(outDir);
            return;
        }

        // One viewer at a time — two instances would fight over the same capture device.
        s_singleInstance = new Mutex(initiallyOwned: true, @"Local\VideoCaptureCardViewer.SingleInstance", out bool isNew);
        if (!isNew)
            return;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            try { s_singleInstance.ReleaseMutex(); } catch { /* ignore */ }
            s_singleInstance.Dispose();
        }
    }

    private static void DumpDevices()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            foreach (var d in new CaptureDevices().EnumerateDescriptors())
            {
                sb.AppendLine($"DEVICE: {d.Name}  [{d.Description}]");
                foreach (var c in d.Characteristics)
                    sb.AppendLine($"    {c.PixelFormat,-8} {c.Width}x{c.Height} @ {c.FramesPerSecond} (compressed={c.IsCompression})");
                sb.AppendLine();
            }
        }
        catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vccv_devices.txt");
        try { System.IO.File.WriteAllText(path, sb.ToString()); } catch { }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
