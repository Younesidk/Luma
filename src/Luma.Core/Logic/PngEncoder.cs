using System.Buffers.Binary;
using System.IO.Compression;

namespace Luma.Core.Logic;

/// <summary>
/// Minimal PNG writer for opaque BGRA frames (video thumbnails and still frames). Writes 8-bit RGB with the "Sub" filter,
/// which compresses video frames well and keeps Luma free of an imaging dependency in the platform layer.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="bgra">Pixels in B, G, R, A order; alpha is ignored.</param>
    /// <param name="stride">Bytes per source row (may include padding).</param>
    public static byte[] EncodeBgra(ReadOnlySpan<byte> bgra, int width, int height, int stride, CompressionLevel level = CompressionLevel.Optimal)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "The image must have a size.");
        if (stride < width * 4) throw new ArgumentOutOfRangeException(nameof(stride));
        if (bgra.Length < (long)stride * (height - 1) + width * 4) throw new ArgumentException("Not enough pixel data.", nameof(bgra));

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: truecolour (RGB)
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter method
        ihdr[12] = 0; // no interlace
        WriteChunk(output, "IHDR"u8, ihdr);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, level, leaveOpen: true))
            {
                var row = new byte[1 + width * 3];
                for (var y = 0; y < height; y++)
                {
                    var source = bgra.Slice(y * stride, width * 4);
                    row[0] = 1; // filter type: Sub
                    byte prevR = 0, prevG = 0, prevB = 0;
                    for (var x = 0; x < width; x++)
                    {
                        byte b = source[x * 4], g = source[x * 4 + 1], r = source[x * 4 + 2];
                        var o = 1 + x * 3;
                        row[o] = (byte)(r - prevR);
                        row[o + 1] = (byte)(g - prevG);
                        row[o + 2] = (byte)(b - prevB);
                        prevR = r; prevG = g; prevB = b;
                    }
                    zlib.Write(row);
                }
            }
            WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }

        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    /// <summary>
    /// Area-averaging downscale of tightly packed BGRA pixels (used to make the gallery thumbnail from the still frame
    /// without a second GPU capture). <paramref name="width"/>/<paramref name="height"/> must not exceed the source size.
    /// </summary>
    public static byte[] DownscaleBgra(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > sourceWidth || height > sourceHeight) throw new ArgumentOutOfRangeException(nameof(width));
        if (source.Length < sourceWidth * sourceHeight * 4) throw new ArgumentException("Not enough pixel data.", nameof(source));
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var y0 = (int)((long)y * sourceHeight / height);
            var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sourceHeight / height));
            for (var x = 0; x < width; x++)
            {
                var x0 = (int)((long)x * sourceWidth / width);
                var x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sourceWidth / width));
                long b = 0, g = 0, r = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = source.Slice((sy * sourceWidth + x0) * 4, (x1 - x0) * 4);
                    for (var i = 0; i < row.Length; i += 4)
                    {
                        b += row[i];
                        g += row[i + 1];
                        r += row[i + 2];
                    }
                }
                var count = (long)(y1 - y0) * (x1 - x0);
                var o = (y * width + x) * 4;
                result[o] = (byte)(b / count);
                result[o + 1] = (byte)(g / count);
                result[o + 2] = (byte)(r / count);
                result[o + 3] = 255;
            }
        }
        return result;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        var crc = Crc32(0xFFFFFFFFu, type);
        crc = Crc32(crc, data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    internal static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
