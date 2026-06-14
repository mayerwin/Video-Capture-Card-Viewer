using System.Buffers.Binary;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FlashCap;

namespace VideoCaptureCardViewer.Services;

/// <summary>
/// High-performance FlashCap wrapper.
///
/// Hot path (per frame, on FlashCap's worker thread): zero-copy <c>ReferImage()</c> → blit the
/// transcoded BGR24 DIB straight into a reusable, double-buffered <see cref="WriteableBitmap"/>
/// (Bgra8888), then post a buffer swap to the UI thread at Render priority. No per-frame managed
/// allocation and no per-frame Skia image decode — this is what keeps it smooth (the previous
/// decode-a-new-Bitmap-per-frame design churned ~14MB/frame at 1080p and caused GC stutter).
///
/// MJPEG/JPEG sources (which FlashCap can't transcode to a DIB) fall back to a Skia decode; we bias
/// format selection toward uncompressed YUY2/NV12 so the fast path is used whenever possible.
/// </summary>
public sealed class CaptureService : IAsyncDisposable
{
    private CaptureDevice? _device;
    private readonly object _lifecycleLock = new();

    // Double buffer: worker writes the back buffer, UI presents the front. Both reused for the
    // lifetime of a resolution.
    private readonly WriteableBitmap?[] _buffers = new WriteableBitmap?[2];
    private int _frontIndex;
    private PixelSize _bufSize;
    private volatile bool _uiBusy; // drop-to-latest: don't touch the back buffer while a swap is pending

    private Bitmap? _fallback; // for the rare MJPEG/JPEG path

    /// <summary>Raised on the UI thread with the bitmap currently showing the newest frame.</summary>
    public event Action<Bitmap>? FrameArrived;
    /// <summary>Raised on the UI thread when capture fails.</summary>
    public event Action<string>? Error;

    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }
    public double FrameRate { get; private set; }
    public string? CurrentDeviceName { get; private set; }
    public bool IsRunning => _device is not null;

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
    /// Choose a capture format. Honors a remembered "WxH@fps", otherwise prefers the largest
    /// UNCOMPRESSED format (YUY2/NV12/RGB) at the highest frame rate — uncompressed avoids a
    /// per-frame JPEG decode and takes the fast zero-copy blit path. Falls back to MJPEG only if no
    /// uncompressed format exists.
    /// </summary>
    public static VideoCharacteristics? PickBestFormat(CaptureDeviceDescriptor descriptor, string? preferred = null)
    {
        var renderable = descriptor.Characteristics
            .Where(c => c.PixelFormat != FlashCap.PixelFormats.Unknown && c.Width > 0 && c.Height > 0)
            .ToList();
        if (renderable.Count == 0)
            return descriptor.Characteristics.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = renderable.FirstOrDefault(c => FormatKey(c) == preferred);
            if (match is not null)
                return match;
        }

        var uncompressed = renderable.Where(c => !c.IsCompression).ToList();
        var pool = uncompressed.Count > 0 ? uncompressed : renderable;

        return pool
            .OrderByDescending(c => (long)c.Width * c.Height)
            .ThenByDescending(DeviceHeuristics.ToFps)
            .First();
    }

    public async Task StartAsync(CaptureDeviceDescriptor descriptor, VideoCharacteristics characteristics)
    {
        await StopAsync().ConfigureAwait(false);

        try
        {
            EnsureBuffers(characteristics.Width, characteristics.Height);

            // Synchronous handler, queuing depth 1: at most one frame in flight, newer frames are
            // dropped while busy (no lag buildup). The handler does all pixel work inline.
            var device = await descriptor.OpenAsync(
                characteristics,
                TranscodeFormats.Auto,
                isScattering: false,
                maxQueuingFrames: 1,
                OnPixelBufferArrived).ConfigureAwait(false);

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
        }

        if (device is not null)
        {
            try { await device.StopAsync().ConfigureAwait(false); }
            catch { /* device may already be gone */ }
            finally { try { await device.DisposeAsync().ConfigureAwait(false); } catch { } }
        }

        _uiBusy = false;
    }

    // Runs on FlashCap's worker thread. Keep it tight and never throw back into FlashCap.
    private void OnPixelBufferArrived(PixelBufferScope scope)
    {
        try
        {
            if (_uiBusy) return; // UI hasn't presented the previous frame yet — drop to latest.

            lock (_lifecycleLock)
            {
                if (_device is null) return; // tearing down
            }

            var seg = scope.Buffer.ReferImage(); // zero-copy view, valid only in this scope
            if (seg.Array is null || seg.Count < 2) return;

            byte m0 = seg.Array[seg.Offset];
            byte m1 = seg.Array[seg.Offset + 1];
            bool isJpeg = m0 == 0xFF && m1 == 0xD8;
            bool isPng = seg.Count >= 8 && m0 == 0x89 && m1 == 0x50;

            if (isJpeg || isPng)
            {
                DecodeCompressedFallback(seg);
                return;
            }

            if (!DibHeader.TryParse(seg, out var hdr)) return;
            if (hdr.Width != _bufSize.Width || hdr.Height != _bufSize.Height) return; // unexpected renegotiation

            int idx = 1 - _frontIndex;
            var back = _buffers[idx];
            if (back is null) return;

            using (var fb = back.Lock())
                Blit(seg, hdr, fb);

            _uiBusy = true;
            Dispatcher.UIThread.Post(() =>
            {
                _frontIndex = idx;
                var bmp = _buffers[idx];
                _uiBusy = false;
                if (bmp is not null)
                    FrameArrived?.Invoke(bmp);
            }, DispatcherPriority.Render);
        }
        catch
        {
            _uiBusy = false;
        }
    }

    private void DecodeCompressedFallback(ArraySegment<byte> seg)
    {
        Bitmap bmp;
        try
        {
            // Decode synchronously here (seg is valid in-scope); the Bitmap owns its pixels.
            using var ms = new MemoryStream(seg.Array!, seg.Offset, seg.Count, writable: false);
            bmp = new Bitmap(ms);
        }
        catch
        {
            return;
        }

        _uiBusy = true;
        Dispatcher.UIThread.Post(() =>
        {
            var previous = _fallback;
            _fallback = bmp;
            _uiBusy = false;
            FrameArrived?.Invoke(bmp);
            previous?.Dispose();
        }, DispatcherPriority.Render);
    }

    private void EnsureBuffers(int width, int height)
    {
        if (_bufSize.Width == width && _bufSize.Height == height && _buffers[0] is not null)
            return;

        var oldA = _buffers[0];
        var oldB = _buffers[1];

        var size = new PixelSize(width, height);
        var dpi = new Vector(96, 96);
        _buffers[0] = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _buffers[1] = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _frontIndex = 0;
        _bufSize = size;

        if (oldA is not null || oldB is not null)
            Dispatcher.UIThread.Post(() => { oldA?.Dispose(); oldB?.Dispose(); });
    }

    // BGR24 (or 32) bottom-up DIB -> top-down BGRA8888. Memory-bound; parallelized over rows.
    private static unsafe void Blit(ArraySegment<byte> seg, in DibHeader h, ILockedFramebuffer fb)
    {
        int width = h.Width, height = h.Height, bpp = h.BitsPerPixel / 8;
        int srcStride = (width * bpp + 3) & ~3;
        int dstStride = fb.RowBytes;
        bool bottomUp = h.BottomUp;
        int firstPixel = seg.Offset + h.PixelOffset;

        fixed (byte* srcFix = &seg.Array![firstPixel])
        {
            long sBase = (long)srcFix;
            long dBase = fb.Address.ToInt64();

            System.Threading.Tasks.Parallel.For(0, height, y =>
            {
                byte* s = (byte*)(sBase + (long)(bottomUp ? height - 1 - y : y) * srcStride);
                byte* d = (byte*)(dBase + (long)y * dstStride);
                if (bpp == 4)
                {
                    System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(d, s, (uint)(width * 4));
                }
                else // 24bpp BGR -> BGRA
                {
                    for (int x = 0; x < width; x++)
                    {
                        d[0] = s[0];
                        d[1] = s[1];
                        d[2] = s[2];
                        d[3] = 0xFF;
                        s += 3;
                        d += 4;
                    }
                }
            });
        }
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
            _buffers[0]?.Dispose();
            _buffers[1]?.Dispose();
            _buffers[0] = _buffers[1] = null;
            _fallback?.Dispose();
            _fallback = null;
        });
    }

    private readonly struct DibHeader
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public int BitsPerPixel { get; init; }
        public int PixelOffset { get; init; } // from the start of the segment's image data
        public bool BottomUp { get; init; }

        public static bool TryParse(ArraySegment<byte> seg, out DibHeader h)
        {
            h = default;
            var d = seg.AsSpan();
            if (d.Length < 54) return false;

            int infoStart, pixelOffset;
            if (d[0] == 0x42 && d[1] == 0x4D) // "BM" file header
            {
                pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(10));
                infoStart = 14;
            }
            else // bare DIB
            {
                int infoSize = BinaryPrimitives.ReadInt32LittleEndian(d);
                if (infoSize is not (40 or 108 or 124)) return false;
                infoStart = 0;
                pixelOffset = infoSize;
            }

            int biHeight = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(infoStart + 8));
            short bpp = BinaryPrimitives.ReadInt16LittleEndian(d.Slice(infoStart + 14));
            if (bpp != 24 && bpp != 32) return false;

            int width = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(infoStart + 4));
            int height = Math.Abs(biHeight);
            if (width <= 0 || height <= 0) return false;

            int stride = (width * (bpp / 8) + 3) & ~3;
            if (pixelOffset + (long)height * stride > seg.Count) return false;

            h = new DibHeader
            {
                Width = width,
                Height = height,
                BitsPerPixel = bpp,
                PixelOffset = pixelOffset,
                BottomUp = biHeight > 0,
            };
            return true;
        }
    }
}
