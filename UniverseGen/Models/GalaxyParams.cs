using System.Runtime.InteropServices;

namespace UniverseGen.Models;

public enum GalaxyAge  { Young, Mature, Old }
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

    public GalaxyParams Clone() => (GalaxyParams)MemberwiseClone();
}

/// <summary>
/// Blittable mirror of GalaxyParams for P/Invoke — must match GalaxyMathParams in UniverseGenMath.h
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GalaxyMathParams
{
    public int    Arms;
    public float  ArmTightness;
    public float  ArmWidth;
    public float  BulgeFactor;
    public float  BulgeStrength;
    public float  Dissipation;
    public float  CoreDensity;
    public float  ArmDensity;
    public int    Age;
    public ulong  Seed;
    public float  BarStrength;
    public float  BarLength;
    public int    GalaxyType;

    internal static GalaxyMathParams From(GalaxyParams p) => new()
    {
        Arms          = p.Arms,
        ArmTightness  = p.ArmTightness,
        ArmWidth      = p.ArmWidth,
        BulgeFactor   = p.BulgeFactor,
        BulgeStrength = p.BulgeStrength,
        Dissipation   = p.Dissipation,
        CoreDensity   = p.CoreDensity,
        ArmDensity    = p.ArmDensity,
        Age           = (int)p.Age,
        Seed          = p.Seed,
        BarStrength   = p.BarStrength,
        BarLength     = p.BarLength,
        GalaxyType    = (int)p.Type
    };
}
