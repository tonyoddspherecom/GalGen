using System.Buffers.Binary;
using UniverseGen.Models;

namespace UniverseGen.Service.Protocol;

/// <summary>What a request asks for. Sent as a byte, so values never change meaning.</summary>
public enum GalaxyRequestKind : byte
{
    Hello     = 1,
    List      = 2,
    Info      = 3,
    Map       = 4,
    Chunks    = 5,
    FindStars = 6,
    Star      = 7,
    System    = 8,
    Moons     = 9,
    Describe  = 10,
    Pick      = 11,
    Clusters  = 12,
    Stats     = 13,
}

/// <summary>
/// GalGen's wire protocol: binary request and response frames that any transport can carry (TCP,
/// HTTP, WebSockets, a game's own UDP channel). The full layout is in the README; in short:
///
///   request:  [version u8][kind u8][id u32][body]
///   response: [version u8][status u8: 0 ok, 1 error][kind u8][id u32][body, or error code u16, message, retry-after ms u32]
///
/// Little-endian throughout, with LEB128 varints for counts and coordinates. The id is the client's,
/// echoed back, so many requests can be in flight at once on one connection and answered in any order.
/// A transport only needs to carry whole frames; <see cref="TryReadResponseId"/> is all it needs to
/// match answers to questions.
/// </summary>
public static class GalaxyProtocol
{
    /// Bumped on any change a peer on the old version couldn't read.
    public const byte Version = 1;

    public const int RequestHeaderSize = 6;
    public const int ResponseHeaderSize = 7;

    internal const byte Ok = 0;
    internal const byte Failed = 1;

    public static bool TryReadRequestId(ReadOnlySpan<byte> frame, out uint id)
    {
        id = frame.Length >= RequestHeaderSize ? BinaryPrimitives.ReadUInt32LittleEndian(frame[2..]) : 0;
        return frame.Length >= RequestHeaderSize;
    }

    public static bool TryReadResponseId(ReadOnlySpan<byte> frame, out uint id)
    {
        id = frame.Length >= ResponseHeaderSize ? BinaryPrimitives.ReadUInt32LittleEndian(frame[3..]) : 0;
        return frame.Length >= ResponseHeaderSize;
    }

    internal static WireWriter Request(GalaxyRequestKind kind, uint id)
    {
        var w = new WireWriter();
        w.U8(Version);
        w.U8((byte)kind);
        w.U32(id);
        return w;
    }

    internal static WireWriter Response(GalaxyRequestKind kind, uint id)
    {
        var w = new WireWriter();
        w.U8(Version);
        w.U8(Ok);
        w.U8((byte)kind);
        w.U32(id);
        return w;
    }

    internal static byte[] Error(GalaxyRequestKind kind, uint id, GalaxyError error, string message, TimeSpan retryAfter)
    {
        var w = new WireWriter();
        w.U8(Version);
        w.U8(Failed);
        w.U8((byte)kind);
        w.U32(id);
        w.U16((ushort)error);
        w.Str(message);
        w.U32((uint)Math.Clamp(retryAfter.TotalMilliseconds, 0, uint.MaxValue));
        return w.ToArray();
    }

    /// Reads a response's header; throws the server's error if it was one. Returns a reader at the body.
    internal static WireReader OpenResponse(ReadOnlyMemory<byte> frame, GalaxyRequestKind expected)
    {
        var r = new WireReader(frame);
        byte version = r.U8();
        byte status = r.U8();
        var kind = (GalaxyRequestKind)r.U8();
        r.U32();
        if (status == Failed)
        {
            var error = (GalaxyError)r.U16();
            string message = r.Str();
            uint retryMs = r.U32();
            throw new GalaxyServiceException(error, message, TimeSpan.FromMilliseconds(retryMs));
        }
        if (version != Version)
            throw new GalaxyServiceException(GalaxyError.VersionMismatch, $"The server speaks protocol {version}; this client speaks {Version}.");
        if (status != Ok || kind != expected)
            throw new GalaxyServiceException(GalaxyError.BadRequest, $"Expected a {expected} response, got {kind}.");
        return r;
    }
}

/// <summary>The protocol's encoding of each type, both ways. Each Read mirrors its Write exactly.</summary>
internal static class Codec
{
    private const byte StarFlagBits = (byte)(StarFlags.Binary | StarFlags.Giant | StarFlags.Supergiant);

    // ── coordinates ──────────────────────────────────────────────────────────

    public static void Write(WireWriter w, ChunkCoord c) { w.VarI(c.X); w.VarI(c.Y); w.VarI(c.Z); }
    public static ChunkCoord ReadChunkCoord(WireReader r) => new(r.VarI(), r.VarI(), r.VarI());

    public static void Write(WireWriter w, StarId id) { w.VarI(id.X); w.VarI(id.Y); w.VarI(id.Z); w.VarI(id.Index); }
    public static StarId ReadStarId(WireReader r) => new(r.VarI(), r.VarI(), r.VarI(), r.Int());

    // ── galaxy ───────────────────────────────────────────────────────────────

    public static void Write(WireWriter w, GalaxyParams p)
    {
        w.U8((byte)p.Type); w.U8((byte)p.Age); w.VarI(p.Arms);
        w.F32(p.ArmTightness); w.F32(p.ArmWidth); w.F32(p.BulgeFactor); w.F32(p.BulgeStrength); w.F32(p.Dissipation);
        w.F32(p.CoreDensity); w.F32(p.ArmDensity); w.F32(p.BarStrength); w.F32(p.BarLength);
        w.U64(p.Seed); w.VarI(p.FeatureDensity);
        w.F32(p.RadiusLightYears); w.F32(p.DiskThickness); w.F32(p.StarDensity);
        w.U8((byte)p.Model); w.F32(p.BulgeThickness); w.F32(p.HaloStrength);
    }

    public static GalaxyParams ReadParams(WireReader r) => new()
    {
        Type = r.Enum<GalaxyType>(), Age = r.Enum<GalaxyAge>(), Arms = r.Int(),
        ArmTightness = r.F32(), ArmWidth = r.F32(), BulgeFactor = r.F32(), BulgeStrength = r.F32(), Dissipation = r.F32(),
        CoreDensity = r.F32(), ArmDensity = r.F32(), BarStrength = r.F32(), BarLength = r.F32(),
        Seed = r.U64(), FeatureDensity = r.Int(),
        RadiusLightYears = r.F32(), DiskThickness = r.F32(), StarDensity = r.F32(),
        Model = r.Enum<GalaxyModel>(), BulgeThickness = r.F32(), HaloStrength = r.F32(),
    };

    public static void Write(WireWriter w, GalaxyInfo i)
    {
        w.Str(i.Id); w.Str(i.Name); w.Str(i.Description);
        Write(w, i.Params);
        w.Str(i.Code); w.Str(i.Fingerprint);
        w.F32(i.ChunkSize); w.VarI(i.MaxStarsPerChunk); w.F32(i.VerticalExtent); w.F32(i.DiskHeight);
        w.VarI(i.EstimatedStars); w.Str(i.BlackHoleDesignation); w.F64(i.BlackHoleMassSolar);
    }

    public static GalaxyInfo ReadInfo(WireReader r) => new()
    {
        Id = r.Str(), Name = r.Str(), Description = r.Str(),
        Params = ReadParams(r),
        Code = r.Str(), Fingerprint = r.Str(),
        ChunkSize = r.F32(), MaxStarsPerChunk = r.Int(1), VerticalExtent = r.F32(), DiskHeight = r.F32(),
        EstimatedStars = r.VarI(), BlackHoleDesignation = r.Str(), BlackHoleMassSolar = r.F64(),
    };

    public static void Write(WireWriter w, ServerHello h)
    {
        w.Str(h.Name); w.VarI(h.ProtocolVersion); w.VarI(h.AlgorithmVersion);
        w.VarI(h.MaxChunksPerRequest); w.VarI(h.MaxQueryChunks); w.VarI(h.MaxResults); w.VarI(h.MaxMapPixels);
    }

    public static ServerHello ReadHello(WireReader r) =>
        new(r.Str(), r.Int(), r.Int(), r.Int(1), r.Int(1), r.Int(1), r.Int(1));

    public static void Write(WireWriter w, GalaxyStats s)
    {
        w.Str(s.Id); w.VarI(s.Requests); w.VarI(s.ChunksCached); w.VarI(s.ChunkHits); w.VarI(s.ChunkMisses);
        w.F32(s.AvgChunkMs); w.VarI(s.SystemsCached); w.VarI(s.MapsCached);
    }

    public static GalaxyStats ReadStats(WireReader r) =>
        new(r.Str(), r.VarI(), r.Int(0), r.VarI(), r.VarI(), r.F32(), r.Int(0), r.Int(0));

    // ── maps ─────────────────────────────────────────────────────────────────

    public static void Write(WireWriter w, MapRequest m)
    {
        w.U8((byte)m.View); w.VarI(m.Width); w.VarI(m.Height);
        w.F32(m.Region.X); w.F32(m.Region.Y); w.F32(m.Region.Width); w.F32(m.Region.Height);
        w.U8((byte)m.Format);
    }

    public static MapRequest ReadMapRequest(WireReader r) => new()
    {
        View = r.Enum<MapView>(), Width = r.Int(0), Height = r.Int(0),
        Region = new RegionF(r.F32(), r.F32(), r.F32(), r.F32()),
        Format = r.Enum<MapFormat>(),
    };

    public static void Write(WireWriter w, DensityImage d)
    {
        w.VarI(d.Width); w.VarI(d.Height); w.U8((byte)d.Format); w.Bytes(d.Data);
    }

    public static DensityImage ReadImage(WireReader r)
    {
        int width = r.Int(1), height = r.Int(1);
        var format = r.Enum<MapFormat>();
        byte[] data = r.Bytes();
        try { return new DensityImage(width, height, format, data); }
        catch (ArgumentException e) { throw new GalaxyServiceException(GalaxyError.BadRequest, $"Malformed message: {e.Message}"); }
    }

    // ── stars ────────────────────────────────────────────────────────────────

    // Positions go as exact floats: a star's position is its identity (its name and system come from it).
    public static void Write(WireWriter w, Star s)
    {
        w.F32(s.X); w.F32(s.Y); w.F32(s.Z); w.U8((byte)s.Class); w.U8(s.Luminosity); w.U8((byte)s.Flags);
    }

    public static Star ReadStar(WireReader r) =>
        new(r.F32(), r.F32(), r.F32(), r.Enum<SpectralClass>(), r.U8(), r.Flags<StarFlags>(StarFlagBits));

    private const int StarBytes = 15;

    public static void Write(WireWriter w, StarChunk c)
    {
        Write(w, c.Coord); w.U8((byte)c.Region); w.F32(c.Density);
        w.VarU((ulong)c.Stars.Length);
        foreach (Star s in c.Stars) Write(w, s);
    }

    public static StarChunk ReadChunk(WireReader r)
    {
        ChunkCoord coord = ReadChunkCoord(r);
        var region = r.Enum<RegionType>();
        float density = r.F32();
        var stars = new Star[r.Count(StarBytes)];
        for (int i = 0; i < stars.Length; i++) stars[i] = ReadStar(r);
        return new StarChunk(coord, stars, density, region);
    }

    public static void Write(WireWriter w, StarRef s)
    {
        Write(w, s.Id); Write(w, s.Star); w.Str(s.Designation); w.U8((byte)s.Region);
    }

    public static StarRef ReadStarRef(WireReader r) => new(ReadStarId(r), ReadStar(r), r.Str(), r.Enum<RegionType>());

    public static void Write(WireWriter w, StarFilter f)
    {
        w.U16(f.Classes); w.U8((byte)f.RequiredFlags); w.U8((byte)f.ExcludedFlags); w.U8(f.MinLuminosity); w.U8(f.Regions);
        w.VarI(f.MinPlanets); w.Bool(f.HabitableOnly);
    }

    public static StarFilter ReadFilter(WireReader r) => new()
    {
        Classes = r.U16(), RequiredFlags = r.Flags<StarFlags>(StarFlagBits), ExcludedFlags = r.Flags<StarFlags>(StarFlagBits),
        MinLuminosity = r.U8(), Regions = r.U8(), MinPlanets = r.Int(0), HabitableOnly = r.Bool(),
    };

    public static void Write(WireWriter w, StarQuery q)
    {
        w.U8((byte)q.Shape); w.Vec3(q.Center); w.F32(q.Radius); w.Vec3(q.Min); w.Vec3(q.Max);
        Write(w, q.Filter); w.VarI(q.Limit); w.U8((byte)q.Order);
    }

    public static StarQuery ReadQuery(WireReader r) => new()
    {
        Shape = r.Enum<QueryShape>(), Center = r.Vec3(), Radius = r.F32(), Min = r.Vec3(), Max = r.Vec3(),
        Filter = ReadFilter(r), Limit = r.Int(), Order = r.Enum<StarOrder>(),
    };

    public static void Write(WireWriter w, StarQueryResult q)
    {
        w.VarU((ulong)q.Stars.Count);
        foreach (StarRef s in q.Stars) Write(w, s);
        w.Bool(q.Truncated); w.VarI(q.ChunksSearched);
    }

    public static StarQueryResult ReadQueryResult(WireReader r)
    {
        var stars = new StarRef[r.Count(StarBytes + 5)];
        for (int i = 0; i < stars.Length; i++) stars[i] = ReadStarRef(r);
        return new StarQueryResult(stars, r.Bool(), r.Int(0));
    }

    public static void Write(WireWriter w, StarPick p)
    {
        w.U64(p.Seed); Write(w, p.Filter); w.F32(p.MinGalacticRadius); w.F32(p.MaxGalacticRadius); w.VarI(p.MaxAttempts);
    }

    public static StarPick ReadPick(WireReader r) => new()
    {
        Seed = r.U64(), Filter = ReadFilter(r), MinGalacticRadius = r.F32(), MaxGalacticRadius = r.F32(), MaxAttempts = r.Int(),
    };

    // ── systems ──────────────────────────────────────────────────────────────

    public static void Write(WireWriter w, StarSystem s)
    {
        w.Str(s.Designation); w.U8((byte)s.StarClass);
        w.F32(s.LuminositySol); w.F32(s.HzInnerAu); w.F32(s.HzOuterAu); w.F32(s.SnowLineAu);
        w.VarU((ulong)s.Companions.Length);
        foreach (CompanionStar c in s.Companions)
        {
            w.U8((byte)c.Class); w.F32(c.MassSolar); w.F32(c.OrbitAu); w.Str(c.Designation);
        }
        w.VarU((ulong)s.Bodies.Length);
        foreach (StarSystemBody b in s.Bodies)
        {
            w.Str(b.Name); w.U8((byte)b.Type); w.U8((byte)b.Atmosphere); w.U8((byte)b.Surface);
            w.F32(b.OrbitAu); w.F32(b.MassEarth); w.F32(b.RadiusEarth); w.F32(b.TemperatureK); w.F32(b.GravitySurface);
            w.VarI(b.MoonCount); w.Bool(b.HasRings); w.Bool(b.InHabitableZone);
        }
        w.Bool(s.HasOortCloud);
    }

    public static StarSystem ReadSystem(WireReader r)
    {
        string designation = r.Str();
        var cls = r.Enum<SpectralClass>();
        float lum = r.F32(), hzIn = r.F32(), hzOut = r.F32(), snow = r.F32();
        var companions = new CompanionStar[r.Count(10)];
        for (int i = 0; i < companions.Length; i++)
            companions[i] = new CompanionStar(r.Enum<SpectralClass>(), r.F32(), r.F32(), r.Str());
        var bodies = new StarSystemBody[r.Count(27)];
        for (int i = 0; i < bodies.Length; i++)
            bodies[i] = new StarSystemBody(r.Str(), r.Enum<BodyType>(), r.Enum<AtmosphereType>(), r.Enum<SurfaceType>(),
                r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.Int(0), r.Bool(), r.Bool());
        return new StarSystem(designation, cls, lum, hzIn, hzOut, snow, companions, bodies, r.Bool());
    }

    public static void Write(WireWriter w, Moon m)
    {
        w.Str(m.Name); w.F32(m.OrbitParentRadii); w.F32(m.RadiusKm); w.U8((byte)m.Type);
    }

    public static Moon ReadMoon(WireReader r) => new(r.Str(), r.F32(), r.F32(), r.Enum<MoonType>());

    // ── places ───────────────────────────────────────────────────────────────

    public static void Write(WireWriter w, GalaxyCluster c)
    {
        w.U8((byte)c.Type); w.Vec3(c.Position); w.F32(c.Radius); w.Str(c.ScientificName); w.Str(c.PoeticName);
    }

    public static GalaxyCluster ReadCluster(WireReader r) => new(r.Enum<ClusterType>(), r.Vec3(), r.F32(), r.Str(), r.Str());

    public static void Write(WireWriter w, LocationInfo l)
    {
        w.Vec3(l.Position); Write(w, l.Chunk); w.U8((byte)l.Region); w.F32(l.Density);
        w.Str(l.ScientificName); w.Str(l.PoeticName);
        w.Bool(l.Cluster != null);
        if (l.Cluster != null) Write(w, l.Cluster);
    }

    public static LocationInfo ReadLocation(WireReader r) =>
        new(r.Vec3(), ReadChunkCoord(r), r.Enum<RegionType>(), r.F32(), r.Str(), r.Str(), r.Bool() ? ReadCluster(r) : null);
}
