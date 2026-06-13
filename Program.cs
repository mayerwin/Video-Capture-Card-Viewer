using System.Threading;
using Avalonia;
using VideoCaptureCardViewer.Services;

namespace VideoCaptureCardViewer;

internal static class Program
{
    private static Mutex? s_singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
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

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
