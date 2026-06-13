using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FlashCap;
using VideoCaptureCardViewer.Kvm;
using VideoCaptureCardViewer.Services;

namespace VideoCaptureCardViewer.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly bool _demoMode;
    private readonly CaptureService _capture = new();
    private readonly KvmController _kvm = new();
    private readonly AudioService _audio = new();

    // Named controls (resolved after XAML load - avoids relying on generated fields).
    private Border _videoSurface = null!;
    private Image _display = null!;
    private Border _noSignalOverlay = null!;
    private TextBlock _noSignalTitle = null!;
    private TextBlock _noSignalBody = null!;
    private Border _chrome = null!;
    private TextBlock _titleText = null!;
    private Panel _resizeGrips = null!;
    private Button _btnMaxRestore = null!;
    private Button _btnFullscreen = null!;

    // Chrome auto-hide
    private readonly DispatcherTimer _hideTimer;
    private bool _pointerOverChrome;
    private readonly Cursor _hiddenCursor = new(StandardCursorType.None);
    private bool _cursorHidden;

    // Drag handling (threshold-based so a stationary click still registers double-click).
    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private bool _maybeDrag;

    // Window state bookkeeping
    private WindowState _nonFullScreenState = WindowState.Normal;
    private bool _placementApplied;

    // Borderless pseudo-maximize (so we don't cover the taskbar with no title bar to escape).
    private bool _pseudoMax;
    private double _restoreW, _restoreH;
    private PixelPoint _restorePos;

    // Signal watchdog / hotplug
    private readonly DispatcherTimer _watchdog;
    private long _lastFrameTick;
    private bool _noSignalShown;
    private long _lastReconnectTick;
    private bool _reconnecting;
    private string? _lastGoodDeviceName;

    private ContextMenu _contextMenu = null!;
    private IReadOnlyList<CaptureDeviceDescriptor> _devices = Array.Empty<CaptureDeviceDescriptor>();

    // Segoe Fluent / MDL2 glyphs.
    private const string GlyphMaximize = "";
    private const string GlyphRestore = "";
    private const string GlyphFullScreen = "";
    private const string GlyphBackToWindow = "";

    // Designer ctor
    public MainWindow() : this(new AppSettings()) { }

    public MainWindow(AppSettings settings, bool demoMode = false)
    {
        _settings = settings;
        _demoMode = demoMode;
        AvaloniaXamlLoader.Load(this);

        _videoSurface = this.FindControl<Border>("VideoSurface")!;
        _display = this.FindControl<Image>("Display")!;
        _noSignalOverlay = this.FindControl<Border>("NoSignalOverlay")!;
        _noSignalTitle = this.FindControl<TextBlock>("NoSignalTitle")!;
        _noSignalBody = this.FindControl<TextBlock>("NoSignalBody")!;
        _chrome = this.FindControl<Border>("Chrome")!;
        _titleText = this.FindControl<TextBlock>("TitleText")!;
        _resizeGrips = this.FindControl<Panel>("ResizeGrips")!;
        _btnMaxRestore = this.FindControl<Button>("BtnMaxRestore")!;
        _btnFullscreen = this.FindControl<Button>("BtnFullscreen")!;

        _capture.FrameArrived += OnFrameArrived;
        _capture.Error += OnCaptureError;

        // Pointer interactions on the video surface.
        _videoSurface.PointerPressed += OnVideoPointerPressed;
        _videoSurface.PointerMoved += OnVideoPointerMoved;
        _videoSurface.PointerReleased += OnVideoPointerReleased;
        _videoSurface.PointerWheelChanged += OnVideoWheel;
        _videoSurface.DoubleTapped += OnVideoDoubleTapped;

        // Any movement reveals the chrome.
        AddHandler(PointerMovedEvent, (_, _) => { if (!_kvm.IsGrabbed) ShowChrome(); }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        // Tunnel key handlers so a KVM grab can intercept everything (incl. Tab / arrows / shortcuts).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);

        _kvm.StateChanged += OnKvmStateChanged;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _hideTimer.Tick += (_, _) => HideChromeIfIdle();

        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += (_, _) => CheckSignal();

        BuildContextMenu();

        // Apply persisted look-and-feel.
        Topmost = _settings.AlwaysOnTop;
        ApplyBorderSetting();
        ApplyStretchMode();

        PositionChanged += (_, _) => SavePlacementDeferred();
        Closing += OnClosing;
    }

    // ----------------------------------------------------------------- lifecycle

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        ApplyPlacement();

        if (_settings.StartFullScreen)
            SetFullScreen(true);

        UpdateResizeGripsVisibility();
        ShowChrome();
        _watchdog.Start();

        if (_demoMode)
        {
            ShowDemoPattern();
            _titleText.Text = "Capture Viewer - Demo";
        }
        else
        {
            _ = AutoStartAsync();
        }

        if (_settings.KvmAutoConnect)
            _ = KvmConnectFromSettingsAsync();
    }

    private void ApplyPlacement()
    {
        if (_placementApplied) return;
        _placementApplied = true;

        if (!_settings.RememberWindowPlacement) return;

        if (_settings.WindowWidth is > 0 && _settings.WindowHeight is > 0)
        {
            Width = _settings.WindowWidth.Value;
            Height = _settings.WindowHeight.Value;
        }
        if (_settings.WindowX is int x && _settings.WindowY is int y)
        {
            // Only restore if the point is on a connected screen.
            var pt = new PixelPoint(x, y);
            if (Screens.All.Any(s => s.Bounds.Contains(pt)))
                Position = pt;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_settings.RememberWindowPlacement)
        {
            double w = 0, h = 0; PixelPoint pos = default;
            if (_pseudoMax) { w = _restoreW; h = _restoreH; pos = _restorePos; }
            else if (WindowState == WindowState.Normal) { w = Width; h = Height; pos = Position; }

            if (w > 0 && h > 0)
            {
                _settings.WindowWidth = w;
                _settings.WindowHeight = h;
                _settings.WindowX = pos.X;
                _settings.WindowY = pos.Y;
            }
        }
        _settings.Save();

        // Stop timers and detach handlers so nothing fires against a tearing-down window.
        _watchdog.Stop();
        _hideTimer.Stop();
        _capture.FrameArrived -= OnFrameArrived;
        _capture.Error -= OnCaptureError;
        _kvm.StateChanged -= OnKvmStateChanged;

        _audio.Dispose();
        _ = _capture.DisposeAsync();
        _ = _kvm.DisposeAsync();
    }

    // ----------------------------------------------------------------- capture

    private async Task AutoStartAsync()
    {
        _devices = CaptureService.EnumerateDevices();
        if (_devices.Count == 0)
        {
            ShowNoSignal("No capture device detected",
                "Connect an HDMI/USB capture card, then choose it in Settings. Webcams are skipped automatically.");
            ShowDemoPattern();
            return;
        }

        CaptureDeviceDescriptor? chosen = null;
        if (!_settings.AutoSelectCaptureCard && !string.IsNullOrWhiteSpace(_settings.PreferredDeviceName))
            chosen = _devices.FirstOrDefault(d => d.Name == _settings.PreferredDeviceName);

        chosen ??= DeviceHeuristics.PickBest(_devices);
        if (chosen is null)
        {
            ShowNoSignal("No usable capture device", "None of the detected devices expose a video format.");
            return;
        }

        var format = CaptureService.PickBestFormat(chosen, _settings.PreferredFormat);
        if (format is null)
        {
            ShowNoSignal("No usable capture format", $"\"{chosen.Name}\" did not report a supported format.");
            return;
        }

        await SwitchDeviceAsync(chosen, format);
    }

    public async Task SwitchDeviceAsync(CaptureDeviceDescriptor descriptor, VideoCharacteristics characteristics)
    {
        HideNoSignal();
        _lastFrameTick = Environment.TickCount64;

        await _capture.StartAsync(descriptor, characteristics);

        if (_capture.IsRunning)
        {
            // Only remember a device/format that actually started.
            _settings.PreferredDeviceName = descriptor.Name;
            _settings.PreferredFormat = CaptureService.FormatKey(characteristics);
            _settings.Save();
            _lastGoodDeviceName = descriptor.Name;
            StartAudioForCurrentDevice();
        }

        UpdateTitle();
    }

    private void OnFrameArrived(Bitmap bitmap)
    {
        _lastFrameTick = Environment.TickCount64;
        if (_noSignalShown) HideNoSignal();
        _display.Source = bitmap;
    }

    private void OnCaptureError(string message)
    {
        ShowNoSignal("Capture error", message);
    }

    private void CheckSignal()
    {
        if (_demoMode) return;

        if (!_capture.IsRunning)
        {
            TryHotplugReconnect();
            return;
        }

        var idle = Environment.TickCount64 - _lastFrameTick;
        if (idle > 2500 && !_noSignalShown)
            ShowNoSignal("Signal lost", "Waiting for the source. If the device is unplugged it reconnects automatically when it returns; otherwise rescan in Settings.");

        // Only force a restart if the device actually disappeared — restarting a stalled-but-present
        // device would just thrash video and audio while the source resumes on its own.
        if (idle > 4000 && _lastGoodDeviceName is not null)
        {
            bool present = CaptureService.EnumerateDevices().Any(d => d.Name == _lastGoodDeviceName);
            if (!present) TryHotplugReconnect();
        }
    }

    private void TryHotplugReconnect()
    {
        if (_reconnecting) return;
        if (Environment.TickCount64 - _lastReconnectTick < 3000) return;
        _lastReconnectTick = Environment.TickCount64;

        _reconnecting = true;
        _ = ReconnectAsync();
    }

    private async Task ReconnectAsync()
    {
        try
        {
            // Prefer re-acquiring the exact device that was working, so a hotplug doesn't silently
            // switch the source to a different device via the heuristics.
            _devices = CaptureService.EnumerateDevices();
            if (_lastGoodDeviceName is not null)
            {
                var same = _devices.FirstOrDefault(d => d.Name == _lastGoodDeviceName);
                if (same is not null)
                {
                    var fmt = CaptureService.PickBestFormat(same, _settings.PreferredFormat);
                    if (fmt is not null) { await SwitchDeviceAsync(same, fmt); return; }
                }
            }
            await AutoStartAsync();
        }
        finally { _reconnecting = false; }
    }

    public void RescanDevices() => _ = AutoStartAsync();

    public string? CurrentDeviceName => _capture.CurrentDeviceName;

    // ----------------------------------------------------------------- audio

    private void StartAudioForCurrentDevice()
    {
        if (_demoMode) return;
        if (_settings.AudioEnabled)
            _audio.Start(_settings.AudioDeviceName, _capture.CurrentDeviceName);
        else
            _audio.Stop();
    }

    /// <summary>Re-apply the audio setting (called from Settings).</summary>
    public void RestartAudio() => StartAudioForCurrentDevice();

    public string AudioStatus
    {
        get
        {
            if (!_settings.AudioEnabled) return "Off";
            if (_audio.IsRunning) return $"On - {_audio.CurrentDeviceName}";
            return "On, but no matching audio input found";
        }
    }

    private void ToggleAudio()
    {
        _settings.AudioEnabled = !_settings.AudioEnabled;
        _settings.Save();
        StartAudioForCurrentDevice();
    }

    // ----------------------------------------------------------------- KVM API (used by Settings)

    public KvmController Kvm => _kvm;

    public static KvmBackendKind ParseBackend(string? value) => value switch
    {
        "Ch9329" => KvmBackendKind.Ch9329,
        "FlipperZero" => KvmBackendKind.FlipperZero,
        _ => KvmBackendKind.Loopback,
    };

    public async Task<string> KvmConnectFromSettingsAsync()
    {
        var options = new KvmConnectionOptions
        {
            Kind = ParseBackend(_settings.KvmBackend),
            PortName = _settings.KvmComPort,
            BaudRate = _settings.KvmBaudRate,
        };

        try
        {
            await _kvm.ConnectAsync(options);
            return $"Connected: {_kvm.BackendName}";
        }
        catch (Exception ex)
        {
            return $"Connection failed: {ex.Message}";
        }
    }

    public async Task KvmDisconnectAsync() => await _kvm.DisconnectAsync();

    private void OnKvmStateChanged() => Dispatcher.UIThread.Post(UpdateGrabVisual);

    private void ShowDemoPattern()
    {
        try
        {
            int w = _capture.FrameWidth > 0 ? _capture.FrameWidth : 1280;
            int h = _capture.FrameHeight > 0 ? _capture.FrameHeight : 720;
            (_display.Source as IDisposable)?.Dispose();
            _display.Source = DemoContent.CreateTestPattern(Math.Max(640, w), Math.Max(360, h));
        }
        catch
        {
            // Rendering a test pattern is best-effort.
        }
    }

    // ----------------------------------------------------------------- no-signal overlay

    private void ShowNoSignal(string title, string body)
    {
        _noSignalShown = true;
        _noSignalTitle.Text = title;
        _noSignalBody.Text = body;
        _noSignalOverlay.IsVisible = true;
    }

    private void HideNoSignal()
    {
        _noSignalShown = false;
        _noSignalOverlay.IsVisible = false;
    }

    // ----------------------------------------------------------------- chrome auto-hide

    private void ShowChrome()
    {
        _chrome.Opacity = 1;
        _chrome.IsHitTestVisible = true;
        ShowCursor();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideChromeIfIdle()
    {
        _hideTimer.Stop();
        if (_pointerOverChrome || _kvm.IsGrabbed) return;

        _chrome.Opacity = 0;
        _chrome.IsHitTestVisible = false;
        if (WindowState == WindowState.FullScreen)
            HideCursor();
    }

    private void ShowCursor()
    {
        if (_cursorHidden) { Cursor = Cursor.Default; _cursorHidden = false; }
    }

    private void HideCursor()
    {
        if (!_cursorHidden) { Cursor = _hiddenCursor; _cursorHidden = true; }
    }

    private void OnChromePointerEntered(object? sender, PointerEventArgs e) => _pointerOverChrome = true;
    private void OnChromePointerExited(object? sender, PointerEventArgs e) => _pointerOverChrome = false;

    // ----------------------------------------------------------------- video pointer / drag / dbl-click

    private void OnVideoPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_display);

        if (_kvm.IsGrabbed)
        {
            ForwardMouseButton(point, pressed: true);
            e.Handled = true;
            return;
        }

        // While a KVM is connected, the first left-click "grabs" input to the target.
        if (_kvm.IsConnected && point.Properties.IsLeftButtonPressed && TryMapPointer(point, out _, out _))
        {
            _kvm.SetGrab(true);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _pressArgs = e;
            _pressPoint = e.GetPosition(this);
            _maybeDrag = WindowState == WindowState.Normal && !_settings.ShowBorder;
        }
    }

    private void OnVideoPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_kvm.IsGrabbed)
        {
            var pt = e.GetCurrentPoint(_display);
            if (TryMapPointer(pt, out int xN, out int yN))
                _kvm.OnMouseMove(xN, yN);
            return;
        }

        if (!_maybeDrag || _pressArgs is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _maybeDrag = false; return; }

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPoint.X) > 4 || Math.Abs(p.Y - _pressPoint.Y) > 4)
        {
            var args = _pressArgs;
            _pressArgs = null;
            _maybeDrag = false;
            if (_pseudoMax)
            {
                // Dragging "restores" a pseudo-maximized window to its previous size (like Windows).
                _pseudoMax = false;
                Width = _restoreW;
                Height = _restoreH;
                UpdateWindowButtons();
                UpdateResizeGripsVisibility();
            }
            try { BeginMoveDrag(args); } catch { /* move loop may reject on some states */ }
        }
    }

    private void OnVideoPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_kvm.IsGrabbed)
        {
            ForwardMouseButton(e.GetCurrentPoint(_display), pressed: false);
            e.Handled = true;
            return;
        }
        _maybeDrag = false;
        _pressArgs = null;
    }

    private void OnVideoWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_kvm.IsGrabbed) return;
        var pt = e.GetCurrentPoint(_display);
        if (TryMapPointer(pt, out int xN, out int yN))
            _kvm.OnWheel(xN, yN, e.Delta.Y);
        e.Handled = true;
    }

    private void OnVideoDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_kvm.IsGrabbed) return;
        _maybeDrag = false;
        _pressArgs = null;
        ToggleFullScreen();
    }

    // ----------------------------------------------------------------- KVM pointer helpers

    private void ForwardMouseButton(PointerPoint point, bool pressed)
    {
        var button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => KvmMouseButtons.Left,
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => KvmMouseButtons.Right,
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => KvmMouseButtons.Middle,
            _ => KvmMouseButtons.None,
        };
        if (button == KvmMouseButtons.None) return;

        // Ignore clicks in the letterbox bars (outside the actual picture).
        if (!TryMapPointer(point, out int xN, out int yN)) return;
        _kvm.OnMouseButton(button, pressed, xN, yN);
    }

    private bool TryMapPointer(PointerPoint point, out int xNorm, out int yNorm)
    {
        xNorm = 0;
        yNorm = 0;

        int sw = _capture.FrameWidth, sh = _capture.FrameHeight;
        if (sw <= 0 || sh <= 0) return false;

        var size = _display.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return false;

        // Map the pointer to source coordinates, honoring the active Stretch mode so KVM clicks
        // land correctly regardless of how the picture is fitted to the window.
        double dw, dh, ox, oy;
        switch (_settings.StretchMode)
        {
            case "Fill":
                dw = size.Width; dh = size.Height; ox = 0; oy = 0;
                break;
            case "UniformToFill":
            {
                double s = Math.Max(size.Width / sw, size.Height / sh);
                dw = sw * s; dh = sh * s; ox = (size.Width - dw) / 2; oy = (size.Height - dh) / 2;
                break;
            }
            case "None":
                dw = sw; dh = sh; ox = (size.Width - dw) / 2; oy = (size.Height - dh) / 2;
                break;
            default: // Uniform (letterbox)
            {
                double s = Math.Min(size.Width / sw, size.Height / sh);
                dw = sw * s; dh = sh * s; ox = (size.Width - dw) / 2; oy = (size.Height - dh) / 2;
                break;
            }
        }

        double u = (point.Position.X - ox) / dw;
        double v = (point.Position.Y - oy) / dh;
        if (u < 0 || u > 1 || v < 0 || v > 1) return false;

        xNorm = (int)Math.Round(u * 32767);
        yNorm = (int)Math.Round(v * 32767);
        return true;
    }

    private void UpdateGrabVisual()
    {
        if (_kvm.IsGrabbed)
        {
            HideCursor();
            _chrome.Opacity = 0;
            _chrome.IsHitTestVisible = false;
        }
        else
        {
            ShowCursor();
            ShowChrome();
        }
    }

    // ----------------------------------------------------------------- resize grips

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_settings.ShowBorder || WindowState != WindowState.Normal || _pseudoMax) return;
        if (sender is Control c && c.Tag is string tag && Enum.TryParse<WindowEdge>(tag, out var edge))
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                try { BeginResizeDrag(edge, e); } catch { /* ignore */ }
            }
        }
    }

    private void UpdateResizeGripsVisibility()
        => _resizeGrips.IsVisible = !_settings.ShowBorder && WindowState == WindowState.Normal && !_pseudoMax;

    // ----------------------------------------------------------------- chrome buttons

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => OpenSettings();
    private void OnRetryClick(object? sender, RoutedEventArgs e) => RescanDevices();
    private void OnNativeClick(object? sender, RoutedEventArgs e) => RestoreNativeResolution();
    private void OnFullscreenClick(object? sender, RoutedEventArgs e) => ToggleFullScreen();
    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    private void OnMaxRestoreClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    // ----------------------------------------------------------------- actions

    public void OpenSettings()
    {
        var dlg = new SettingsWindow(this, _settings);
        dlg.ShowDialog(this);
    }

    public void ToggleFullScreen() => SetFullScreen(WindowState != WindowState.FullScreen);

    private void SetFullScreen(bool on)
    {
        if (on)
        {
            if (WindowState != WindowState.FullScreen)
                _nonFullScreenState = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else if (WindowState == WindowState.FullScreen)
        {
            WindowState = _nonFullScreenState == WindowState.FullScreen ? WindowState.Normal : _nonFullScreenState;
        }
        UpdateWindowButtons();
        UpdateResizeGripsVisibility();
        ShowChrome();
    }

    private double EffectiveScale() => RenderScaling <= 0 ? 1.0 : RenderScaling;

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.FullScreen) SetFullScreen(false);

        if (_settings.ShowBorder)
        {
            // With OS decorations, normal maximize already respects the work area.
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        // Borderless: emulate maximize to the work area so the taskbar stays visible.
        if (_pseudoMax)
        {
            _pseudoMax = false;
            Position = _restorePos;
            Width = _restoreW;
            Height = _restoreH;
        }
        else
        {
            var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
            if (screen is null) return;

            _restoreW = Width;
            _restoreH = Height;
            _restorePos = Position;
            _pseudoMax = true;

            var wa = screen.WorkingArea;
            Position = wa.Position;
            Width = wa.Width / screen.Scaling;
            Height = wa.Height / screen.Scaling;
        }
        UpdateWindowButtons();
        UpdateResizeGripsVisibility();
    }

    public void RestoreNativeResolution()
    {
        if (_capture.FrameWidth <= 0 || _capture.FrameHeight <= 0) return;

        _pseudoMax = false;
        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;

        // Use the target screen's scaling consistently for both the px->DIP conversion and the clamp.
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        double scale = screen?.Scaling ?? EffectiveScale();
        if (scale <= 0) scale = 1.0;

        double w = _capture.FrameWidth / scale;
        double h = _capture.FrameHeight / scale;

        // Clamp to the screen's work area so a big source doesn't push chrome off-screen.
        if (screen is not null)
        {
            w = Math.Min(w, screen.WorkingArea.Width / scale);
            h = Math.Min(h, screen.WorkingArea.Height / scale);
        }

        Width = w;
        Height = h;
        UpdateResizeGripsVisibility();
    }

    public void ApplyBorderSetting()
    {
        // Switching decoration mode invalidates the borderless pseudo-maximize bookkeeping.
        _pseudoMax = false;
        WindowDecorations = _settings.ShowBorder
            ? Avalonia.Controls.WindowDecorations.Full
            : Avalonia.Controls.WindowDecorations.None;
        UpdateResizeGripsVisibility();
        UpdateWindowButtons();
    }

    public void ApplyAlwaysOnTop() => Topmost = _settings.AlwaysOnTop;

    public void ApplyStretchMode()
    {
        _display.Stretch = _settings.StretchMode switch
        {
            "UniformToFill" => Stretch.UniformToFill,
            "Fill" => Stretch.Fill,
            "None" => Stretch.None,
            _ => Stretch.Uniform,
        };
    }

    private void ToggleBorder()
    {
        _settings.ShowBorder = !_settings.ShowBorder;
        ApplyBorderSetting();
        _settings.Save();
    }

    private void SaveSnapshot()
    {
        if (_display.Source is not Bitmap bmp) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Capture Viewer");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            bmp.Save(path);
        }
        catch
        {
            // Snapshot is best-effort.
        }
    }

    // ----------------------------------------------------------------- window-state reaction

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            UpdateWindowButtons();
            UpdateResizeGripsVisibility();
        }
    }

    private void UpdateWindowButtons()
    {
        bool maxed = WindowState == WindowState.Maximized || _pseudoMax;
        _btnMaxRestore.Content = maxed ? GlyphRestore : GlyphMaximize;
        _btnFullscreen.Content = WindowState == WindowState.FullScreen ? GlyphBackToWindow : GlyphFullScreen;
    }

    private void UpdateTitle()
    {
        if (_capture.IsRunning && _capture.CurrentDeviceName is { } name)
        {
            var fps = _capture.FrameRate > 0 ? $" {_capture.FrameRate:0.##}fps" : string.Empty;
            _titleText.Text = $"Capture Viewer - {name} - {_capture.FrameWidth}x{_capture.FrameHeight}{fps}";
            Title = _titleText.Text;
        }
        else
        {
            _titleText.Text = "Capture Viewer";
            Title = "Capture Viewer";
        }
    }

    private DateTime _lastPlacementSave;
    private void SavePlacementDeferred()
    {
        // Throttle: PositionChanged fires rapidly while dragging.
        var now = DateTime.UtcNow;
        if ((now - _lastPlacementSave).TotalMilliseconds < 500) return;
        _lastPlacementSave = now;
        if (_settings.RememberWindowPlacement && WindowState == WindowState.Normal && !_pseudoMax)
        {
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
        }
    }

    // ----------------------------------------------------------------- keyboard

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Scroll Lock toggles the KVM grab so you're never trapped controlling the target.
        if (_kvm.IsConnected && e.Key == Key.Scroll)
        {
            _kvm.ToggleGrab();
            e.Handled = true;
            return;
        }

        if (_kvm.IsGrabbed)
        {
            _kvm.OnKeyDown(e.Key);
            e.Handled = true;
            return;
        }

        if (e.Handled) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullScreen(); e.Handled = true; break;
            case Key.Escape when WindowState == WindowState.FullScreen:
                SetFullScreen(false); e.Handled = true; break;
            case Key.OemComma when ctrl:
                OpenSettings(); e.Handled = true; break;
            case Key.B when ctrl:
                ToggleBorder(); e.Handled = true; break;
            case Key.D0 when ctrl:
            case Key.NumPad0 when ctrl:
                RestoreNativeResolution(); e.Handled = true; break;
            case Key.M when ctrl && shift:
                ToggleAudio(); e.Handled = true; break;
            case Key.M when ctrl:
                WindowState = WindowState.Minimized; e.Handled = true; break;
            case Key.S when ctrl:
                SaveSnapshot(); e.Handled = true; break;
        }
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (_kvm.IsGrabbed)
        {
            _kvm.OnKeyUp(e.Key);
            e.Handled = true;
        }
    }

    // ----------------------------------------------------------------- context menu

    private void BuildContextMenu()
    {
        _contextMenu = new ContextMenu();
        _contextMenu.Opening += (_, _) => RebuildContextMenu();
        _videoSurface.ContextMenu = _contextMenu;
        _display.ContextMenu = _contextMenu;
    }

    private void RebuildContextMenu()
    {
        var items = new List<object>();

        var settingsItem = new MenuItem { Header = "Settings...", InputGesture = new KeyGesture(Key.OemComma, KeyModifiers.Control) };
        settingsItem.Click += (_, _) => OpenSettings();
        items.Add(settingsItem);

        // Device submenu
        var deviceMenu = new MenuItem { Header = "Select device" };
        var devices = _devices.Count > 0 ? _devices : (_devices = CaptureService.EnumerateDevices());
        if (devices.Count == 0)
        {
            deviceMenu.Items.Add(new MenuItem { Header = "(no devices found)", IsEnabled = false });
        }
        else
        {
            foreach (var d in devices)
            {
                var captured = d;
                var mi = new MenuItem
                {
                    Header = d.Name,
                    ToggleType = MenuItemToggleType.Radio,
                    GroupName = "Devices",
                    IsChecked = d.Name == _capture.CurrentDeviceName,
                };
                mi.Click += async (_, _) =>
                {
                    var fmt = CaptureService.PickBestFormat(captured, _settings.PreferredFormat);
                    if (fmt is not null) await SwitchDeviceAsync(captured, fmt);
                };
                deviceMenu.Items.Add(mi);
            }
        }
        var rescan = new MenuItem { Header = "Rescan devices" };
        rescan.Click += (_, _) => RescanDevices();
        deviceMenu.Items.Add(new Separator());
        deviceMenu.Items.Add(rescan);
        items.Add(deviceMenu);

        items.Add(new Separator());

        var fs = new MenuItem { Header = "Fullscreen", InputGesture = new KeyGesture(Key.F11), ToggleType = MenuItemToggleType.CheckBox, IsChecked = WindowState == WindowState.FullScreen };
        fs.Click += (_, _) => ToggleFullScreen();
        items.Add(fs);

        var native = new MenuItem { Header = "Restore native resolution (1:1)", InputGesture = new KeyGesture(Key.D0, KeyModifiers.Control) };
        native.Click += (_, _) => RestoreNativeResolution();
        items.Add(native);

        var snap = new MenuItem { Header = "Save snapshot", InputGesture = new KeyGesture(Key.S, KeyModifiers.Control) };
        snap.Click += (_, _) => SaveSnapshot();
        items.Add(snap);

        var border = new MenuItem { Header = "Show window border", InputGesture = new KeyGesture(Key.B, KeyModifiers.Control), ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.ShowBorder };
        border.Click += (_, _) => ToggleBorder();
        items.Add(border);

        var aot = new MenuItem { Header = "Always on top", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.AlwaysOnTop };
        aot.Click += (_, _) => { _settings.AlwaysOnTop = !_settings.AlwaysOnTop; ApplyAlwaysOnTop(); _settings.Save(); };
        items.Add(aot);

        var audio = new MenuItem { Header = "Audio passthrough", InputGesture = new KeyGesture(Key.M, KeyModifiers.Control | KeyModifiers.Shift), ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.AudioEnabled };
        audio.Click += (_, _) => ToggleAudio();
        items.Add(audio);

        items.Add(new Separator());

        var min = new MenuItem { Header = "Minimize" };
        min.Click += (_, _) => WindowState = WindowState.Minimized;
        items.Add(min);

        var max = new MenuItem { Header = (WindowState == WindowState.Maximized || _pseudoMax) ? "Restore" : "Maximize" };
        max.Click += (_, _) => ToggleMaximize();
        items.Add(max);

        items.Add(new Separator());

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Close();
        items.Add(exit);

        _contextMenu.ItemsSource = items;
    }
}

