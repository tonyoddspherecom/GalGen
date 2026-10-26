using System.Numerics;

namespace UniverseGen.Models;

/// <summary>
/// The supermassive black hole at the galactic centre.
///
/// Mass is derived from the galaxy's type and bulge factor using the empirical
/// M_BH – M_bulge correlation, then varied by up to ±50 % via the galaxy seed
/// so every galaxy has a unique SMBH without changing the overall distribution.
///
/// Position is always the galactic origin (0, 0, 0) in normalised space.
/// 1 normalised unit = RadiusLightYears (50,000 light-years by default).
/// </summary>
public sealed class GalacticBlackHole
{
    /// Always at the galactic origin in normalised galaxy space.
    public static readonly Vector3 Position = Vector3.Zero;

    /// Mass in solar masses, derived from galaxy type × bulge factor × seed scatter.
    public double MassSolarMasses { get; }

    /// Schwarzschild radius in light-years.
    /// Purely informational — negligible on any galaxy-scale view.
    public double SchwarzschildRadiusLy { get; }

    /// Gravitational sphere-of-influence radius in normalised units.
    /// Scales with √(M / M_MW), anchored at 0.0004 normalised (~20 ly) for a
    /// Milky-Way-mass black hole.  Clamped to [0.0001, 0.05].
    public float InfluenceRadiusNorm { get; }

    /// 0 = completely dormant, 1 = quasar-bright.
    /// Young galaxies tend toward high values; old galaxies toward low values.
    public float ActivityLevel { get; }

    /// Scientific designation in J2000 catalogue format, e.g. "J0229+47*".
    public string Designation { get; }

    public GalacticBlackHole(GalaxyParams p)
    {
        // ── mass (M_BH – M_bulge relation, order-of-magnitude) ───────────────
        // Reference masses at BulgeFactor = 0.20, seed-factor = 1.0:
        //   Spiral     ≈  4 million M☉  (Milky Way ~ 4.1e6)
        //   Lenticular ≈ 200 million M☉
        //   Elliptical ≈   2 billion M☉ (M87 ~ 6.5e9 is an extreme outlier)
        //   Irregular  ≈    50,000 M☉   (many irregulars lack a confirmed SMBH)
        double baseM = p.Type switch
        {
            GalaxyType.Elliptical => 2_000_000_000.0,
            GalaxyType.Lenticular =>   200_000_000.0,
            GalaxyType.Irregular  =>        50_000.0,
            _                     =>     4_000_000.0,  // Spiral
        };

        // More bulge → more mass (0.5× – 3.0× range over BulgeFactor 0..1)
        double bulgeScale = 0.5 + p.BulgeFactor * 2.5;

        // Seed-based ±50 % scatter for individuality
        ulong  h          = Mix(p.Seed ^ 0x424C41434B484F4CUL);   // "BLACKHOL"
        double seedFactor = 0.5 + (h & 0xFFFFUL) / (double)0xFFFF;

        MassSolarMasses = baseM * bulgeScale * seedFactor;

        // Schwarzschild radius: r_S = 2GM/c² ≈ 2.953 km per solar mass
        // 1 ly ≈ 9.461 × 10¹² km
        SchwarzschildRadiusLy = 2.953e-10 * MassSolarMasses / 9.461e12;

        // Sphere of influence scales with √(M / M_MW)
        float massRatio = (float)(MassSolarMasses / 4_000_000.0);
        InfluenceRadiusNorm = Math.Clamp(0.0004f * MathF.Sqrt(massRatio), 0.0001f, 0.05f);

        // Activity: younger galaxies have more gas to feed the accretion disk.
        float baseActivity = p.Age switch
        {
            GalaxyAge.Young  => 0.60f,
            GalaxyAge.Mature => 0.25f,
            GalaxyAge.Old    => 0.04f,
            _                => 0.25f,
        };
        float jitter = (float)((h >> 24 & 0xFFUL) / 255.0) * 0.25f;
        ActivityLevel = Math.Clamp(baseActivity + jitter, 0f, 1f);

        Designation = BuildDesignation(p.Seed);
    }

    /// Formats mass for display: "4.1M M☉", "2.3G M☉", "48k M☉", etc.
    public string MassDisplay =>
        MassSolarMasses >= 1e9 ? $"{MassSolarMasses / 1e9:F1}G M☉"
      : MassSolarMasses >= 1e6 ? $"{MassSolarMasses / 1e6:F1}M M☉"
      : MassSolarMasses >= 1e3 ? $"{MassSolarMasses / 1e3:F0}k M☉"
                               : $"{MassSolarMasses:F0} M☉";

    // ── helpers ───────────────────────────────────────────────────────────────

    /// Generates a J2000-style radio source designation, e.g. "J1423-32*".
    private static string BuildDesignation(ulong seed)
    {
        ulong h   = Mix(seed ^ 0x534D42484F4C4555UL);   // "SMBHOL"
        int   rah = (int)(h         % 24);               // 00–23 h
        int   ram = (int)((h >>  6) % 60);               // 00–59 m
        int   dec = (int)((h >> 12) % 90);               // 00–89 °
        char  sgn = (h >> 20 & 1) == 0 ? '+' : '-';
        return $"J{rah:D2}{ram:D2}{sgn}{dec:D2}*";
    }

    private static ulong Mix(ulong h)
    {
        h = (h ^ (h >> 30)) * 0xbf58476d1ce4e5b9UL;
        h = (h ^ (h >> 27)) * 0x94d049bb133111ebUL;
        return h ^ (h >> 31);
    }
}
