using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace UniverseGen.Service;

/// <summary>A minimal PNG writer for greyscale density maps, with no dependency on any imaging library.</summary>
internal static class Png
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Greyscale(DensityImage image)
    {
        bool deep = image.Format != MapFormat.Byte;
        int bytesPerPixel = deep ? 2 : 1;
        int stride = image.Width * bytesPerPixel;

        // Each row starts with its filter type (0, none); 16-bit samples are big-endian.
        var raw = new byte[(stride + 1) * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            int row = y * (stride + 1);
            for (int x = 0; x < image.Width; x++)
            {
                if (deep)
                    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(row + 1 + x * 2), (ushort)MathF.Round(Math.Clamp(image[x, y], 0f, 1f) * 65535f));
                else
                    raw[row + 1 + x] = image.Data[y * image.Width + x];
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = (byte)(deep ? 16 : 8);   // bit depth
        header[9] = 0;                       // greyscale
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        png.Write(number);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
