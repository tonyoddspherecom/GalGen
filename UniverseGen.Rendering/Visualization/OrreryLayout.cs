using UniverseGen.Models;

namespace UniverseGen.Rendering;

// ── data records ─────────────────────────────────────────────────────────────

/// <summary>Layout data for a single orbital body in the orrery.</summary>
/// <param name="Body">Source model object.</param>
/// <param name="NormX">
/// Normalised horizontal position [0, 1] within the orrery's log-scale span.
/// Multiply by the usable pixel width to get screen X.
/// </param>
/// <param name="Radius">Pixel radius at standard scale (see <see cref="SystemVisuals.BodyRadius"/>).</param>
/// <param name="Color">RGB fill colour.</param>
/// <param name="Label">Short label (last character of designation, e.g. "b", "c").</param>
/// <param name="IsBelt">True when this entry represents an asteroid belt.</param>
public sealed record OrreryBodyItem(
    StarSystemBody Body,
    float NormX,
    float Radius,
    (byte R, byte G, byte B) Color,
    string Label,
    bool IsBelt);

/// <summary>Layout data for a companion star at the orrery's right margin.</summary>
/// <param name="Companion">Source model object.</param>
/// <param name="CompanionIndex">0-based index; used to stack multiple companions without overlap.</param>
/// <param name="Color">RGB fill colour.</param>
/// <param name="Label">Single letter designation suffix ("B", "C").</param>
public sealed record OrreryCompanionItem(
    CompanionStar Companion,
    int CompanionIndex,
    (byte R, byte G, byte B) Color,
    string Label);

// ── layout ───────────────────────────────────────────────────────────────────

/// <summary>
/// Computes a platform-agnostic log-scale orrery layout for a <see cref="StarSystem"/>.
///
/// <para>All horizontal positions are normalised to [0, 1] relative to the
/// log-scale span of the system's orbital bodies.  Callers multiply by the
/// usable pixel width to obtain screen coordinates:</para>
/// <code>
/// float pixelX = scaleLeft + layout.NormX(au) * (panelWidth - scaleLeft - pad);
/// </code>
///
/// <para>This class has no dependency on <c>System.Drawing</c> or any UI framework,
/// so it can be used unchanged from WinForms, Godot, Unity, or a web renderer.</para>
/// </summary>
public sealed class OrreryLayout
{
    // ── scale parameters (useful to expose for tick-mark positioning) ─────────

    /// <summary>Inner edge of the log scale in AU.</summary>
    public float MinOrbitAu { get; }

    /// <summary>Outer edge of the log scale in AU.</summary>
    public float MaxOrbitAu { get; }

    // ── zone markers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Normalised X of the inner habitable-zone edge.
    /// <see cref="float.NaN"/> when no HZ exists for this star.
    /// </summary>
    public float HzInnerNorm { get; }

    /// <summary>
    /// Normalised X of the outer habitable-zone edge.
    /// <see cref="float.NaN"/> when no HZ exists for this star.
    /// </summary>
    public float HzOuterNorm { get; }

    /// <summary>
    /// Normalised X of the frost/snow line.
    /// <see cref="float.NaN"/> when outside the visible range.
    /// </summary>
    public float SnowLineNorm { get; }

    // ── elements ─────────────────────────────────────────────────────────────

    /// <summary>AU scale tick marks: normalised X and display label.</summary>
    public IReadOnlyList<(float NormX, string Label)> Ticks { get; }

    /// <summary>Orbital bodies in the order they should be drawn.</summary>
    public IReadOnlyList<OrreryBodyItem> Bodies { get; }

    /// <summary>
    /// Companion stars for display at the right margin.
    /// These are NOT positioned by the log scale (their orbits are typically
    /// hundreds of AU and would compress the planetary zone unreadably).
    /// The rendering layer should stack them from the right edge inward.
    /// </summary>
    public IReadOnlyList<OrreryCompanionItem> Companions { get; }

    /// <summary>RGB colour of the host star disc.</summary>
    public (byte R, byte G, byte B) StarColor { get; }

    // ── constructor ───────────────────────────────────────────────────────────

    /// <summary>Computes layout for <paramref name="sys"/>.</summary>
    public OrreryLayout(StarSystem sys)
    {
        // ── log-scale extent ──────────────────────────────────────────────────
        float maxOrbit = sys.Bodies.Length > 0
            ? sys.Bodies.Max(b => b.OrbitAu) * 1.5f
            : Math.Max(sys.HzOuterAu * 3f, 5f);
        float minOrbit = sys.HzInnerAu > 0f
            ? Math.Min(sys.HzInnerAu * 0.15f, 0.05f)
            : 0.02f;
        minOrbit = Math.Max(minOrbit, 0.005f);
        maxOrbit = Math.Max(maxOrbit, minOrbit * 100f);

        MinOrbitAu = minOrbit;
        MaxOrbitAu = maxOrbit;

        float logMin   = MathF.Log10(minOrbit);
        float logRange = MathF.Max(MathF.Log10(maxOrbit) - logMin, 0.01f);

        float Norm(float au) =>
            (MathF.Log10(MathF.Max(au, minOrbit)) - logMin) / logRange;

        // ── star ──────────────────────────────────────────────────────────────
        StarColor = SystemVisuals.SpectralRgb(sys.StarClass);

        // ── zones ─────────────────────────────────────────────────────────────
        bool hasHz  = sys.HzInnerAu > 0f;
        HzInnerNorm = hasHz ? Norm(sys.HzInnerAu) : float.NaN;
        HzOuterNorm = hasHz ? Norm(sys.HzOuterAu) : float.NaN;
        SnowLineNorm = (sys.SnowLineAu > minOrbit && sys.SnowLineAu < maxOrbit)
                        ? Norm(sys.SnowLineAu) : float.NaN;

        // ── tick marks ────────────────────────────────────────────────────────
        var ticks = new List<(float, string)>();
        foreach (float au in new[] { 0.1f, 0.5f, 1f, 5f, 10f, 50f, 100f })
        {
            if (au < minOrbit || au > maxOrbit) continue;
            string lbl = au < 1f ? $"{au} AU" : $"{(int)au} AU";
            ticks.Add((Norm(au), lbl));
        }
        Ticks = ticks;

        // ── orbital bodies ────────────────────────────────────────────────────
        var bodies = new List<OrreryBodyItem>();
        foreach (var body in sys.Bodies)
        {
            if (body.OrbitAu <= 0f) continue;
            float normX = Norm(body.OrbitAu);
            // Slightly outside range is still included; the renderer will clip.
            if (normX < -0.05f || normX > 1.05f) continue;

            string label  = body.Name.Length > 0 ? body.Name[^1..] : "?";
            var    color  = SystemVisuals.BodyRgb(body.Type);
            float  radius = SystemVisuals.BodyRadius(body.Type);
            bool   isBelt = body.Type == BodyType.AsteroidBelt;
            bodies.Add(new OrreryBodyItem(body, normX, radius, color, label, isBelt));
        }
        Bodies = bodies;

        // ── companions ────────────────────────────────────────────────────────
        var companions = new List<OrreryCompanionItem>();
        for (int i = 0; i < sys.Companions.Length; i++)
        {
            var comp  = sys.Companions[i];
            var color = SystemVisuals.SpectralRgb(comp.Class);
            string label = comp.Designation.Length > 0 ? comp.Designation[^1..] : "B";
            companions.Add(new OrreryCompanionItem(comp, i, color, label));
        }
        Companions = companions;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts an AU value to a normalised [0, 1] position in the log scale.
    /// Values outside the scale range may return values outside [0, 1].
    /// </summary>
    public float NormX(float au)
    {
        float logMin   = MathF.Log10(MinOrbitAu);
        float logRange = MathF.Max(MathF.Log10(MaxOrbitAu) - logMin, 0.01f);
        return (MathF.Log10(MathF.Max(au, MinOrbitAu)) - logMin) / logRange;
    }
}
