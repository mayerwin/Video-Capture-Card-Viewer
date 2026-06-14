using System.IO.Ports;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FlashCap;
using VideoCaptureCardViewer.Kvm;
using VideoCaptureCardViewer.Services;

namespace VideoCaptureCardViewer.Views;

public partial class SettingsWindow : Window
{
    private readonly MainWindow? _owner;
    private readonly AppSettings _settings;

    private ComboBox _deviceCombo = null!;
    private ComboBox _formatCombo = null!;
    private Button _applyButton = null!;
    private CheckBox _autoSelectCheck = null!;
    private CheckBox _borderCheck = null!;
    private CheckBox _alwaysOnTopCheck = null!;
    private CheckBox _startFsCheck = null!;
    private CheckBox _rememberCheck = null!;
    private TextBlock _statusText = null!;

    private ComboBox _stretchCombo = null!;
    private CheckBox _audioCheck = null!;
    private ComboBox _audioCombo = null!;
    private TextBlock _audioStatusText = null!;

    private ComboBox _kvmBackendCombo = null!;
    private ComboBox _kvmPortCombo = null!;
    private TextBlock _kvmPortLabel = null!;
    private ComboBox _kvmBaudCombo = null!;
    private StackPanel _kvmBaudPanel = null!;
    private CheckBox _kvmAutoConnectCheck = null!;
    private Button _kvmConnectButton = null!;
    private TextBlock _kvmStatus = null!;

    private const string AudioAutoLabel = "Auto (match capture device)";

    private IReadOnlyList<CaptureDeviceDescriptor> _devices = Array.Empty<CaptureDeviceDescriptor>();
    private bool _suppressEvents;

    private sealed record DeviceItem(CaptureDeviceDescriptor Descriptor, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record FormatItem(VideoCharacteristics Characteristics, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record BackendItem(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record BleItem(string Id, string Name)
    {
        public override string ToString() => string.IsNullOrEmpty(Name) ? Id : Name;
    }

    // Designer ctor
    public SettingsWindow() : this(null!, new AppSettings()) { }

    public SettingsWindow(MainWindow owner, AppSettings settings)
    {
        _owner = owner;
        _settings = settings;
        AvaloniaXamlLoader.Load(this);
        Icon = AppIcon.LoadWindowIcon();

        _deviceCombo = this.FindControl<ComboBox>("DeviceCombo")!;
        _formatCombo = this.FindControl<ComboBox>("FormatCombo")!;
        _applyButton = this.FindControl<Button>("ApplyButton")!;
        _autoSelectCheck = this.FindControl<CheckBox>("AutoSelectCheck")!;
        _borderCheck = this.FindControl<CheckBox>("BorderCheck")!;
        _alwaysOnTopCheck = this.FindControl<CheckBox>("AlwaysOnTopCheck")!;
        _startFsCheck = this.FindControl<CheckBox>("StartFsCheck")!;
        _rememberCheck = this.FindControl<CheckBox>("RememberCheck")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;

        _stretchCombo = this.FindControl<ComboBox>("StretchCombo")!;
        _audioCheck = this.FindControl<CheckBox>("AudioCheck")!;
        _audioCombo = this.FindControl<ComboBox>("AudioCombo")!;
        _audioStatusText = this.FindControl<TextBlock>("AudioStatus")!;

        _kvmBackendCombo = this.FindControl<ComboBox>("KvmBackendCombo")!;
        _kvmPortCombo = this.FindControl<ComboBox>("KvmPortCombo")!;
        _kvmPortLabel = this.FindControl<TextBlock>("KvmPortLabel")!;
        _kvmBaudCombo = this.FindControl<ComboBox>("KvmBaudCombo")!;
        _kvmBaudPanel = this.FindControl<StackPanel>("KvmBaudPanel")!;
        _kvmAutoConnectCheck = this.FindControl<CheckBox>("KvmAutoConnectCheck")!;
        _kvmConnectButton = this.FindControl<Button>("KvmConnectButton")!;
        _kvmStatus = this.FindControl<TextBlock>("KvmStatus")!;

        _suppressEvents = true;
        _autoSelectCheck.IsChecked = _settings.AutoSelectCaptureCard;
        _borderCheck.IsChecked = _settings.ShowBorder;
        _alwaysOnTopCheck.IsChecked = _settings.AlwaysOnTop;
        _startFsCheck.IsChecked = _settings.StartFullScreen;
        _rememberCheck.IsChecked = _settings.RememberWindowPlacement;
        _audioCheck.IsChecked = _settings.AudioEnabled;
        _kvmAutoConnectCheck.IsChecked = _settings.KvmAutoConnect;
        _suppressEvents = false;

        PopulateDevices();
        PopulateStretch();
        PopulateAudio();
        PopulateKvm();

        _deviceCombo.SelectionChanged += OnDeviceSelectionChanged;
        _autoSelectCheck.IsCheckedChanged += (_, _) => { if (!_suppressEvents) { _settings.AutoSelectCaptureCard = _autoSelectCheck.IsChecked == true; _settings.Save(); } };
        _borderCheck.IsCheckedChanged += (_, _) => { if (_suppressEvents) return; _settings.ShowBorder = _borderCheck.IsChecked == true; _owner?.ApplyBorderSetting(); _settings.Save(); };
        _alwaysOnTopCheck.IsCheckedChanged += (_, _) => { if (_suppressEvents) return; _settings.AlwaysOnTop = _alwaysOnTopCheck.IsChecked == true; _owner?.ApplyAlwaysOnTop(); _settings.Save(); };
        _startFsCheck.IsCheckedChanged += (_, _) => { if (!_suppressEvents) { _settings.StartFullScreen = _startFsCheck.IsChecked == true; _settings.Save(); } };
        _rememberCheck.IsCheckedChanged += (_, _) => { if (!_suppressEvents) { _settings.RememberWindowPlacement = _rememberCheck.IsChecked == true; _settings.Save(); } };

        _stretchCombo.SelectionChanged += (_, _) => { if (_suppressEvents) return; if (_stretchCombo.SelectedItem is string s) { _settings.StretchMode = s; _owner?.ApplyStretchMode(); _settings.Save(); } };
        _audioCheck.IsCheckedChanged += (_, _) => { if (_suppressEvents) return; _settings.AudioEnabled = _audioCheck.IsChecked == true; _settings.Save(); _owner?.RestartAudio(); UpdateAudioStatus(); };
        _audioCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            _settings.AudioDeviceName = _audioCombo.SelectedItem as string == AudioAutoLabel ? null : _audioCombo.SelectedItem as string;
            _settings.Save();
            _owner?.RestartAudio();
            UpdateAudioStatus();
        };

        _kvmBackendCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            if (_kvmBackendCombo.SelectedItem is BackendItem b)
            {
                _settings.KvmBackend = b.Value;
                _settings.Save();
                ApplyBackendMode(b.Value);
                UpdateKvmUi();
            }
        };
        _kvmPortCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            if (_settings.KvmBackend == "FlipperBle")
            {
                // Ignore the "Scanning..."/"(none)" placeholders, which have an empty Id.
                if (_kvmPortCombo.SelectedItem is BleItem ble && !string.IsNullOrEmpty(ble.Id))
                {
                    _settings.KvmBleDeviceId = ble.Id;
                    _settings.KvmBleDeviceName = ble.Name;
                    _settings.Save();
                }
            }
            else
            {
                _settings.KvmComPort = _kvmPortCombo.SelectedItem as string;
                _settings.Save();
            }
        };
        _kvmBaudCombo.SelectionChanged += (_, _) => { if (_suppressEvents) return; if (_kvmBaudCombo.SelectedItem is int baud) { _settings.KvmBaudRate = baud; _settings.Save(); } };
        _kvmAutoConnectCheck.IsCheckedChanged += (_, _) => { if (!_suppressEvents) { _settings.KvmAutoConnect = _kvmAutoConnectCheck.IsChecked == true; _settings.Save(); } };

        if (_owner is not null)
            _owner.Kvm.StateChanged += OnKvmStateChanged;
        Closed += (_, _) => { if (_owner is not null) _owner.Kvm.StateChanged -= OnKvmStateChanged; };

        UpdateKvmUi();
        UpdateAudioStatus();
    }

    private void PopulateStretch()
    {
        _suppressEvents = true;
        var modes = new List<string> { "Uniform", "UniformToFill", "Fill", "None" };
        _stretchCombo.ItemsSource = modes;
        _stretchCombo.SelectedItem = modes.Contains(_settings.StretchMode) ? _settings.StretchMode : "Uniform";
        _suppressEvents = false;
    }

    private void PopulateAudio()
    {
        _suppressEvents = true;
        var items = new List<string> { AudioAutoLabel };
        try { items.AddRange(AudioService.ListInputs().Select(i => i.Name)); } catch { }
        _audioCombo.ItemsSource = items;
        _audioCombo.SelectedItem = (!string.IsNullOrEmpty(_settings.AudioDeviceName) && items.Contains(_settings.AudioDeviceName))
            ? _settings.AudioDeviceName
            : AudioAutoLabel;
        _suppressEvents = false;
    }

    private void OnRefreshAudioClick(object? sender, RoutedEventArgs e) { PopulateAudio(); UpdateAudioStatus(); }

    private void UpdateAudioStatus()
    {
        if (_owner is not null) _audioStatusText.Text = "Status: " + _owner.AudioStatus;
    }

    private void PopulateKvm()
    {
        _suppressEvents = true;

        var backends = new List<BackendItem>
        {
            new("Loopback", "Loopback (no hardware - logs only)"),
            new("Ch9329", "CH9329 USB-serial HID dongle (recommended dongle)"),
            new("FlipperBle", "Flipper Zero - Bluetooth (recommended for Flipper)"),
            new("FlipperSerial", "Flipper Zero - USB / serial"),
        };
        _kvmBackendCombo.ItemsSource = backends;
        var selected = backends.FirstOrDefault(b => b.Value == _settings.KvmBackend)
            ?? (_settings.KvmBackend == "FlipperZero" ? backends.First(b => b.Value == "FlipperSerial") : backends[0]);
        _kvmBackendCombo.SelectedItem = selected;

        var bauds = new List<int> { 9600, 19200, 38400, 57600, 115200 };
        _kvmBaudCombo.ItemsSource = bauds;
        _kvmBaudCombo.SelectedItem = bauds.Contains(_settings.KvmBaudRate) ? _settings.KvmBaudRate : 115200;

        _suppressEvents = false;

        ApplyBackendMode(selected.Value);
    }

    /// <summary>Switch the device picker between COM ports (serial backends) and paired BLE devices.</summary>
    private void ApplyBackendMode(string backend)
    {
        bool ble = backend == "FlipperBle";
        _kvmPortLabel.Text = ble ? "Bluetooth device (paired)" : "Serial / COM port";
        _kvmBaudPanel.IsVisible = !ble;

        if (ble) PopulateBleDevices();
        else RefreshPorts();
    }

    private void RefreshPorts()
    {
        string[] ports;
        try { ports = SerialPort.GetPortNames(); }
        catch { ports = Array.Empty<string>(); }

        var wasSuppressed = _suppressEvents;
        _suppressEvents = true;
        _kvmPortCombo.ItemsSource = ports;
        _kvmPortCombo.SelectedItem = ports.FirstOrDefault(p => p == _settings.KvmComPort) ?? ports.FirstOrDefault();
        _suppressEvents = wasSuppressed;
    }

    private int _bleScanGen;

    private async void PopulateBleDevices()
    {
        int gen = ++_bleScanGen; // supersede any in-flight scan (rapid backend switch / refresh)

        _suppressEvents = true;
        _kvmPortCombo.ItemsSource = new List<BleItem> { new(string.Empty, "Scanning paired devices...") };
        _kvmPortCombo.SelectedIndex = 0;
        _suppressEvents = false;

        IReadOnlyList<(string Id, string Name)> devices;
        try { devices = await Kvm.FlipperBleBackend.ListPairedAsync(); }
        catch { devices = Array.Empty<(string, string)>(); }

        if (gen != _bleScanGen) return; // a newer scan/backend-change won

        try
        {
            var items = devices.Select(d => new BleItem(d.Id, d.Name)).ToList();
            _suppressEvents = true;
            if (items.Count == 0)
            {
                _kvmPortCombo.ItemsSource = new List<BleItem> { new(string.Empty, "(no paired Bluetooth devices)") };
                _kvmPortCombo.SelectedIndex = 0;
            }
            else
            {
                _kvmPortCombo.ItemsSource = items;
                _kvmPortCombo.SelectedItem =
                    items.FirstOrDefault(i => i.Id == _settings.KvmBleDeviceId)
                    ?? items.FirstOrDefault(i => i.Name == _settings.KvmBleDeviceName)
                    ?? items.FirstOrDefault();
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void OnRefreshPortsClick(object? sender, RoutedEventArgs e) => ApplyBackendMode(_settings.KvmBackend);

    private async void OnKvmConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_owner is null) return;

        if (_owner.Kvm.IsConnected)
        {
            await _owner.KvmDisconnectAsync();
            _kvmStatus.Text = "Disconnected";
            UpdateKvmUi();
            return;
        }

        // Persist current selections, then connect.
        if (_kvmBackendCombo.SelectedItem is BackendItem b) _settings.KvmBackend = b.Value;
        if (_settings.KvmBackend == "FlipperBle")
        {
            if (_kvmPortCombo.SelectedItem is BleItem ble && !string.IsNullOrEmpty(ble.Id))
            {
                _settings.KvmBleDeviceId = ble.Id;
                _settings.KvmBleDeviceName = ble.Name;
            }
        }
        else
        {
            _settings.KvmComPort = _kvmPortCombo.SelectedItem as string;
            if (_kvmBaudCombo.SelectedItem is int baud) _settings.KvmBaudRate = baud;
        }
        _settings.Save();

        _kvmStatus.Text = "Connecting...";
        _kvmConnectButton.IsEnabled = false;
        var result = await _owner.KvmConnectFromSettingsAsync();
        _kvmConnectButton.IsEnabled = true;
        _kvmStatus.Text = result;
        UpdateKvmUi();
    }

    private void OnKvmStateChanged() => Dispatcher.UIThread.Post(UpdateKvmUi);

    private void UpdateKvmUi()
    {
        bool connected = _owner?.Kvm.IsConnected == true;
        var status = _kvmStatus.Text ?? string.Empty;
        _kvmConnectButton.Content = connected ? "Disconnect" : "Connect";
        if (connected && (status.Length == 0 || status == "Disconnected"))
            _kvmStatus.Text = $"Connected: {_owner!.Kvm.BackendName}";
        else if (!connected && _owner is not null && !status.StartsWith("Connection failed"))
            _kvmStatus.Text = "Disconnected";
    }

    private void PopulateDevices()
    {
        _devices = CaptureService.EnumerateDevices();
        var autoBest = DeviceHeuristics.PickBest(_devices);

        var items = new List<DeviceItem>();
        foreach (var d in _devices)
        {
            var tag = ReferenceEquals(d, autoBest) ? "  ✓ auto-detected capture card" : string.Empty;
            items.Add(new DeviceItem(d, d.Name + tag));
        }

        _suppressEvents = true;
        _deviceCombo.ItemsSource = items;

        var currentName = _owner?.CurrentDeviceName ?? _settings.PreferredDeviceName;
        var selected = items.FirstOrDefault(i => i.Descriptor.Name == currentName) ?? items.FirstOrDefault();
        _deviceCombo.SelectedItem = selected;
        _suppressEvents = false;

        _applyButton.IsEnabled = items.Count > 0;
        PopulateFormats();

        if (items.Count == 0)
            _statusText.Text = "No capture devices found.";
    }

    private void OnDeviceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        PopulateFormats();
    }

    private void PopulateFormats()
    {
        if (_deviceCombo.SelectedItem is not DeviceItem dev)
        {
            _formatCombo.ItemsSource = null;
            return;
        }

        var formats = dev.Descriptor.Characteristics
            .Where(c => c.PixelFormat != PixelFormats.Unknown && c.Width > 0 && c.Height > 0)
            .OrderByDescending(c => (long)c.Width * c.Height)
            .ThenByDescending(DeviceHeuristics.ToFps)
            .Select(c =>
            {
                var fps = DeviceHeuristics.ToFps(c);
                var fpsText = fps > 0 ? $" @ {fps:0.##}fps" : string.Empty;
                return new FormatItem(c, $"{c.Width} × {c.Height}{fpsText}  ·  {c.PixelFormat}");
            })
            .ToList();

        _suppressEvents = true;
        _formatCombo.ItemsSource = formats;

        var preferredKey = (dev.Descriptor.Name == _owner?.CurrentDeviceName)
            ? _settings.PreferredFormat
            : null;
        var match = preferredKey is not null
            ? formats.FirstOrDefault(f => CaptureService.FormatKey(f.Characteristics) == preferredKey)
            : null;
        _formatCombo.SelectedItem = match ?? formats.FirstOrDefault();
        _suppressEvents = false;
    }

    private async void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (_owner is null) return;
        if (_deviceCombo.SelectedItem is not DeviceItem dev) return;

        var format = (_formatCombo.SelectedItem as FormatItem)?.Characteristics
                     ?? CaptureService.PickBestFormat(dev.Descriptor);
        if (format is null)
        {
            _statusText.Text = "Selected device has no usable format.";
            return;
        }

        _statusText.Text = $"Starting {dev.Descriptor.Name}…";
        await _owner.SwitchDeviceAsync(dev.Descriptor, format);
        _statusText.Text = "Source applied.";
    }

    private void OnNativeClick(object? sender, RoutedEventArgs e)
    {
        _owner?.RestoreNativeResolution();
        _statusText.Text = "Window set to native resolution.";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
