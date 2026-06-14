using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace VideoCaptureCardViewer.Kvm;

/// <summary>
/// Drives a Flipper Zero over Bluetooth LE — the primary KVM topology:
///   PC (this app, paired over Bluetooth) → Flipper → USB HID → target machine.
///
/// Connects to the Flipper's custom serial GATT service and writes the same ASCII line protocol the
/// serial backend uses (see <see cref="FlipperProtocol"/>). It also subscribes to the serial RX
/// notify characteristic — this forces a real GATT connection and matches what the Flipper serial
/// service expects of a client — and watches the link so a drop faults instead of silently
/// pretending to be connected. The Flipper must be paired in Windows Bluetooth settings and running
/// the companion app (flipper-companion/) in BLE-bridge mode.
/// </summary>
public sealed class FlipperBleBackend : IKvmBackend
{
    // Flipper Zero custom serial service.
    private static readonly Guid ServiceUuid = new("8fe5b3d5-2e7f-4a98-2a48-7acc60fe0000");
    // Characteristic the central WRITES to (data -> Flipper).
    private static readonly Guid WriteCharUuid = new("19ed82ae-ed21-4c9d-4145-228e62fe0000");
    // Characteristic the Flipper NOTIFIES on (data -> central). We subscribe to force the connection.
    private static readonly Guid NotifyCharUuid = new("19ed82ae-ed21-4c9d-4145-228e61fe0000");

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeChar;
    private GattCharacteristic? _notifyChar;
    private GattWriteOption _writeOption = GattWriteOption.WriteWithoutResponse;
    private volatile bool _linkDown;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private TypedEventHandler<BluetoothLEDevice, object>? _connHandler;
    private TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? _notifyHandler;

    public string Name => "Flipper Zero (Bluetooth)";
    public bool IsConnected => _writeChar is not null && !_linkDown;

    /// <summary>Paired BLE devices (Flippers first) for the Settings picker.</summary>
    public static async Task<IReadOnlyList<(string Id, string Name)>> ListPairedAsync()
    {
        var list = new List<(string Id, string Name)>();
        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var found = await DeviceInformation.FindAllAsync(selector).AsTask().ConfigureAwait(false);
            foreach (var di in found)
                list.Add((di.Id, di.Name ?? string.Empty));
        }
        catch { /* BLE unavailable */ }

        return list
            .OrderByDescending(x => x.Name.Contains("Flipper", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Name)
            .ToList();
    }

    public async Task ConnectAsync(KvmConnectionOptions options, CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        _linkDown = false;

        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var found = await DeviceInformation.FindAllAsync(selector).AsTask().ConfigureAwait(false);

        DeviceInformation? target = null;
        if (!string.IsNullOrWhiteSpace(options.BleDeviceId))
            target = found.FirstOrDefault(d => d.Id == options.BleDeviceId);
        if (target is null && !string.IsNullOrWhiteSpace(options.BleDeviceName))
            target = found.FirstOrDefault(d => string.Equals(d.Name, options.BleDeviceName, StringComparison.OrdinalIgnoreCase));
        target ??= found.FirstOrDefault(d => (d.Name ?? string.Empty).Contains("Flipper", StringComparison.OrdinalIgnoreCase));

        if (target is null)
            throw new InvalidOperationException("No paired Flipper found. Pair it in Windows Bluetooth settings first.");

        _device = await BluetoothLEDevice.FromIdAsync(target.Id).AsTask().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not open the Bluetooth device.");

        var svc = await _device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
        if (svc.Status != GattCommunicationStatus.Success || svc.Services.Count == 0)
            throw new InvalidOperationException($"Flipper serial service not found ({svc.Status}). Is the VCKVM Bridge app running on the Flipper?");

        var service = svc.Services[0];

        var chr = await service.GetCharacteristicsForUuidAsync(WriteCharUuid, BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
        if (chr.Status != GattCommunicationStatus.Success || chr.Characteristics.Count == 0)
            throw new InvalidOperationException($"Flipper serial write characteristic not found ({chr.Status}).");

        _writeChar = chr.Characteristics[0];
        _writeOption = _writeChar.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;

        // Subscribe to the notify characteristic. The CCCD write is a with-response op, so it forces
        // a real connection and fails loudly here if the Flipper is out of range / app not running.
        var ntf = await service.GetCharacteristicsForUuidAsync(NotifyCharUuid, BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
        if (ntf.Status == GattCommunicationStatus.Success && ntf.Characteristics.Count > 0)
        {
            _notifyChar = ntf.Characteristics[0];
            _notifyHandler = (_, _) => { /* inbound bytes unused */ };
            _notifyChar.ValueChanged += _notifyHandler;
            var cccd = await _notifyChar
                .WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask().ConfigureAwait(false);
            if (cccd != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"Could not subscribe to the Flipper serial notify characteristic ({cccd}).");
        }

        // Fault the session if the link drops (write-without-response won't surface this otherwise).
        _connHandler = (d, _) => { if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected) _linkDown = true; };
        _device.ConnectionStatusChanged += _connHandler;

        // Greet the companion app (with response, so a dead link is caught immediately).
        await SendLineAsync(FlipperProtocol.Hello, force: true).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        _linkDown = false;

        var notify = _notifyChar;
        _notifyChar = null;
        if (notify is not null)
        {
            if (_notifyHandler is not null) { try { notify.ValueChanged -= _notifyHandler; } catch { } }
            try
            {
                await notify.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None).AsTask().ConfigureAwait(false);
            }
            catch { /* link may already be gone */ }
        }
        _notifyHandler = null;

        var dev = _device;
        _device = null;
        if (dev is not null && _connHandler is not null) { try { dev.ConnectionStatusChanged -= _connHandler; } catch { } }
        _connHandler = null;

        _writeChar = null;
        try { dev?.Dispose(); } catch { /* ignore */ }
    }

    private async Task SendLineAsync(string line, bool force = false)
    {
        var chr = _writeChar;
        if (chr is null) return;
        if (_linkDown) throw new InvalidOperationException("Bluetooth link lost.");

        var writer = new DataWriter();
        writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes(line + "\n"));
        var buffer = writer.DetachBuffer();
        var option = force ? GattWriteOption.WriteWithResponse : _writeOption;

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var status = await chr.WriteValueAsync(buffer, option).AsTask().ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"BLE write failed: {status}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task SendKeyboardAsync(byte modifiers, IReadOnlyList<byte> keys)
        => SendLineAsync(FlipperProtocol.Keyboard(modifiers, keys));

    public Task SendMouseAbsoluteAsync(int xNorm, int yNorm, byte buttons, sbyte wheel)
        => SendLineAsync(FlipperProtocol.MouseAbsolute(xNorm, yNorm, buttons, wheel));

    public Task SendMouseRelativeAsync(sbyte dx, sbyte dy, byte buttons, sbyte wheel)
        => SendLineAsync(FlipperProtocol.MouseRelative(dx, dy, buttons, wheel));

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }
}
