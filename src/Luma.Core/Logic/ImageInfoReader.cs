using Luma.Core.Models;

namespace Luma.Core.Logic;

public sealed record ImageInfo(string Format, string Extension, string MimeType, WallpaperResolution Resolution);

/// <summary>
/// Lightweight header sniffing for PNG, JPEG, GIF, BMP and WebP. Used to validate downloads (never trust the
/// server's Content-Type) and to read dimensions without decoding pixels.
/// </summary>
public static class ImageInfoReader
{
    public static ImageInfo? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            return TryRead(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ImageInfo? TryRead(Stream stream)
    {
        Span<byte> head = stackalloc byte[32];
        var read = ReadFully(stream, head);
        if (read < 12) return null;
        head = head[..read];

        if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
        {
            if (read < 24) return null;
            var w = ReadInt32BigEndian(head, 16);
            var h = ReadInt32BigEndian(head, 20);
            return Make("PNG", ".png", "image/png", w, h);
        }

        if (head[0] == 0xFF && head[1] == 0xD8)
        {
            return ReadJpeg(stream);
        }

        if (head[0] == 'G' && head[1] == 'I' && head[2] == 'F' && head[3] == '8')
        {
            var w = head[6] | (head[7] << 8);
            var h = head[8] | (head[9] << 8);
            return Make("GIF", ".gif", "image/gif", w, h);
        }

        if (head[0] == 'B' && head[1] == 'M' && read >= 26)
        {
            var w = BitConverter.ToInt32(head[18..22]);
            var h = Math.Abs(BitConverter.ToInt32(head[22..26]));
            return Make("BMP", ".bmp", "image/bmp", w, h);
        }

        if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
        {
            return ReadWebP(head, read);
        }

        return null;
    }

    private static ImageInfo? ReadWebP(ReadOnlySpan<byte> head, int read)
    {
        if (read < 30) return null;
        var fourCc = System.Text.Encoding.ASCII.GetString(head.Slice(12, 4));
        int w, h;
        switch (fourCc)
        {
            case "VP8 ":
                w = (head[26] | (head[27] << 8)) & 0x3FFF;
                h = (head[28] | (head[29] << 8)) & 0x3FFF;
                break;
            case "VP8L":
                w = 1 + (((head[22] & 0x3F) << 8) | head[21]);
                h = 1 + (((head[24] & 0xF) << 10) | (head[23] << 2) | ((head[22] & 0xC0) >> 6));
                break;
            case "VP8X":
                w = 1 + (head[24] | (head[25] << 8) | (head[26] << 16));
                h = 1 + (head[27] | (head[28] << 8) | (head[29] << 16));
                break;
            default:
                return null;
        }
        return Make("WebP", ".webp", "image/webp", w, h);
    }

    private static ImageInfo? ReadJpeg(Stream stream)
    {
        // Stream is positioned after the first 32 header bytes, so rewind and walk the segments.
        if (!stream.CanSeek) return null;
        stream.Position = 2;
        Span<byte> buffer = stackalloc byte[8];
        for (var guard = 0; guard < 512; guard++)
        {
            int b;
            do { b = stream.ReadByte(); } while (b is not -1 and not 0xFF);
            if (b == -1) return null;
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xFF);
            if (marker == -1) return null;

            if (marker is 0x01 or (>= 0xD0 and <= 0xD9)) continue; // standalone markers
            if (ReadFully(stream, buffer[..2]) < 2) return null;
            var length = (buffer[0] << 8) | buffer[1];
            if (length < 2) return null;

            var isSof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isSof)
            {
                if (ReadFully(stream, buffer[..5]) < 5) return null;
                var h = (buffer[1] << 8) | buffer[2];
                var w = (buffer[3] << 8) | buffer[4];
                return Make("JPEG", ".jpg", "image/jpeg", w, h);
            }

            stream.Seek(length - 2, SeekOrigin.Current);
        }
        return null;
    }

    private static ImageInfo? Make(string format, string ext, string mime, int w, int h) =>
        w > 0 && h > 0 && w <= 100_000 && h <= 100_000 ? new ImageInfo(format, ext, mime, new WallpaperResolution(w, h)) : null;

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> span, int offset) =>
        (span[offset] << 24) | (span[offset + 1] << 16) | (span[offset + 2] << 8) | span[offset + 3];

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
