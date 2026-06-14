using System.Text;

namespace VideoCaptureCardViewer.Kvm;

/// <summary>
/// The ASCII line protocol the viewer sends to the Flipper companion app, shared by the serial and
/// Bluetooth backends. See flipper-companion/README.md.
/// </summary>
public static class FlipperProtocol
{
    public const string Hello = "VCKVM 1";

    public static string Keyboard(byte modifiers, IReadOnlyList<byte> keys)
    {
        var sb = new StringBuilder("KB");
        sb.Append(' ').Append(modifiers.ToString("X2"));
        sb.Append(' ').Append("00"); // reserved byte
        for (int i = 0; i < 6; i++)
            sb.Append(' ').Append((i < keys.Count ? keys[i] : (byte)0).ToString("X2"));
        return sb.ToString();
    }

    public static string MouseAbsolute(int xNorm, int yNorm, byte buttons, sbyte wheel)
        => FormattableString.Invariant($"MA {xNorm} {yNorm} {buttons} {wheel}");

    public static string MouseRelative(sbyte dx, sbyte dy, byte buttons, sbyte wheel)
        => FormattableString.Invariant($"MR {dx} {dy} {buttons} {wheel}");
}
