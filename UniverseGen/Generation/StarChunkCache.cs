using System.Collections.Concurrent;
using System.Diagnostics;
using UniverseGen.Models;

namespace UniverseGen.Generation;

// ── stats snapshot ────────────────────────────────────────────────────────────

/// <summary>
/// Point-in-time snapshot of <see cref="StarChunkCache"/> health.
/// All counts are monotonically increasing since construction (or the last
/// <see cref="StarChunkCache.ResetStats"/> call) except the live values
/// <see cref="Count"/>, <see cref="FillPct"/>, and <see cref="TotalStarsCached"/>,
/// which always reflect current cache contents.
/// </summary>
/// <param name="Count">Chunks currently held in the cache.</param>
/// <param name="Capacity">Maximum chunks before LRU eviction begins.</param>
/// <param name="FillPct">Percentage of capacity in use (0–100).</param>
/// <param name="Hits">Lookups satisfied from cache.</param>
/// <param name="Misses">Lookups that required generation.</param>
/// <param name="Evictions">Chunks silently dropped to make room.</param>
/// <param name="HitRate">Hits / (Hits + Misses) as a fraction [0, 1].  NaN when no lookups recorded.</param>
/// <param name="Generations">Total chunks generated (equals Misses minus any TryAdd races).</param>
/// <param name="AvgGenerationMs">Mean time per generation in milliseconds.  NaN when no generations recorded.</param>
/// <param name="TotalStarsCached">Sum of star counts across all currently-held chunks — a rough memory footprint proxy.</param>
public sealed record CacheStats(
    int   Count,
    int   Capacity,
    float FillPct,
    long  Hits,
    long  Misses,
    long  Evictions,
    float HitRate,
    long  Generations,
    float AvgGenerationMs,
    long  TotalStarsCached)
{
    /// <summary>Formatted summary suitable for a debug overlay or log line.</summary>
    public override string ToString() =>
        $"Cache {Count}/{Capacity} ({FillPct:F1}%)  " +
        $"hit={HitRate:P1}  H/M/E={Hits}/{Misses}/{Evictions}  " +
        $"gen={Generations} ({AvgGenerationMs:F1} ms avg)  " +
        $"stars={TotalStarsCached:N0}";
}

// ── cache ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Thread-safe LRU cache for generated star chunks.
/// Game clients hold one instance per galaxy; chunks are generated on first access
/// and evicted when the cache exceeds <see cref="Capacity"/>.
///
/// <para>Call <see cref="GetStats"/> at any time for a point-in-time health snapshot.
/// Use <see cref="TrimTo"/> to shed chunks under memory pressure without discarding
/// the entire cache.  <see cref="Clear"/> fully flushes contents but preserves stats;
/// combine with <see cref="ResetStats"/> for a clean-slate benchmark.</para>
/// </summary>
public sealed class StarChunkCache
{
    private readonly StarGenerator                            _gen;
    private readonly GalaxyParams                            _parms;
    private readonly ConcurrentDictionary<ChunkCoord, Node> _map  = new();
    private readonly object                                  _lock = new();
    private Node? _head, _tail;
    private int   _count;

    // ── stat counters (all mutated via Interlocked) ───────────────────────────
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _generations;
    private long _totalGenerationTicks; // Stopwatch ticks — converted to ms in GetStats

    public int Capacity { get; }

    public StarChunkCache(GalaxyParams parms, StarGenerator? generator = null, int capacity = 4096)
    {
        _parms   = parms;
        _gen     = generator ?? new StarGenerator();
        Capacity = capacity;
    }

    public float ChunkSize => _gen.ChunkSize;

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the chunk at <paramref name="coord"/>, generating it synchronously
    /// if not already cached.  Safe to call from multiple threads simultaneously.
    /// </summary>
    public StarChunk GetOrGenerate(ChunkCoord coord)
    {
        if (_map.TryGetValue(coord, out var existing))
        {
            Touch(existing);
            Interlocked.Increment(ref _hits);
            return existing.Chunk;
        }

        Interlocked.Increment(ref _misses);

        var sw    = Stopwatch.GetTimestamp();
        var chunk = _gen.Generate(_parms, coord);
        Interlocked.Add(ref _totalGenerationTicks, Stopwatch.GetTimestamp() - sw);
        Interlocked.Increment(ref _generations);

        var node = new Node(coord, chunk);
        if (_map.TryAdd(coord, node))
        {
            lock (_lock)
            {
                Prepend(node);
                _count++;
                if (_count > Capacity) Evict();
            }
        }

        return _map.TryGetValue(coord, out var final) ? final.Chunk : chunk;
    }

    /// <summary>
    /// Returns the cached chunk without generating.
    /// Promotes the node to MRU position on a hit, just like <see cref="GetOrGenerate"/>.
    /// Returns <see langword="false"/> when the chunk is not cached.
    /// </summary>
    public bool TryGet(ChunkCoord coord, out StarChunk? chunk)
    {
        if (_map.TryGetValue(coord, out var node))
        {
            Touch(node);
            Interlocked.Increment(ref _hits);
            chunk = node.Chunk;
            return true;
        }
        chunk = null;
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the chunk is cached, without altering
    /// LRU order or incrementing any counter.  Useful in prefetch logic to avoid
    /// re-queuing chunks that are already present.
    /// </summary>
    public bool Peek(ChunkCoord coord) => _map.ContainsKey(coord);

    /// <summary>
    /// Evicts chunks from the LRU tail until <see cref="Count"/> ≤
    /// <paramref name="targetCount"/>.  A no-op when already within budget.
    /// Useful for responding to OS memory-pressure notifications.
    /// </summary>
    public void TrimTo(int targetCount)
    {
        if (targetCount < 0) throw new ArgumentOutOfRangeException(nameof(targetCount));
        lock (_lock)
        {
            while (_count > targetCount)
                Evict();
        }
    }

    /// <summary>
    /// Removes all cached chunks.  Stats counters are preserved; call
    /// <see cref="ResetStats"/> too for a clean-slate benchmark.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _head = _tail = null;
            _count = 0;
        }
    }

    /// <summary>
    /// Resets all hit/miss/eviction/generation counters to zero.
    /// Does not affect cache contents — use <see cref="Clear"/> for that.
    /// </summary>
    public void ResetStats()
    {
        Interlocked.Exchange(ref _hits,                0);
        Interlocked.Exchange(ref _misses,              0);
        Interlocked.Exchange(ref _evictions,           0);
        Interlocked.Exchange(ref _generations,         0);
        Interlocked.Exchange(ref _totalGenerationTicks, 0);
    }

    /// <summary>
    /// Returns a point-in-time <see cref="CacheStats"/> snapshot.
    /// Iterates current contents once to compute <see cref="CacheStats.TotalStarsCached"/>;
    /// call infrequently (e.g. once per second for a debug overlay).
    /// </summary>
    public CacheStats GetStats()
    {
        // Read _count under lock so it's consistent with the live list length.
        int   count;
        lock (_lock) count = _count;

        long hits         = Interlocked.Read(ref _hits);
        long misses       = Interlocked.Read(ref _misses);
        long evictions    = Interlocked.Read(ref _evictions);
        long generations  = Interlocked.Read(ref _generations);
        long totalTicks   = Interlocked.Read(ref _totalGenerationTicks);

        float hitRate     = (hits + misses) > 0
            ? hits / (float)(hits + misses)
            : float.NaN;

        float avgGenMs    = generations > 0
            ? (float)(totalTicks / (double)Stopwatch.Frequency * 1000.0 / generations)
            : float.NaN;

        // Walk the dictionary once for the star count — acceptable for a stats call.
        long totalStars = 0;
        foreach (var node in _map.Values)
            totalStars += node.Chunk.Stars.Length;

        return new CacheStats(
            Count:            count,
            Capacity:         Capacity,
            FillPct:          count / (float)Capacity * 100f,
            Hits:             hits,
            Misses:           misses,
            Evictions:        evictions,
            HitRate:          hitRate,
            Generations:      generations,
            AvgGenerationMs:  avgGenMs,
            TotalStarsCached: totalStars);
    }

    /// <summary>Converts a world-space position to the chunk that contains it.</summary>
    public ChunkCoord WorldToChunk(System.Numerics.Vector3 pos) => _gen.WorldToChunk(pos);

    /// <summary>Number of chunks currently held.</summary>
    public int Count { get { lock (_lock) return _count; } }

    // ── LRU internals ─────────────────────────────────────────────────────────

    private void Touch(Node n)
    {
        lock (_lock)
        {
            if (n == _head) return;
            Unlink(n);
            Prepend(n);
        }
    }

    private void Prepend(Node n)
    {
        n.Next = _head; n.Prev = null;
        if (_head != null) _head.Prev = n;
        _head = n;
        _tail ??= n;
    }

    private void Unlink(Node n)
    {
        if (n.Prev != null) n.Prev.Next = n.Next; else _head = n.Next;
        if (n.Next != null) n.Next.Prev = n.Prev; else _tail = n.Prev;
    }

    private void Evict()
    {
        // Must be called under _lock.
        var victim = _tail;
        if (victim == null) return;
        Unlink(victim);
        _map.TryRemove(victim.Key, out _);
        _count--;
        Interlocked.Increment(ref _evictions);
    }

    private sealed class Node(ChunkCoord key, StarChunk chunk)
    {
        public readonly ChunkCoord Key   = key;
        public readonly StarChunk  Chunk = chunk;
        public Node? Prev, Next;
    }
}
