using Avalonia.Input;

namespace VideoCaptureCardViewer.Kvm;

/// <summary>
/// Maps Avalonia <see cref="Key"/> values to USB HID Usage IDs (Keyboard/Keypad page 0x07)
/// and to the HID modifier bitmask. Built by enum-name lookup so a missing key name on a given
/// Avalonia version simply isn't mapped (rather than failing to compile).
/// </summary>
public static class HidKeyMap
{
    // HID modifier bits (byte 0 of the boot keyboard report).
    public const byte ModLeftCtrl = 0x01;
    public const byte ModLeftShift = 0x02;
    public const byte ModLeftAlt = 0x04;
    public const byte ModLeftGui = 0x08;
    public const byte ModRightCtrl = 0x10;
    public const byte ModRightShift = 0x20;
    public const byte ModRightAlt = 0x40;
    public const byte ModRightGui = 0x80;

    private static readonly Dictionary<Key, byte> s_usage = new();
    private static readonly Dictionary<Key, byte> s_modifier = new();

    static HidKeyMap()
    {
        // Modifiers
        AddMod("LeftCtrl", ModLeftCtrl);
        AddMod("RightCtrl", ModRightCtrl);
        AddMod("LeftShift", ModLeftShift);
        AddMod("RightShift", ModRightShift);
        AddMod("LeftAlt", ModLeftAlt);
        AddMod("RightAlt", ModRightAlt);
        AddMod("LWin", ModLeftGui);
        AddMod("RWin", ModRightGui);

        // Letters a-z => 0x04..0x1D
        for (int i = 0; i < 26; i++)
            Add(((char)('A' + i)).ToString(), (byte)(0x04 + i));

        // Top-row digits 1-9 then 0
        Add("D1", 0x1E); Add("D2", 0x1F); Add("D3", 0x20); Add("D4", 0x21); Add("D5", 0x22);
        Add("D6", 0x23); Add("D7", 0x24); Add("D8", 0x25); Add("D9", 0x26); Add("D0", 0x27);

        Add("Return", 0x28); Add("Enter", 0x28);
        Add("Escape", 0x29);
        Add("Back", 0x2A);
        Add("Tab", 0x2B);
        Add("Space", 0x2C);
        Add("OemMinus", 0x2D);
        Add("OemPlus", 0x2E);            // '=' / '+'
        Add("OemOpenBrackets", 0x2F);
        Add("OemCloseBrackets", 0x30);
        Add("OemPipe", 0x31); Add("OemBackslash", 0x31);
        Add("OemSemicolon", 0x33);
        Add("OemQuotes", 0x34);
        Add("OemTilde", 0x35);
        Add("OemComma", 0x36);
        Add("OemPeriod", 0x37);
        Add("OemQuestion", 0x38);        // '/'
        Add("CapsLock", 0x39);

        // F1-F12 => 0x3A..0x45
        for (int i = 1; i <= 12; i++)
            Add("F" + i, (byte)(0x3A + (i - 1)));

        Add("PrintScreen", 0x46); Add("Snapshot", 0x46);
        Add("Scroll", 0x47);
        Add("Pause", 0x48);
        Add("Insert", 0x49);
        Add("Home", 0x4A);
        Add("PageUp", 0x4B); Add("Prior", 0x4B);
        Add("Delete", 0x4C);
        Add("End", 0x4D);
        Add("PageDown", 0x4E); Add("Next", 0x4E);
        Add("Right", 0x4F);
        Add("Left", 0x50);
        Add("Down", 0x51);
        Add("Up", 0x52);
        Add("NumLock", 0x53);

        Add("Divide", 0x54);
        Add("Multiply", 0x55);
        Add("Subtract", 0x56);
        Add("Add", 0x57);
        // Keypad Enter shares 0x58 (no distinct Avalonia key)
        for (int i = 1; i <= 9; i++)
            Add("NumPad" + i, (byte)(0x59 + (i - 1)));
        Add("NumPad0", 0x62);
        Add("Decimal", 0x63);

        Add("Apps", 0x65); // Application / context-menu key

        // F13-F24 => 0x68..0x73
        for (int i = 13; i <= 24; i++)
            Add("F" + i, (byte)(0x68 + (i - 13)));
    }

    private static void Add(string keyName, byte usage)
    {
        if (Enum.TryParse<Key>(keyName, out var key) && !s_usage.ContainsKey(key))
            s_usage[key] = usage;
    }

    private static void AddMod(string keyName, byte bit)
    {
        if (Enum.TryParse<Key>(keyName, out var key))
            s_modifier[key] = bit;
    }

    public static bool TryGetModifier(Key key, out byte bit) => s_modifier.TryGetValue(key, out bit);

    public static bool TryGetUsage(Key key, out byte usage) => s_usage.TryGetValue(key, out usage);

    public static bool IsModifier(Key key) => s_modifier.ContainsKey(key);
}
