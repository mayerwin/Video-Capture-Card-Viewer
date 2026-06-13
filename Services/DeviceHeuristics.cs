using FlashCap;

namespace VideoCaptureCardViewer.Services;

/// <summary>
/// Heuristics to tell an HDMI/SDI *capture card* apart from an ordinary *webcam*.
/// Nothing here is perfect — device names are vendor soup — so we score on several
/// weak signals and pick the best. The user can always override in Settings.
/// </summary>
public static class DeviceHeuristics
{
    // Strong "this is a capture card" name signals.
    private static readonly string[] s_captureKeywords =
    {
        "capture", "hdmi", "sdi", "cam link", "camlink", "elgato", "avermedia", "magewell",
        "mirabox", "ezcap", "ezcapture", "live gamer", "game capture", "hd60", "hd 60",
        "u3 capture", "uvc", "usb video", "usb3 video", "fhd capture", "screen capture",
        "ms2109", "ms2130", "ms2131", "video grabber", "frame grabber", "acasis", "startech",
        "blackmagic", "decklink", "intensity", "yuan", "epiphan", "rgbeasy", "datapath",
    };

    // Things that are almost certainly NOT the capture card we want.
    private static readonly string[] s_webcamKeywords =
    {
        "webcam", "integrated camera", "integrated webcam", "built-in", "builtin", "facetime",
        "front camera", "rear camera", "facecam", "user facing", "world facing", "ir camera",
        "infrared", "depth", "windows hello", "logitech c", "logitech brio", "brio",
        "obs virtual", "obs-camera", "virtual camera", "droidcam", "nvidia broadcast",
        "snap camera", "xsplit", "iriun", "ndi",
    };

    public sealed record Scored(CaptureDeviceDescriptor Descriptor, int Score, string Reason);

    /// <summary>Rank devices, most-likely capture card first.</summary>
    public static IReadOnlyList<Scored> Rank(IEnumerable<CaptureDeviceDescriptor> devices)
    {
        var scored = new List<Scored>();
        foreach (var d in devices)
        {
            var (score, reason) = Score(d);
            scored.Add(new Scored(d, score, reason));
        }

        // Stable sort by descending score.
        return scored
            .Select((s, i) => (s, i))
            .OrderByDescending(t => t.s.Score)
            .ThenBy(t => t.i)
            .Select(t => t.s)
            .ToList();
    }

    /// <summary>The single best guess, or null if there are no devices.</summary>
    public static CaptureDeviceDescriptor? PickBest(IEnumerable<CaptureDeviceDescriptor> devices)
        => Rank(devices).FirstOrDefault()?.Descriptor;

    private static (int score, string reason) Score(CaptureDeviceDescriptor d)
    {
        var haystack = ((d.Name ?? string.Empty) + " " + (d.Description ?? string.Empty)).ToLowerInvariant();
        var reasons = new List<string>();
        int score = 0;

        foreach (var kw in s_captureKeywords)
        {
            if (haystack.Contains(kw))
            {
                score += 50;
                reasons.Add($"+50 name~'{kw}'");
                break; // one name hit is enough; don't stack duplicates
            }
        }

        foreach (var kw in s_webcamKeywords)
        {
            if (haystack.Contains(kw))
            {
                score -= 60;
                reasons.Add($"-60 name~'{kw}'");
                break;
            }
        }

        // Capability signals: capture cards expose big HDMI-ish modes and high frame rates.
        var (maxW, maxH, maxFps) = MaxCapabilities(d);
        if (maxW >= 3840 && maxH >= 2160)
        {
            score += 20;
            reasons.Add("+20 supports 2160p");
        }
        else if (maxW >= 1920 && maxH >= 1080)
        {
            score += 12;
            reasons.Add("+12 supports 1080p");
        }
        else if (maxW > 0 && maxW <= 1280)
        {
            score -= 8;
            reasons.Add("-8 max <=720p");
        }

        if (maxFps >= 59)
        {
            score += 12;
            reasons.Add("+12 >=60fps mode");
        }

        return (score, reasons.Count == 0 ? "no signal" : string.Join(", ", reasons));
    }

    private static (int w, int h, double fps) MaxCapabilities(CaptureDeviceDescriptor d)
    {
        int w = 0, h = 0;
        double fps = 0;
        foreach (var c in d.Characteristics)
        {
            if (c.Width * c.Height > w * h)
            {
                w = c.Width;
                h = c.Height;
            }
            var f = ToFps(c);
            if (f > fps) fps = f;
        }
        return (w, h, fps);
    }

    public static double ToFps(VideoCharacteristics c)
    {
        // FlashCap exposes FramesPerSecond as a Fraction (Numerator/Denominator).
        try
        {
            var fr = c.FramesPerSecond;
            if (fr.Denominator != 0)
                return (double)fr.Numerator / fr.Denominator;
        }
        catch
        {
            // ignore
        }
        return 0;
    }
}
