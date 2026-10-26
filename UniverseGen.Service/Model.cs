using System.Globalization;
using System.Numerics;
using UniverseGen.Models;

namespace UniverseGen.Service;

// ── stars ─────────────────────────────────────────────────────────────────────

/// <summary>
/// A star's permanent address: its chunk and its place in that chunk. The same galaxy (the same
/// <see cref="GalaxyInfo.Fingerprint"/>) always has the same star at the same id, so ids can be saved,
/// sent and shared. Written "x:y:z:index".
/// </summary>
public readonly record struct StarId(long X, long Y, long Z, int Index)
{
    public StarId(ChunkCoord chunk, int index) : this(chunk.X, chunk.Y, chunk.Z, index) { }

    public ChunkCoord Chunk => new(X, Y, Z);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X}:{Y}:{Z}:{Index}");

    public static bool TryParse(string? text, out StarId id)
    {
        id = default;
        string[] parts = (text ?? "").Split(':');
        if (parts.Length != 4) return false;
        const NumberStyles style = NumberStyles.AllowLeadingSign;
        if (!long.TryParse(parts[0], style, CultureInfo.InvariantCulture, out long x)
            || !long.TryParse(parts[1], style, CultureInfo.InvariantCulture, out long y)
            || !long.TryParse(parts[2], style, CultureInfo.InvariantCulture, out long z)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            return false;
        id = new StarId(x, y, z, index);
        return true;
    }

    public static StarId Parse(string text) =>
        TryParse(text, out StarId id) ? id : throw new FormatException($"'{text}' isn't a star id (x:y:z:index).");
}

/// <summary>A star, its id, its catalogue designation, and the stellar environment it's in.</summary>
public sealed record StarRef(StarId Id, Star Star, string Designation, RegionType Region);

/// <summary>
/// Which stars a query or pick wants. Everything is allowed by default; each setting narrows it.
/// Planet conditions need each candidate's system generated, which costs far more than a star, so a
/// server counts them against its limits.
/// </summary>
public sealed record StarFilter
{
    public const ushort AllClasses = (1 << 10) - 1;
    public const byte AllRegions = (1 << 6) - 1;

    public static StarFilter Any { get; } = new();

    /// Spectral classes allowed, one bit each (bit n is <see cref="SpectralClass"/> n). See <see cref="ClassMask"/>.
    public ushort Classes { get; init; } = AllClasses;
    /// Flags every star must have, and flags none may have.
    public StarFlags RequiredFlags { get; init; }
    public StarFlags ExcludedFlags { get; init; }
    /// The dimmest allowed, 0–255.
    public byte MinLuminosity { get; init; }
    /// Stellar environments allowed, one bit each (bit n is <see cref="RegionType"/> n). See <see cref="RegionMask"/>.
    public byte Regions { get; init; } = AllRegions;
    /// At least this many planets (asteroid belts don't count).
    public int MinPlanets { get; init; }
    /// Only stars with a habitable world.
    public bool HabitableOnly { get; init; }

    public static ushort ClassMask(params SpectralClass[] classes) => (ushort)classes.Aggregate(0, (mask, c) => mask | 1 << (int)c);
    public static byte RegionMask(params RegionType[] regions) => (byte)regions.Aggregate(0, (mask, r) => mask | 1 << (int)r);

    public bool NeedsSystem => MinPlanets > 0 || HabitableOnly;

    /// Whether a star passes everything but the planet conditions.
    public bool Accepts(Star star, RegionType region) =>
        (Classes & 1 << (int)star.Class) != 0
        && (star.Flags & RequiredFlags) == RequiredFlags
        && (star.Flags & ExcludedFlags) == 0
        && star.Luminosity >= MinLuminosity
        && (Regions & 1 << (int)region) != 0;

    /// Whether a star's system passes the planet conditions.
    public bool Accepts(StarSystem system) =>
        system.PlanetCount >= MinPlanets && (!HabitableOnly || system.HabitableCount > 0);
}

public enum QueryShape : byte { Sphere, Box }

public enum StarOrder : byte
{
    /// Nearest the query's centre first.
    Nearest,
    /// Brightest first.
    Brightest,
    /// In chunk order: the cheapest, for when every star in the space is wanted.
    None,
}

/// <summary>
/// The stars in a sphere or a box of space, in galaxy normalised coordinates (see
/// <see cref="GalaxyInfo.ToNormalised"/> to work in light years).
/// </summary>
public sealed record StarQuery
{
    public QueryShape Shape { get; init; }
    /// A sphere's centre and radius.
    public Vector3 Center { get; init; }
    public float Radius { get; init; }
    /// A box's corners.
    public Vector3 Min { get; init; }
    public Vector3 Max { get; init; }
    public StarFilter Filter { get; init; } = StarFilter.Any;
    /// The most stars to return; a result that hit it says so (<see cref="StarQueryResult.Truncated"/>).
    public int Limit { get; init; } = 1000;
    public StarOrder Order { get; init; } = StarOrder.Nearest;

    public static StarQuery Sphere(Vector3 center, float radius, StarFilter? filter = null, int limit = 1000, StarOrder order = StarOrder.Nearest) =>
        new() { Shape = QueryShape.Sphere, Center = center, Radius = radius, Filter = filter ?? StarFilter.Any, Limit = limit, Order = order };

    public static StarQuery Box(Vector3 min, Vector3 max, StarFilter? filter = null, int limit = 1000, StarOrder order = StarOrder.Nearest) =>
        new() { Shape = QueryShape.Box, Min = Vector3.Min(min, max), Max = Vector3.Max(min, max), Center = (min + max) * 0.5f,
                Filter = filter ?? StarFilter.Any, Limit = limit, Order = order };

    /// The space it covers, as a box.
    public (Vector3 Min, Vector3 Max) Bounds => Shape == QueryShape.Sphere
        ? (Center - new Vector3(Radius), Center + new Vector3(Radius))
        : (Min, Max);

    public bool Contains(Vector3 p) => Shape == QueryShape.Sphere
        ? Vector3.DistanceSquared(p, Center) <= Radius * Radius
        : p.X >= Min.X && p.Y >= Min.Y && p.Z >= Min.Z && p.X <= Max.X && p.Y <= Max.Y && p.Z <= Max.Z;
}

public sealed record StarQueryResult(IReadOnlyList<StarRef> Stars, bool Truncated, int ChunksSearched);

/// <summary>
/// A star chosen at random, but the same one every time for the same seed: a new player's home, a
/// mission's target, an event's location. Places are drawn in proportion to how dense the galaxy is
/// there, so picks land where stars are, and the nearest star there that passes the filter is taken.
/// </summary>
public sealed record StarPick
{
    public ulong Seed { get; init; }
    public StarFilter Filter { get; init; } = StarFilter.Any;
    /// How far from the galactic centre, in the plane, in normalised units (the galaxy's edge is 1).
    public float MinGalacticRadius { get; init; }
    public float MaxGalacticRadius { get; init; } = 1f;
    /// Places tried before giving up (and returning nothing).
    public int MaxAttempts { get; init; } = 64;
}

// ── maps ──────────────────────────────────────────────────────────────────────

public enum MapView : byte
{
    /// The whole galaxy from above.
    FaceOn,
    /// Part of the galaxy from above, with more detail: see <see cref="MapRequest.Region"/>.
    Region,
    /// The whole galaxy edge-on: width is the radial axis, height the vertical.
    EdgeOn,
}

public enum MapFormat : byte
{
    /// One byte a pixel, 0–255. A quarter the size of Float32, and plenty to draw with.
    Byte,
    /// Two bytes a pixel, 0–65535.
    UInt16,
    /// Exactly what the generator made.
    Float32,
}

/// <summary>A density map of the galaxy: values 0–1, row-major, as an image.</summary>
public sealed record MapRequest
{
    public MapView View { get; init; }
    public int Width { get; init; } = 512;
    public int Height { get; init; } = 512;
    /// For <see cref="MapView.Region"/>: the part of the galaxy, in normalised [0,1] map space.
    public RegionF Region { get; init; } = RegionF.Full;
    public MapFormat Format { get; init; }

    public static MapRequest FaceOn(int size = 512, MapFormat format = MapFormat.Byte) =>
        new() { View = MapView.FaceOn, Width = size, Height = size, Format = format };

    public static MapRequest EdgeOn(int width = 512, int height = 128, MapFormat format = MapFormat.Byte) =>
        new() { View = MapView.EdgeOn, Width = width, Height = height, Format = format };

    public static MapRequest Zoomed(RegionF region, int size = 512, MapFormat format = MapFormat.Byte) =>
        new() { View = MapView.Region, Width = size, Height = size, Region = region, Format = format };
}

/// <summary>A generated density map, stored in its <see cref="Format"/> (little-endian).</summary>
public sealed class DensityImage
{
    public int Width { get; }
    public int Height { get; }
    public MapFormat Format { get; }
    /// The pixels, row-major, in Format.
    public byte[] Data { get; }

    public DensityImage(int width, int height, MapFormat format, byte[] data)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "A map needs at least one pixel.");
        if (data.Length != (long)width * height * BytesPerPixel(format))
            throw new ArgumentException($"{data.Length} bytes isn't a {width}×{height} {format} map.", nameof(data));
        (Width, Height, Format, Data) = (width, height, format, data);
    }

    public static int BytesPerPixel(MapFormat format) => format switch
    {
        MapFormat.Byte => 1,
        MapFormat.UInt16 => 2,
        _ => 4,
    };

    /// Packs generated values (0–1) into a format.
    public static DensityImage From(ReadOnlySpan<float> values, int width, int height, MapFormat format)
    {
        var data = new byte[values.Length * BytesPerPixel(format)];
        for (int i = 0; i < values.Length; i++)
        {
            float v = Math.Clamp(values[i], 0f, 1f);
            switch (format)
            {
                case MapFormat.Byte: data[i] = (byte)MathF.Round(v * 255f); break;
                case MapFormat.UInt16: BitConverter.TryWriteBytes(data.AsSpan(i * 2), (ushort)MathF.Round(v * 65535f)); break;
                default: BitConverter.TryWriteBytes(data.AsSpan(i * 4), values[i]); break;
            }
        }
        return new DensityImage(width, height, format, data);
    }

    /// The density at a pixel, 0–1.
    public float this[int x, int y]
    {
        get
        {
            int i = y * Width + x;
            return Format switch
            {
                MapFormat.Byte => Data[i] / 255f,
                MapFormat.UInt16 => BitConverter.ToUInt16(Data, i * 2) / 65535f,
                _ => BitConverter.ToSingle(Data, i * 4),
            };
        }
    }

    public float[] ToFloats()
    {
        var values = new float[Width * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                values[y * Width + x] = this[x, y];
        return values;
    }

    /// The map as a greyscale PNG (8-bit for Byte, 16-bit otherwise), for the web or any image viewer.
    public byte[] ToPng() => Png.Greyscale(this);
}

// ── places ────────────────────────────────────────────────────────────────────

/// <summary>What's at a point in the galaxy: its environment, the names of its region, and any cluster or nebula.</summary>
/// <param name="Position">The point asked about, in normalised galaxy space.</param>
/// <param name="Chunk">The chunk it's in.</param>
/// <param name="Region">Its stellar environment, as its chunk's stars have it.</param>
/// <param name="Density">Face-on density there, 0–1.</param>
/// <param name="ScientificName">Its region's catalogue name, e.g. "NGC 2244".</param>
/// <param name="PoeticName">Its region's everyday name, e.g. "The Crimson Forge".</param>
/// <param name="Cluster">The nebula or cluster it's inside, if any.</param>
public sealed record LocationInfo(
    Vector3        Position,
    ChunkCoord     Chunk,
    RegionType     Region,
    float          Density,
    string         ScientificName,
    string         PoeticName,
    GalaxyCluster? Cluster);

// ── galaxies and servers ──────────────────────────────────────────────────────

/// <summary>Everything about a galaxy a client needs to use it, and to know whether what it has cached still holds.</summary>
public sealed record GalaxyInfo
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// A copy: changing it changes nothing on the server.
    public required GalaxyParams Params { get; init; }
    /// The galaxy as a shareable code (<see cref="GalaxyCode"/>).
    public required string Code { get; init; }
    /// Names exactly this galaxy and this generator: anything cached under it stays right for ever.
    public required string Fingerprint { get; init; }
    /// A chunk's side in normalised units, and in light years.
    public required float ChunkSize { get; init; }
    public required int MaxStarsPerChunk { get; init; }
    /// How far from the plane any star can be (normalised).
    public required float VerticalExtent { get; init; }
    public required float DiskHeight { get; init; }
    /// About how many stars it has in all.
    public required long EstimatedStars { get; init; }
    public string BlackHoleDesignation { get; init; } = "";
    public double BlackHoleMassSolar { get; init; }

    public float ChunkLightYears => Params.ToLightYears(ChunkSize);
    public float RadiusLightYears => Params.RadiusLightYears;
    public float ToLightYears(float normalised) => Params.ToLightYears(normalised);
    public float ToNormalised(float lightYears) => Params.ToNormalised(lightYears);

    /// The chunk a point is in. Chunks are centred on their coordinate × ChunkSize.
    public ChunkCoord ChunkAt(Vector3 position) => Chunks.At(position, ChunkSize);
}

/// <summary>What a server says when a client says hello: who it is and what it allows.</summary>
public sealed record ServerHello(
    string Name,
    int    ProtocolVersion,
    int    AlgorithmVersion,
    int    MaxChunksPerRequest,
    int    MaxQueryChunks,
    int    MaxResults,
    int    MaxMapPixels);

/// <summary>How hard a galaxy is working: for a debug overlay or a health check.</summary>
public sealed record GalaxyStats(
    string Id,
    long   Requests,
    int    ChunksCached,
    long   ChunkHits,
    long   ChunkMisses,
    float  AvgChunkMs,
    int    SystemsCached,
    int    MapsCached);

internal static class Chunks
{
    /// The chunk containing a point: chunk n is centred on n × size, so it covers n × size ± size/2.
    public static ChunkCoord At(Vector3 p, float size) =>
        new(Index(p.X, size), Index(p.Y, size), Index(p.Z, size));

    public static long Index(float v, float size) => (long)MathF.Floor(v / size + 0.5f);
}
