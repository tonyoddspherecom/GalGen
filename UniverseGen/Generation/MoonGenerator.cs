using UniverseGen.Models;

namespace UniverseGen.Generation;

/// <summary>
/// Generates a deterministic set of natural satellites for a given planetary body.
///
/// The moon count is already stored in <see cref="StarSystemBody.MoonCount"/> (rolled
/// by <see cref="StarSystemGenerator"/>); this class fills in orbital radius, physical
/// size, and composition using the same splitmix64 PRNG seeded from the planet's
/// orbital elements so the same planet always produces the same moons.
///
/// Distribution:
///   Gas giants   — first four moons are large/Galilean-scale (1 200–2 600 km).
///   Ice giants   — first two moons are medium (400–1 200 km).
///   All others   — small moons (20–300 km).
/// </summary>
public sealed class MoonGenerator
{
    /// <summary>
    /// Returns exactly <c>planet.MoonCount</c> moons, ordered innermost first.
    /// Returns an empty array for asteroid belts or bodies with zero moons.
    /// </summary>
    public Moon[] Generate(StarSystemBody planet, string planetDesignation)
    {
        if (planet.Type == BodyType.AsteroidBelt || planet.MoonCount == 0)
            return [];

        int    count = planet.MoonCount;
        ulong  seed  = PlanetSeed(planet);
        var    moons = new Moon[count];

        // Innermost moon starts at a safe distance from the Roche limit (in parent radii).
        float orbitR = planet.Type switch
        {
            BodyType.GasGiant => 5.0f,
            BodyType.IceGiant => 6.0f,
            _                 => 3.5f,
        };

        for (int i = 0; i < count; i++)
        {
            // Each successive moon is 1.4–2.3× farther out.
            orbitR *= 1.40f + NextFloat(ref seed) * 0.90f;

            bool isGalilean = planet.Type == BodyType.GasGiant && count >= 4 && i < 4;
            bool isMedium   = planet.Type == BodyType.IceGiant && i < 2;

            float    radKm;
            MoonType mtype;

            if (isGalilean)
            {
                radKm = 1_200f + NextFloat(ref seed) * 1_400f;  // 1 200 – 2 600 km
                mtype = NextFloat(ref seed) < 0.50f ? MoonType.Large : MoonType.Rocky;
            }
            else if (isMedium)
            {
                radKm = 400f + NextFloat(ref seed) * 800f;      // 400 – 1 200 km
                mtype = NextFloat(ref seed) < 0.50f ? MoonType.Icy : MoonType.Rocky;
            }
            else
            {
                radKm = 20f + NextFloat(ref seed) * 280f;       // 20 – 300 km
                bool beyondSnow = planet.OrbitAu > 2f;
                mtype = beyondSnow
                    ? (NextFloat(ref seed) < 0.60f ? MoonType.Icy   : MoonType.Small)
                    : (NextFloat(ref seed) < 0.50f ? MoonType.Rocky : MoonType.Small);
            }

            moons[i] = new Moon($"{planetDesignation}-{RomanNumeral(i + 1)}", orbitR, radKm, mtype);
        }

        return moons;
    }

    // ── seed derivation ───────────────────────────────────────────────────────

    /// Deterministic seed from the planet's orbital and physical parameters.
    private static ulong PlanetSeed(StarSystemBody planet)
    {
        ulong a = (ulong)(uint)BitConverter.SingleToInt32Bits(planet.OrbitAu);
        ulong b = (ulong)(uint)BitConverter.SingleToInt32Bits(planet.MassEarth);
        ulong h = a * 0x9e3779b97f4a7c15UL ^ b * 0x6c62272e07bb0142UL;
        h = (h ^ (h >> 30)) * 0xbf58476d1ce4e5b9UL;
        h = (h ^ (h >> 27)) * 0x94d049bb133111ebUL;
        return h ^ (h >> 31);
    }

    // ── PRNG ─────────────────────────────────────────────────────────────────

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

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string RomanNumeral(int n) => n switch
    {
        1  => "I",   2 => "II",   3 => "III",  4 => "IV",  5 => "V",
        6  => "VI",  7 => "VII",  8 => "VIII", 9 => "IX", 10 => "X",
        _  => n.ToString(),
    };
}
