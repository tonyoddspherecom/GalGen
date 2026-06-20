namespace UniverseGen.Models;

/// <summary>
/// Stellar environment classification for a chunk of galaxy space.
/// Drives both the visual character (star type mix) and the procedural name.
/// Must stay numerically in sync with the RegionType enum in UniverseGenMath.h.
/// </summary>
public enum RegionType : byte
{
    DiskArm   = 0,  // inside a spiral arm — standard Salpeter IMF
    DiskInter = 1,  // inter-arm disk — quieter, older population
    Bulge     = 2,  // galactic bulge — ancient, metal-rich, K/M heavy
    Halo      = 3,  // stellar halo — ancient, metal-poor, very sparse
    Nursery   = 4,  // star-forming region — hot young OB/A stars
    Void      = 5,  // deep void — almost empty, old field stars
}
