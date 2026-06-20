using UniverseGen.Models;

namespace UniverseGen.Generation;

/// <summary>
/// Procedurally generates a complete star system for any given <see cref="Star"/>.
///
/// Generation is fully deterministic from the star's world-space position combined
/// with the galaxy seed, so the same star always produces the same system.
///
/// Physical bases used:
///   • Luminosity   — log-linear interpolation within each spectral class's range.
///   • Habitable zone — Kopparapu (2013) conservative: [0.95, 1.37] × √L AU.
///   • Snow line    — r ≈ 2.7 × √L AU.
///   • Orbits       — Titius–Bode-like log-spaced with ×1.4–2.5 step scatter.
///   • Temperature  — equilibrium T = 278.5 × L^0.25 / √r (K), greenhouse-adjusted.
///   • Gravity      — M / R² (Earth = 1).
///   • Mass–radius  — rocky: R ∝ M^0.27; ice giant capped 3–5.5 R♁; gas giant 8–13 R♁.
/// </summary>
public sealed class StarSystemGenerator
{
    // ── public entry point ─────────────────────────────────────────────────────

    /// <summary>
    /// Generate the star system for <paramref name="star"/>.
    /// </summary>
    /// <param name="star">The host star.</param>
    /// <param name="age">Galaxy age — affects atmosphere and composition.</param>
    /// <param name="galaxySeed">Galaxy-wide seed for cross-system variety.</param>
    /// <param name="designation">
    ///   Catalogue name already shown in the UI tooltip (e.g. "HD 40307").
    ///   Planets are named "{designation} b", "c", … in orbital order.
    /// </param>
    public StarSystem Generate(Star star, GalaxyAge age, ulong galaxySeed, string designation)
    {
        ulong seed = StarSeed(star) ^ galaxySeed;

        var   cls  = star.Class;
        float lum  = LuminositySol(cls, star.Luminosity / 255f, star.IsGiant, star.IsSupergiant);

        float hzIn   = lum > 0f ? 0.95f  * MathF.Sqrt(lum) : 0f;
        float hzOut  = lum > 0f ? 1.37f  * MathF.Sqrt(lum) : 0f;
        float snow   = lum > 0f ? 2.70f  * MathF.Sqrt(lum) : 999f;

        var companions = BuildCompanions(star, ref seed, designation);
        var bodies     = BuildBodies(star, ref seed, age, lum, hzIn, hzOut, snow, designation);
        bool oort      = bodies.Length > 0 && NextFloat(ref seed) > 0.15f;

        return new StarSystem(designation, cls, lum, hzIn, hzOut, snow, companions, bodies, oort);
    }

    // ── luminosity ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts spectral class + within-class luminosity fraction to solar luminosities.
    /// Uses log-linear interpolation between the class's dim and bright main-sequence ends.
    /// Giants/supergiants receive a multiplier consistent with luminosity class III/I.
    /// </summary>
    private static float LuminositySol(SpectralClass cls, float t,
                                        bool isGiant, bool isSupergiant)
    {
        var (lo, hi) = cls switch
        {
            SpectralClass.O          => (30_000f, 300_000f),
            SpectralClass.B          => (     25f,  30_000f),
            SpectralClass.A          => (      5f,      25f),
            SpectralClass.F          => (    1.5f,       5f),
            SpectralClass.G          => (   0.45f,     1.5f),
            SpectralClass.K          => (   0.08f,    0.45f),
            SpectralClass.M          => ( 0.0001f,    0.08f),
            SpectralClass.WhiteDwarf => ( 0.0001f,   0.005f),
            _                        => (      0f,       0f),
        };
        if (lo <= 0f) return 0f;

        float baseL = MathF.Pow(10f, MathF.Log10(lo) + t * (MathF.Log10(hi) - MathF.Log10(lo)));
        if (isSupergiant) return baseL * 180f;
        if (isGiant)      return baseL *  12f;
        return baseL;
    }

    // ── companion stars ────────────────────────────────────────────────────────

    private static CompanionStar[] BuildCompanions(Star star, ref ulong seed, string designation)
    {
        if (!star.IsBinary) return [];

        // 25 % chance of a third component (trinary)
        int count = NextFloat(ref seed) < 0.25f ? 2 : 1;
        var result = new CompanionStar[count];

        for (int i = 0; i < count; i++)
        {
            // Companions tend to be same class or cooler (realistic mass ratio)
            var companionClass = LowerClass(star.Class, ref seed);
            float massSol      = CompanionMassSol(companionClass);
            float orbit        = 30f + NextFloat(ref seed) * 770f;   // 30–800 AU (wide binary)
            string suffix      = i == 0 ? " B" : " C";
            result[i] = new CompanionStar(companionClass, massSol, orbit, designation + suffix);
        }
        return result;
    }

    /// Returns a spectral class that is the same or one step cooler than <paramref name="primary"/>.
    private static SpectralClass LowerClass(SpectralClass primary, ref ulong seed)
    {
        // 30 % same class, 50 % one step cooler, 20 % two steps cooler
        int step = NextFloat(ref seed) switch { < 0.30f => 0, < 0.80f => 1, _ => 2 };
        int idx  = Math.Min((int)primary + step, (int)SpectralClass.M);
        return (SpectralClass)idx;
    }

    private static float CompanionMassSol(SpectralClass cls) => cls switch
    {
        SpectralClass.O          => 20f,
        SpectralClass.B          =>  5f,
        SpectralClass.A          =>  2f,
        SpectralClass.F          => 1.3f,
        SpectralClass.G          =>  1f,
        SpectralClass.K          => 0.7f,
        SpectralClass.M          => 0.3f,
        SpectralClass.WhiteDwarf => 0.7f,
        _                        => 1.0f,
    };

    // ── bodies (planets + belts) ───────────────────────────────────────────────

    private static StarSystemBody[] BuildBodies(
        Star star, ref ulong seed, GalaxyAge age,
        float lum, float hzIn, float hzOut, float snow, string designation)
    {
        // Neutron stars and black holes don't host planets in this generator.
        // White dwarfs can have remnant planets; supergiants rarely have stable systems.
        if (star.Class is SpectralClass.NeutronStar or SpectralClass.BlackHole)
            return [];
        if (star.IsSupergiant && NextFloat(ref seed) > 0.12f)
            return [];

        int planetCount = RollPlanetCount(star, ref seed);
        if (planetCount == 0) return [];

        // ── orbital radii ──────────────────────────────────────────────────
        float rMin = star.Class == SpectralClass.M
            ? 0.015f                                        // M dwarfs pack planets tight
            : Math.Max(0.04f, 0.04f * MathF.Pow(lum, 0.25f));
        float rMax = Math.Min(80f * MathF.Sqrt(Math.Max(lum, 0.001f)), 120f);

        var orbits = new List<float>(planetCount);
        float r = rMin;
        for (int i = 0; i < planetCount; i++)
        {
            orbits.Add(r);
            r *= 1.4f + NextFloat(ref seed) * 1.1f;   // 1.4–2.5 × per step
            if (r > rMax) break;
        }

        // ── maybe insert a hot Jupiter ─────────────────────────────────────
        // ~1 % of systems have a gas giant that migrated inward of the snow line.
        bool hotJupiter = NextFloat(ref seed) < 0.01f && lum > 0.1f;

        // ── build bodies in orbital order ──────────────────────────────────
        var bodies = new List<StarSystemBody>(orbits.Count + 1);
        char letter = 'b';

        foreach (float orbit in orbits)
        {
            bool inHz     = hzIn > 0f && orbit >= hzIn && orbit <= hzOut;
            bool pastSnow = orbit >= snow;

            // Determine body type
            BodyType type;
            float    mass;
            if (!pastSnow || (hotJupiter && orbit < snow && NextFloat(ref seed) < 0.15f))
            {
                (type, mass) = AssignRockyBody(orbit, hzIn, hzOut, snow, ref seed);
            }
            else
            {
                (type, mass) = AssignIcyBody(orbit, ref seed);
            }

            float   radius  = RadiusFromMass(mass, type);
            float   tempEq  = EquilTemp(lum, orbit);
            var     atm     = AssignAtmosphere(type, inHz, star.Class, age, ref seed);
            float   tempFin = ApplyGreenhouse(tempEq, atm);
            var     surface = AssignSurface(type, mass, age, ref seed);
            float   gravity = mass / (radius * radius);
            int     moons   = RollMoons(type, mass, ref seed);
            bool    rings   = RollRings(type, ref seed);

            bodies.Add(new StarSystemBody(
                $"{designation} {letter++}",
                type, atm, surface,
                orbit, mass, radius, tempFin, gravity,
                moons, rings, inHz));
        }

        // ── asteroid belt ──────────────────────────────────────────────────
        TryInsertAsteroidBelt(bodies, snow, designation, ref seed);

        bodies.Sort((a, b) => a.OrbitAu.CompareTo(b.OrbitAu));

        return [.. bodies];
    }

    // ── planet count ───────────────────────────────────────────────────────────

    private static int RollPlanetCount(Star star, ref ulong seed)
    {
        int baseCount = star.Class switch
        {
            SpectralClass.O          => 1,
            SpectralClass.B          => 2,
            SpectralClass.A          => 4,
            SpectralClass.F          => 5,
            SpectralClass.G          => 6,
            SpectralClass.K          => 5,
            SpectralClass.M          => 4,
            SpectralClass.WhiteDwarf => 1,
            _                        => 0,
        };
        if (star.IsSupergiant) baseCount = Math.Min(baseCount, 2);

        // ±2 scatter
        int scatter = (int)(Next(ref seed) % 5) - 2;
        return Math.Max(0, Math.Min(12, baseCount + scatter));
    }

    // ── body type + mass ──────────────────────────────────────────────────────

    /// Assigns a type and mass for a body inside the snow line.
    private static (BodyType Type, float Mass) AssignRockyBody(
        float orbit, float hzIn, float hzOut, float snow, ref ulong seed)
    {
        float f = NextFloat(ref seed);

        if (hzIn > 0f && orbit < hzIn * 0.45f)
        {
            // Very hot inner zone — lava world
            return (BodyType.Lava, 0.05f + f * 2.5f);
        }
        if (hzIn > 0f && orbit < hzIn)
        {
            // Hot zone — dry desert or airless
            return f < 0.70f
                ? (BodyType.Desert, 0.05f + NextFloat(ref seed) * 2.0f)
                : (BodyType.Airless, 0.01f + NextFloat(ref seed) * 1.0f);
        }
        if (hzIn > 0f && orbit <= hzOut)
        {
            // Habitable zone — varied outcomes
            return (f % 1.0f) switch
            {
                < 0.30f => (BodyType.Terrestrial, 0.3f  + NextFloat(ref seed) * 2.5f),
                < 0.55f => (BodyType.OceanWorld,  0.4f  + NextFloat(ref seed) * 3.0f),
                < 0.75f => (BodyType.Desert,      0.1f  + NextFloat(ref seed) * 2.0f),
                _       => (BodyType.Airless,      0.01f + NextFloat(ref seed) * 0.8f),
            };
        }
        // Warm zone between HZ outer and snow line
        return f < 0.65f
            ? (BodyType.Airless,     0.01f + NextFloat(ref seed) * 1.0f)
            : (BodyType.Terrestrial, 0.1f  + NextFloat(ref seed) * 1.5f);
    }

    /// Assigns a type and mass for a body beyond the snow line.
    private static (BodyType Type, float Mass) AssignIcyBody(float orbit, ref ulong seed)
    {
        float f    = NextFloat(ref seed);
        float mass = RollGiantMass(ref seed);

        if (mass >= 50f)  return (BodyType.GasGiant,  mass);
        if (mass >= 10f)  return (BodyType.IceGiant,  mass);
        return                   (BodyType.Frozen,    0.001f + NextFloat(ref seed) * 0.15f);
    }

    /// Mass for a potential giant beyond the snow line (heavy-tailed distribution).
    private static float RollGiantMass(ref ulong seed)
    {
        float f = NextFloat(ref seed);
        if (f < 0.30f) return 0.001f + NextFloat(ref seed) * 9.9f;   // sub-10 M♁ (Pluto-like)
        if (f < 0.55f) return 10f    + NextFloat(ref seed) * 40f;    // ice giant
        if (f < 0.80f) return 50f    + NextFloat(ref seed) * 450f;   // gas giant (Saturn–Jupiter)
        return                500f   + NextFloat(ref seed) * 2500f;  // super-Jupiter
    }

    // ── radius from mass ───────────────────────────────────────────────────────

    private static float RadiusFromMass(float mass, BodyType type) => type switch
    {
        BodyType.GasGiant  => Math.Clamp(9.0f  + (mass - 50f)  * 0.002f, 7.5f, 13.0f),
        BodyType.IceGiant  => Math.Clamp(3.5f  + (mass - 10f)  * 0.060f, 3.0f,  5.5f),
        _                  => MathF.Pow(Math.Max(mass, 0.001f), 0.27f),   // rocky
    };

    // ── temperature ────────────────────────────────────────────────────────────

    /// Equilibrium temperature assuming bond albedo ≈ 0.3.
    private static float EquilTemp(float lum, float orbitAu) =>
        lum > 0f
            ? 278.5f * MathF.Pow(lum, 0.25f) / MathF.Sqrt(Math.Max(orbitAu, 0.001f))
            : 50f;

    private static float ApplyGreenhouse(float T, AtmosphereType atm) => atm switch
    {
        AtmosphereType.Thick     => T * 2.4f,
        AtmosphereType.Toxic     => T * 1.7f,
        AtmosphereType.Breathable=> T * 1.15f,
        AtmosphereType.Thin      => T * 1.05f,
        _                        => T * 0.95f,
    };

    // ── atmosphere ────────────────────────────────────────────────────────────

    private static AtmosphereType AssignAtmosphere(
        BodyType type, bool inHz, SpectralClass cls, GalaxyAge age, ref ulong seed)
    {
        float f = NextFloat(ref seed);

        return type switch
        {
            BodyType.GasGiant  => AtmosphereType.Thick,
            BodyType.IceGiant  => AtmosphereType.Thick,
            BodyType.Lava      => AtmosphereType.None,
            BodyType.Airless   => AtmosphereType.None,
            BodyType.Frozen    => f < 0.75f ? AtmosphereType.None : AtmosphereType.Thin,
            BodyType.Desert    => f < 0.55f ? AtmosphereType.None
                                : f < 0.80f ? AtmosphereType.Thin
                                            : AtmosphereType.Toxic,
            BodyType.OceanWorld => f < 0.15f ? AtmosphereType.Thick : AtmosphereType.Breathable,
            BodyType.Terrestrial or BodyType.Desert when inHz =>
                RollHzAtmosphere(cls, age, ref seed),
            _ => f < 0.40f ? AtmosphereType.None
               : f < 0.70f ? AtmosphereType.Thin
                            : AtmosphereType.Toxic,
        };
    }

    private static AtmosphereType RollHzAtmosphere(SpectralClass cls, GalaxyAge age, ref ulong seed)
    {
        // Probability of a breathable atmosphere in the HZ:
        //   G/K stars + mature age are the sweet spot.
        float breathableP = cls switch
        {
            SpectralClass.G  => 0.32f,
            SpectralClass.K  => 0.28f,
            SpectralClass.F  => 0.18f,
            SpectralClass.M  => 0.10f,   // flares strip atmospheres
            SpectralClass.A  => 0.06f,   // too luminous, short lifetime
            _                => 0.04f,
        };
        float ageMul = age switch
        {
            GalaxyAge.Mature => 1.00f,
            GalaxyAge.Old    => 0.80f,
            GalaxyAge.Young  => 0.55f,   // not enough time for O₂ build-up
            _                => 1.00f,
        };

        float f = NextFloat(ref seed);
        float p = breathableP * ageMul;

        if (f < p)            return AtmosphereType.Breathable;
        if (f < p + 0.25f)    return AtmosphereType.Thin;
        if (f < p + 0.55f)    return AtmosphereType.Toxic;
        return                       AtmosphereType.None;
    }

    // ── surface ───────────────────────────────────────────────────────────────

    private static SurfaceType AssignSurface(BodyType type, float mass, GalaxyAge age, ref ulong seed)
    {
        float f = NextFloat(ref seed);
        return type switch
        {
            BodyType.GasGiant  => SurfaceType.Gaseous,
            BodyType.IceGiant  => SurfaceType.Gaseous,
            BodyType.Lava      => SurfaceType.Volcanic,
            BodyType.Frozen    => SurfaceType.Icy,
            BodyType.OceanWorld => SurfaceType.Oceanic,
            BodyType.Airless   => SurfaceType.Cratered,
            _ => (mass, age, f) switch
            {
                // Massive + geologically active → mountainous
                var (m, _, r) when m > 1.5f && r < 0.45f => SurfaceType.Mountainous,
                // Old star or small planet → ancient cratered surface
                var (_, a, r) when a == GalaxyAge.Old  && r < 0.50f => SurfaceType.Cratered,
                var (m, _, r) when m < 0.3f            && r < 0.60f => SurfaceType.Cratered,
                // Default mix: flat or mountainous
                var (_, _, r) when r < 0.50f => SurfaceType.Flat,
                _                            => SurfaceType.Mountainous,
            },
        };
    }

    // ── moons & rings ─────────────────────────────────────────────────────────

    private static int RollMoons(BodyType type, float mass, ref ulong seed)
    {
        float f = NextFloat(ref seed);
        return type switch
        {
            // Cap at 80: a super-Jupiter (3 000 M♁) gets at most Saturn-scale count.
            BodyType.GasGiant  => Math.Min(5 + (int)(f * (mass / 50f) * 15f), 80),
            BodyType.IceGiant  => 2  + (int)(f * 13f),
            BodyType.AsteroidBelt => 0,
            BodyType.Airless   => (int)(f * 2.5f),        // 0–2
            BodyType.Lava      => (int)(f * 2f),
            BodyType.Terrestrial or BodyType.OceanWorld or BodyType.Desert =>
                f < 0.40f ? 0 : f < 0.80f ? 1 : 2,
            _ => 0,
        };
    }

    private static bool RollRings(BodyType type, ref ulong seed)
    {
        float f = NextFloat(ref seed);
        return type switch
        {
            BodyType.GasGiant  => f < 0.60f,
            BodyType.IceGiant  => f < 0.40f,
            BodyType.Terrestrial or BodyType.Desert => f < 0.03f,
            _                  => false,
        };
    }

    // ── asteroid belt ─────────────────────────────────────────────────────────

    private static void TryInsertAsteroidBelt(
        List<StarSystemBody> bodies, float snow, string designation, ref ulong seed)
    {
        // Look for a gap that straddles or precedes the snow line where a belt makes sense.
        bool hasGasGiant = bodies.Any(b => b.Type is BodyType.GasGiant or BodyType.IceGiant);
        float chance = hasGasGiant ? 0.70f : 0.20f;
        if (NextFloat(ref seed) > chance) return;

        // Place belt just inside the snow line, or between last rocky and first giant.
        float beltOrbit = snow * (0.50f + NextFloat(ref seed) * 0.40f);
        if (beltOrbit <= 0.05f) return;

        // Don't insert if another body is already very close
        if (bodies.Any(b => MathF.Abs(b.OrbitAu - beltOrbit) < beltOrbit * 0.15f)) return;

        bodies.Add(new StarSystemBody(
            $"{designation} Belt", BodyType.AsteroidBelt,
            AtmosphereType.None, SurfaceType.Cratered,
            beltOrbit, 0f, 0f, 0f, 0f, 0, false, false));
    }

    // ── PRNG helpers ──────────────────────────────────────────────────────────

    /// Splitmix64 step — advances state and returns a well-distributed 64-bit value.
    private static ulong Next(ref ulong s)
    {
        s += 0x9e3779b97f4a7c15UL;
        ulong x = s;
        x = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
        x = (x ^ (x >> 27)) * 0x94d049bb133111ebUL;
        return x ^ (x >> 31);
    }

    private static float NextFloat(ref ulong s) =>
        (Next(ref s) >> 11) / (float)(1UL << 53);

    /// Deterministic seed derived from a star's world-space position.
    private static ulong StarSeed(Star star)
    {
        ulong bx = (ulong)(uint)BitConverter.SingleToInt32Bits(star.X);
        ulong by = (ulong)(uint)BitConverter.SingleToInt32Bits(star.Y);
        ulong bz = (ulong)(uint)BitConverter.SingleToInt32Bits(star.Z);
        ulong h  = bx * 0x9e3779b97f4a7c15UL
                 ^ by * 0x6c62272e07bb0142UL
                 ^ bz * 0x517cc1b727220a95UL;
        h = (h ^ (h >> 30)) * 0xbf58476d1ce4e5b9UL;
        h = (h ^ (h >> 27)) * 0x94d049bb133111ebUL;
        return h ^ (h >> 31);
    }
}
