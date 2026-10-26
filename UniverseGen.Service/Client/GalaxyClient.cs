using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using UniverseGen.Models;
using UniverseGen.Service.Protocol;

namespace UniverseGen.Service;

/// <summary>How a <see cref="GalaxyClient"/> talks to its server and what it keeps.</summary>
public sealed record GalaxyClientOptions
{
    /// What the client calls itself in its hello, for the server's logs.
    public string ClientName { get; init; } = "GalGen client";
    /// How long to wait for any one answer.
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// Answers kept in memory, per galaxy.
    public int ChunkCacheCapacity { get; init; } = 16_384;
    public int SystemCacheCapacity { get; init; } = 2_048;
    public int MapCacheCapacity { get; init; } = 32;
    /// Where answers are kept between runs (on disk, say). Null keeps them in memory only.
    public IResponseStore? Store { get; init; }
    /// How many times a request is retried when the server is busy (rate-limited) or the connection drops.
    public int MaxRetries { get; init; } = 3;
    /// The longest it will wait before a retry, whatever the server asks.
    public TimeSpan MaxRetryWait { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// A GalGen server's galaxies, over any <see cref="IGalaxyTransport"/>. Each galaxy it opens is an
/// <see cref="IGalaxy"/> just like a local one, with caching in memory (and on disk, given a
/// <see cref="GalaxyClientOptions.Store"/>), batching of chunk requests to the server's limits, and one
/// request between any number of callers asking for the same thing at once.
/// Thread-safe.
/// </summary>
public sealed class GalaxyClient : IGalaxyCatalog, IAsyncDisposable
{
    private readonly IGalaxyTransport _transport;
    private readonly bool _ownsTransport;
    private readonly ConcurrentDictionary<string, Lazy<Task<RemoteGalaxy>>> _galaxies = new(StringComparer.Ordinal);
    private Lazy<Task<ServerHello>> _hello;
    private uint _nextId;

    public GalaxyClientOptions Options { get; }

    public GalaxyClient(IGalaxyTransport transport, GalaxyClientOptions? options = null, bool ownsTransport = true)
    {
        _transport = transport;
        _ownsTransport = ownsTransport;
        Options = options ?? new GalaxyClientOptions();
        _hello = NewHello();
    }

    /// A client for a <see cref="TcpGalaxyServer"/>. It connects on its first request.
    public static GalaxyClient Tcp(string host, int port = TcpGalaxyServer.DefaultPort, GalaxyClientOptions? options = null) =>
        new(new TcpGalaxyTransport(host, port), options);

    private Lazy<Task<ServerHello>> NewHello() => new(() => FetchHello(CancellationToken.None));

    private async Task<ServerHello> FetchHello(CancellationToken cancellationToken)
    {
        WireReader r = await CallAsync(GalaxyRequestKind.Hello, w => w.Str(Options.ClientName), null, cancellationToken).ConfigureAwait(false);
        return Codec.ReadHello(r);
    }

    /// Who the server is and what it allows. Asked once, then remembered.
    public async Task<ServerHello> HelloAsync(CancellationToken cancellationToken = default)
    {
        Lazy<Task<ServerHello>> hello = _hello;
        try { return await hello.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (GalaxyServiceException)
        {
            // Don't remember a failure: the next caller asks again.
            Interlocked.CompareExchange(ref _hello, NewHello(), hello);
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<GalaxyInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        WireReader r = await CallAsync(GalaxyRequestKind.List, _ => { }, null, cancellationToken).ConfigureAwait(false);
        var list = new GalaxyInfo[r.Count(20)];
        for (int i = 0; i < list.Length; i++) list[i] = Codec.ReadInfo(r);
        return list;
    }

    async ValueTask<IGalaxy> IGalaxyCatalog.OpenAsync(string id, CancellationToken cancellationToken) =>
        await OpenAsync(id, cancellationToken).ConfigureAwait(false);

    /// A galaxy on the server. Opened once (its info fetched), then the same object every time.
    public async ValueTask<RemoteGalaxy> OpenAsync(string id, CancellationToken cancellationToken = default)
    {
        var opening = _galaxies.GetOrAdd(id, key => new Lazy<Task<RemoteGalaxy>>(() => Fetch(key)));
        try { return await opening.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (GalaxyServiceException)
        {
            _galaxies.TryRemove(new KeyValuePair<string, Lazy<Task<RemoteGalaxy>>>(id, opening));
            throw;
        }

        async Task<RemoteGalaxy> Fetch(string key)
        {
            WireReader r = await CallAsync(GalaxyRequestKind.Info, w => w.Str(key), null, CancellationToken.None).ConfigureAwait(false);
            return new RemoteGalaxy(this, Codec.ReadInfo(r));
        }
    }

    /// The server's cache and request counts: for all its galaxies, or one.
    public async ValueTask<IReadOnlyList<GalaxyStats>> StatsAsync(string? galaxyId = null, CancellationToken cancellationToken = default)
    {
        WireReader r = await CallAsync(GalaxyRequestKind.Stats, w => w.Str(galaxyId ?? ""), null, cancellationToken).ConfigureAwait(false);
        var stats = new GalaxyStats[r.Count(8)];
        for (int i = 0; i < stats.Length; i++) stats[i] = Codec.ReadStats(r);
        return stats;
    }

    /// <summary>
    /// Sends one request and reads its answer, retrying when the server is busy or the connection
    /// drops. Given a galaxy's fingerprint, the answer is immutable, so it's looked for in (and kept in)
    /// the response store.
    /// </summary>
    internal async ValueTask<WireReader> CallAsync(GalaxyRequestKind kind, Action<WireWriter> writeBody, string? fingerprint, CancellationToken cancellationToken)
    {
        var body = new WireWriter();
        writeBody(body);
        string? key = fingerprint != null && Options.Store != null ? StoreKey(fingerprint, kind, body.Written) : null;
        if (key != null && Options.Store!.TryGet(key, out byte[] stored))
        {
            try { return GalaxyProtocol.OpenResponse(stored, kind); }
            catch (GalaxyServiceException) { }   // a damaged entry: ask the server instead
        }

        for (int attempt = 0; ; attempt++)
        {
            WireWriter frame = GalaxyProtocol.Request(kind, Interlocked.Increment(ref _nextId));
            frame.Raw(body.Written);
            try
            {
                ReadOnlyMemory<byte> response;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(Options.RequestTimeout);
                    try { response = await _transport.SendAsync(frame.ToArray(), timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new GalaxyServiceException(GalaxyError.Unavailable, $"The galaxy server didn't answer within {Options.RequestTimeout.TotalSeconds:0.#} s.");
                    }
                }
                WireReader reader = GalaxyProtocol.OpenResponse(response, kind);
                if (key != null) Options.Store!.Put(key, response.ToArray());
                return reader;
            }
            catch (GalaxyServiceException e) when (attempt < Options.MaxRetries && e.Error is GalaxyError.RateLimited or GalaxyError.Unavailable)
            {
                TimeSpan wait = e.Error == GalaxyError.RateLimited ? e.RetryAfter : TimeSpan.FromMilliseconds(200 << attempt);
                await Task.Delay(wait < Options.MaxRetryWait ? wait : Options.MaxRetryWait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string StoreKey(string fingerprint, GalaxyRequestKind kind, ReadOnlySpan<byte> body)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(fingerprint));
        hash.AppendData([GalaxyProtocol.Version, (byte)kind]);
        hash.AppendData(body);
        return Convert.ToHexString(hash.GetHashAndReset())[..40];
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsTransport) await _transport.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A galaxy on a GalGen server. Get one from <see cref="GalaxyClient.OpenAsync"/>.</summary>
public sealed class RemoteGalaxy : IGalaxy
{
    private readonly GalaxyClient _client;
    private readonly LruCache<ChunkCoord, StarChunk> _chunks;
    private readonly LruCache<StarId, StarSystem> _systems;
    private readonly LruCache<MapRequest, DensityImage> _maps;
    private readonly ConcurrentDictionary<ChunkCoord, Task<StarChunk>> _chunksInFlight = new();
    private readonly ConcurrentDictionary<object, Lazy<Task<object?>>> _inFlight = new();
    private IReadOnlyList<GalaxyCluster>? _clusters;

    public GalaxyInfo Info { get; }

    internal RemoteGalaxy(GalaxyClient client, GalaxyInfo info)
    {
        _client = client;
        Info = info;
        _chunks = new LruCache<ChunkCoord, StarChunk>(client.Options.ChunkCacheCapacity);
        _systems = new LruCache<StarId, StarSystem>(client.Options.SystemCacheCapacity);
        _maps = new LruCache<MapRequest, DensityImage>(client.Options.MapCacheCapacity);
    }

    private ValueTask<WireReader> Call(GalaxyRequestKind kind, Action<WireWriter> body, CancellationToken cancellationToken) =>
        _client.CallAsync(kind, w => { w.Str(Info.Id); body(w); }, Info.Fingerprint, cancellationToken);

    /// One request for any number of callers asking the same thing at once.
    private async Task<T> Shared<T>(object key, Func<Task<T>> fetch)
    {
        var shared = _inFlight.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () => await fetch().ConfigureAwait(false)));
        try { return (T)(await shared.Value.ConfigureAwait(false))!; }
        finally { _inFlight.TryRemove(new KeyValuePair<object, Lazy<Task<object?>>>(key, shared)); }
    }

    public async ValueTask<DensityImage> GetMapAsync(MapRequest request, CancellationToken cancellationToken = default)
    {
        if (request.View != MapView.Region) request = request with { Region = RegionF.Full };
        if (_maps.TryGet(request, out DensityImage cached)) return cached;
        DensityImage map = await Shared(("map", request), async () =>
        {
            WireReader r = await Call(GalaxyRequestKind.Map, w => Codec.Write(w, request), cancellationToken).ConfigureAwait(false);
            return Codec.ReadImage(r);
        }).ConfigureAwait(false);
        _maps.Set(request, map);
        return map;
    }

    public async ValueTask<IReadOnlyList<StarChunk>> GetChunksAsync(IReadOnlyList<ChunkCoord> chunks, CancellationToken cancellationToken = default)
    {
        var result = new StarChunk[chunks.Count];
        var waiting = new List<(int Index, Task<StarChunk> Chunk)>();
        var fetching = new Dictionary<ChunkCoord, TaskCompletionSource<StarChunk>>();
        for (int i = 0; i < chunks.Count; i++)
        {
            ChunkCoord coord = chunks[i];
            if (_chunks.TryGet(coord, out StarChunk cached)) { result[i] = cached; continue; }
            if (fetching.TryGetValue(coord, out var ours)) { waiting.Add((i, ours.Task)); continue; }
            var mine = new TaskCompletionSource<StarChunk>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<StarChunk> chunk = _chunksInFlight.GetOrAdd(coord, mine.Task);
            if (chunk == mine.Task) fetching[coord] = mine;
            waiting.Add((i, chunk));
        }

        if (fetching.Count > 0)
        {
            int batch = (await _client.HelloAsync(cancellationToken).ConfigureAwait(false)).MaxChunksPerRequest;
            var batches = fetching.Keys.Chunk(Math.Max(batch, 1)).Select(coords => Fetch(coords, fetching, cancellationToken));
            await Task.WhenAll(batches).ConfigureAwait(false);
        }
        foreach (var (index, chunk) in waiting)
            result[index] = await chunk.ConfigureAwait(false);
        return result;
    }

    private async Task Fetch(ChunkCoord[] coords, Dictionary<ChunkCoord, TaskCompletionSource<StarChunk>> fetching, CancellationToken cancellationToken)
    {
        try
        {
            WireReader r = await Call(GalaxyRequestKind.Chunks, w =>
            {
                w.VarU((ulong)coords.Length);
                foreach (ChunkCoord c in coords) Codec.Write(w, c);
            }, cancellationToken).ConfigureAwait(false);
            int count = r.Count(6);
            if (count != coords.Length) throw new GalaxyServiceException(GalaxyError.BadRequest, "The server answered for different chunks.");
            for (int i = 0; i < count; i++)
            {
                StarChunk chunk = Codec.ReadChunk(r);
                _chunks.Set(coords[i], chunk);
                fetching[coords[i]].TrySetResult(chunk);
            }
        }
        catch (Exception e)
        {
            foreach (ChunkCoord c in coords) fetching[c].TrySetException(e);
        }
        finally
        {
            foreach (ChunkCoord c in coords) _chunksInFlight.TryRemove(new KeyValuePair<ChunkCoord, Task<StarChunk>>(c, fetching[c].Task));
        }
    }

    public async ValueTask<StarQueryResult> FindStarsAsync(StarQuery query, CancellationToken cancellationToken = default)
    {
        WireReader r = await Call(GalaxyRequestKind.FindStars, w => Codec.Write(w, query), cancellationToken).ConfigureAwait(false);
        return Codec.ReadQueryResult(r);
    }

    public async ValueTask<StarRef?> GetStarAsync(StarId id, CancellationToken cancellationToken = default)
    {
        WireReader r = await Call(GalaxyRequestKind.Star, w => Codec.Write(w, id), cancellationToken).ConfigureAwait(false);
        return r.Bool() ? Codec.ReadStarRef(r) : null;
    }

    public async ValueTask<StarSystem?> GetSystemAsync(StarId id, CancellationToken cancellationToken = default)
    {
        if (_systems.TryGet(id, out StarSystem cached)) return cached;
        StarSystem? system = await Shared(("system", id), async () =>
        {
            WireReader r = await Call(GalaxyRequestKind.System, w => Codec.Write(w, id), cancellationToken).ConfigureAwait(false);
            return r.Bool() ? Codec.ReadSystem(r) : null;
        }).ConfigureAwait(false);
        if (system != null) _systems.Set(id, system);
        return system;
    }

    public async ValueTask<IReadOnlyList<Moon>> GetMoonsAsync(StarId id, int bodyIndex, CancellationToken cancellationToken = default)
    {
        WireReader r = await Call(GalaxyRequestKind.Moons, w => { Codec.Write(w, id); w.VarI(bodyIndex); }, cancellationToken).ConfigureAwait(false);
        var moons = new Moon[r.Count(14)];
        for (int i = 0; i < moons.Length; i++) moons[i] = Codec.ReadMoon(r);
        return moons;
    }

    public async ValueTask<LocationInfo> DescribeAsync(Vector3 position, CancellationToken cancellationToken = default)
    {
        WireReader r = await Call(GalaxyRequestKind.Describe, w => w.Vec3(position), cancellationToken).ConfigureAwait(false);
        return Codec.ReadLocation(r);
    }

    public async ValueTask<StarRef?> PickStarAsync(StarPick pick, CancellationToken cancellationToken = default)
    {
        WireReader r = await Call(GalaxyRequestKind.Pick, w => Codec.Write(w, pick), cancellationToken).ConfigureAwait(false);
        return r.Bool() ? Codec.ReadStarRef(r) : null;
    }

    public async ValueTask<IReadOnlyList<GalaxyCluster>> GetClustersAsync(CancellationToken cancellationToken = default)
    {
        if (_clusters != null) return _clusters;
        WireReader r = await Call(GalaxyRequestKind.Clusters, _ => { }, cancellationToken).ConfigureAwait(false);
        var clusters = new GalaxyCluster[r.Count(23)];
        for (int i = 0; i < clusters.Length; i++) clusters[i] = Codec.ReadCluster(r);
        return _clusters = clusters;
    }
}
