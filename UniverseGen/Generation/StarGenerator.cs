using UniverseGen.Interop;
using UniverseGen.Models;

namespace UniverseGen.Generation;

public sealed class StarGenerator
{
    /// Side length of each chunk cube in galaxy normalised units.
    /// 0.0002 = 10 ly/side at the 50,000 ly/unit scale — calibrated for
    /// realistic solar-neighbourhood stellar density (~0.14 stars/ly³).
    public float ChunkSize { get; init; } = 0.0002f;

    /// Stars generated at density = 1.0 (galactic centre peak).
    /// 200 stars in a 10 ly cube = 0.2/ly³ — close to real arm density.
    public int MaxStarsPerChunk { get; init; } = 200;

    /// Generate all stars for the given chunk.
    public StarChunk Generate(GalaxyParams parms, ChunkCoord coord)
    {
        var mp  = GalaxyMathParams.From(parms);
        var raw = new StarDataInterop[MaxStarsPerChunk];

        int count = MathDll.GenerateStarChunk(
            ref mp,
            coord.X, coord.Y, coord.Z,
            ChunkSize, MaxStarsPerChunk, raw);

        var stars = new Star[count];
        for (int i = 0; i < count; i++)
        {
            stars[i] = new Star(
                raw[i].X, raw[i].Y, raw[i].Z,
                (SpectralClass)raw[i].SpectralClass,
                raw[i].Luminosity,
                (StarFlags)raw[i].Flags);
        }

        var regionByte = MathDll.GetChunkRegionType(ref mp, coord.X, coord.Y, coord.Z, ChunkSize);
        var region     = (RegionType)regionByte;

        float density = MaxStarsPerChunk > 0 ? (float)count / MaxStarsPerChunk : 0f;
        return new StarChunk(coord, stars, density, region);
    }

    /// Converts a galaxy-normalised position to the chunk that contains it.
    public ChunkCoord WorldToChunk(System.Numerics.Vector3 pos) =>
        new((long)MathF.Floor(pos.X / ChunkSize),
            (long)MathF.Floor(pos.Y / ChunkSize),
            (long)MathF.Floor(pos.Z / ChunkSize));
}
