using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VideoCaptureCardViewer.Services;

/// <summary>
/// Plays a capture card's audio (its WASAPI *capture* endpoint, e.g. "Digital Audio Interface")
/// through the default output device. Everything is best-effort and heavily guarded: if anything
/// fails, audio is simply silent — it never destabilizes the viewer.
/// </summary>
public sealed class AudioService : IDisposable
{
    private WasapiCapture? _capture;
    private IWavePlayer? _output;
    private BufferedWaveProvider? _buffer;
    private IDisposable? _resampler;
    private MMDevice? _inDevice;
    private MMDevice? _outDevice;

    public event Action<string>? Log;

    public bool IsRunning { get; private set; }
    public string? CurrentDeviceName { get; private set; }

    public readonly record struct AudioInput(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    public static IReadOnlyList<AudioInput> ListInputs()
    {
        var list = new List<AudioInput>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try { list.Add(new AudioInput(d.ID, d.FriendlyName)); }
                catch { /* skip flaky endpoint */ }
                finally { try { d.Dispose(); } catch { } }
            }
        }
        catch { /* enumeration unavailable */ }
        return list;
    }

    /// <summary>
    /// Start passthrough. Uses <paramref name="preferredName"/> if given, else the capture endpoint
    /// whose name best overlaps the video device name (so we never grab the laptop mic by accident).
    /// </summary>
    public void Start(string? preferredName, string? videoDeviceName)
    {
        Stop();
        try
        {
            using var en = new MMDeviceEnumerator();
            var inputs = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

            MMDevice? chosen = null;
            if (!string.IsNullOrWhiteSpace(preferredName))
                chosen = inputs.FirstOrDefault(d => Safe(d) == preferredName);
            chosen ??= MatchByName(inputs, videoDeviceName);
            // Many cards expose audio as the generic "Digital Audio Interface" with no name overlap.
            chosen ??= FallbackMatch(inputs);

            // Release every endpoint we won't keep.
            foreach (var d in inputs)
                if (!ReferenceEquals(d, chosen)) { try { d.Dispose(); } catch { } }

            if (chosen is null)
            {
                Log?.Invoke("Audio: no matching capture endpoint; passthrough off.");
                return;
            }

            _inDevice = chosen;
            _outDevice = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _capture = new WasapiCapture(_inDevice);
            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(500),
            };
            _capture.DataAvailable += (_, a) => _buffer?.AddSamples(a.Buffer, 0, a.BytesRecorded);

            _output = new WasapiOut(_outDevice, AudioClientShareMode.Shared, true, 100);

            // Resample to the render device's mix format so shared-mode init won't reject us.
            IWaveProvider source = _buffer;
            try
            {
                var mix = _outDevice.AudioClient.MixFormat;
                if (!FormatsMatch(_buffer.WaveFormat, mix))
                {
                    var r = new MediaFoundationResampler(_buffer, mix) { ResamplerQuality = 60 };
                    _resampler = r;
                    source = r;
                }
            }
            catch { /* fall back to the raw buffer */ }

            _output.Init(source);
            _capture.StartRecording();
            _output.Play();

            CurrentDeviceName = Safe(_inDevice);
            IsRunning = true;
            Log?.Invoke($"Audio: passthrough from \"{CurrentDeviceName}\".");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Audio: passthrough unavailable ({ex.Message}).");
            Stop();
        }
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch { }
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _resampler?.Dispose(); } catch { }
        try { _capture?.Dispose(); } catch { }
        try { _inDevice?.Dispose(); } catch { }
        try { _outDevice?.Dispose(); } catch { }
        _output = null;
        _resampler = null;
        _capture = null;
        _buffer = null;
        _inDevice = null;
        _outDevice = null;
        IsRunning = false;
        CurrentDeviceName = null;
    }

    private static MMDevice? MatchByName(List<MMDevice> inputs, string? videoName)
    {
        if (string.IsNullOrWhiteSpace(videoName)) return null;

        var tokens = videoName
            .Split(new[] { ' ', '-', '_', '(', ')', '/', '\\', '.', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 4)
            .Select(t => t.ToLowerInvariant())
            .ToArray();
        if (tokens.Length == 0) return null;

        MMDevice? best = null;
        int bestScore = 0;
        foreach (var d in inputs)
        {
            var name = Safe(d).ToLowerInvariant();
            int score = tokens.Count(t => name.Contains(t));
            if (score > bestScore) { bestScore = score; best = d; }
        }
        return bestScore > 0 ? best : null;
    }

    private static readonly string[] s_audioInclude =
        { "digital audio", "hdmi", "line in", "line-in", "spdif", "capture", "video", "what u hear", "loopback", "aux" };
    private static readonly string[] s_audioExclude =
        { "microphone", "mic ", "mic(", "array", "webcam", "headset", "communications", "wave out", "speaker" };

    /// <summary>
    /// When name-matching fails, pick the capture endpoint that most looks like a line/HDMI input
    /// and least like the built-in mic, so a generic "Digital Audio Interface" still gets chosen.
    /// </summary>
    private static MMDevice? FallbackMatch(List<MMDevice> inputs)
    {
        MMDevice? best = null;
        int bestScore = int.MinValue;
        foreach (var d in inputs)
        {
            var name = Safe(d).ToLowerInvariant();
            if (name.Length == 0) continue;
            int score = 0;
            foreach (var t in s_audioInclude) if (name.Contains(t)) score += 2;
            foreach (var t in s_audioExclude) if (name.Contains(t)) score -= 3;
            if (score > bestScore) { bestScore = score; best = d; }
        }
        // Only use the fallback if it's at least plausibly a line/digital input (not a mic).
        return bestScore > 0 ? best : null;
    }

    private static bool FormatsMatch(WaveFormat a, WaveFormat b)
        => a.SampleRate == b.SampleRate && a.Channels == b.Channels && a.BitsPerSample == b.BitsPerSample;

    private static string Safe(MMDevice d)
    {
        try { return d.FriendlyName; } catch { return string.Empty; }
    }

    public void Dispose() => Stop();
}
