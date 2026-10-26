using UniverseGen.Models;

namespace UniverseGen.Generation;

public class GalaxyGenerator
{
    public const int DefaultResolution = 512;

    public Galaxy Generate(GalaxyParams parms, int resolution = DefaultResolution)
    {
        var density = new float[resolution * resolution];
        GalaxyMath.For(parms).DensityMap(density, resolution, resolution);
        var galaxy     = new Galaxy(parms.Clone(), density, resolution, resolution);
        var clusterMap = new GalaxyClusterMap(galaxy);
        var blackHole  = new GalacticBlackHole(parms);
        return new Galaxy(parms.Clone(), density, resolution, resolution,
                          clusterMap: clusterMap, blackHole: blackHole);
    }

    /// <summary>
    /// Generates the edge-on (side) view density map.
    /// Width maps to the radial axis, height to the vertical axis.
    /// A 4:1 aspect ratio (e.g. 512×128) looks natural for a disk galaxy.
    /// </summary>
    public Galaxy GenerateSideView(GalaxyParams parms, int width = 512, int height = 128)
    {
        var density = new float[width * height];
        GalaxyMath.For(parms).SideView(density, width, height);
        return new Galaxy(parms.Clone(), density, width, height);
    }

    /// <summary>
    /// Generates a zoomed view of a sub-region of the galaxy.
    /// region is in normalised [0,1] coordinates relative to the full galaxy extent.
    /// Noise scale is boosted proportionally so the result shows genuine new structure
    /// rather than just a magnified version of the top-level image.
    /// </summary>
    public Galaxy GenerateZoomedView(GalaxyParams parms, RegionF region,
                                     int zoomLevel, int resolution = DefaultResolution)
    {
        var density = new float[resolution * resolution];
        GalaxyMath.For(parms).DensityMap(density, resolution, resolution,
                                         region.X, region.Y, region.Width, region.Height);
        return new Galaxy(parms.Clone(), density, resolution, resolution, region, zoomLevel);
    }
}
