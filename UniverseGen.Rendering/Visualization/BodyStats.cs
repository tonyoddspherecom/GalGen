using System.Text;
using UniverseGen.Models;

namespace UniverseGen.Rendering;

/// <summary>
/// Platform-agnostic formatted statistics for star systems and their bodies.
///
/// <para>All methods return plain strings or key-value pairs that any UI framework
/// can render — no <c>System.Drawing</c> dependency.</para>
/// </summary>
public static class BodyStats
{
    // ── body stats ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the stat rows for a body detail panel as (key, value) pairs.
    ///
    /// Asteroid belts are handled gracefully: only orbit is returned.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> GetStatRows(StarSystemBody body)
    {
        var rows = new List<(string, string)>
        {
            ("Orbit", $"{body.OrbitAu:F3} AU"),
        };

        if (body.Type == BodyType.AsteroidBelt)
            return rows;

        rows.Add(("Mass",    $"{body.MassEarth:F2} M♁"));
        rows.Add(("Radius",  $"{body.RadiusEarth:F2} R♁"));
        rows.Add(("Temp",    $"{body.TemperatureC:F0}°C  ({body.TemperatureK:F0} K)"));
        rows.Add(("Gravity", $"{body.GravitySurface:F2} g"));
        rows.Add(("Atm",     body.Atmosphere.ToString()));
        rows.Add(("Surface", body.Surface.ToString()));

        if (body.MoonCount > 0)
            rows.Add(("Moons", body.MoonCount.ToString()));
        if (body.HasRings)
            rows.Add(("Rings", "yes"));

        rows.Add(("Hab. zone", body.InHabitableZone ? "yes" : "no"));

        return rows;
    }

    /// <summary>
    /// Human-readable type string for a body (including "Asteroid Belt" expansion).
    /// </summary>
    public static string BodyTypeLabel(BodyType type) =>
        type == BodyType.AsteroidBelt ? "Asteroid Belt" : type.ToString();

    // ── system summary ────────────────────────────────────────────────────────

    /// <summary>
    /// One-line summary of a star system's contents for sidebar headers.
    ///
    /// Example: <c>"4 planets  ·  HZ: 1 (1 habitable)  ·  2 gas giants  ·  belt"</c>
    /// </summary>
    public static string BuildSystemSummary(StarSystem sys)
    {
        var parts = new List<string>();

        int pc = sys.PlanetCount;
        parts.Add($"{pc} planet{(pc == 1 ? "" : "s")}");

        int hz = sys.HzBodyCount;
        if (hz > 0)
        {
            string hzTxt = $"HZ: {hz}";
            if (sys.HabitableCount > 0) hzTxt += $" ({sys.HabitableCount} habitable)";
            parts.Add(hzTxt);
        }

        int gas = sys.Bodies.Count(b => b.Type == BodyType.GasGiant);
        if (gas > 0) parts.Add($"{gas} gas giant{(gas == 1 ? "" : "s")}");

        if (sys.Bodies.Any(b => b.Type == BodyType.AsteroidBelt))
            parts.Add("belt");
        if (sys.Companions.Length > 0)
            parts.Add(sys.Companions.Length == 1 ? "binary" : "trinary");
        if (sys.HasOortCloud)
            parts.Add("Oort cloud");

        return string.Join("  ·  ", parts);
    }

    // ── luminosity formatting ─────────────────────────────────────────────────

    /// <summary>
    /// Compact luminosity string (e.g. "2.45 L☉", "12k L☉", "0.003 L☉").
    /// </summary>
    public static string FormatLuminosity(float luminositySol) =>
        luminositySol >= 10_000f ? $"{luminositySol / 1000f:F0}k L☉"
        : luminositySol >= 1f   ? $"{luminositySol:F2} L☉"
        :                         $"{luminositySol:G3} L☉";

    // ── plain-text compact body description (for sidebar labels) ─────────────

    /// <summary>
    /// Builds a compact multi-line plain-text description suitable for a sidebar
    /// label (no rich-text markup).
    /// </summary>
    public static string BuildBodySidebarText(StarSystemBody body)
    {
        var sb = new StringBuilder();
        sb.AppendLine(body.Name);
        sb.AppendLine(BodyTypeLabel(body.Type));
        sb.AppendLine();
        sb.AppendLine($"Orbit   {body.OrbitAu:F3} AU");

        if (body.Type != BodyType.AsteroidBelt)
        {
            sb.AppendLine($"Mass    {body.MassEarth:F2} M♁");
            sb.AppendLine($"Radius  {body.RadiusEarth:F2} R♁");
            sb.AppendLine($"Temp    {body.TemperatureC:F0}°C");
            sb.AppendLine($"Gravity {body.GravitySurface:F2} g");
            sb.AppendLine($"Atm     {body.Atmosphere}");
            sb.AppendLine($"Surface {body.Surface}");
            if (body.MoonCount > 0) sb.AppendLine($"Moons   {body.MoonCount}");
            if (body.HasRings)      sb.AppendLine("Rings   yes");
            if (body.IsHabitable)   sb.AppendLine("\n★ Habitable");
        }

        return sb.ToString();
    }
}
