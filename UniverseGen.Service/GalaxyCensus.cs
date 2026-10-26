using UniverseGen.Generation;
using UniverseGen.Models;

namespace UniverseGen.Service;

/// <summary>
/// How many stars a galaxy has: an estimate in a fraction of a second at any size, or an exact count,
/// chunk by chunk, for galaxies small enough to count. For tuning a galaxy to a size ("a few million
/// stars"), and for showing players how big it is.
/// </summary>
public static class GalaxyCensus
{
    /// <summary>
    /// About how many stars the galaxy has, from its density integrated over a grid of columns
    /// (<paramref name="resolution"/>² of them). Usually within a few percent of the true count.
    /// </summary>
    public static long Estimate(GalaxyParams parms, float chunkSize, int maxStarsPerChunk, int resolution = 160)
    {
        var math = GalaxyMath.For(parms);
        float reach = Reach(math);
        int layers = (int)MathF.Ceiling(math.VerticalExtent() / chunkSize);
        float cell = 2f * reach / resolution;
        // Each grid column stands for (cell / chunkSize)² columns of chunks.
        double columnsPerCell = (double)cell / chunkSize * cell / chunkSize;
        double total = 0;
        object gate = new();
        Parallel.For(0, resolution, () => 0.0, (i, _, sum) =>
        {
            float x = -reach + (i + 0.5f) * cell;
            for (int j = 0; j < resolution; j++)
            {
                float z = -reach + (j + 0.5f) * cell;
                sum += Column(math, x, z, chunkSize, layers, maxStarsPerChunk) * columnsPerCell;
            }
            return sum;
        }, sum => { lock (gate) total += sum; });
        return (long)Math.Round(total);
    }

    /// <summary>
    /// Exactly how many stars the galaxy has, counted chunk by chunk without making them. Refuses a
    /// galaxy of more than <paramref name="maxChunks"/> chunks (a Milky Way has tens of billions):
    /// use <see cref="Estimate"/> for those.
    /// </summary>
    public static long Count(GalaxyParams parms, float chunkSize, int maxStarsPerChunk,
                             long maxChunks = 200_000_000, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var math = GalaxyMath.For(parms);
        long half = (long)MathF.Ceiling(Reach(math) / chunkSize);
        long layers = (long)MathF.Ceiling(math.VerticalExtent() / chunkSize);
        long side = 2 * half + 1;
        long chunks = side * side * (2 * layers + 1);
        if (chunks > maxChunks)
            throw new InvalidOperationException($"The galaxy has {chunks:N0} chunks to count, over the {maxChunks:N0} allowed: estimate it instead.");

        long total = 0, done = 0;
        Parallel.For(-half, half + 1, new ParallelOptions { CancellationToken = cancellationToken }, () => 0L, (x, _, sum) =>
        {
            for (long z = -half; z <= half; z++)
                for (long y = -layers; y <= layers; y++)
                    sum += math.StarCount(x, y, z, chunkSize, maxStarsPerChunk);
            if (progress != null) progress.Report((double)Interlocked.Increment(ref done) / side);
            return sum;
        }, sum => Interlocked.Add(ref total, sum));
        return total;
    }

    /// How far from the centre (normalised) any star can be: the disk's edge, or the bulge's, if that's further.
    private static float Reach(GalaxyMath math) => MathF.Max(1f, math.VerticalExtent());

    /// <summary>
    /// The stars expected in one column of chunks, as each chunk would count them. The layers near the
    /// plane, where a thin disk changes from one chunk to the next, are each counted; further out, where
    /// only the smooth bulge and halo reach, a sample of layers stands for the rest.
    /// </summary>
    private static double Column(GalaxyMath math, float x, float z, float chunkSize, int layers, int maxStars)
    {
        float plane = math.SampleDensity(x, z);
        float perDensity = math.StarsPerDensity(maxStars);
        int capacity = math.Capacity(maxStars);
        double Expected(long k)
        {
            float d = math.ChunkDensity(plane, x, k * chunkSize, z);
            if (d < math.Empty) return 0;
            float expected = d * perDensity;
            return math.RoundsToNearest ? Math.Min((int)(expected + 0.5f), capacity) : Math.Min(expected, capacity);
        }

        int near = Math.Min(layers, 64);
        double sum = 0;
        for (int k = -near; k <= near; k++) sum += Expected(k);
        if (layers > near)
        {
            int stride = Math.Max(1, (layers - near + 95) / 96);
            for (int k = near + 1; k <= layers; k += stride)
            {
                int covers = Math.Min(stride, layers - k + 1);
                sum += (Expected(k) + Expected(-k)) * covers;
            }
        }
        return sum;
    }
}
