using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace VideoCaptureCardViewer.Kvm;

/// <summary>
/// Drives a Flipper Zero over Bluetooth LE — the primary KVM topology:
///   PC (this app, paired over Bluetooth) → Flipper → USB HID → target machine.
///
/// Connects to the Flipper's custom serial GATT service and writes the same ASCII line protocol the
/// serial backend uses (see <see cref="FlipperProtocol"/>). The Flipper must be paired in Windows
/// Bluetooth settings and running the companion app (flipper-companion/) in BLE-bridge mode.
/// </summary>
public sealed class FlipperBleBackend : IKvmBackend
{
    // Flipper Zero custom serial service + the characteristic the central writes to (data -> Flipper).
    private static readonly Guid ServiceUuid = new("8fe5b3d5-2e7f-4a98-2a48-7acc60fe0000");
    private static readonly Guid WriteCharUuid = new("19ed82ae-ed21-4c9d-4145-228e62fe0000");

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeChar;
    private GattWriteOption _writeOption = GattWriteOption.WriteWithoutResponse;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public string Name => "Flipper Zero (Bluetooth)";
    public bool IsConnected { get; private set; }

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

        var chr = await svc.Services[0].GetCharacteristicsForUuidAsync(WriteCharUuid, BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
        if (chr.Status != GattCommunicationStatus.Success || chr.Characteristics.Count == 0)
            throw new InvalidOperationException($"Flipper serial write characteristic not found ({chr.Status}).");

        _writeChar = chr.Characteristics[0];
        _writeOption = _writeChar.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;

        IsConnected = true;
        await SendLineAsync(FlipperProtocol.Hello).ConfigureAwait(false);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        _writeChar = null;
        var dev = _device;
        _device = null;
        try { dev?.Dispose(); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    private async Task SendLineAsync(string line)
    {
        var chr = _writeChar;
        if (chr is null) return;

        var writer = new DataWriter();
        writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes(line + "\n"));
        var buffer = writer.DetachBuffer();

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var status = await chr.WriteValueAsync(buffer, _writeOption).AsTask().ConfigureAwait(false);
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
