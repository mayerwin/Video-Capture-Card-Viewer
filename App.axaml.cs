using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VideoCaptureCardViewer.Services;
using VideoCaptureCardViewer.Views;

namespace VideoCaptureCardViewer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            var demoMode = desktop.Args?.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase)) == true;
            desktop.MainWindow = new MainWindow(settings, demoMode);
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
