using UniverseGen.Models;

namespace UniverseGen.Rendering;

/// <summary>
/// Platform-agnostic colour, size, and label data for star-system visualisation.
///
/// All colours are returned as value-type <c>(byte R, byte G, byte B)</c> tuples
/// so any rendering framework (WinForms, Godot, Unity, web) can consume them
/// without taking a dependency on <c>System.Drawing</c>.
/// </summary>
public static class SystemVisuals
{
    // ── spectral colours ──────────────────────────────────────────────────────

    /// <summary>RGB colour appropriate for a star of the given spectral class.</summary>
    public static (byte R, byte G, byte B) SpectralRgb(SpectralClass cls) => cls switch
    {
        SpectralClass.O           => (180, 200, 255),  // blue-white
        SpectralClass.B           => (200, 218, 255),  // blue-white
        SpectralClass.A           => (240, 245, 255),  // white
        SpectralClass.F           => (255, 248, 200),  // yellow-white
        SpectralClass.G           => (255, 225, 120),  // yellow (Sun-like)
        SpectralClass.K           => (255, 175,  70),  // orange
        SpectralClass.M           => (255, 100,  50),  // red
        SpectralClass.WhiteDwarf  => (200, 220, 255),  // pale blue
        SpectralClass.NeutronStar => (160, 255, 255),  // cyan
        SpectralClass.BlackHole   => (120,  60, 200),  // purple
        _                         => (255, 255, 255),
    };

    // ── body colours ──────────────────────────────────────────────────────────

    /// <summary>RGB colour for an orbital body disc.</summary>
    public static (byte R, byte G, byte B) BodyRgb(BodyType type) => type switch
    {
        BodyType.Lava        => (255,  80,  15),  // volcanic orange-red
        BodyType.Desert      => (205, 155,  65),  // sandy brown
        BodyType.Terrestrial => ( 75, 190,  75),  // green-blue
        BodyType.OceanWorld  => ( 40, 130, 230),  // deep blue
        BodyType.Airless     => (130, 130, 130),  // grey
        BodyType.Frozen      => (190, 220, 255),  // icy blue-white
        BodyType.IceGiant    => (100, 160, 225),  // Neptune blue
        BodyType.GasGiant    => (210, 150,  80),  // Jupiter tan
        _                    => (120, 120, 120),  // fallback grey
    };

    /// <summary>RGB colour for a moon disc.</summary>
    public static (byte R, byte G, byte B) MoonRgb(MoonType type) => type switch
    {
        MoonType.Large => (215, 215, 235),  // bright grey-blue (Ganymede-scale)
        MoonType.Rocky => (170, 148, 122),  // rocky tan
        MoonType.Icy   => (195, 225, 248),  // icy pale
        _              => (155, 155, 155),  // small grey
    };

    // ── body radii ────────────────────────────────────────────────────────────

    /// <summary>
    /// Pixel radius for an orrery body disc at the standard orrery scale.
    ///
    /// <para>GasGiant is the baseline at 8 px.  All other types are proportional.
    /// Scale by the caller's desired size multiplier when drawing at different zoom
    /// levels (e.g. the full-panel orrery uses ×1.6, the tooltip mini-orrery uses
    /// ×1.0).</para>
    /// </summary>
    public static float BodyRadius(BodyType type) => type switch
    {
        BodyType.GasGiant    => 8.0f,
        BodyType.IceGiant    => 6.0f,
        BodyType.OceanWorld  => 5.0f,
        BodyType.Terrestrial => 5.0f,
        BodyType.Desert      => 4.0f,
        BodyType.Lava        => 4.0f,
        _                    => 3.0f,
    };

    // ── labels ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Short, human-readable description of a spectral class.
    /// Suitable for sidebar labels and tooltips.
    /// </summary>
    public static string SpectralLabel(SpectralClass cls) => cls switch
    {
        SpectralClass.O           => "O-class  (blue supergiant)",
        SpectralClass.B           => "B-class  (blue-white)",
        SpectralClass.A           => "A-class  (white)",
        SpectralClass.F           => "F-class  (yellow-white)",
        SpectralClass.G           => "G-class  (yellow dwarf)",
        SpectralClass.K           => "K-class  (orange dwarf)",
        SpectralClass.M           => "M-class  (red dwarf)",
        SpectralClass.WhiteDwarf  => "White dwarf",
        SpectralClass.NeutronStar => "Neutron star",
        SpectralClass.BlackHole   => "Black hole",
        _                         => cls.ToString(),
    };

    /// <summary>
    /// Full descriptive name for a spectral class (used in hover tooltips).
    /// </summary>
    public static string SpectralDescription(SpectralClass cls) => cls switch
    {
        SpectralClass.O           => "O-class Blue Supergiant",
        SpectralClass.B           => "B-class Blue-White Star",
        SpectralClass.A           => "A-class White Star",
        SpectralClass.F           => "F-class Yellow-White Star",
        SpectralClass.G           => "G-class Yellow Dwarf",
        SpectralClass.K           => "K-class Orange Dwarf",
        SpectralClass.M           => "M-class Red Dwarf",
        SpectralClass.WhiteDwarf  => "White Dwarf",
        SpectralClass.NeutronStar => "Neutron Star",
        SpectralClass.BlackHole   => "Black Hole",
        _                         => "Unknown",
    };

    // ── star designation ──────────────────────────────────────────────────────

    /// <summary>
    /// Deterministic catalogue-style designation for a star, derived from its
    /// position alone so the same star always gets the same name across sessions.
    ///
    /// Example outputs: <c>"HD 042761"</c>, <c>"SAO 836205"</c>.
    /// </summary>
    public static string StarDesignation(Star star) => UniverseGen.Generation.StarNames.Designation(star);
}
