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
/// A galaxy of the Classic model at the default scale has just those (a 23-character code), which is
/// every code shared before the Realistic model: they all still decode, as Classic galaxies. Any other
/// galaxy has 23 more bytes (a 54-character code):
///   [17]     Extension version (2)
///   [18-21]  RadiusLightYears  float32 little-endian
///   [22-25]  DiskThickness     float32
///   [26-29]  StarDensity       float32
///   [30]     FeatureDensity    (0-255)
///   [31]     Model
///   [32-35]  BulgeThickness    float32
///   [36-39]  HaloStrength      float32
/// Version 1 of the extension (bytes 17–30 only) was Classic, and still decodes.
///
/// BulgeStrength / CoreDensity / ArmDensity are fixed by the UI and are
/// restored to their standard values (1.0 / 0.90 / 0.75) on decode.
/// </summary>
public static class GalaxyCode
{
    private const int  BaseLength      = 17;
    private const int  ScaleLength     = 31;   // extension version 1
    private const int  ExtendedLength  = 40;   // extension version 2
    private const byte ExtensionVersion = 2;

    public static string Encode(GalaxyParams p)
    {
        var defaults = new GalaxyParams();
        bool extended = p.Model != GalaxyModel.Classic
                     || p.RadiusLightYears != defaults.RadiusLightYears || p.DiskThickness != defaults.DiskThickness
                     || p.StarDensity != defaults.StarDensity || p.FeatureDensity != defaults.FeatureDensity
                     || p.BulgeThickness != defaults.BulgeThickness || p.HaloStrength != defaults.HaloStrength;
        var d = new byte[extended ? ExtendedLength : BaseLength];
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

        if (extended)
        {
            d[17] = ExtensionVersion;
            BitConverter.TryWriteBytes(d.AsSpan(18), p.RadiusLightYears);
            BitConverter.TryWriteBytes(d.AsSpan(22), p.DiskThickness);
            BitConverter.TryWriteBytes(d.AsSpan(26), p.StarDensity);
            d[30] = (byte)Math.Clamp(p.FeatureDensity, 0, 255);
            d[31] = (byte)p.Model;
            BitConverter.TryWriteBytes(d.AsSpan(32), p.BulgeThickness);
            BitConverter.TryWriteBytes(d.AsSpan(36), p.HaloStrength);
        }

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

        if (d.Length < BaseLength)
            throw new FormatException("Galaxy code is too short.");
        bool scaleOnly = d.Length == ScaleLength && d[17] == 1;
        bool extended = d.Length == ExtendedLength && d[17] == ExtensionVersion;
        if (d.Length > BaseLength && !scaleOnly && !extended)
            throw new FormatException("Galaxy code is from a newer version, or damaged.");

        ulong seed = 0;
        for (int i = 7; i >= 0; i--) seed = (seed << 8) | d[9 + i];

        var p = new GalaxyParams
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
            Model         = GalaxyModel.Classic,
        };
        if (scaleOnly || extended)
        {
            p.RadiusLightYears = BitConverter.ToSingle(d, 18);
            p.DiskThickness    = BitConverter.ToSingle(d, 22);
            p.StarDensity      = BitConverter.ToSingle(d, 26);
            p.FeatureDensity   = d[30];
        }
        if (extended)
        {
            p.Model          = (GalaxyModel)d[31];
            p.BulgeThickness = BitConverter.ToSingle(d, 32);
            p.HaloStrength   = BitConverter.ToSingle(d, 36);
        }
        if (scaleOnly || extended)
        {
            try { p.Validate(); }
            catch (ArgumentException e) { throw new FormatException($"Galaxy code has a bad setting: {e.Message}"); }
        }
        return p;
    }
}
