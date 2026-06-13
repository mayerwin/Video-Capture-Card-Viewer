namespace VideoCaptureCardViewer.Kvm;

public enum KvmBackendKind
{
    /// <summary>No hardware — logs reports. Lets you verify wiring/mapping without a dongle.</summary>
    Loopback,

    /// <summary>CH9329 USB-serial → HID chip (recommended, ~$10).</summary>
    Ch9329,

    /// <summary>Flipper Zero running the companion bridge app (USB CDC or BLE-bound COM port).</summary>
    FlipperZero,
}

public sealed class KvmConnectionOptions
{
    public KvmBackendKind Kind { get; init; } = KvmBackendKind.Loopback;
    public string? PortName { get; init; }
    // 115200 matches the Flipper companion firmware. Configure a CH9329 module to the same rate
    // (or pick its current rate, often 9600, from the dropdown).
    public int BaudRate { get; init; } = 115200;
}

/// <summary>
/// A transport that delivers HID keyboard/mouse reports to a physically-attached target machine.
/// A PC cannot emulate USB HID by itself, so every real backend drives external hardware.
/// </summary>
public interface IKvmBackend : IAsyncDisposable
{
    string Name { get; }
    bool IsConnected { get; }

    Task ConnectAsync(KvmConnectionOptions options, CancellationToken ct = default);
    Task DisconnectAsync();

    /// <summary>Boot keyboard report: <paramref name="modifiers"/> bitmask + up to 6 simultaneous usage IDs.</summary>
    Task SendKeyboardAsync(byte modifiers, IReadOnlyList<byte> keys);

    /// <summary>Absolute pointer. <paramref name="xNorm"/>/<paramref name="yNorm"/> are 0..32767 across the target screen.</summary>
    Task SendMouseAbsoluteAsync(int xNorm, int yNorm, byte buttons, sbyte wheel);

    /// <summary>Relative pointer movement (−127..127 per axis).</summary>
    Task SendMouseRelativeAsync(sbyte dx, sbyte dy, byte buttons, sbyte wheel);
}

/// <summary>HID mouse button bits.</summary>
[Flags]
public enum KvmMouseButtons : byte
{
    None = 0,
    Left = 0x01,
    Right = 0x02,
    Middle = 0x04,
}
