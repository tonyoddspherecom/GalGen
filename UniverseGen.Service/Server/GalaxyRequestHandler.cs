using System.Diagnostics;
using UniverseGen.Generation;
using UniverseGen.Models;
using UniverseGen.Service.Protocol;

namespace UniverseGen.Service;

/// <summary>How a server answers: its name, what it allows, and how much any one connection may ask of it.</summary>
public sealed record GalaxyServerOptions
{
    /// What the server calls itself in its hello.
    public string Name { get; init; } = "GalGen";
    /// The limits it advertises in its hello. Normally the host's galaxy options.
    public LocalGalaxyOptions Limits { get; init; } = new();
    /// The biggest request frame it reads.
    public int MaxRequestBytes { get; init; } = 1 << 20;
    /// Work a connection may ask for each second, and save up for a burst, in cost units (roughly one
    /// per chunk generated; see the README). 0 means no limit, which suits a server only your own game talks to.
    public double CostPerSecond { get; init; }
    public double CostBurst { get; init; }
    /// Whether anyone may ask for cache and request counts.
    public bool ExposeStats { get; init; } = true;
    /// <summary>
    /// Decides whether a connection may make a request of a galaxy (null for requests not about one: hello,
    /// list, stats of all). Null allows everything. For servers where players only see their own galaxies,
    /// or a list hides some.
    /// </summary>
    public Func<GalaxyConnection, GalaxyRequestKind, string?, bool>? Authorize { get; init; }
}

/// <summary>One client's state on a server: who it is, and what it has used of its rate limit.</summary>
public sealed class GalaxyConnection
{
    private long _requests;

    public string Remote { get; }
    public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;
    /// For the application: a player, an account, a session.
    public object? Tag { get; set; }
    public long Requests => Interlocked.Read(ref _requests);

    internal CostLimiter? Limiter { get; }

    internal GalaxyConnection(string remote, CostLimiter? limiter)
    {
        Remote = remote;
        Limiter = limiter;
    }

    internal void Count() => Interlocked.Increment(ref _requests);
}

/// <summary>A request a server answered, for logs and metrics.</summary>
public sealed record GalaxyRequestLog(
    GalaxyConnection Connection,
    GalaxyRequestKind Kind,
    string? Galaxy,
    TimeSpan Duration,
    int RequestBytes,
    int ResponseBytes,
    GalaxyError? Error,
    string? Message);

/// <summary>
/// Answers GalGen protocol frames from any <see cref="IGalaxyCatalog"/>: usually a <see cref="GalaxyHost"/>,
/// but a <see cref="GalaxyClient"/> works too, which makes a caching relay in front of another server.
/// Transport-free: the TCP server and the HTTP endpoint use it, and so can any channel a game already has.
///
/// Thread-safe: any number of frames, from any number of connections, can be handled at once.
/// </summary>
public sealed class GalaxyRequestHandler
{
    private readonly IGalaxyCatalog _catalog;

    public GalaxyServerOptions Options { get; }

    /// Raised after every request, from the thread that handled it. Keep it quick.
    public event Action<GalaxyRequestLog>? RequestHandled;

    public GalaxyRequestHandler(IGalaxyCatalog catalog, GalaxyServerOptions? options = null)
    {
        _catalog = catalog;
        Options = options ?? new GalaxyServerOptions
        {
            Limits = catalog is GalaxyHost host ? host.DefaultOptions : new LocalGalaxyOptions(),
        };
    }

    /// A new client's state. Keep it for as long as the client is connected, and pass it with each frame.
    public GalaxyConnection Connect(string remote = "") =>
        new(remote, Options.CostPerSecond > 0 ? new CostLimiter(Options.CostPerSecond, Math.Max(Options.CostBurst, Options.CostPerSecond)) : null);

    private ServerHello Hello() => new(
        Options.Name, GalaxyProtocol.Version, GalaxyMath.AlgorithmVersion,
        Options.Limits.MaxChunksPerRequest, Options.Limits.MaxQueryChunks, Options.Limits.MaxResults, Options.Limits.MaxMapPixels);

    /// <summary>
    /// Answers one request frame with one response frame. Never throws for anything the client sent:
    /// every failure becomes an error frame the client can read.
    /// </summary>
    public async ValueTask<byte[]> HandleAsync(GalaxyConnection connection, ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        connection.Count();
        var kind = (GalaxyRequestKind)0;
        uint id = 0;
        string? galaxy = null;
        byte[] response;
        GalaxyError? error = null;
        string? message = null;
        try
        {
            if (request.Length > Options.MaxRequestBytes)
                throw new GalaxyServiceException(GalaxyError.LimitExceeded, $"Requests can't be over {Options.MaxRequestBytes} bytes.");
            var r = new WireReader(request);
            byte version = r.U8();
            kind = (GalaxyRequestKind)r.U8();
            id = r.U32();
            if (version != GalaxyProtocol.Version)
                throw new GalaxyServiceException(GalaxyError.VersionMismatch, $"This server speaks protocol {GalaxyProtocol.Version}, not {version}.");
            if (!Enum.IsDefined(kind))
                throw new GalaxyServiceException(GalaxyError.Unsupported, $"This server doesn't know request kind {(byte)kind}.");
            response = await Answer(connection, kind, id, r, g => galaxy = g, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            (error, message, TimeSpan retry) = e switch
            {
                GalaxyServiceException s => (s.Error, s.Message, s.RetryAfter),
                OperationCanceledException => (GalaxyError.Unavailable, "The request was cancelled: the server may be shutting down.", TimeSpan.Zero),
                ArgumentException a => (GalaxyError.BadRequest, a.Message, TimeSpan.Zero),
                // Not the client's fault, and its details aren't the client's business.
                _ => (GalaxyError.Internal, "The server failed to answer that.", TimeSpan.Zero),
            };
            if (error == GalaxyError.Internal) message = $"{message} ({e.GetType().Name})";
            response = GalaxyProtocol.Error(kind, id, error.Value, message!, retry);
        }
        RequestHandled?.Invoke(new GalaxyRequestLog(connection, kind, galaxy, Stopwatch.GetElapsedTime(started),
            request.Length, response.Length, error, message));
        return response;
    }

    private async ValueTask<byte[]> Answer(GalaxyConnection connection, GalaxyRequestKind kind, uint id, WireReader r,
                                           Action<string> noteGalaxy, CancellationToken ct)
    {
        WireWriter w = GalaxyProtocol.Response(kind, id);
        switch (kind)
        {
            case GalaxyRequestKind.Hello:
                r.Str();   // the client's name, for logs that want it
                r.End();
                Allow(connection, kind, null, 0);
                Codec.Write(w, Hello());
                return w.ToArray();

            case GalaxyRequestKind.List:
            {
                r.End();
                Allow(connection, kind, null, 0);
                var visible = (await _catalog.ListAsync(ct).ConfigureAwait(false))
                    .Where(g => Options.Authorize?.Invoke(connection, kind, g.Id) ?? true).ToList();
                w.VarU((ulong)visible.Count);
                foreach (GalaxyInfo info in visible) Codec.Write(w, info);
                return w.ToArray();
            }

            case GalaxyRequestKind.Stats:
            {
                string only = r.Str();
                r.End();
                if (!Options.ExposeStats) throw new GalaxyServiceException(GalaxyError.Unsupported, "This server doesn't share its stats.");
                Allow(connection, kind, only.Length > 0 ? only : null, 0);
                var stats = (_catalog as GalaxyHost)?.Galaxies.Where(g => only.Length == 0 || g.Info.Id == only).Select(g => g.Stats()).ToList() ?? [];
                w.VarU((ulong)stats.Count);
                foreach (GalaxyStats s in stats) Codec.Write(w, s);
                return w.ToArray();
            }
        }

        // Everything else is about one galaxy.
        string galaxyId = r.Str();
        noteGalaxy(galaxyId);
        // Before opening it, so a relay never fetches what this client may not see.
        Allow(connection, kind, galaxyId, 0);
        IGalaxy galaxy = await _catalog.OpenAsync(galaxyId, ct).ConfigureAwait(false);
        switch (kind)
        {
            case GalaxyRequestKind.Info:
                r.End();
                Allow(connection, kind, galaxyId, 0);
                Codec.Write(w, galaxy.Info);
                break;

            case GalaxyRequestKind.Map:
            {
                MapRequest map = Codec.ReadMapRequest(r);
                r.End();
                Allow(connection, kind, galaxyId, Math.Max(1.0, (double)map.Width * map.Height / 16384));
                Codec.Write(w, await galaxy.GetMapAsync(map, ct).ConfigureAwait(false));
                break;
            }

            case GalaxyRequestKind.Chunks:
            {
                var coords = new ChunkCoord[r.Count(3)];
                for (int i = 0; i < coords.Length; i++) coords[i] = Codec.ReadChunkCoord(r);
                r.End();
                Allow(connection, kind, galaxyId, coords.Length);
                var chunks = await galaxy.GetChunksAsync(coords, ct).ConfigureAwait(false);
                w.VarU((ulong)chunks.Count);
                foreach (StarChunk chunk in chunks) Codec.Write(w, chunk);
                break;
            }

            case GalaxyRequestKind.FindStars:
            {
                StarQuery query = Codec.ReadQuery(r);
                r.End();
                Allow(connection, kind, galaxyId, galaxy is LocalGalaxy local ? Math.Max(1, local.QueryChunkCount(query)) : 1);
                Codec.Write(w, await galaxy.FindStarsAsync(query, ct).ConfigureAwait(false));
                break;
            }

            case GalaxyRequestKind.Star:
            {
                StarId star = Codec.ReadStarId(r);
                r.End();
                Allow(connection, kind, galaxyId, 1);
                StarRef? found = await galaxy.GetStarAsync(star, ct).ConfigureAwait(false);
                w.Bool(found != null);
                if (found != null) Codec.Write(w, found);
                break;
            }

            case GalaxyRequestKind.System:
            {
                StarId star = Codec.ReadStarId(r);
                r.End();
                Allow(connection, kind, galaxyId, 1);
                StarSystem? system = await galaxy.GetSystemAsync(star, ct).ConfigureAwait(false);
                w.Bool(system != null);
                if (system != null) Codec.Write(w, system);
                break;
            }

            case GalaxyRequestKind.Moons:
            {
                StarId star = Codec.ReadStarId(r);
                int body = r.Int();
                r.End();
                Allow(connection, kind, galaxyId, 1);
                var moons = await galaxy.GetMoonsAsync(star, body, ct).ConfigureAwait(false);
                w.VarU((ulong)moons.Count);
                foreach (Moon moon in moons) Codec.Write(w, moon);
                break;
            }

            case GalaxyRequestKind.Describe:
            {
                var position = r.Vec3();
                r.End();
                Allow(connection, kind, galaxyId, 1);
                Codec.Write(w, await galaxy.DescribeAsync(position, ct).ConfigureAwait(false));
                break;
            }

            case GalaxyRequestKind.Pick:
            {
                StarPick pick = Codec.ReadPick(r);
                r.End();
                Allow(connection, kind, galaxyId, Math.Max(1, pick.MaxAttempts));
                StarRef? found = await galaxy.PickStarAsync(pick, ct).ConfigureAwait(false);
                w.Bool(found != null);
                if (found != null) Codec.Write(w, found);
                break;
            }

            case GalaxyRequestKind.Clusters:
            {
                r.End();
                Allow(connection, kind, galaxyId, 1);
                var clusters = await galaxy.GetClustersAsync(ct).ConfigureAwait(false);
                w.VarU((ulong)clusters.Count);
                foreach (GalaxyCluster cluster in clusters) Codec.Write(w, cluster);
                break;
            }

            default:
                throw new GalaxyServiceException(GalaxyError.Unsupported, $"This server doesn't know request kind {kind}.");
        }
        return w.ToArray();
    }

    /// Checks the connection may do this, and charges its rate limit for it.
    private void Allow(GalaxyConnection connection, GalaxyRequestKind kind, string? galaxy, double cost)
    {
        if (Options.Authorize != null && !Options.Authorize(connection, kind, galaxy))
            // As if it weren't there: an unauthorised client can't even learn which ids exist.
            throw galaxy != null
                ? new GalaxyServiceException(GalaxyError.UnknownGalaxy, $"There's no galaxy '{galaxy}'.")
                : new GalaxyServiceException(GalaxyError.Unsupported, $"{kind} isn't allowed.");
        if (cost > 0 && connection.Limiter != null && !connection.Limiter.TryTake(cost, out TimeSpan wait))
            throw new GalaxyServiceException(GalaxyError.RateLimited, "Too many requests: slow down.", wait);
    }
}

/// <summary>A token bucket: spends cost, refills at a steady rate, holds at most a burst.</summary>
internal sealed class CostLimiter
{
    private readonly double _rate, _burst;
    private readonly object _lock = new();
    private double _tokens;
    private long _last;

    public CostLimiter(double ratePerSecond, double burst)
    {
        _rate = ratePerSecond;
        _burst = burst;
        _tokens = burst;
        _last = Stopwatch.GetTimestamp();
    }

    /// Takes the cost if there's enough, or if the bucket is full (so one request bigger than the whole
    /// burst can still run, after a wait). Otherwise says how long until there will be.
    public bool TryTake(double cost, out TimeSpan wait)
    {
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            _tokens = Math.Min(_burst, _tokens + Stopwatch.GetElapsedTime(_last, now).TotalSeconds * _rate);
            _last = now;
            if (_tokens >= cost || _tokens >= _burst)
            {
                _tokens -= cost;
                wait = TimeSpan.Zero;
                return true;
            }
            wait = TimeSpan.FromSeconds((Math.Min(cost, _burst) - _tokens) / _rate);
            return false;
        }
    }
}
