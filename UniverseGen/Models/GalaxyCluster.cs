using System.Numerics;

namespace UniverseGen.Models;

public enum ClusterType
{
    Nebula,           // emission/reflection nebula — young OB star character
    OpenCluster,      // young open cluster — along spiral arms
    GlobularCluster,  // ancient dense ball — halo distribution
}

/// <summary>
/// A named, placed astronomical feature (nebula, open cluster, or globular cluster).
/// Position is in galaxy normalised space: X and Z are in the galactic plane,
/// Y is perpendicular (vertical).  Radius is the influence sphere radius (normalised units).
/// </summary>
public sealed record GalaxyCluster(
    ClusterType Type,
    Vector3     Position,
    float       Radius,          // influence sphere in normalised units
    string      ScientificName,
    string      PoeticName);
