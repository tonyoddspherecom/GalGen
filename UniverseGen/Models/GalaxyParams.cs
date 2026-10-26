namespace UniverseGen.Models;

public enum GalaxyAge  { Young, Mature, Old }

/// <summary>
/// How a galaxy is built in depth, and how many stars it has. The face-on shapes and maps are the
/// same in both.
/// </summary>
public enum GalaxyModel
{
    /// GalGen's original model, kept exactly: every galaxy made before the Realistic model, and every
    /// code shared from then, is this one. A thick disk, a big spherical bulge, and about fifty times
    /// the real density of stars.
    Classic = 0,
    /// Real structure and numbers: a thin disk and a thick one, a compact flattened bulge, a faint
    /// halo, and a few hundred billion stars in a Milky Way. The default.
    Realistic = 1,
}
public enum GalaxyType { Spiral, Elliptical, Lenticular, Irregular }

/// <summary>
/// A normalised rectangular region in [0,1] galaxy space.
/// Used to specify sub-regions for zoomed generation without any dependency on System.Drawing.
/// </summary>
public readonly record struct RegionF(float X, float Y, float Width, float Height)
{
    /// The full galaxy extent.
    public static readonly RegionF Full = new(0f, 0f, 1f, 1f);
}

public class GalaxyParams
{
    public int        Arms          { get; set; } = 4;
    public float      ArmTightness  { get; set; } = 0.5f;
    public float      ArmWidth      { get; set; } = 0.25f;
    public float      BulgeFactor   { get; set; } = 0.20f;
    public float      BulgeStrength { get; set; } = 1.0f;
    public float      Dissipation   { get; set; } = 0.15f;
    public float      CoreDensity   { get; set; } = 0.90f;
    public float      ArmDensity    { get; set; } = 0.75f;
    public GalaxyAge  Age           { get; set; } = GalaxyAge.Mature;
    public ulong      Seed          { get; set; } = 12345;
    public float      BarStrength     { get; set; } = 0.0f;
    public float      BarLength       { get; set; } = 0.35f;
    public GalaxyType Type            { get; set; } = GalaxyType.Spiral;
    /// Scales the physically-derived nebula and cluster counts (25–200, default 100 = realistic baseline).
    /// Actual counts are computed from Type, Age and Arms — this multiplies that baseline.
    public int        FeatureDensity  { get; set; } = 100;

    // ── scale ─────────────────────────────────────────────────────────────────
    // The shape above is the same at any size: these set how big the galaxy is, how thick its disk,
    // and how crowded with stars. The defaults are a Milky Way: they give exactly the galaxy GalGen
    // always has, so existing seeds and codes keep their stars.

    /// The galaxy's radius in light years, which is one normalised unit. 50,000 is a Milky Way.
    public float      RadiusLightYears { get; set; } = 50_000f;
    /// Multiplies the disk's thickness (its scale height, which grows with age and bulge).
    /// Below 1 is flatter: 0.1 is a very thin disk, easy to read top-down. The bulge keeps its shape.
    public float      DiskThickness    { get; set; } = 1.0f;
    /// Multiplies how many stars a region holds. 1 is about the Sun's neighbourhood (~0.14 stars a
    /// cubic light year in the arms); 0.01 is a hundredth as many. Below 1, chunks expecting less than
    /// one star roll for it rather than round it away, so a sparse galaxy still has its faint scatter.
    public float      StarDensity      { get; set; } = 1.0f;
    /// Realistic model: the bulge's height as a share of its width. 0.6 is a Milky Way's; lower flattens
    /// it into the disk (with DiskThickness, for a galaxy that's flat all over).
    public float      BulgeThickness   { get; set; } = 0.6f;
    /// Realistic model: the faint spherical halo of old stars round the galaxy. 1 is a Milky Way's (about
    /// one star in two hundred); 0 has none, so no star is far from the plane.
    public float      HaloStrength     { get; set; } = 1.0f;
    /// How the galaxy is built in depth: see <see cref="GalaxyModel"/>.
    public GalaxyModel Model           { get; set; } = GalaxyModel.Realistic;

    /// A distance in normalised galaxy units, in light years.
    public float ToLightYears(float normalised) => normalised * RadiusLightYears;
    /// A distance in light years, in normalised galaxy units.
    public float ToNormalised(float lightYears) => lightYears / RadiusLightYears;

    /// <summary>
    /// Throws <see cref="ArgumentException"/> naming the first setting that would break generation
    /// rather than change it (a zero arm width, a negative density). For params from outside: a
    /// config file, a request, a user.
    /// </summary>
    public void Validate()
    {
        static void Require(bool ok, string what)
        {
            if (!ok) throw new ArgumentException(what);
        }
        Require(Enum.IsDefined(Type), $"{nameof(Type)} isn't a galaxy type");
        Require(Enum.IsDefined(Age), $"{nameof(Age)} isn't a galaxy age");
        Require(Arms is >= 1 and <= 32, $"{nameof(Arms)} must be 1 to 32");
        Require(ArmTightness > 0f && float.IsFinite(ArmTightness), $"{nameof(ArmTightness)} must be above 0");
        Require(ArmWidth > 0f && float.IsFinite(ArmWidth), $"{nameof(ArmWidth)} must be above 0");
        Require(BulgeFactor > 0f && float.IsFinite(BulgeFactor), $"{nameof(BulgeFactor)} must be above 0");
        Require(BulgeStrength >= 0f && float.IsFinite(BulgeStrength), $"{nameof(BulgeStrength)} can't be negative");
        Require(Dissipation >= 0f && float.IsFinite(Dissipation), $"{nameof(Dissipation)} can't be negative");
        Require(CoreDensity >= 0f && float.IsFinite(CoreDensity), $"{nameof(CoreDensity)} can't be negative");
        Require(ArmDensity >= 0f && float.IsFinite(ArmDensity), $"{nameof(ArmDensity)} can't be negative");
        Require(BarStrength is >= 0f and <= 1f, $"{nameof(BarStrength)} must be 0 to 1");
        Require(BarLength > 0f && float.IsFinite(BarLength), $"{nameof(BarLength)} must be above 0");
        Require(FeatureDensity is >= 0 and <= 1000, $"{nameof(FeatureDensity)} must be 0 to 1000");
        Require(RadiusLightYears > 0f && float.IsFinite(RadiusLightYears), $"{nameof(RadiusLightYears)} must be above 0");
        Require(DiskThickness > 0f && float.IsFinite(DiskThickness), $"{nameof(DiskThickness)} must be above 0");
        Require(StarDensity >= 0f && StarDensity <= 100f, $"{nameof(StarDensity)} must be 0 to 100");
        Require(BulgeThickness > 0f && BulgeThickness <= 4f, $"{nameof(BulgeThickness)} must be above 0, and at most 4");
        Require(HaloStrength >= 0f && HaloStrength <= 100f, $"{nameof(HaloStrength)} must be 0 to 100");
        Require(Enum.IsDefined(Model), $"{nameof(Model)} isn't a galaxy model");
    }

    /// <summary>
    /// A short name for exactly this galaxy: the same params always give the same one, and any change
    /// (including to the generation maths itself) gives another. For keying caches, on a server or on
    /// disk, and for a client to tell whether what it holds is still right.
    /// </summary>
    public string Fingerprint()
    {
        var data = new byte[96];
        int n = 0;
        void Int(int v)     { BitConverter.TryWriteBytes(data.AsSpan(n), v); n += 4; }
        void Float(float v) { BitConverter.TryWriteBytes(data.AsSpan(n), v); n += 4; }
        Int(Generation.GalaxyMath.AlgorithmVersion);
        Int((int)Type); Int((int)Age); Int(Arms);
        Float(ArmTightness); Float(ArmWidth); Float(BulgeFactor); Float(BulgeStrength); Float(Dissipation);
        Float(CoreDensity); Float(ArmDensity); Float(BarStrength); Float(BarLength);
        BitConverter.TryWriteBytes(data.AsSpan(n), Seed); n += 8;
        Int(FeatureDensity); Float(RadiusLightYears); Float(DiskThickness); Float(StarDensity);
        Int((int)Model); Float(BulgeThickness); Float(HaloStrength);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.AsSpan(0, n)))[..16].ToLowerInvariant();
    }

    public GalaxyParams Clone() => (GalaxyParams)MemberwiseClone();
}
