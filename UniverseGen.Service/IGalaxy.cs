using System.Numerics;
using UniverseGen.Models;

namespace UniverseGen.Service;

/// <summary>
/// One galaxy's data: its maps, stars, systems and places. The same interface whether the galaxy is
/// generated right here (<see cref="LocalGalaxy"/>) or lives on a server (<see cref="GalaxyClient"/>),
/// so a game can work against either, switch between them, or test against a local one.
///
/// Everything a galaxy returns is a pure function of its <see cref="GalaxyInfo.Fingerprint"/> and the
/// request, so any of it can be cached for as long as the fingerprint holds.
/// All methods are safe to call from any number of threads at once.
/// </summary>
public interface IGalaxy
{
    GalaxyInfo Info { get; }

    /// A density map: face-on, zoomed into a region, or edge-on.
    ValueTask<DensityImage> GetMapAsync(MapRequest request, CancellationToken cancellationToken = default);

    /// The stars in each chunk asked for, in the same order.
    ValueTask<IReadOnlyList<StarChunk>> GetChunksAsync(IReadOnlyList<ChunkCoord> chunks, CancellationToken cancellationToken = default);

    /// The stars in a sphere or box of space that pass a filter.
    ValueTask<StarQueryResult> FindStarsAsync(StarQuery query, CancellationToken cancellationToken = default);

    /// One star, or null if there's no star at that id.
    ValueTask<StarRef?> GetStarAsync(StarId id, CancellationToken cancellationToken = default);

    /// A star's system (its companions, planets and belts), or null if there's no star at that id.
    ValueTask<StarSystem?> GetSystemAsync(StarId id, CancellationToken cancellationToken = default);

    /// A planet's moons, by the planet's index in its system's <see cref="StarSystem.Bodies"/>.
    ValueTask<IReadOnlyList<Moon>> GetMoonsAsync(StarId id, int bodyIndex, CancellationToken cancellationToken = default);

    /// What's at a point: its environment, its region's names, and any cluster or nebula.
    ValueTask<LocationInfo> DescribeAsync(Vector3 position, CancellationToken cancellationToken = default);

    /// A random star that's the same every time for the same pick, or null if none was found.
    ValueTask<StarRef?> PickStarAsync(StarPick pick, CancellationToken cancellationToken = default);

    /// The galaxy's named nebulae and clusters.
    ValueTask<IReadOnlyList<GalaxyCluster>> GetClustersAsync(CancellationToken cancellationToken = default);
}

/// <summary>A set of galaxies by id: everything a host serves, or everything a server offers a client.</summary>
public interface IGalaxyCatalog
{
    ValueTask<IReadOnlyList<GalaxyInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// A galaxy by id. Throws <see cref="GalaxyServiceException"/> (<see cref="GalaxyError.UnknownGalaxy"/>) if there's none.
    ValueTask<IGalaxy> OpenAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Why a request failed. Sent over the wire as a number, so values never change meaning.</summary>
public enum GalaxyError : ushort
{
    /// Something went wrong that isn't the caller's fault.
    Internal = 1,
    /// The request couldn't be read, or asked for something impossible.
    BadRequest = 2,
    /// No galaxy has that id.
    UnknownGalaxy = 3,
    /// The request is bigger than the server allows (see <see cref="ServerHello"/>).
    LimitExceeded = 4,
    /// Too much, too fast: try again after <see cref="GalaxyServiceException.RetryAfter"/>.
    RateLimited = 5,
    /// The two ends speak different versions of the protocol.
    VersionMismatch = 6,
    /// The server doesn't do that.
    Unsupported = 7,
    /// The server is busy or shutting down.
    Unavailable = 8,
}

/// <summary>A request a galaxy, a server or the connection to it couldn't answer.</summary>
public sealed class GalaxyServiceException : Exception
{
    public GalaxyError Error { get; }
    /// For <see cref="GalaxyError.RateLimited"/>: how long to wait before trying again.
    public TimeSpan RetryAfter { get; }

    public GalaxyServiceException(GalaxyError error, string message, TimeSpan retryAfter = default, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
        RetryAfter = retryAfter;
    }
}
