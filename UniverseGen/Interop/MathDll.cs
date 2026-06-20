using System.Runtime.InteropServices;
using UniverseGen.Models;

namespace UniverseGen.Interop;

/// <summary>
/// Blittable mirror of StarDataC in UniverseGenMath.h.
/// Must remain 16 bytes and field-order identical.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StarDataInterop
{
    public float X, Y, Z;
    public byte  SpectralClass;
    public byte  Luminosity;
    public byte  Flags;
    public byte  Pad;
}

internal static class MathDll
{
    private const string Dll = "UniverseGen.Math";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void GenerateGalaxyDensityMap(
        ref GalaxyMathParams parms,
        [Out] float[]        outDensity,
        int                  width,
        int                  height);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void GenerateGalaxySideViewDensityMap(
        ref GalaxyMathParams parms,
        [Out] float[]        outDensity,
        int                  width,
        int                  height);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void GenerateGalaxyDensityMapRegion(
        ref GalaxyMathParams parms,
        [Out] float[]        outDensity,
        int                  width,
        int                  height,
        float                regionX,
        float                regionY,
        float                regionW,
        float                regionH);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GenerateStarChunk(
        ref GalaxyMathParams      parms,
        long                      chunkX,
        long                      chunkY,
        long                      chunkZ,
        float                     chunkSize,
        int                       maxStars,
        [Out] StarDataInterop[]   outStars);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte GetChunkRegionType(
        ref GalaxyMathParams parms,
        long                 chunkX,
        long                 chunkY,
        long                 chunkZ,
        float                chunkSize);
}
