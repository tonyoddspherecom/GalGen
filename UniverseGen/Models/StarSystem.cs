namespace UniverseGen.Models;

/// <summary>A companion star in a binary or trinary system.</summary>
/// <param name="Class">Spectral class of the companion.</param>
/// <param name="MassSolar">Mass relative to the Sun.</param>
/// <param name="OrbitAu">Wide-binary separation in AU.</param>
/// <param name="Designation">Catalogue designation, e.g. "HD 40307 B".</param>
public sealed record CompanionStar(
    SpectralClass Class,
    float         MassSolar,
    float         OrbitAu,
    string        Designation);

/// <summary>
/// A fully generated star system anchored on one star.
///
/// Physical zones are in astronomical units (AU).
/// The habitable zone uses Kopparapu (2013) conservative estimates.
/// The snow line follows r_snow ≈ 2.7 × √L AU.
/// </summary>
public sealed class StarSystem
{
    /// Host-star designation (matches the visual tooltip designation).
    public string          Designation     { get; }

    /// Host spectral class — used for colouring the star in the orrery.
    public SpectralClass   StarClass       { get; }

    /// Host luminosity in solar units — drives all zone calculations.
    public float           LuminositySol   { get; }

    /// Habitable zone inner edge (AU).  0 if the star has no meaningful HZ.
    public float           HzInnerAu       { get; }

    /// Habitable zone outer edge (AU).
    public float           HzOuterAu       { get; }

    /// Snow line (AU) — water-ice condenses beyond this radius.
    public float           SnowLineAu      { get; }

    /// Companion stars (empty = single; 1 = binary; 2 = trinary).
    public CompanionStar[] Companions      { get; }

    /// All orbital bodies sorted by semi-major axis (including asteroid belts).
    public StarSystemBody[] Bodies         { get; }

    /// Whether the system has an Oort-cloud analogue at very large radii.
    public bool            HasOortCloud    { get; }

    // ── convenience properties ────────────────────────────────────────────────

    public int PlanetCount  => Bodies.Count(b => b.Type != BodyType.AsteroidBelt);
    public int HzBodyCount  => Bodies.Count(b => b.InHabitableZone && b.Type != BodyType.AsteroidBelt);
    public int HabitableCount => Bodies.Count(b => b.IsHabitable);

    internal StarSystem(
        string designation, SpectralClass starClass, float luminositySol,
        float hzInnerAu, float hzOuterAu, float snowLineAu,
        CompanionStar[] companions, StarSystemBody[] bodies, bool hasOortCloud)
    {
        Designation   = designation;
        StarClass     = starClass;
        LuminositySol = luminositySol;
        HzInnerAu     = hzInnerAu;
        HzOuterAu     = hzOuterAu;
        SnowLineAu    = snowLineAu;
        Companions    = companions;
        Bodies        = bodies;
        HasOortCloud  = hasOortCloud;
    }
}
