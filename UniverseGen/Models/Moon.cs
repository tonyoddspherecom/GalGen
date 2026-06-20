namespace UniverseGen.Models;

/// <summary>Composition/size category of a natural satellite.</summary>
public enum MoonType { Small, Rocky, Icy, Large }

/// <summary>
/// A natural satellite of a planetary body.
/// </summary>
/// <param name="Name">Roman-numeral suffix, e.g. "I", "II", "III".</param>
/// <param name="OrbitParentRadii">Semi-major axis in Earth radii of the parent planet.</param>
/// <param name="RadiusKm">Equatorial radius in kilometres.</param>
/// <param name="Type">Size/composition category — drives display colour.</param>
public sealed record Moon(
    string   Name,
    float    OrbitParentRadii,
    float    RadiusKm,
    MoonType Type);
