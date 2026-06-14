using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoCaptureCardViewer.Services;

/// <summary>
/// User-facing settings, persisted as JSON under %APPDATA%\VideoCaptureCardViewer\settings.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Settings schema version, for forward migration.</summary>
    public int SchemaVersion { get; set; } = 1;

    // ---- Window / chrome ----
    public bool ShowBorder { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool StartFullScreen { get; set; }

    /// <summary>How the video fills the window: "Uniform" (letterbox), "UniformToFill", "Fill", or "None".</summary>
    public string StretchMode { get; set; } = "Uniform";

    // ---- Audio passthrough ----
    /// <summary>Play the capture card's audio through the default output device.</summary>
    public bool AudioEnabled { get; set; } = true;
    /// <summary>Remembered audio capture endpoint name; empty = auto-match to the video device.</summary>
    public string? AudioDeviceName { get; set; }

    /// <summary>Restore the last window placement on launch.</summary>
    public bool RememberWindowPlacement { get; set; } = true;
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    // ---- Device selection ----
    /// <summary>When true, pick the most likely capture card automatically (ignores <see cref="PreferredDeviceName"/>).</summary>
    public bool AutoSelectCaptureCard { get; set; } = true;

    /// <summary>Remembered device (used when auto-select is off, or as a tie-break hint).</summary>
    public string? PreferredDeviceName { get; set; }

    /// <summary>Remembered capture format "WxH@fps" to re-pick the same characteristics when available.</summary>
    public string? PreferredFormat { get; set; }

    // ---- KVM (requires a USB HID dongle such as a CH9329; a PC cannot emulate USB HID by itself) ----
    /// <summary>"Loopback", "Ch9329", "FlipperBle", or "FlipperSerial".</summary>
    public string KvmBackend { get; set; } = "Loopback";
    public string? KvmComPort { get; set; }
    public int KvmBaudRate { get; set; } = 115200;
    /// <summary>Paired Bluetooth device for the Flipper BLE backend.</summary>
    public string? KvmBleDeviceId { get; set; }
    public string? KvmBleDeviceName { get; set; }
    /// <summary>Connect the KVM backend automatically on launch.</summary>
    public bool KvmAutoConnect { get; set; }

    [JsonIgnore]
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoCaptureCardViewer");

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, s_json);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Corrupt/unreadable settings shouldn't stop the app from starting.
        }

        return new AppSettings();
    }

    private static readonly object s_ioLock = new();

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(this, s_json);
            lock (s_ioLock)
            {
                // Write-then-rename so a crash mid-write can't truncate settings.json to garbage.
                var tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, ConfigPath, overwrite: true);
            }
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
