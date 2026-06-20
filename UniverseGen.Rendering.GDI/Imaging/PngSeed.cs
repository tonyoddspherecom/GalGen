using System.Drawing;
using System.Drawing.Imaging;
using UniverseGen.Models;

namespace UniverseGen.Imaging;

/// <summary>
/// Embeds GalaxyParams deterministically into the LSBs of a PNG image so the file is
/// self-contained — share the PNG, regenerate the identical galaxy.
/// PNG must be kept lossless; JPEG will corrupt the hidden data.
/// </summary>
public static class PngSeed
{
    private static readonly byte[] Magic   = [0x55, 0x47, 0x53, 0x44]; // "UGSD"
    private const           byte   Version = 3;

    // ── public API ──────────────────────────────────────────────────────────

    public static void Save(Bitmap bmp, GalaxyParams parms, string path)
    {
        // Flatten onto opaque black before encoding. The galaxy bitmap has alpha=0 for
        // background pixels; GDI+ zeroes their RGB during internal premultiplied alpha
        // conversions, destroying the steganographic payload. Compositing onto a solid
        // black background makes every pixel fully opaque before we touch the LSBs.
        using var flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(flat))
        {
            g.Clear(Color.Black);
            g.DrawImage(bmp, 0, 0);
        }
        using var encoded = Encode(flat, Serialize(parms));
        encoded.Save(path, ImageFormat.Png);
    }

    public static (Bitmap image, GalaxyParams parms) Load(string path)
    {
        var bmp   = new Bitmap(path);
        var parms = Deserialize(Decode(bmp));
        return (bmp, parms);
    }

    // ── serialise / deserialise ─────────────────────────────────────────────

    private static byte[] Serialize(GalaxyParams p)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(Version);
        w.Write(p.Arms);
        w.Write(p.ArmTightness);
        w.Write(p.ArmWidth);
        w.Write(p.BulgeFactor);
        w.Write(p.BulgeStrength);
        w.Write(p.Dissipation);
        w.Write(p.CoreDensity);
        w.Write(p.ArmDensity);
        w.Write((int)p.Age);
        w.Write(p.Seed);
        w.Write(p.BarStrength);
        w.Write(p.BarLength);
        w.Write((int)p.Type);
        return ms.ToArray();
    }

    private static GalaxyParams Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r  = new BinaryReader(ms);
        if (!r.ReadBytes(4).SequenceEqual(Magic))
            throw new InvalidDataException("Not a valid galaxy seed image.");
        byte ver = r.ReadByte();
        if (ver != Version)
            throw new InvalidDataException($"Unsupported seed version {ver}.");
        return new GalaxyParams
        {
            Arms          = r.ReadInt32(),
            ArmTightness  = r.ReadSingle(),
            ArmWidth      = r.ReadSingle(),
            BulgeFactor   = r.ReadSingle(),
            BulgeStrength = r.ReadSingle(),
            Dissipation   = r.ReadSingle(),
            CoreDensity   = r.ReadSingle(),
            ArmDensity    = r.ReadSingle(),
            Age         = (GalaxyAge)r.ReadInt32(),
            Seed        = r.ReadUInt64(),
            BarStrength = r.ReadSingle(),
            BarLength   = r.ReadSingle(),
            Type        = (GalaxyType)r.ReadInt32(),
        };
    }

    // ── steganography ────────────────────────────────────────────────────────

    private static Bitmap Encode(Bitmap source, byte[] payload)
    {
        // Layout: [4-byte big-endian length][payload]
        // ReadBits accumulates MSB-first, so the length must be written big-endian.
        var full = new byte[4 + payload.Length];
        full[0] = (byte)(payload.Length >> 24);
        full[1] = (byte)(payload.Length >> 16);
        full[2] = (byte)(payload.Length >>  8);
        full[3] = (byte)(payload.Length      );
        payload.CopyTo(full, 4);

        if (full.Length * 8 > source.Width * source.Height * 3)
            throw new InvalidOperationException("Image too small to store seed data.");

        var result = new Bitmap(source);
        var data   = result.LockBits(new Rectangle(0, 0, result.Width, result.Height),
                                     ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* pixels = (byte*)data.Scan0;
            int   stride = data.Stride;

            int bitIdx = 0;
            foreach (byte b in full)
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    int pixelIdx = bitIdx / 3;
                    int channel  = bitIdx % 3;           // BGRA layout: 0=B 1=G 2=R
                    int px       = pixelIdx % result.Width;
                    int py       = pixelIdx / result.Width;
                    int offset   = py * stride + px * 4 + channel;
                    pixels[offset] = (byte)((pixels[offset] & 0xFE) | ((b >> bit) & 1));
                    bitIdx++;
                }
            }
        }
        result.UnlockBits(data);
        return result;
    }

    private static byte[] Decode(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] result;
        unsafe
        {
            byte* pixels = (byte*)data.Scan0;
            int   stride = data.Stride;
            int   length = ReadBits(pixels, stride, bmp.Width, 0, 32);
            if (length <= 0 || length > 65536)
                throw new InvalidDataException("No valid seed data found in image.");
            result = new byte[length];
            for (int i = 0; i < length; i++)
                result[i] = (byte)ReadBits(pixels, stride, bmp.Width, 32 + i * 8, 8);
        }
        bmp.UnlockBits(data);
        return result;
    }

    private static unsafe int ReadBits(byte* pixels, int stride, int width, int startBit, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            int bitIdx   = startBit + i;
            int pixelIdx = bitIdx / 3;
            int channel  = bitIdx % 3;
            int px       = pixelIdx % width;
            int py       = pixelIdx / width;
            value = (value << 1) | (pixels[py * stride + px * 4 + channel] & 1);
        }
        return value;
    }
}
