using UniverseGen.Models;

namespace UniverseGen.Generation;

public sealed class StarGenerator
{
    /// A chunk's side in light years, at any galaxy size: see <see cref="For"/>.
    public const float DefaultChunkLightYears = 10f;

    /// Side length of each chunk cube in galaxy normalised units.
    /// 0.0002 = 10 ly/side at the default 50,000 ly radius — calibrated for
    /// realistic solar-neighbourhood stellar density (~0.14 stars/ly³).
    public float ChunkSize { get; init; } = 0.0002f;

    /// Stars generated at density = 1.0 (galactic centre peak), before
    /// <see cref="GalaxyParams.StarDensity"/>.
    /// 200 stars in a 10 ly cube = 0.2/ly³ — close to real arm density.
    public int MaxStarsPerChunk { get; init; } = 200;

    // The last galaxy's maths (its noise tables), reused for as long as its params don't change.
    private GalaxyMath? _math;

    /// <summary>
    /// A generator whose chunks are <paramref name="chunkLightYears"/> on a side in this galaxy, at
    /// any <see cref="GalaxyParams.RadiusLightYears"/>, so star density per cubic light year stays the
    /// same however big the galaxy is. At the default radius it's the same as <c>new StarGenerator()</c>.
    /// </summary>
    public static StarGenerator For(GalaxyParams parms, float chunkLightYears = DefaultChunkLightYears) =>
        new() { ChunkSize = chunkLightYears / parms.RadiusLightYears };

    /// Generate all stars for the given chunk. Safe to call from any number of threads.
    public StarChunk Generate(GalaxyParams parms, ChunkCoord coord)
    {
        var stars = MathFor(parms).StarChunk(coord.X, coord.Y, coord.Z, ChunkSize, MaxStarsPerChunk, out var region);
        float density = MaxStarsPerChunk > 0 ? (float)stars.Length / MaxStarsPerChunk : 0f;
        return new StarChunk(coord, stars, density, region);
    }

    /// Converts a galaxy-normalised position to the chunk that contains it. Chunk n is centred on
    /// n × ChunkSize, so it covers n × ChunkSize ± half a chunk.
    public ChunkCoord WorldToChunk(System.Numerics.Vector3 pos) =>
        new((long)MathF.Floor(pos.X / ChunkSize + 0.5f),
            (long)MathF.Floor(pos.Y / ChunkSize + 0.5f),
            (long)MathF.Floor(pos.Z / ChunkSize + 0.5f));

    private GalaxyMath MathFor(GalaxyParams parms)
    {
        var key  = GalaxyMathParams.From(parms);
        var math = Volatile.Read(ref _math);
        if (math == null || math.P != key)
        {
            math = new GalaxyMath(key);
            Volatile.Write(ref _math, math);
        }
        return math;
    }
}
