using System.IO.Ports;
using System.Text;

namespace VideoCaptureCardViewer.Kvm;

/// <summary>Shared serial-port lifecycle for hardware KVM backends.</summary>
public abstract class SerialKvmBackend : IKvmBackend
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    protected SerialPort? Port;

    public abstract string Name { get; }
    public bool IsConnected => Port?.IsOpen == true;

    public virtual async Task ConnectAsync(KvmConnectionOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.PortName))
            throw new InvalidOperationException($"{Name}: no serial/COM port selected.");

        await DisconnectAsync().ConfigureAwait(false);

        var port = new SerialPort(options.PortName, options.BaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
            Handshake = Handshake.None,
            // CH9329 needs no handshake lines; asserting DTR/RTS can pulse-reset some USB-serial bridges.
            DtrEnable = false,
            RtsEnable = false,
        };
        port.Open();
        Port = port;

        await OnConnectedAsync(ct).ConfigureAwait(false);
    }

    protected virtual Task OnConnectedAsync(CancellationToken ct) => Task.CompletedTask;

    public Task DisconnectAsync()
    {
        var port = Port;
        Port = null;
        if (port is not null)
        {
            try { if (port.IsOpen) port.Close(); } catch { /* ignore */ }
            try { port.Dispose(); } catch { /* ignore */ }
        }
        return Task.CompletedTask;
    }

    protected async Task WriteAsync(byte[] data)
    {
        var port = Port;
        if (port is null || !port.IsOpen) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            port.Write(data, 0, data.Length);
        }
        finally
        {
            _gate.Release();
        }
    }

    protected Task WriteLineAsync(string line) => WriteAsync(Encoding.ASCII.GetBytes(line + "\n"));

    public abstract Task SendKeyboardAsync(byte modifiers, IReadOnlyList<byte> keys);
    public abstract Task SendMouseAbsoluteAsync(int xNorm, int yNorm, byte buttons, sbyte wheel);
    public abstract Task SendMouseRelativeAsync(sbyte dx, sbyte dy, byte buttons, sbyte wheel);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    protected static byte[] BuildKeyboardReport(byte modifiers, IReadOnlyList<byte> keys)
    {
        var report = new byte[8];
        report[0] = modifiers;
        report[1] = 0x00;
        for (int i = 0; i < 6; i++)
            report[2 + i] = i < keys.Count ? keys[i] : (byte)0;
        return report;
    }
}

/// <summary>
/// CH9329 USB-serial → HID controller. Frame: 0x57 0xAB &lt;addr&gt; &lt;cmd&gt; &lt;len&gt; &lt;data...&gt; &lt;checksum&gt;,
/// checksum = sum of all preceding bytes (mod 256).
/// </summary>
public sealed class Ch9329Backend : SerialKvmBackend
{
    private const byte Head0 = 0x57, Head1 = 0xAB, Addr = 0x00;
    private const byte CmdKeyboard = 0x02, CmdMouseAbs = 0x04, CmdMouseRel = 0x05;

    public override string Name => "CH9329";

    public override Task SendKeyboardAsync(byte modifiers, IReadOnlyList<byte> keys)
        => WriteAsync(Frame(CmdKeyboard, BuildKeyboardReport(modifiers, keys)));

    public override Task SendMouseAbsoluteAsync(int xNorm, int yNorm, byte buttons, sbyte wheel)
    {
        int x = Math.Clamp((int)((long)xNorm * 4096 / 32767), 0, 4095);
        int y = Math.Clamp((int)((long)yNorm * 4096 / 32767), 0, 4095);
        var data = new byte[]
        {
            0x02, buttons,
            (byte)(x & 0xFF), (byte)((x >> 8) & 0xFF),
            (byte)(y & 0xFF), (byte)((y >> 8) & 0xFF),
            (byte)wheel,
        };
        return WriteAsync(Frame(CmdMouseAbs, data));
    }

    public override Task SendMouseRelativeAsync(sbyte dx, sbyte dy, byte buttons, sbyte wheel)
    {
        var data = new byte[] { 0x01, buttons, (byte)dx, (byte)dy, (byte)wheel };
        return WriteAsync(Frame(CmdMouseRel, data));
    }

    private static byte[] Frame(byte cmd, byte[] data)
    {
        var frame = new byte[5 + data.Length + 1];
        frame[0] = Head0;
        frame[1] = Head1;
        frame[2] = Addr;
        frame[3] = cmd;
        frame[4] = (byte)data.Length;
        Array.Copy(data, 0, frame, 5, data.Length);

        int sum = 0;
        for (int i = 0; i < frame.Length - 1; i++) sum += frame[i];
        frame[^1] = (byte)(sum & 0xFF);
        return frame;
    }
}

/// <summary>
/// Flipper Zero companion-bridge backend. Speaks a simple ASCII line protocol over the Flipper's
/// serial link (USB CDC, or a BLE-bound virtual COM port). Requires the companion app in
/// <c>flipper-companion/</c> to be installed on the Flipper. See that folder's README for the protocol.
/// </summary>
public sealed class FlipperZeroBackend : SerialKvmBackend
{
    public override string Name => "Flipper Zero (companion bridge)";

    protected override async Task OnConnectedAsync(CancellationToken ct)
    {
        // Greet the companion app so it can switch into bridge mode.
        await WriteLineAsync("VCKVM 1").ConfigureAwait(false);
    }

    public override Task SendKeyboardAsync(byte modifiers, IReadOnlyList<byte> keys)
    {
        var report = BuildKeyboardReport(modifiers, keys);
        var sb = new StringBuilder("KB");
        foreach (var b in report) sb.Append(' ').Append(b.ToString("X2"));
        return WriteLineAsync(sb.ToString());
    }

    public override Task SendMouseAbsoluteAsync(int xNorm, int yNorm, byte buttons, sbyte wheel)
        => WriteLineAsync($"MA {xNorm} {yNorm} {buttons} {wheel}");

    public override Task SendMouseRelativeAsync(sbyte dx, sbyte dy, byte buttons, sbyte wheel)
        => WriteLineAsync($"MR {dx} {dy} {buttons} {wheel}");
}
