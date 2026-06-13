using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FlashCap;

namespace VideoCaptureCardViewer.Services;

/// <summary>
/// Thin wrapper over FlashCap: enumerates devices, picks a sensible format, runs a capture
/// session and surfaces decoded frames as Avalonia <see cref="Bitmap"/>s on the UI thread.
///
/// Frame flow: FlashCap invokes <see cref="OnFrameAsync"/> off the UI thread, we decode the
/// frame there, then marshal the finished bitmap to the UI thread. A single-slot "busy" guard
/// drops frames whenever the UI hasn't consumed the previous one, so latency stays low and the
/// pipeline never backs up.
/// </summary>
public sealed class CaptureService : IAsyncDisposable
{
    private CaptureDevice? _device;
    private volatile bool _uiBusy;
    private Bitmap? _latest;
    private readonly object _lifecycleLock = new();
    private int _epoch; // bumped on every Stop; lets in-flight frames from an old device be dropped

    /// <summary>Raised on the UI thread with the newest decoded frame.</summary>
    public event Action<Bitmap>? FrameArrived;

    /// <summary>Raised on the UI thread when capture fails.</summary>
    public event Action<string>? Error;

    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }
    public double FrameRate { get; private set; }
    public string? CurrentDeviceName { get; private set; }
    public bool IsRunning => _device is not null;

    /// <summary>Devices that expose at least one video format (filters out audio-only entries).</summary>
    public static IReadOnlyList<CaptureDeviceDescriptor> EnumerateDevices()
    {
        try
        {
            return new CaptureDevices()
                .EnumerateDescriptors()
                .Where(d => d.Characteristics.Any())
                .ToList();
        }
        catch
        {
            return Array.Empty<CaptureDeviceDescriptor>();
        }
    }

    public static string FormatKey(VideoCharacteristics c) =>
        $"{c.Width}x{c.Height}@{DeviceHeuristics.ToFps(c):0.##}";

    /// <summary>
    /// Choose a capture format: honor a remembered "WxH@fps" if present, else the largest
    /// resolution at the highest frame rate (the transcoder handles YUY2/MJPG/RGB for us).
    /// </summary>
    public static VideoCharacteristics? PickBestFormat(CaptureDeviceDescriptor descriptor, string? preferred = null)
    {
        var renderable = descriptor.Characteristics
            .Where(c => c.PixelFormat != PixelFormats.Unknown && c.Width > 0 && c.Height > 0)
            .ToList();

        if (renderable.Count == 0)
            return descriptor.Characteristics.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = renderable.FirstOrDefault(c => FormatKey(c) == preferred);
            if (match is not null)
                return match;
        }

        return renderable
            .OrderByDescending(c => (long)c.Width * c.Height)
            .ThenByDescending(DeviceHeuristics.ToFps)
            .First();
    }

    public async Task StartAsync(CaptureDeviceDescriptor descriptor, VideoCharacteristics characteristics)
    {
        await StopAsync().ConfigureAwait(false);

        try
        {
            var device = await descriptor.OpenAsync(
                characteristics,
                TranscodeFormats.Auto,
                OnFrameAsync).ConfigureAwait(false);

            lock (_lifecycleLock)
            {
                _device = device;
                FrameWidth = characteristics.Width;
                FrameHeight = characteristics.Height;
                FrameRate = DeviceHeuristics.ToFps(characteristics);
                CurrentDeviceName = descriptor.Name;
            }

            await device.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RaiseError($"Could not start \"{descriptor.Name}\": {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        CaptureDevice? device;
        lock (_lifecycleLock)
        {
            device = _device;
            _device = null;
            _epoch++; // invalidate any frames already decoded for the old device
        }

        if (device is not null)
        {
            try
            {
                await device.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Device may already be gone (e.g. cable unplugged) — ignore on teardown.
            }
            finally
            {
                try { await device.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            }
        }

        _uiBusy = false;
    }

    private Task OnFrameAsync(PixelBufferScope bufferScope)
    {
        // Drop this frame if the UI still hasn't taken the previous one.
        if (_uiBusy)
            return Task.CompletedTask;

        int epoch;
        lock (_lifecycleLock)
        {
            if (_device is null) return Task.CompletedTask; // tearing down
            epoch = _epoch;
        }

        byte[] image;
        try
        {
            image = bufferScope.Buffer.CopyImage();
        }
        catch
        {
            return Task.CompletedTask;
        }

        Bitmap bitmap;
        try
        {
            bitmap = ImageDecoding.Decode(image);
        }
        catch
        {
            return Task.CompletedTask;
        }

        _uiBusy = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // Device was stopped/switched after this frame was decoded — drop and dispose it.
                if (epoch != Volatile.Read(ref _epoch))
                {
                    bitmap.Dispose();
                    return;
                }

                var previous = _latest;
                _latest = bitmap;
                FrameArrived?.Invoke(bitmap);
                previous?.Dispose();
            }
            finally
            {
                _uiBusy = false;
            }
        }, DispatcherPriority.Background);

        return Task.CompletedTask;
    }

    private void RaiseError(string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
            Error?.Invoke(message);
        else
            Dispatcher.UIThread.Post(() => Error?.Invoke(message));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            _latest?.Dispose();
            _latest = null;
        });
    }
}
