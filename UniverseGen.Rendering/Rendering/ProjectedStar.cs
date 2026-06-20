using UniverseGen.Models;

namespace UniverseGen.Rendering;

/// <summary>
/// One star as it appeared in the last rendered frame —
/// screen position, depth, pixel radius, and the underlying star data.
/// Used for hover hit-testing in the demo and in any game client.
/// </summary>
public readonly record struct ProjectedStar(
    float ScreenX,
    float ScreenY,
    float Depth,
    int   PixelRadius,
    Star  Star);
