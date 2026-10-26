using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UniverseGen.Generation;
using UniverseGen.Models;

namespace UniverseGen.Tests;

/// <summary>
/// The C# maths gives exactly the galaxies the C++ (UniverseGen.Math) did. The fingerprints were taken
/// from a Release build of the C++ before it was retired: maps of every galaxy type face-on, zoomed and
/// edge-on, and 1,800 star chunks each, from the halo to the bulge.
///
/// Exact to the bit on Windows, where .NET and the C++ share the C runtime's float functions. Another OS
/// can round a transcendental function differently in its last place, so there these may not hold, and
/// the statistical tests elsewhere are what matter.
///
/// The C++ was what's now the Classic model, which is kept exactly so every galaxy shared before stays
/// the same; these hold it to that.
/// </summary>
public class PortTests
{
    private static readonly Dictionary<string, (string Face, string Region, string Side, string Chunks, long Stars)> FromCpp = new()
    {
        ["Default"] = ("E8CFF11CB419D4C4B994CEC60085EFC8269092529E1B1896360F05609CE0CBB7", "EFC5A11C886409DDA8AF50A565BB4AEA6692F672589EC8564D2B90A336FE722A", "FC5FB3BF778B435CF44D9B402801D554569F6E84BA0AF61AFD7F7BEA595A43D3", "FD97FA78A2D4E598355374B6C38012F3E1288FA8B6CD90698FA2363B4839BF07", 80821),
        ["BarredYoung"] = ("3BA4137C10473FD2A304832CCCD375B8A1264BB4D814158912B87D1E06F74FE4", "501D7AD7FF9476E101D5016AE1497C6D40D4299DB3640BB0F5115692B2815CBF", "802FA90170F5757F1CC49D4FD8FCF624E91BAB1041F88C4105E6BA5626ED2AD9", "20317145F2D1B30F534611DB446423F6BB4E93B77A0E19CFC2EA14AE227831D7", 45445),
        ["Elliptical"] = ("DD973C2E849CA77E3DD0286507F329B1FD6FEB97AB704153ADE1B8B2FF0F8BF3", "C761BE2582E00C76B4DF7BF1F458F4E27AA97B1BC8727274C36FE0E8EB979F39", "B7CDBE2CB84B278B9BFEB7413A25CA5055D7AB414898BD12205EEF3CB31FACA3", "539E0A63A97B12C51D334F2A664793F9C81D4AC17E793AF2FDCF01A457EC35DD", 128924),
        ["Lenticular"] = ("3C49224D848DBCEA6986452FC3A99F14A189009EA6A381D5CE84CB7E9452E141", "4B190D88670F82C9881B103F280D6C754DCA51CBB35C49C06FECD635ABFC2FFE", "9712B7BF56D3C81D41C28A1256ED7D5F988D3E21F6A2B261BB88E0EF5BD7DCF0", "633BEED2E5B0AB56546536B1BDAE89E8FAC9F9F2A1BA35DE88B1B91EB229CF61", 108545),
        ["Irregular"] = ("EC88934383E9B5590C66C9DF022B770D0355C6A3F9F3D33648A539860FC573A0", "2026CB494667D651C0339423F5A3D21D6CD69A0CD533F69824E10F7BDC53E1FF", "3056921DD0166C3BBE269675134C523398D771718D0FEE8A29BC1770B7481C3E", "BEA0143751BB6D8D15E4C984F5C1BC26FFF32F4D4DF55B1077156B0F8243C5A8", 87370),
    };

    internal static GalaxyParams Galaxy(string name) => name switch
    {
        "Default" => new GalaxyParams { Model = GalaxyModel.Classic },
        "BarredYoung" => Make(GalaxyType.Spiral, GalaxyAge.Young, 2, 0.35f, 0.18f, 0.12f, 0.8f, 0.30f, 0.85f, 0.80f, 987654321, 0.6f, 0.45f),
        "Elliptical" => Make(GalaxyType.Elliptical, GalaxyAge.Old, 4, 0.5f, 0.25f, 0.30f, 1.0f, 0.20f, 0.90f, 0.75f, 42, 0f, 0.35f),
        "Lenticular" => Make(GalaxyType.Lenticular, GalaxyAge.Mature, 4, 0.5f, 0.25f, 0.35f, 1.1f, 0.10f, 0.95f, 0.70f, 7, 0f, 0.35f),
        "Irregular" => Make(GalaxyType.Irregular, GalaxyAge.Old, 3, 0.6f, 0.30f, 0.15f, 0.9f, 0.40f, 0.80f, 0.70f, 99, 0f, 0.35f),
        _ => throw new ArgumentException(name),
    };

    private static GalaxyParams Make(GalaxyType type, GalaxyAge age, int arms, float tight, float width, float bulge, float bulgeStrength,
        float dissipation, float core, float armDensity, ulong seed, float bar, float barLength) => new()
    {
        Type = type, Age = age, Arms = arms, ArmTightness = tight, ArmWidth = width, BulgeFactor = bulge, BulgeStrength = bulgeStrength,
        Dissipation = dissipation, CoreDensity = core, ArmDensity = armDensity, Seed = seed, BarStrength = bar, BarLength = barLength,
        Model = GalaxyModel.Classic,
    };

    /// The chunks the fingerprints were taken over: scattered through the disk, out into the halo, and
    /// two dense blocks (an arm, and the bulge at the centre).
    internal static IEnumerable<ChunkCoord> Chunks()
    {
        ulong s = 0xC0FFEEUL;
        ulong Next()
        {
            s += 0x9e3779b97f4a7c15UL;
            ulong x = s;
            x = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
            x = (x ^ (x >> 27)) * 0x94d049bb133111ebUL;
            return x ^ (x >> 31);
        }
        for (int i = 0; i < 1500; i++)
        {
            long x = (long)(Next() % 10001) - 5000, z = (long)(Next() % 10001) - 5000;
            long y = i < 1000 ? (long)(Next() % 41) - 20 : (long)(Next() % 2001) - 1000;
            yield return new ChunkCoord(x, y, z);
        }
        for (long dx = -4; dx < 4; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -4; dz < 4; dz++)
            yield return new ChunkCoord(1500 + dx, dy, 600 + dz);
        for (long dx = -3; dx < 3; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -3; dz < 3; dz++)
            yield return new ChunkCoord(dx, dy, dz);
    }

    private static string Hash(float[] values) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));

    public static TheoryData<string> Galaxies => new() { "Default", "BarredYoung", "Elliptical", "Lenticular", "Irregular" };

    [Theory]
    [MemberData(nameof(Galaxies))]
    public void Every_map_is_the_one_the_cpp_made(string name)
    {
        GalaxyParams p = Galaxy(name);
        var generator = new GalaxyGenerator();
        Assert.Equal(FromCpp[name].Face, Hash(generator.Generate(p, 256).DensityMap));
        Assert.Equal(FromCpp[name].Region, Hash(generator.GenerateZoomedView(p, new RegionF(0.30f, 0.40f, 0.12f, 0.12f), 1, 128).DensityMap));
        Assert.Equal(FromCpp[name].Side, Hash(generator.GenerateSideView(p, 256, 64).DensityMap));
    }

    [Theory]
    [MemberData(nameof(Galaxies))]
    public void Every_star_is_the_one_the_cpp_made(string name)
    {
        GalaxyParams p = Galaxy(name);
        var generator = new StarGenerator();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        Span<byte> star = stackalloc byte[16];
        foreach (ChunkCoord coord in Chunks())
        {
            StarChunk chunk = generator.Generate(p, coord);
            total += chunk.Stars.Length;
            hash.AppendData(BitConverter.GetBytes(chunk.Stars.Length));
            // The C++ star's layout: three floats, then class, luminosity, flags and a pad byte.
            foreach (Star s in chunk.Stars)
            {
                BitConverter.TryWriteBytes(star, s.X);
                BitConverter.TryWriteBytes(star[4..], s.Y);
                BitConverter.TryWriteBytes(star[8..], s.Z);
                star[12] = (byte)s.Class;
                star[13] = s.Luminosity;
                star[14] = (byte)s.Flags;
                star[15] = 0;
                hash.AppendData(star);
            }
            hash.AppendData(new[] { (byte)chunk.Region });
        }
        Assert.Equal(FromCpp[name].Stars, total);
        Assert.Equal(FromCpp[name].Chunks, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
