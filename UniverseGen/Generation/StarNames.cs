using UniverseGen.Models;

namespace UniverseGen.Generation;

/// <summary>Catalogue names for stars.</summary>
public static class StarNames
{
    /// <summary>
    /// A star's catalogue designation, e.g. "HD 040307": the name its system and planets are given.
    /// Derived from its position, so the same star always has the same name.
    /// </summary>
    public static string Designation(Star star)
    {
        // Through long: a negative float cast straight to ulong wraps on .NET 8 but is 0 on .NET 9 and later,
        // which named most stars with a negative coordinate "HD 000000", and named them differently by runtime.
        // Via long every runtime gives the names .NET 8 always gave.
        ulong h = (ulong)(long)(star.X * 73856093f)
                ^ (ulong)(long)(star.Y * 19349663f)
                ^ (ulong)(long)(star.Z * 83492791f);
        h  = (h ^ (h >> 30)) * 0xbf58476d1ce4e5b9UL;
        h ^= h >> 27;
        int    num    = (int)(h & 0xFFFFF);       // 0 – 1,048,575
        string prefix = (int)((h >> 20) & 3) switch
        {
            0 => "HD", 1 => "HIP", 2 => "SAO", _ => "TYC",
        };
        return $"{prefix} {num:D6}";
    }
}
