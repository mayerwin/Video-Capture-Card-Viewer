using System.Buffers.Binary;
using System.IO;
using Avalonia.Media.Imaging;

namespace VideoCaptureCardViewer.Services;

/// <summary>
/// Decodes a frame buffer coming out of FlashCap into an Avalonia <see cref="Bitmap"/>.
///
/// FlashCap returns self-describing data for most formats (JPEG/PNG, or a full BMP), which
/// Avalonia's Skia codecs read directly. As a safety net we also handle a bare DIB
/// (BITMAPINFOHEADER + pixels with no 14-byte file header) by synthesizing the file header,
/// so we don't depend on the exact transcoder output shape.
/// </summary>
internal static class ImageDecoding
{
    public static Bitmap Decode(byte[] data)
    {
        if (data.Length >= 2 && IsSelfDescribing(data))
        {
            using var ms = new MemoryStream(data, writable: false);
            return new Bitmap(ms);
        }

        // Looks like a bare DIB? First 4 bytes are the info-header size (40 / 108 / 124).
        if (data.Length >= 40)
        {
            int headerSize = BinaryPrimitives.ReadInt32LittleEndian(data);
            if (headerSize is 40 or 108 or 124)
                return DecodeBareDib(data, headerSize);
        }

        // Last resort: hand it to Skia as-is and let it try.
        using var fallback = new MemoryStream(data, writable: false);
        return new Bitmap(fallback);
    }

    private static bool IsSelfDescribing(byte[] d)
    {
        // "BM" (BMP file)
        if (d[0] == 0x42 && d[1] == 0x4D) return true;
        // JPEG
        if (d[0] == 0xFF && d[1] == 0xD8) return true;
        // PNG
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return true;
        return false;
    }

    private static Bitmap DecodeBareDib(byte[] dib, int headerSize)
    {
        short bitCount = BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(14));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16));
        int clrUsed = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32));

        int paletteBytes = 0;
        if (bitCount is > 0 and <= 8)
        {
            int colors = clrUsed != 0 ? clrUsed : (1 << bitCount);
            paletteBytes = colors * 4;
        }

        // BI_BITFIELDS (==3) with a classic 40-byte header stores 3 colour masks before the pixels.
        int maskBytes = (headerSize == 40 && compression == 3) ? 12 : 0;

        const int FileHeaderSize = 14;
        int offBits = FileHeaderSize + headerSize + maskBytes + paletteBytes;
        int total = FileHeaderSize + dib.Length;

        var file = new byte[total];
        file[0] = 0x42; // 'B'
        file[1] = 0x4D; // 'M'
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), total);
        // bytes 6..9 reserved = 0
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), offBits);
        Buffer.BlockCopy(dib, 0, file, FileHeaderSize, dib.Length);

        using var ms = new MemoryStream(file, writable: false);
        return new Bitmap(ms);
    }
}
