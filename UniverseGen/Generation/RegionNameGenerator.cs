using UniverseGen.Models;

namespace UniverseGen.Generation;

/// <summary>
/// Procedural name generator for galaxy regions.
///
/// Each named region spans a cell of <see cref="RegionCellChunks"/> chunks
/// on each axis.  All chunks inside the same cell share the same name,
/// giving coherent nebula / void / cluster identities as you fly through them.
///
/// Two name styles are produced:
/// • Scientific  e.g. "NGC 2244",  "Sh2-106",  "Pal 5"
/// • Poetic      e.g. "The Crimson Forge",  "Kepler's Rest",  "The Stellar Hoyle"
/// </summary>
public static class RegionNameGenerator
{
    /// Side length (in chunks) of one named-region cell.
    /// 10 chunks × 0.0002 normalised = 10 ly/side → ~100 ly cells.
    public const int RegionCellChunks = 10;

    // ── scientific catalogue prefixes, keyed by RegionType ───────────────────

    private static readonly string[][] ScientificPrefixes =
    [
        ["OB", "Tr", "Cr", "Stock", "Berk", "Cl"],        // DiskArm  — OB assoc, open clusters
        ["HD", "BD", "SAO", "PPM", "TYC"],                 // DiskInter — field star catalogues
        ["NGC", "M", "IC", "ESO", "Baade"],                // Bulge
        ["Pal", "AM", "E", "Ko", "Erid"],                  // Halo     — globular/halo catalogues
        ["NGC", "IC", "Sh2", "RCW", "Gum", "W", "LH"],    // Nursery  — emission/reflection nebulae
        ["KBC", "LV", "Bo", "CV", "WF"],                   // Void
    ];

    private static readonly string[][] ScientificFormats =
    [
        ["{P} {N}"],                  // DiskArm
        ["{P} {N}", "{P}-{N}"],       // DiskInter
        ["{P} {N}", "{P}{N}"],        // Bulge
        ["{P} {N}", "{P}-{N}"],       // Halo
        ["{P} {N}", "{P}-{N}"],       // Nursery
        ["{P}-{N}", "{P} {N}"],       // Void
    ];

    // ── poetic word banks ─────────────────────────────────────────────────────

    private static readonly string[][] PoeticAdjectives =
    [
        // DiskArm — active, flowing, bright
        ["Swift", "Blazing", "Bright", "Dense", "Gleaming", "Streaming",
         "Vivid", "Living", "Turbulent", "Rushing", "Fierce", "Bold"],
        // DiskInter — quiet, open, calm
        ["Quiet", "Dim", "Still", "Calm", "Sparse", "Gentle",
         "Faint", "Muted", "Open", "Broad", "Soft", "Pale"],
        // Bulge — ancient, dense, grand
        ["Burning", "Ancient", "Dense", "Radiant", "Eternal", "Grand",
         "Mighty", "Deep", "Blazing", "Old", "Vast", "Hallowed"],
        // Halo — ancient, lonely, ghostly
        ["Ancient", "Eternal", "Wandering", "Forgotten", "Primordial", "Ghostly",
         "Ageless", "Faded", "Lonely", "Lost", "Timeless", "Dim"],
        // Nursery — hot, young, colourful
        ["Crimson", "Amber", "Radiant", "Luminous", "Searing", "Brilliant",
         "Golden", "Vivid", "Glowing", "Fervent", "Fierce", "Young"],
        // Void — dark, empty, silent
        ["Silent", "Hollow", "Dark", "Distant", "Empty", "Desolate",
         "Barren", "Cold", "Fading", "Pale", "Vast", "Deep"],
    ];

    private static readonly string[][] PoeticNouns =
    [
        // DiskArm
        ["Current", "Stream", "River", "Crossing", "Passage", "Channel",
         "Highway", "Road", "Bridge", "Rush", "Flow", "Way"],
        // DiskInter
        ["Field", "Plain", "Meadow", "Stretch", "Expanse", "Country",
         "Reach", "Clearing", "Steppe", "Passage", "Drift", "Prairie"],
        // Bulge
        ["Heart", "Core", "Hearth", "Throne", "Citadel", "Nexus",
         "Centre", "Forge", "Fire", "Crown", "Keep", "Hub"],
        // Halo
        ["Wanderer", "Relic", "Remnant", "Ghost", "Echo", "Exile",
         "Memory", "Shade", "Pilgrim", "Outcast", "Elder", "Spectre"],
        // Nursery
        ["Forge", "Cradle", "Crucible", "Furnace", "Nursery", "Font",
         "Ember", "Hearth", "Kiln", "Cauldron", "Birthplace", "Spark"],
        // Void
        ["Drift", "Expanse", "Desert", "Abyss", "Reaches", "Silence",
         "Waste", "Gulf", "Deep", "Hollow", "Emptiness", "Chasm"],
    ];

    // Surnames used in "The Stellar Hoyle" / "Kepler's Rest" style names
    private static readonly string[] Surnames =
    [
        "Brahe", "Herschel", "Kepler", "Halley", "Hubble", "Sagan",
        "Curie", "Newton", "Cassini", "Messier", "Flamsteed", "Bessel",
        "Hertzsprung", "Russell", "Eddington", "Jeans", "Schwarzschild",
        "Chandrasekhar", "Zwicky", "Burnell", "Rubin", "Hoyle", "Oort",
        "Shapley", "Kapteyn", "Copernicus", "Lowell", "Tombaugh", "Leavitt",
        "Cannon", "Payne", "Milgrom", "Penrose", "Hawking", "Thorne",
    ];

    // Region type display names (used in "The Wellington Nebula" style)
    private static readonly string[] RegionTypeWords =
    [
        "Association", "Cluster", "Bulge", "Wanderers", "Nebula", "Void",
    ];

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the named-region cell coordinate for a chunk coord.
    /// All chunks in the same cell share an identical name.
    /// </summary>
    public static (long RX, long RY, long RZ) RegionCell(long cx, long cy, long cz)
    {
        static long Snap(long v) => v >= 0 ? v / RegionCellChunks
                                            : (v - RegionCellChunks + 1) / RegionCellChunks;
        return (Snap(cx), Snap(cy), Snap(cz));
    }

    /// <summary>
    /// Generates a scientific catalogue designation for the region containing
    /// <paramref name="cx"/> (e.g. "NGC 2244", "Sh2-106", "Pal 5").
    /// </summary>
    public static string ScientificName(
        long cx, long cy, long cz, RegionType region, ulong galaxySeed)
    {
        var (rx, ry, rz) = RegionCell(cx, cy, cz);
        ulong h = Mix(RegionHash(rx, ry, rz, galaxySeed), 0x1A2B3C4D5E6F7080UL);

        var prefixes = ScientificPrefixes[(int)region];
        var formats  = ScientificFormats [(int)region];

        string prefix = Pick(prefixes, h);
        string fmt    = Pick(formats,  h >> 8);
        int    number = (int)((h >> 16) % 9000) + 1000;   // 1000–9999

        return fmt.Replace("{P}", prefix).Replace("{N}", number.ToString());
    }

    /// <summary>
    /// Generates a poetic human name for the region containing
    /// <paramref name="cx"/> (e.g. "The Crimson Forge", "Kepler's Rest").
    /// </summary>
    public static string PoeticName(
        long cx, long cy, long cz, RegionType region, ulong galaxySeed)
    {
        var (rx, ry, rz) = RegionCell(cx, cy, cz);
        ulong h = Mix(RegionHash(rx, ry, rz, galaxySeed), 0xFEDCBA9876543210UL);

        int ri  = (int)region;
        int pat = (int)((h >> 4) % 5);   // 5 name patterns

        return pat switch
        {
            0 => $"The {Pick(PoeticAdjectives[ri], h >> 8)} {Pick(PoeticNouns[ri], h >> 16)}",
            1 => $"{Pick(PoeticAdjectives[ri], h >> 8)} {Pick(PoeticNouns[ri], h >> 16)}",
            2 => $"{Pick(Surnames, h >> 8)}'s {Pick(PoeticNouns[ri], h >> 16)}",
            3 => $"The {Pick(Surnames, h >> 8)} {RegionTypeWords[ri]}",
            _ => $"The {Pick(PoeticAdjectives[ri], h >> 8)} {Pick(Surnames, h >> 24)}",
        };
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ulong RegionHash(long rx, long ry, long rz, ulong seed) =>
        Mix((ulong)(uint)rx * 0x9e3779b97f4a7c15UL
          ^ (ulong)(uint)ry * 0x6c62272e07bb0142UL
          ^ (ulong)(uint)rz * 0x517cc1b727220a95UL
          ^ seed);

    private static ulong Mix(ulong h)
    {
        h = (h ^ (h >> 30)) * 0xbf58476d1ce4e5b9UL;
        h = (h ^ (h >> 27)) * 0x94d049bb133111ebUL;
        return h ^ (h >> 31);
    }

    private static ulong Mix(ulong h, ulong salt) => Mix(h ^ salt);

    private static T Pick<T>(T[] arr, ulong h) => arr[(int)(h % (uint)arr.Length)];

    /// Scientific name for a specifically placed cluster/nebula (index-based, not cell-based).
    public static string ClusterScientificName(int index, ClusterType type, ulong galaxySeed)
    {
        var region  = ClusterRegionType(type);
        ulong h     = Mix(Mix((ulong)index * 0x9e3779b97f4a7c15UL ^ galaxySeed) ^ 0x1234ABCD5678EF90UL);
        var prefixes = ScientificPrefixes[(int)region];
        var formats  = ScientificFormats [(int)region];
        string prefix = Pick(prefixes, h);
        string fmt    = Pick(formats,  h >> 8);
        int    number = (int)((h >> 16) % 9000) + 1000;
        return fmt.Replace("{P}", prefix).Replace("{N}", number.ToString());
    }

    /// Poetic name for a specifically placed cluster/nebula (index-based, not cell-based).
    public static string ClusterPoeticName(int index, ClusterType type, ulong galaxySeed)
    {
        var region = ClusterRegionType(type);
        ulong h    = Mix(Mix((ulong)index * 0x9e3779b97f4a7c15UL ^ galaxySeed) ^ 0xFEDCBA0987654321UL);
        int ri     = (int)region;
        int pat    = (int)((h >> 4) % 5);
        return pat switch
        {
            0 => $"The {Pick(PoeticAdjectives[ri], h >> 8)} {Pick(PoeticNouns[ri], h >> 16)}",
            1 => $"{Pick(PoeticAdjectives[ri], h >> 8)} {Pick(PoeticNouns[ri], h >> 16)}",
            2 => $"{Pick(Surnames, h >> 8)}'s {Pick(PoeticNouns[ri], h >> 16)}",
            3 => $"The {Pick(Surnames, h >> 8)} {RegionTypeWords[ri]}",
            _ => $"The {Pick(PoeticAdjectives[ri], h >> 8)} {Pick(Surnames, h >> 24)}",
        };
    }

    private static RegionType ClusterRegionType(ClusterType t) => t switch
    {
        ClusterType.Nebula          => RegionType.Nursery,
        ClusterType.OpenCluster     => RegionType.DiskArm,
        ClusterType.GlobularCluster => RegionType.Halo,
        _                           => RegionType.DiskArm,
    };
}
