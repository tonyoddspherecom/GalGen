using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using UniverseGen.Generation;
using UniverseGen.Models;

namespace UniverseGen.Service;

/// <summary>How a <see cref="LocalGalaxy"/> generates, caches, and what it refuses.</summary>
public sealed record LocalGalaxyOptions
{
    /// A star chunk's side in light years. Part of what the galaxy is: changing it changes every star.
    public float ChunkLightYears { get; init; } = StarGenerator.DefaultChunkLightYears;
    /// A chunk's stars at density 1, before <see cref="GalaxyParams.StarDensity"/>. Also part of what the galaxy is.
    public int MaxStarsPerChunk { get; init; } = 200;

    public int ChunkCacheCapacity { get; init; } = 65_536;
    public int SystemCacheCapacity { get; init; } = 8_192;
    public int MapCacheCapacity { get; init; } = 64;

    /// The most chunks one request may ask for by coordinate.
    public int MaxChunksPerRequest { get; init; } = 4_096;
    /// The most chunks one query may search.
    public int MaxQueryChunks { get; init; } = 250_000;
    /// The most stars one query may return.
    public int MaxResults { get; init; } = 20_000;
    /// The biggest map, in pixels.
    public int MaxMapPixels { get; init; } = 4096 * 4096;
    /// The most systems a planet filter may generate for one query or pick.
    public int MaxSystemsPerQuery { get; init; } = 5_000;
    /// The most places one pick may try.
    public int MaxPickAttempts { get; init; } = 1_024;
    /// How many chunks one big request generates at once. 1 keeps each request on one thread.
    public int Parallelism { get; init; } = Environment.ProcessorCount;
}

/// <summary>
/// A galaxy generated in this process: GalGen behind the <see cref="IGalaxy"/> interface, with caching,
/// request coalescing and limits. Use it directly for offline play and tools, put it in a
/// <see cref="GalaxyHost"/> to serve it, or both.
///
/// The async methods complete synchronously (generation is CPU work, done on the caller's thread and,
/// for big requests, spread over <see cref="LocalGalaxyOptions.Parallelism"/> threads); the synchronous
/// versions are public too, for callers that don't want a ValueTask.
/// </summary>
public sealed class LocalGalaxy : IGalaxy
{
    private readonly GalaxyParams _params;
    private readonly GalaxyMath _math;
    private readonly GalaxySampler _sampler;
    private readonly StarGenerator _generator;
    private readonly StarChunkCache _chunks;
    private readonly LruCache<StarId, StarSystem> _systems;
    private readonly LruCache<MapRequest, Lazy<DensityImage>> _maps;
    private readonly Lazy<GalaxyClusterMap> _clusters;
    private readonly StarSystemGenerator _systemGenerator = new();
    private readonly MoonGenerator _moonGenerator = new();
    private readonly int _verticalChunks;
    private long _requests;

    public GalaxyInfo Info { get; }
    public LocalGalaxyOptions Options { get; }

    public LocalGalaxy(string id, GalaxyParams parms, LocalGalaxyOptions? options = null, string name = "", string description = "")
    {
        GalaxyHost.ValidateId(id);
        parms.Validate();
        Options = options ?? new LocalGalaxyOptions();
        if (!(Options.ChunkLightYears > 0f) || Options.MaxStarsPerChunk <= 0)
            throw new ArgumentException("Chunks need a size above 0 and room for at least one star.", nameof(options));

        _params    = parms.Clone();
        _math      = GalaxyMath.For(_params);
        _sampler   = new GalaxySampler(_params);
        _generator = new StarGenerator
        {
            ChunkSize        = _params.ToNormalised(Options.ChunkLightYears),
            MaxStarsPerChunk = Options.MaxStarsPerChunk,
        };
        _chunks   = new StarChunkCache(_params, _generator, Math.Max(Options.ChunkCacheCapacity, 1));
        _systems  = new LruCache<StarId, StarSystem>(Options.SystemCacheCapacity);
        _maps     = new LruCache<MapRequest, Lazy<DensityImage>>(Options.MapCacheCapacity);
        _clusters = new Lazy<GalaxyClusterMap>(() => new GalaxyGenerator().Generate(_params).ClusterMap!);

        float extent = _sampler.VerticalExtent;
        _verticalChunks = (int)Math.Min(MathF.Ceiling(extent / _generator.ChunkSize), int.MaxValue / 4);
        var blackHole = new GalacticBlackHole(_params);
        Info = new GalaxyInfo
        {
            Id                   = id,
            Name                 = name,
            Description          = description,
            Params               = _params.Clone(),
            Code                 = GalaxyCode.Encode(_params),
            Fingerprint          = Fingerprint(_params, _generator),
            ChunkSize            = _generator.ChunkSize,
            MaxStarsPerChunk     = _generator.MaxStarsPerChunk,
            VerticalExtent       = extent,
            DiskHeight           = _sampler.DiskHeight,
            EstimatedStars       = GalaxyCensus.Estimate(_params, _generator.ChunkSize, _generator.MaxStarsPerChunk),
            BlackHoleDesignation = blackHole.Designation,
            BlackHoleMassSolar   = blackHole.MassSolarMasses,
        };
    }

    /// The galaxy's fingerprint with the chunking it's served at, since that changes its stars too.
    private static string Fingerprint(GalaxyParams p, StarGenerator g)
    {
        string text = $"{p.Fingerprint()}/{BitConverter.SingleToInt32Bits(g.ChunkSize)}/{g.MaxStarsPerChunk}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    public GalaxyStats Stats()
    {
        CacheStats chunks = _chunks.GetStats();
        return new GalaxyStats(Info.Id, Interlocked.Read(ref _requests), chunks.Count, chunks.Hits, chunks.Misses,
            float.IsNaN(chunks.AvgGenerationMs) ? 0f : chunks.AvgGenerationMs, _systems.Count, _maps.Count);
    }

    private void Count() => Interlocked.Increment(ref _requests);

    private static GalaxyServiceException Bad(string message) => new(GalaxyError.BadRequest, message);
    private static GalaxyServiceException TooBig(string message) => new(GalaxyError.LimitExceeded, message);

    // ── maps ──────────────────────────────────────────────────────────────────

    /// Throws if a map request is malformed or over the limits.
    public void Check(MapRequest request)
    {
        if (request.Width < 2 || request.Height < 2) throw Bad("A map must be at least 2×2.");
        if ((long)request.Width * request.Height > Options.MaxMapPixels) throw TooBig($"A map can't be over {Options.MaxMapPixels} pixels.");
        if (!Enum.IsDefined(request.View) || !Enum.IsDefined(request.Format)) throw Bad("Unknown map view or format.");
        RegionF r = request.Region;
        if (request.View == MapView.Region
            && !(r.Width > 0f && r.Height > 0f && float.IsFinite(r.X) && float.IsFinite(r.Y) && float.IsFinite(r.Width) && float.IsFinite(r.Height)))
            throw Bad("A region map needs a region with a size above 0.");
    }

    public DensityImage GetMap(MapRequest request)
    {
        Count();
        Check(request);
        // Normalise what doesn't affect the image, so equal maps share one cache entry.
        if (request.View != MapView.Region) request = request with { Region = RegionF.Full };
        // Two callers asking for the same new map get the one generation between them.
        Lazy<DensityImage> map = _maps.GetOrAdd(request, r => new Lazy<DensityImage>(() => Render(r)));
        return map.Value;
    }

    private DensityImage Render(MapRequest r)
    {
        var values = new float[r.Width * r.Height];
        switch (r.View)
        {
            case MapView.EdgeOn: _math.SideView(values, r.Width, r.Height); break;
            case MapView.Region: _math.DensityMap(values, r.Width, r.Height, r.Region.X, r.Region.Y, r.Region.Width, r.Region.Height); break;
            default: _math.DensityMap(values, r.Width, r.Height); break;
        }
        return DensityImage.From(values, r.Width, r.Height, r.Format);
    }

    // ── chunks ────────────────────────────────────────────────────────────────

    public StarChunk GetChunk(ChunkCoord coord) => _chunks.GetOrGenerate(coord);

    public IReadOnlyList<StarChunk> GetChunks(IReadOnlyList<ChunkCoord> coords, CancellationToken cancellationToken = default)
    {
        Count();
        if (coords.Count > Options.MaxChunksPerRequest) throw TooBig($"A request can't ask for more than {Options.MaxChunksPerRequest} chunks.");
        var result = new StarChunk[coords.Count];
        Generate(coords.Count, i => result[i] = _chunks.GetOrGenerate(coords[i]), cancellationToken);
        return result;
    }

    /// Runs body(0..count-1), spread over threads when there's enough to be worth it.
    private void Generate(int count, Action<int> body, CancellationToken cancellationToken)
    {
        if (count <= 32 || Options.Parallelism <= 1)
        {
            for (int i = 0; i < count; i++)
            {
                if ((i & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                body(i);
            }
            return;
        }
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Options.Parallelism, CancellationToken = cancellationToken }, body);
    }

    // ── stars ─────────────────────────────────────────────────────────────────

    private StarRef Ref(StarId id, Star star, RegionType region) => new(id, star, StarNames.Designation(star), region);

    public StarRef? GetStar(StarId id)
    {
        Count();
        if (id.Index < 0) return null;
        StarChunk chunk = _chunks.GetOrGenerate(id.Chunk);
        return id.Index < chunk.Stars.Length ? Ref(id, chunk.Stars[id.Index], chunk.Region) : null;
    }

    /// The chunk-index range a query must search, with the vertical range cut to where stars can be.
    private (long X0, long X1, long Y0, long Y1, long Z0, long Z1) Range(StarQuery query)
    {
        float s = _generator.ChunkSize;
        (Vector3 lo, Vector3 hi) = query.Bounds;
        // Chunk n covers (n - ½)s to (n + ½)s; a hair of slack for stars right on a boundary.
        const float slack = 1e-3f;
        long First(float v) => (long)MathF.Ceiling(v / s - 0.5f - slack);
        long Last(float v) => (long)MathF.Floor(v / s + 0.5f + slack);
        return (First(lo.X), Last(hi.X),
                Math.Max(First(lo.Y), -_verticalChunks), Math.Min(Last(hi.Y), _verticalChunks),
                First(lo.Z), Last(hi.Z));
    }

    /// How many chunks a query would search: what limits and rate limits charge for it.
    public long QueryChunkCount(StarQuery query)
    {
        var (x0, x1, y0, y1, z0, z1) = Range(query);
        if (x1 < x0 || y1 < y0 || z1 < z0) return 0;
        return (x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
    }

    /// Throws if a query is malformed or over the limits.
    public void Check(StarQuery query)
    {
        bool finite = query.Shape == QueryShape.Sphere
            ? float.IsFinite(query.Radius) && query.Radius >= 0f && IsFinite(query.Center)
            : IsFinite(query.Min) && IsFinite(query.Max) && query.Min.X <= query.Max.X && query.Min.Y <= query.Max.Y && query.Min.Z <= query.Max.Z;
        if (!finite || !Enum.IsDefined(query.Shape) || !Enum.IsDefined(query.Order)) throw Bad("The query's space isn't a real sphere or box.");
        if (query.Limit < 1 || query.Limit > Options.MaxResults) throw TooBig($"A query returns 1 to {Options.MaxResults} stars.");
        if (query.Filter.MinPlanets < 0) throw Bad("A planet count can't be negative.");
        // Far beyond the galaxy, chunk indices would overflow before they meant anything.
        (Vector3 lo, Vector3 hi) = query.Bounds;
        if (MathF.Max(Vector3.Abs(lo).Max(), Vector3.Abs(hi).Max()) > 16f) throw Bad("The query is far outside the galaxy.");
        long chunks = QueryChunkCount(query);
        if (chunks > Options.MaxQueryChunks) throw TooBig($"That query would search {chunks} chunks; the most is {Options.MaxQueryChunks}.");
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private readonly record struct Candidate(StarId Id, Star Star, RegionType Region, float Key);

    /// Orders candidates best first: the smaller key, then the smaller id, so equal keys still come out the same every time.
    private sealed class BestFirst : IComparer<Candidate>
    {
        public static readonly BestFirst Instance = new();
        public int Compare(Candidate a, Candidate b)
        {
            int c = a.Key.CompareTo(b.Key);
            if (c != 0) return c;
            c = a.Id.X.CompareTo(b.Id.X); if (c != 0) return c;
            c = a.Id.Y.CompareTo(b.Id.Y); if (c != 0) return c;
            c = a.Id.Z.CompareTo(b.Id.Z); if (c != 0) return c;
            return a.Id.Index.CompareTo(b.Id.Index);
        }
    }

    public StarQueryResult FindStars(StarQuery query, CancellationToken cancellationToken = default)
    {
        Count();
        Check(query);
        return Search(query, Options.MaxSystemsPerQuery, cancellationToken);
    }

    private StarQueryResult Search(StarQuery query, int maxSystems, CancellationToken cancellationToken)
    {
        var (x0, x1, y0, y1, z0, z1) = Range(query);
        if (x1 < x0 || y1 < y0 || z1 < z0) return new StarQueryResult([], false, 0);
        long nx = x1 - x0 + 1, ny = y1 - y0 + 1, nz = z1 - z0 + 1;
        long total = nx * ny * nz;

        StarFilter filter = query.Filter;
        int limit = query.Limit;
        int systemsChecked = 0;
        long matches = 0;
        // The best `limit` so far, worst on top, so the worst can be dropped as better ones arrive.
        var best = new PriorityQueue<Candidate, Candidate>(Comparer<Candidate>.Create((a, b) => BestFirst.Instance.Compare(b, a)));
        var inOrder = new List<Candidate>();

        // Chunks are generated a batch at a time, in parallel, then scanned in a fixed order, so the
        // result is the same however the threads ran.
        const int batch = 512;
        var chunks = new StarChunk[batch];
        for (long start = 0; start < total; start += batch)
        {
            int n = (int)Math.Min(batch, total - start);
            Generate(n, i =>
            {
                long k = start + i;
                long x = x0 + k / (ny * nz), y = y0 + k / nz % ny, z = z0 + k % nz;
                chunks[i] = _chunks.GetOrGenerate(new ChunkCoord(x, y, z));
            }, cancellationToken);

            for (int i = 0; i < n; i++)
            {
                StarChunk chunk = chunks[i];
                Star[] stars = chunk.Stars;
                for (int s = 0; s < stars.Length; s++)
                {
                    Star star = stars[s];
                    if (!query.Contains(star.Position) || !filter.Accepts(star, chunk.Region)) continue;
                    var id = new StarId(chunk.Coord, s);
                    if (filter.NeedsSystem)
                    {
                        if (++systemsChecked > maxSystems)
                            throw TooBig($"The planet filter would need more than {maxSystems} systems: narrow the query or the other filters.");
                        if (!filter.Accepts(System(id, star))) continue;
                    }
                    matches++;
                    float key = query.Order switch
                    {
                        StarOrder.Nearest => Vector3.DistanceSquared(star.Position, query.Center),
                        StarOrder.Brightest => -star.Luminosity,
                        _ => 0f,
                    };
                    var candidate = new Candidate(id, star, chunk.Region, key);
                    if (query.Order == StarOrder.None)
                    {
                        if (inOrder.Count < limit) inOrder.Add(candidate);
                        continue;
                    }
                    best.Enqueue(candidate, candidate);
                    if (best.Count > limit) best.Dequeue();
                }
            }
            // In chunk order, the first `limit` are the answer: once there's one more, nothing later changes it.
            if (query.Order == StarOrder.None && matches > limit) break;
        }

        List<Candidate> chosen = query.Order == StarOrder.None ? inOrder : best.UnorderedItems.Select(e => e.Element).ToList();
        if (query.Order != StarOrder.None) chosen.Sort(BestFirst.Instance);
        var result = chosen.Select(c => Ref(c.Id, c.Star, c.Region)).ToList();
        return new StarQueryResult(result, matches > limit, (int)Math.Min(total, int.MaxValue));
    }

    // ── systems ───────────────────────────────────────────────────────────────

    private StarSystem System(StarId id, Star star) =>
        _systems.GetOrAdd(id, _ => _systemGenerator.Generate(star, _params.Age, _params.Seed, StarNames.Designation(star)));

    public StarSystem? GetSystem(StarId id)
    {
        StarRef? star = GetStar(id);
        return star == null ? null : System(id, star.Star);
    }

    public IReadOnlyList<Moon> GetMoons(StarId id, int bodyIndex)
    {
        StarSystem system = GetSystem(id) ?? throw Bad($"There's no star at {id}.");
        if ((uint)bodyIndex >= (uint)system.Bodies.Length) throw Bad($"{system.Designation} has no body {bodyIndex}.");
        StarSystemBody body = system.Bodies[bodyIndex];
        return _moonGenerator.Generate(body, body.Name);
    }

    // ── places ────────────────────────────────────────────────────────────────

    public ChunkCoord ChunkAt(Vector3 position) => Chunks.At(position, _generator.ChunkSize);

    public LocationInfo Describe(Vector3 position)
    {
        Count();
        if (!IsFinite(position) || Vector3.Abs(position).Max() > 16f) throw Bad("That point isn't in the galaxy.");
        ChunkCoord chunk = ChunkAt(position);
        // Classified at the chunk's centre, as its stars are, so a place and its stars always agree.
        RegionType region = _sampler.RegionAt(chunk.Centre(_generator.ChunkSize));
        return new LocationInfo(
            position, chunk, region, _sampler.DensityAt(position.X, position.Z),
            RegionNameGenerator.ScientificName(chunk.X, chunk.Y, chunk.Z, region, _params.Seed),
            RegionNameGenerator.PoeticName(chunk.X, chunk.Y, chunk.Z, region, _params.Seed),
            _clusters.Value.FindNearest(position));
    }

    public IReadOnlyList<GalaxyCluster> Clusters => _clusters.Value.Clusters;

    // ── picks ─────────────────────────────────────────────────────────────────

    public StarRef? PickStar(StarPick pick, CancellationToken cancellationToken = default)
    {
        Count();
        if (!(pick.MinGalacticRadius >= 0f && pick.MaxGalacticRadius > pick.MinGalacticRadius && pick.MaxGalacticRadius <= 2f))
            throw Bad("A pick's radii must be 0 ≤ min < max ≤ 2.");
        if (pick.MaxAttempts < 1 || pick.MaxAttempts > Options.MaxPickAttempts) throw TooBig($"A pick tries 1 to {Options.MaxPickAttempts} places.");
        if (pick.Filter.MinPlanets < 0) throw Bad("A planet count can't be negative.");

        float min2 = pick.MinGalacticRadius * pick.MinGalacticRadius, max2 = pick.MaxGalacticRadius * pick.MaxGalacticRadius;
        float chunk = _generator.ChunkSize;
        int systemsLeft = Options.MaxSystemsPerQuery;
        for (int attempt = 0; attempt < pick.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong h = Mix(pick.Seed ^ Mix((ulong)attempt + 0x9E3779B97F4A7C15UL));
            float u1 = (h & 0x1FFFFF) / (float)0x200000, u2 = ((h >> 21) & 0x1FFFFF) / (float)0x200000, u3 = (h >> 42) / (float)0x400000;
            // Uniform by area across the ring, then kept in proportion to the density there.
            float r = MathF.Sqrt(min2 + u1 * (max2 - min2)), angle = u2 * MathF.Tau;
            var place = new Vector3(r * MathF.Cos(angle), 0f, r * MathF.Sin(angle));
            if (u3 >= _sampler.DensityAt(place.X, place.Z)) continue;
            // The nearest star there that passes, looking a little further each time.
            for (float reach = chunk * 1.5f; reach <= chunk * 12f; reach *= 2f)
            {
                var near = StarQuery.Sphere(place, reach, pick.Filter, limit: 1);
                StarQueryResult found = Search(near, systemsLeft, cancellationToken);
                if (found.Stars.Count > 0) return found.Stars[0];
            }
        }
        return null;
    }

    private static ulong Mix(ulong h)
    {
        h = (h ^ (h >> 30)) * 0xbf58476d1ce4e5b9UL;
        h = (h ^ (h >> 27)) * 0x94d049bb133111ebUL;
        return h ^ (h >> 31);
    }

    // ── IGalaxy ───────────────────────────────────────────────────────────────

    public ValueTask<DensityImage> GetMapAsync(MapRequest request, CancellationToken cancellationToken = default) =>
        new(GetMap(request));

    public ValueTask<IReadOnlyList<StarChunk>> GetChunksAsync(IReadOnlyList<ChunkCoord> chunks, CancellationToken cancellationToken = default) =>
        new(GetChunks(chunks, cancellationToken));

    public ValueTask<StarQueryResult> FindStarsAsync(StarQuery query, CancellationToken cancellationToken = default) =>
        new(FindStars(query, cancellationToken));

    public ValueTask<StarRef?> GetStarAsync(StarId id, CancellationToken cancellationToken = default) => new(GetStar(id));

    public ValueTask<StarSystem?> GetSystemAsync(StarId id, CancellationToken cancellationToken = default) => new(GetSystem(id));

    public ValueTask<IReadOnlyList<Moon>> GetMoonsAsync(StarId id, int bodyIndex, CancellationToken cancellationToken = default) =>
        new(GetMoons(id, bodyIndex));

    public ValueTask<LocationInfo> DescribeAsync(Vector3 position, CancellationToken cancellationToken = default) => new(Describe(position));

    public ValueTask<StarRef?> PickStarAsync(StarPick pick, CancellationToken cancellationToken = default) =>
        new(PickStar(pick, cancellationToken));

    public ValueTask<IReadOnlyList<GalaxyCluster>> GetClustersAsync(CancellationToken cancellationToken = default) => new(Clusters);
}

internal static class VectorExtensions
{
    public static float Max(this Vector3 v) => MathF.Max(v.X, MathF.Max(v.Y, v.Z));
}
