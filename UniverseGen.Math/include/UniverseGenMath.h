#pragma once
#include <cstdint>

#ifdef _WIN32
#  define UGMATH_API extern "C" __declspec(dllexport)
#else
#  define UGMATH_API extern "C" __attribute__((visibility("default")))
#endif

/// Must stay binary-compatible with GalaxyMathParams in GalaxyParams.cs (LayoutKind.Sequential)
struct GalaxyMathParams
{
    int      arms;
    float    armTightness;
    float    armWidth;
    float    bulgeFactor;
    float    bulgeStrength;
    float    dissipation;
    float    coreDensity;
    float    armDensity;
    int      age;           // 0=Young 1=Mature 2=Old
    uint64_t seed;
    float    barStrength;   // 0 = no bar, 1 = full bar
    float    barLength;     // fraction of galaxy radius [0,1]
    int      galaxyType;    // 0=Spiral 1=Elliptical 2=Lenticular 3=Irregular
};

UGMATH_API void GenerateGalaxyDensityMap(
    const GalaxyMathParams* params,
    float*                  outDensity,
    int                     width,
    int                     height);

/// Edge-on (side) view. width = radial axis, height = vertical axis.
/// bulgeFactor controls the vertical extent of the central spheroid.
UGMATH_API void GenerateGalaxySideViewDensityMap(
    const GalaxyMathParams* params,
    float*                  outDensity,
    int                     width,
    int                     height);

/// Like GenerateGalaxyDensityMap but renders only the sub-region of galaxy space
/// defined by [regionX, regionY, regionW, regionH] in normalised [0,1] coordinates.
/// Noise scale is automatically boosted so zoomed views show genuine new structure.
UGMATH_API void GenerateGalaxyDensityMapRegion(
    const GalaxyMathParams* params,
    float*                  outDensity,
    int                     width,
    int                     height,
    float                   regionX,
    float                   regionY,
    float                   regionW,
    float                   regionH);

/// Stellar environment classification for a chunk of space.
/// Determines both density character and stellar population mix.
enum RegionType : uint8_t
{
    REGION_DISK_ARM   = 0,  // inside a spiral arm — standard Salpeter population
    REGION_DISK_INTER = 1,  // inter-arm disk — slightly older population
    REGION_BULGE      = 2,  // galactic bulge — ancient, metal-rich
    REGION_HALO       = 3,  // stellar halo — ancient, metal-poor, sparse
    REGION_NURSERY    = 4,  // star-forming region — hot young OB stars
    REGION_VOID       = 5,  // deep void — very sparse, old field stars
};

/// One star as returned by GenerateStarChunk.
/// 16 bytes, blittable — must stay in sync with StarDataInterop in MathDll.cs.
struct StarDataC
{
    float   x, y, z;        // position in galaxy normalised space [-1,1]
    uint8_t spectralClass;  // 0=O 1=B 2=A 3=F 4=G 5=K 6=M 7=WD 8=NS 9=BH
    uint8_t luminosity;     // 0-255 normalised brightness
    uint8_t flags;          // bit0=binary  bit1=giant  bit2=supergiant
    uint8_t pad;
};

/// Generate stars for a 3-D chunk of galaxy space.
/// Chunk (chunkX,chunkY,chunkZ) covers the cube centred on
///   (chunkX*chunkSize, chunkY*chunkSize, chunkZ*chunkSize)
/// in normalised galaxy coordinates (X and Z are in the galactic plane,
/// Y is perpendicular).  chunkSize is the side length of the cube.
/// Returns the number of stars written into outStars (always <= maxStars).
UGMATH_API int GenerateStarChunk(
    const GalaxyMathParams* params,
    int64_t                 chunkX,
    int64_t                 chunkY,
    int64_t                 chunkZ,
    float                   chunkSize,
    int                     maxStars,
    StarDataC*              outStars);

/// Classify the stellar environment at the given chunk position.
/// Returns a RegionType value (cast to uint8_t for ABI safety).
UGMATH_API uint8_t GetChunkRegionType(
    const GalaxyMathParams* params,
    int64_t                 chunkX,
    int64_t                 chunkY,
    int64_t                 chunkZ,
    float                   chunkSize);
