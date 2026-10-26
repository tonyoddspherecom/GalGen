using System.Numerics;
using UniverseGen.Models;

namespace UniverseGen.Generation;

/// <summary>
/// Point queries on a galaxy's shape: its density and stellar environment anywhere, and how far
/// from the plane it has any stars. Cheap (no star generation), immutable and thread-safe, so it
/// suits pathing, placement and culling as well as servers.
/// Coordinates are galaxy normalised space: X and Z in the plane, Y vertical, the galaxy in [-1, 1].
/// </summary>
public sealed class GalaxySampler
{
    private readonly GalaxyMath _math;

    /// A copy of the params it was built from: changing the original afterwards doesn't change it.
    public GalaxyParams Params { get; }

    public GalaxySampler(GalaxyParams parms)
    {
        Params = parms.Clone();
        _math  = GalaxyMath.For(Params);
    }

    /// Face-on density (0–1) at a point in the plane.
    public float DensityAt(float x, float z) => _math.SampleDensity(x, z);

    /// The stellar environment at a point: arm, inter-arm, bulge, halo, nursery or void.
    public RegionType RegionAt(Vector3 position) => _math.ClassifyRegion(position.X, position.Y, position.Z);

    /// The disk's scale height, in normalised units.
    public float DiskHeight => _math.DiskHeight;

    /// How far above or below the plane (normalised units) any star can be. Conservative: beyond it,
    /// every chunk is empty.
    public float VerticalExtent => _math.VerticalExtent();

    /// The most stars one chunk can hold, for sizing buffers.
    public int ChunkCapacity(int maxStarsPerChunk) => _math.Capacity(maxStarsPerChunk);

    /// The 3-D density at a point (before StarDensity): what decides how many stars a chunk centred there has.
    public float DensityAt(Vector3 position) => _math.DensityAt(position.X, position.Y, position.Z);
}
