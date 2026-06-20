namespace UniverseGen.Models;

public enum SpectralClass : byte
{
    O = 0, B = 1, A = 2, F = 3, G = 4, K = 5, M = 6,
    WhiteDwarf = 7, NeutronStar = 8, BlackHole = 9,
}

[Flags]
public enum StarFlags : byte
{
    None       = 0,
    Binary     = 1,
    Giant      = 2,
    Supergiant = 4,
}

/// <summary>
/// A single star returned by the chunk generator.
/// Coordinates are in galaxy normalised space (X and Z in the galactic plane, Y vertical).
/// The full galaxy spans roughly [-1, 1] in X and Z, and [-0.2, 0.2] in Y.
/// </summary>
public readonly record struct Star(
    float         X,
    float         Y,
    float         Z,
    SpectralClass Class,
    byte          Luminosity,
    StarFlags     Flags)
{
    public bool IsBinary     => (Flags & StarFlags.Binary)     != 0;
    public bool IsGiant      => (Flags & StarFlags.Giant)      != 0;
    public bool IsSupergiant => (Flags & StarFlags.Supergiant) != 0;

    public System.Numerics.Vector3 Position => new(X, Y, Z);
}
