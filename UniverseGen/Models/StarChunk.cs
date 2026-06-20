using System.Numerics;

namespace UniverseGen.Models;

/// <summary>
/// Integer address of a cubic chunk in galaxy space.
/// The chunk at (X,Y,Z) is centred on (X*chunkSize, Y*chunkSize, Z*chunkSize)
/// in galaxy normalised coordinates.
/// </summary>
public readonly struct ChunkCoord(long x, long y, long z) : IEquatable<ChunkCoord>
{
    public long X { get; } = x;
    public long Y { get; } = y;
    public long Z { get; } = z;

    public Vector3 Centre(float chunkSize) =>
        new(X * chunkSize, Y * chunkSize, Z * chunkSize);

    public bool Equals(ChunkCoord other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object? obj) => obj is ChunkCoord c && Equals(c);
    public override int  GetHashCode()       => HashCode.Combine(X, Y, Z);
    public static bool operator ==(ChunkCoord a, ChunkCoord b) => a.Equals(b);
    public static bool operator !=(ChunkCoord a, ChunkCoord b) => !a.Equals(b);
    public override string ToString() => $"({X},{Y},{Z})";
}

/// <summary>
/// A generated batch of stars occupying one cubic chunk of galaxy space.
/// </summary>
public sealed class StarChunk
{
    public ChunkCoord Coord      { get; }
    public Star[]     Stars      { get; }
    /// Average density of this chunk (0–1), as sampled by the generator.
    public float      Density    { get; }
    /// Stellar environment classification — drives population mix and naming.
    public RegionType Region     { get; }

    internal StarChunk(ChunkCoord coord, Star[] stars, float density, RegionType region)
    {
        Coord   = coord;
        Stars   = stars;
        Density = density;
        Region  = region;
    }
}
