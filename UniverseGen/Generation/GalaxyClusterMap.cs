using System.Numerics;
using UniverseGen.Models;

namespace UniverseGen.Generation;

/// <summary>
/// Procedurally places nebulae, open clusters and globular clusters for one galaxy.
///
/// Counts are not free parameters — they are derived from the galaxy's physical
/// properties (type, age, arm count, bulge size) scaled by
/// <see cref="GalaxyParams.FeatureDensity"/>.  This keeps the galaxy internally
/// consistent: an old elliptical gets many globulars and no nebulae; a young
/// spiral with many arms gets lots of star-forming regions.
/// </summary>
public sealed class GalaxyClusterMap
{
    public IReadOnlyList<GalaxyCluster> Clusters { get; }

    // Influence radii in galaxy normalised units (1 unit = 50,000 ly)
    private const float NebRadiusNorm  = 0.010f;   // ~500 ly
    private const float OpenRadiusNorm = 0.005f;   // ~250 ly
    private const float GlobRadiusNorm = 0.007f;   // ~350 ly

    public GalaxyClusterMap(Galaxy galaxy)
    {
        var parms = galaxy.Params;
        var (nebCount, openCount, globCount) = ComputeCounts(parms);
        var all = new List<GalaxyCluster>(nebCount + openCount + globCount);

        all.AddRange(PlaceNebulae     (parms, galaxy, nebCount));
        all.AddRange(PlaceOpenClusters(parms, galaxy, openCount));
        all.AddRange(PlaceGlobulars   (parms,         globCount));

        Clusters = all;
    }

    /// <summary>
    /// Returns the physically-motivated counts for this galaxy's params.
    /// Multiply each by <c>FeatureDensity/100</c> — the caller doesn't need
    /// to do this; it is already applied inside.
    /// </summary>
    public static (int Nebulae, int OpenClusters, int Globulars) ComputeCounts(GalaxyParams p)
    {
        float density = p.FeatureDensity / 100f;

        // Age multipliers: young galaxies have active star formation; old ones do not.
        float[] ageGlob = { 0.5f, 1.0f, 1.7f };   // globulars accumulate with time
        float[] ageNeb  = { 2.2f, 1.0f, 0.15f };  // nebulae need active star formation
        float[] ageOpen = { 2.5f, 1.0f, 0.10f };  // open clusters also need young stars
        int age = (int)p.Age;

        // Spiral arm scale: more arms → more star-forming sites along them
        float armScale = p.Type == GalaxyType.Spiral ? p.Arms / 4f : 1f;

        // Bulge scale: more massive bulge → more globulars (correlated with total mass)
        float bulgeScale = 0.7f + p.BulgeFactor * 0.9f;

        // Base counts per galaxy type at age=Mature, density=100%
        int globBase = p.Type switch
        {
            GalaxyType.Elliptical => 450,   // giant ellipticals host thousands — we show the brightest
            GalaxyType.Lenticular => 180,
            GalaxyType.Irregular  =>  30,
            _                     => 120,   // Spiral (Milky Way ~150–200)
        };
        int nebBase = p.Type switch
        {
            GalaxyType.Elliptical =>   0,   // essentially no star formation
            GalaxyType.Lenticular =>   3,   // very little
            GalaxyType.Irregular  =>  14,
            _                     =>  22,   // Spiral
        };
        int openBase = p.Type switch
        {
            GalaxyType.Elliptical =>   0,
            GalaxyType.Lenticular =>   4,
            GalaxyType.Irregular  =>  12,
            _                     =>  28,   // Spiral
        };

        return (
            Math.Max(0, (int)(nebBase  * ageNeb[age]  * armScale   * density)),
            Math.Max(0, (int)(openBase * ageOpen[age] * armScale   * density)),
            Math.Max(0, (int)(globBase * ageGlob[age] * bulgeScale * density))
        );
    }

    /// Returns the nearest cluster whose influence sphere contains <paramref name="pos"/>,
    /// or null if the position is outside every cluster.
    public GalaxyCluster? FindNearest(Vector3 pos)
    {
        GalaxyCluster? best     = null;
        float          bestDist = float.MaxValue;
        foreach (var c in Clusters)
        {
            float d = Vector3.Distance(pos, c.Position);
            if (d < c.Radius && d < bestDist)
            {
                bestDist = d;
                best     = c;
            }
        }
        return best;
    }

    // ── placement ─────────────────────────────────────────────────────────────

    private static List<GalaxyCluster> PlaceNebulae(GalaxyParams parms, Galaxy galaxy, int count)
    {
        var result = new List<GalaxyCluster>(count);
        if (count <= 0) return result;

        ulong salt = 0xABCD1234ABCD1234UL;
        int   idx  = 0;

        for (int attempt = 0; attempt < 20000 && result.Count < count; attempt++)
        {
            ulong h     = Mix((ulong)attempt ^ parms.Seed ^ salt);
            float r     = 0.08f + HF(h, 0) * 0.75f;     // [0.08, 0.83] from centre
            float angle = HF(h, 1) * MathF.Tau;
            float x     = r * MathF.Cos(angle);
            float z     = r * MathF.Sin(angle);
            float y     = (HF(h, 2) - 0.5f) * 0.002f;   // very thin disk

            if (galaxy.SampleDensity(x, z) < 0.55f) continue;
            if (TooClose(result, new Vector3(x, y, z), NebRadiusNorm * 2.5f)) continue;

            result.Add(new GalaxyCluster(
                ClusterType.Nebula,
                new Vector3(x, y, z),
                NebRadiusNorm,
                RegionNameGenerator.ClusterScientificName(idx, ClusterType.Nebula,   parms.Seed),
                RegionNameGenerator.ClusterPoeticName    (idx, ClusterType.Nebula,   parms.Seed)));
            idx++;
        }
        return result;
    }

    private static List<GalaxyCluster> PlaceOpenClusters(GalaxyParams parms, Galaxy galaxy, int count)
    {
        var result = new List<GalaxyCluster>(count);
        if (count <= 0) return result;

        ulong salt = 0x5678ABCD5678ABCDUL;
        int   idx  = 0;

        for (int attempt = 0; attempt < 20000 && result.Count < count; attempt++)
        {
            ulong h     = Mix((ulong)attempt ^ parms.Seed ^ salt);
            float r     = 0.06f + HF(h, 0) * 0.78f;
            float angle = HF(h, 1) * MathF.Tau;
            float x     = r * MathF.Cos(angle);
            float z     = r * MathF.Sin(angle);
            float y     = (HF(h, 2) - 0.5f) * 0.002f;

            if (galaxy.SampleDensity(x, z) < 0.32f) continue;
            if (TooClose(result, new Vector3(x, y, z), OpenRadiusNorm * 2.5f)) continue;

            result.Add(new GalaxyCluster(
                ClusterType.OpenCluster,
                new Vector3(x, y, z),
                OpenRadiusNorm,
                RegionNameGenerator.ClusterScientificName(idx, ClusterType.OpenCluster, parms.Seed),
                RegionNameGenerator.ClusterPoeticName    (idx, ClusterType.OpenCluster, parms.Seed)));
            idx++;
        }
        return result;
    }

    private static List<GalaxyCluster> PlaceGlobulars(GalaxyParams parms, int count)
    {
        var result = new List<GalaxyCluster>(count);
        if (count <= 0) return result;

        ulong salt  = 0x9876FEDC9876FEDCUL;
        float diskH = 0.04f + (int)parms.Age * 0.02f + parms.BulgeFactor * 0.04f;
        int   idx   = 0;

        for (int attempt = 0; attempt < 200000 && result.Count < count; attempt++)
        {
            ulong h = Mix((ulong)attempt ^ parms.Seed ^ salt);

            // Uniform random point on sphere
            float phi      = HF(h, 0) * MathF.Tau;
            float cosTheta = HF(h, 1) * 2f - 1f;
            float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
            float r        = 0.15f + HF(h, 2) * 0.60f;   // [0.15, 0.75] normalised

            float x = r * sinTheta * MathF.Cos(phi);
            float y = r * cosTheta;
            float z = r * sinTheta * MathF.Sin(phi);

            if (MathF.Abs(y) < diskH * 2.5f) continue;   // must be above/below disk
            if (TooClose(result, new Vector3(x, y, z), GlobRadiusNorm * 2.5f)) continue;

            result.Add(new GalaxyCluster(
                ClusterType.GlobularCluster,
                new Vector3(x, y, z),
                GlobRadiusNorm,
                RegionNameGenerator.ClusterScientificName(idx, ClusterType.GlobularCluster, parms.Seed),
                RegionNameGenerator.ClusterPoeticName    (idx, ClusterType.GlobularCluster, parms.Seed)));
            idx++;
        }
        return result;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static bool TooClose(List<GalaxyCluster> existing, Vector3 pos, float minDist)
    {
        foreach (var c in existing)
            if (Vector3.Distance(c.Position, pos) < minDist) return true;
        return false;
    }

    private static float HF(ulong h, int slot) =>
        ((h >> (slot * 16)) & 0xFFFF) / (float)0xFFFF;

    private static ulong Mix(ulong h)
    {
        h = (h ^ (h >> 30)) * 0xbf58476d1ce4e5b9UL;
        h = (h ^ (h >> 27)) * 0x94d049bb133111ebUL;
        return h ^ (h >> 31);
    }
}
