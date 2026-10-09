using System.Buffers.Binary;
using System.IO.Compression;

namespace PazScan.Core.Scanning;

/// <summary>
/// An 8-bit greyscale PNG writer and a PNG/JPEG size reader — enough for the demo scanner, which has
/// to run where no image library does (the macOS development machine).
/// </summary>
internal static class SimplePng
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="pixels">Row-major luminance, <paramref name="width"/> × <paramref name="height"/>.</param>
    public static byte[] EncodeGray(byte[] pixels, int width, int height, int dpi)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 0; // greyscale
        WriteChunk(output, "IHDR", header);

        // pHYs, so whoever opens the file sees the page at its real size. Pixels per metre.
        var physical = new byte[9];
        var perMetre = (int)Math.Round(dpi / 0.0254);
        BinaryPrimitives.WriteInt32BigEndian(physical.AsSpan(0), perMetre);
        BinaryPrimitives.WriteInt32BigEndian(physical.AsSpan(4), perMetre);
        physical[8] = 1;
        WriteChunk(output, "pHYs", physical);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (var y = 0; y < height; y++)
                {
                    zlib.WriteByte(0); // filter: none
                    zlib.Write(pixels, y * width, width);
                }
            }
            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Width and height from a PNG's IHDR or a JPEG's first SOF marker.</summary>
    public static (int Width, int Height)? ReadSize(byte[] bytes)
    {
        if (bytes.Length > 24 && bytes.AsSpan(0, 8).SequenceEqual(Signature))
            return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));

        if (bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var offset = 2;
            while (offset + 9 < bytes.Length)
            {
                if (bytes[offset] != 0xFF) return null;
                var marker = bytes[offset + 1];
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2));
                // SOF0–SOF15, less DHT (C4), JPG (C8) and DAC (CC), which share the range.
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 7)),
                            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5)));
                offset += 2 + length;
            }
        }
        return null;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
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
