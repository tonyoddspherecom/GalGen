namespace UniverseGen.Models;

/// <summary>
/// A single orbital body in a star system — planet, ice world, gas giant, or
/// asteroid belt.  All physical values are in SI-relative units (Earth = 1 for
/// mass, radius, and gravity; AU for orbits; Kelvin for temperature).
/// </summary>
public sealed class StarSystemBody
{
    /// Catalogue designation, e.g. "HD 40307 b".
    public string        Name            { get; }

    public BodyType      Type            { get; }
    public AtmosphereType Atmosphere     { get; }
    public SurfaceType   Surface         { get; }

    /// Semi-major axis in astronomical units.
    public float         OrbitAu         { get; }

    /// Mass in Earth masses (M♁).
    public float         MassEarth       { get; }

    /// Equatorial radius in Earth radii (R♁).
    public float         RadiusEarth     { get; }

    /// Effective equilibrium surface temperature in Kelvin (greenhouse-adjusted).
    public float         TemperatureK    { get; }

    /// Surface gravitational acceleration in Earth g.
    public float         GravitySurface  { get; }

    /// Number of natural satellites.
    public int           MoonCount       { get; }

    /// Whether the body has a visible ring system.
    public bool          HasRings        { get; }

    /// Whether the orbit falls within the parent star's habitable zone.
    public bool          InHabitableZone { get; }

    internal StarSystemBody(
        string name, BodyType type, AtmosphereType atmosphere, SurfaceType surface,
        float orbitAu, float massEarth, float radiusEarth,
        float temperatureK, float gravitySurface,
        int moonCount, bool hasRings, bool inHabitableZone)
    {
        Name            = name;
        Type            = type;
        Atmosphere      = atmosphere;
        Surface         = surface;
        OrbitAu         = orbitAu;
        MassEarth       = massEarth;
        RadiusEarth     = radiusEarth;
        TemperatureK    = temperatureK;
        GravitySurface  = gravitySurface;
        MoonCount       = moonCount;
        HasRings        = hasRings;
        InHabitableZone = inHabitableZone;
    }

    /// Temperature in Celsius for display.
    public float TemperatureC => TemperatureK - 273.15f;

    /// True if this body could support surface liquid water.
    public bool IsHabitable =>
        InHabitableZone &&
        Atmosphere is AtmosphereType.Breathable or AtmosphereType.Thin &&
        Type is BodyType.Terrestrial or BodyType.OceanWorld or BodyType.Desert;
}
