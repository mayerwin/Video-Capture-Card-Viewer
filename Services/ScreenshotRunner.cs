using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace VideoCaptureCardViewer.Services;

/// <summary>
/// Renders marketing screenshots (app UI over synthetic "fake screen content") straight to PNG via
/// <see cref="RenderTargetBitmap"/>. Deterministic and display-independent — no live window needed.
/// Invoked with: <c>VideoCaptureCardViewer.exe --screenshots &lt;outDir&gt;</c>.
/// </summary>
public static class ScreenshotRunner
{
    private const int W = 1280;
    private const int H = 720;

    private static readonly IBrush ChromeBg = new SolidColorBrush(Color.Parse("#B0151517"));
    private static readonly IBrush TextDim = new SolidColorBrush(Color.Parse("#D8FFFFFF"));
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#FF1F1F22"));
    private static readonly IBrush PanelBg = new SolidColorBrush(Color.Parse("#FF161618"));
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol");

    private static string Glyph(int cp) => char.ConvertFromUtf32(cp);

    public static void RenderAll(string outDir)
    {
        Directory.CreateDirectory(outDir);
        Render(Path.Combine(outDir, "01-main-view.png"), BuildMainView());
        Render(Path.Combine(outDir, "02-settings.png"), BuildSettings(), W, 940);
        Render(Path.Combine(outDir, "03-borderless-native.png"), BuildBorderless());
    }

    private static void Render(string path, Control content, int width = W, int height = H)
    {
        var size = new Size(width, height);
        content.Measure(size);
        content.Arrange(new Rect(size));

        var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        rtb.Render(content);
        rtb.Save(path);
    }

    private static Image FakeScreen() => new()
    {
        Source = DemoContent.CreateTestPattern(W, H),
        Stretch = Stretch.Fill,
    };

    // ---- Scene 1: the normal viewer, chrome visible, capture card auto-selected ----
    private static Control BuildMainView()
    {
        var root = new Grid { Width = W, Height = H, Background = Brushes.Black };
        root.Children.Add(FakeScreen());

        // Top chrome bar
        var chrome = new Border { Height = 34, Background = ChromeBg, VerticalAlignment = VerticalAlignment.Top };
        var chromeGrid = new Grid();
        chromeGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto");

        var title = new TextBlock
        {
            Text = "Capture Viewer  -  Game Capture HDMI  -  1920x1080 60fps",
            Foreground = TextDim,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(title, 0);
        chromeGrid.Children.Add(title);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ChromeButton(Glyph(0xE713)));            // settings
        buttons.Children.Add(ChromeButton("1:1", isText: true));      // native
        buttons.Children.Add(ChromeButton(Glyph(0xE740)));            // fullscreen
        buttons.Children.Add(ChromeButton(Glyph(0xE921)));            // minimize
        buttons.Children.Add(ChromeButton(Glyph(0xE922)));            // maximize
        buttons.Children.Add(ChromeButton(Glyph(0xE8BB), isClose: true)); // close
        Grid.SetColumn(buttons, 1);
        chromeGrid.Children.Add(buttons);

        chrome.Child = chromeGrid;
        root.Children.Add(chrome);

        // "auto-selected capture card" badge
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E0148A3C")),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12, 6),
            Margin = new Thickness(14, 46, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "✓  Auto-selected capture card (webcam skipped)",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
            },
        };
        root.Children.Add(badge);

        return root;
    }

    // ---- Scene 2: settings panel over a dimmed feed ----
    private static Control BuildSettings()
    {
        var root = new Grid { Width = W, Height = 940, Background = Brushes.Black };
        root.Children.Add(FakeScreen());
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#99000000")) });

        var panel = new Border
        {
            Width = 560,
            Background = PanelBg,
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.Parse("#22FFFFFF")),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(18),
        };

        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new TextBlock { Text = "Settings", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4) });

        stack.Children.Add(Card("Source", new[]
        {
            FakeCheck("Automatically pick the capture card (recommended)", true),
            FakeCombo("Device", "Game Capture HDMI   ✓ auto-detected"),
            FakeCombo("Format", "1920 x 1080 @ 60fps   .   YUY2"),
        }));

        stack.Children.Add(Card("Window", new[]
        {
            FakeCheck("Show window border / title bar", false),
            FakeCheck("Always on top", false),
            FakeCheck("Start in fullscreen", false),
            FakeCombo("Scaling", "Uniform (letterbox)"),
            FakeButton("Restore native resolution (1:1, no scaling)"),
        }));

        stack.Children.Add(Card("Audio", new[]
        {
            FakeCheck("Play the capture card's audio (passthrough)", true),
            FakeCombo("Audio input", "Auto (match capture device)"),
        }));

        stack.Children.Add(Card("KVM - control the target's keyboard & mouse", new[]
        {
            FakeCombo("Backend", "Flipper Zero - Bluetooth (recommended for Flipper)"),
            FakeCombo("Bluetooth device (paired)", "Flipper Ak3z"),
            FakeButton("Connect"),
        }));

        panel.Child = stack;
        root.Children.Add(panel);
        return root;
    }

    // ---- Scene 3: borderless 1:1 look ----
    private static Control BuildBorderless()
    {
        var root = new Grid { Width = W, Height = H, Background = Brushes.Black };
        root.Children.Add(FakeScreen());

        var pill = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF15151A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#33FFFFFF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 10),
            Margin = new Thickness(0, 0, 0, 28),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock
            {
                Text = "Borderless  .  1:1 native resolution (no up/downscaling)  .  double-click for fullscreen",
                Foreground = Brushes.White,
                FontSize = 13,
            },
        };
        root.Children.Add(pill);
        return root;
    }

    // ---- building blocks ----

    private static Control ChromeButton(string content, bool isText = false, bool isClose = false)
    {
        return new Border
        {
            Width = 46,
            Height = 34,
            Background = isClose ? new SolidColorBrush(Color.Parse("#E81123")) : Brushes.Transparent,
            Child = new TextBlock
            {
                Text = content,
                Foreground = new SolidColorBrush(Color.Parse("#ECFFFFFF")),
                FontFamily = isText ? FontFamily.Default : IconFont,
                FontSize = isText ? 12 : 13,
                FontWeight = isText ? FontWeight.SemiBold : FontWeight.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static Control Card(string header, IEnumerable<Control> children)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = header, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var c in children) stack.Children.Add(c);
        return new Border { Background = CardBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Child = stack };
    }

    private static Control FakeCombo(string label, string value)
    {
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.Parse("#CCFFFFFF")), FontSize = 12 });
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF2A2A2E")),
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Color.Parse("#22FFFFFF")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new TextBlock { Text = value, Foreground = Brushes.White, FontSize = 12 },
                    Chevron(),
                },
            },
        });
        return stack;
    }

    private static Control Chevron()
    {
        var t = new TextBlock { Text = Glyph(0xE70D), FontFamily = IconFont, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#99FFFFFF")) };
        Grid.SetColumn(t, 1);
        return t;
    }

    private static Control FakeCheck(string label, bool check)
    {
        var box = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = check ? new SolidColorBrush(Color.Parse("#FF3B82F6")) : Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.Parse("#88FFFFFF")),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = check ? new TextBlock { Text = "✓", Foreground = Brushes.White, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static Control FakeButton(string label)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF3A3A40")),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 12 },
        };
    }
}
