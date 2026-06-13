using System.Text;

namespace VideoCaptureCardViewer.Kvm;

/// <summary>
/// Hardware-free backend that logs reports instead of transmitting them. Lets you verify input
/// capture, key mapping and pointer math without a dongle attached.
/// </summary>
public sealed class LoopbackBackend : IKvmBackend
{
    private readonly Action<string>? _log;
    public LoopbackBackend(Action<string>? log = null) => _log = log;

    public string Name => "Loopback (no hardware)";
    public bool IsConnected { get; private set; }

    public Task ConnectAsync(KvmConnectionOptions options, CancellationToken ct = default)
    {
        IsConnected = true;
        _log?.Invoke("Loopback connected.");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task SendKeyboardAsync(byte modifiers, IReadOnlyList<byte> keys)
    {
        var sb = new StringBuilder($"KB mod=0x{modifiers:X2} keys=");
        foreach (var k in keys) sb.Append(k.ToString("X2")).Append(' ');
        _log?.Invoke(sb.ToString().TrimEnd());
        return Task.CompletedTask;
    }

    public Task SendMouseAbsoluteAsync(int xNorm, int yNorm, byte buttons, sbyte wheel)
    {
        _log?.Invoke($"MOUSE abs x={xNorm} y={yNorm} btn=0x{buttons:X2} wheel={wheel}");
        return Task.CompletedTask;
    }

    public Task SendMouseRelativeAsync(sbyte dx, sbyte dy, byte buttons, sbyte wheel)
    {
        _log?.Invoke($"MOUSE rel dx={dx} dy={dy} btn=0x{buttons:X2} wheel={wheel}");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
