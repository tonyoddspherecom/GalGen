using UniverseGen.Generation;

namespace UniverseGen.Models;

public class Galaxy
{
    public GalaxyParams Params     { get; }
    public float[]      DensityMap { get; }
    public int          Width      { get; }
    public int          Height     { get; }
    /// The sub-region of galaxy space this instance covers, in normalised [0,1] coords.
    /// RegionF.Full for a top-level generation; a sub-rect for zoomed views.
    public RegionF      Region     { get; }
    /// 0 = full galaxy view; increments by 1 for each zoom step.
    /// Used by the renderer to progressively reveal more stellar detail.
    public int          ZoomLevel  { get; }
    public GalaxyClusterMap?  ClusterMap { get; }
    public GalacticBlackHole? BlackHole  { get; }

    internal Galaxy(GalaxyParams parms, float[] densityMap, int width, int height,
                    RegionF? region = null, int zoomLevel = 0,
                    GalaxyClusterMap? clusterMap = null, GalacticBlackHole? blackHole = null)
    {
        Params     = parms;
        DensityMap = densityMap;
        Width      = width;
        Height     = height;
        Region     = region ?? RegionF.Full;
        ZoomLevel  = zoomLevel;
        ClusterMap = clusterMap;
        BlackHole  = blackHole;
    }

    public float GetDensity(int x, int y) => DensityMap[y * Width + x];

    public float SampleDensity(float nx, float ny)
    {
        int x = Math.Clamp((int)((nx + 1f) * 0.5f * Width),  0, Width  - 1);
        int y = Math.Clamp((int)((ny + 1f) * 0.5f * Height), 0, Height - 1);
        return DensityMap[y * Width + x];
    }
}
