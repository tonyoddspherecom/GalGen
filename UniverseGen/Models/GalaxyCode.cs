namespace UniverseGen.Models;

/// <summary>
/// Encodes a complete GalaxyParams as a compact, pasteable 23-character string.
/// All user-controllable parameters are packed into 17 bytes and Base64URL-encoded,
/// producing an opaque token with no visible structure.
///
/// Layout (17 bytes):
///   [0]  GalaxyType  (0-3)
///   [1]  GalaxyAge   (0-2)
///   [2]  Arms        (2-8)
///   [3]  ArmTightness × 100  (10-100)
///   [4]  ArmWidth     × 100  (5-80)
///   [5]  BulgeFactor  × 100  (5-60)
///   [6]  Dissipation  × 100  (0-60)
///   [7]  BarStrength  × 100  (0-100)
///   [8]  BarLength    × 100  (10-60)
///   [9-16] Seed as little-endian uint64
///
/// BulgeStrength / CoreDensity / ArmDensity are fixed by the UI and are
/// restored to their standard values (1.0 / 0.90 / 0.75) on decode.
/// </summary>
public static class GalaxyCode
{
    public static string Encode(GalaxyParams p)
    {
        var d = new byte[17];
        d[0] = (byte)Math.Clamp((int)p.Type,        0,   3);
        d[1] = (byte)Math.Clamp((int)p.Age,         0,   2);
        d[2] = (byte)Math.Clamp(p.Arms,             2,   8);
        d[3] = (byte)Math.Clamp((int)(p.ArmTightness * 100), 10, 100);
        d[4] = (byte)Math.Clamp((int)(p.ArmWidth     * 100),  5,  80);
        d[5] = (byte)Math.Clamp((int)(p.BulgeFactor  * 100),  5,  60);
        d[6] = (byte)Math.Clamp((int)(p.Dissipation  * 100),  0,  60);
        d[7] = (byte)Math.Clamp((int)(p.BarStrength  * 100),  0, 100);
        d[8] = (byte)Math.Clamp((int)(p.BarLength    * 100), 10,  60);

        ulong s = p.Seed;
        for (int i = 0; i < 8; i++) { d[9 + i] = (byte)(s & 0xFF); s >>= 8; }

        return Convert.ToBase64String(d).TrimEnd('=')
                      .Replace('+', '-').Replace('/', '_');
    }

    public static GalaxyParams Decode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new FormatException("Galaxy code is empty.");

        string b64 = code.Trim().Replace('-', '+').Replace('_', '/');
        int pad = (4 - b64.Length % 4) % 4;
        b64 += new string('=', pad);

        byte[] d;
        try   { d = Convert.FromBase64String(b64); }
        catch { throw new FormatException("Not a valid galaxy code."); }

        if (d.Length < 17)
            throw new FormatException("Galaxy code is too short.");

        ulong seed = 0;
        for (int i = 7; i >= 0; i--) seed = (seed << 8) | d[9 + i];

        return new GalaxyParams
        {
            Type          = (GalaxyType)Math.Clamp((int)d[0], 0,   3),
            Age           = (GalaxyAge) Math.Clamp((int)d[1], 0,   2),
            Arms          =             Math.Clamp((int)d[2], 2,   8),
            ArmTightness  =             Math.Clamp((int)d[3], 10, 100) / 100f,
            ArmWidth      =             Math.Clamp((int)d[4],  5,  80) / 100f,
            BulgeFactor   =             Math.Clamp((int)d[5],  5,  60) / 100f,
            BulgeStrength = 1.0f,
            Dissipation   =             Math.Clamp((int)d[6],  0,  60) / 100f,
            CoreDensity   = 0.90f,
            ArmDensity    = 0.75f,
            BarStrength   =             Math.Clamp((int)d[7],  0, 100) / 100f,
            BarLength     =             Math.Clamp((int)d[8], 10,  60) / 100f,
            Seed          = seed,
        };
    }
}
