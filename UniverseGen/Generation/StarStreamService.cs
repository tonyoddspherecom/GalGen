using System.Numerics;
using UniverseGen.Models;

namespace UniverseGen.Generation;

/// <summary>
/// Background pre-fetch service.  Call <see cref="NotifyCameraPosition"/> whenever
/// the camera moves to a new chunk; the service pre-fetches a shell of surrounding
/// chunks on the thread-pool, firing <see cref="ChunkReady"/> for each one that
/// completes.  Chunks already cached are skipped silently.
///
/// The renderer should re-draw whenever <see cref="ChunkReady"/> fires.
/// </summary>
public sealed class StarStreamService : IDisposable
{
    private readonly StarChunkCache _cache;
    private readonly int            _radiusXZ;
    private readonly int            _radiusY;
    private ChunkCoord              _lastCenter;
    private CancellationTokenSource _cts = new();

    /// Fired on a thread-pool thread when a new chunk finishes generating.
    public event Action<StarChunk>? ChunkReady;

    public StarStreamService(StarChunkCache cache, int radiusXZ = 15, int radiusY = 5)
    {
        _cache     = cache;
        _radiusXZ  = radiusXZ;
        _radiusY   = radiusY;
        _lastCenter = new ChunkCoord(long.MaxValue, 0, 0); // force first fetch
    }

    /// Call this every time the camera position changes (or on a timer).
    /// Re-issues a background sweep if the camera has moved to a different chunk.
    public void NotifyCameraPosition(Vector3 worldPos)
    {
        var center = _cache.WorldToChunk(worldPos);
        if (center == _lastCenter) return;
        _lastCenter = center;

        // Cancel any in-flight sweep and start fresh
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token  = _cts.Token;

        Task.Run(() => Sweep(center, token), token);
    }

    /// Synchronously yields all cached chunks within <paramref name="radius"/> chunks
    /// of <paramref name="worldPos"/>.  Use this to collect what's available right now
    /// for a render frame.
    public IEnumerable<StarChunk> GetVisibleChunks(Vector3 worldPos, int radiusXZ = -1, int radiusY = -1)
    {
        if (radiusXZ < 0) radiusXZ = _radiusXZ;
        if (radiusY  < 0) radiusY  = _radiusY;

        var center = _cache.WorldToChunk(worldPos);
        for (int dx = -radiusXZ; dx <= radiusXZ; dx++)
        for (int dy = -radiusY;  dy <= radiusY;  dy++)
        for (int dz = -radiusXZ; dz <= radiusXZ; dz++)
        {
            var coord = new ChunkCoord(center.X + dx, center.Y + dy, center.Z + dz);
            if (_cache.TryGet(coord, out var chunk) && chunk != null)
                yield return chunk;
        }
    }

    // ── background sweep ─────────────────────────────────────────────────────

    private void Sweep(ChunkCoord center, CancellationToken token)
    {
        // Visit chunks in order of distance from centre so the nearest ones
        // appear first, giving the renderer something useful immediately.
        for (int r = 0; r <= Math.Max(_radiusXZ, _radiusY); r++)
        {
            for (int dx = -_radiusXZ; dx <= _radiusXZ; dx++)
            for (int dy = -_radiusY;  dy <= _radiusY;  dy++)
            for (int dz = -_radiusXZ; dz <= _radiusXZ; dz++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
                if (dist != r) continue;

                if (token.IsCancellationRequested) return;

                var coord = new ChunkCoord(center.X + dx, center.Y + dy, center.Z + dz);
                // Use Peek (not TryGet) so prefetch probes don't inflate hit
                // counters or disturb LRU order for chunks we aren't rendering yet.
                if (!_cache.Peek(coord))
                {
                    var chunk = _cache.GetOrGenerate(coord);
                    ChunkReady?.Invoke(chunk);
                }
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
