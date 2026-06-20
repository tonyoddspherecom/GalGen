using UniverseGen.Models;

namespace UniverseGen.Rendering;

// ── data record ──────────────────────────────────────────────────────────────

/// <summary>Layout data for a single moon in the moon-zone strip.</summary>
/// <param name="Moon">Source model object.</param>
/// <param name="NormX">
/// Normalised horizontal position [0, 1] within the moon zone's log scale.
/// Multiply by the usable pixel width (after the parent-planet icon) to get screen X.
/// </param>
/// <param name="Radius">Display radius in pixels at the standard 800 km/px scale.</param>
/// <param name="Color">RGB fill colour.</param>
/// <param name="Label">Short moon suffix label (e.g. "I", "ii", "3").</param>
/// <param name="RadiusLabel">Compact radius string (e.g. "1.2k", "340").</param>
public sealed record MoonLayoutItem(
    Moon   Moon,
    float  NormX,
    float  Radius,
    (byte R, byte G, byte B) Color,
    string Label,
    string RadiusLabel);

// ── layout ───────────────────────────────────────────────────────────────────

/// <summary>
/// Computes a platform-agnostic log-scale layout for a planet's moon system.
///
/// <para>Because moons are generated with exponentially increasing orbital radii,
/// a log₁₀ scale gives visually even spacing.  All positions are normalised to
/// [0, 1] so the layout works at any panel width.</para>
///
/// <para>Only the first <see cref="MaxDisplayed"/> moons are included in
/// <see cref="Items"/>; any remainder are summarised by <see cref="HiddenCount"/>.
/// The rendering layer should draw a "+N more" note at the right edge when
/// <see cref="HiddenCount"/> &gt; 0.</para>
/// </summary>
public sealed class MoonLayout
{
    /// <summary>Maximum number of moons shown before the "+N more" overflow note.</summary>
    public const int MaxDisplayed = 8;

    /// <summary>Moon items to render, in innermost-first order.</summary>
    public IReadOnlyList<MoonLayoutItem> Items { get; }

    /// <summary>Number of moons omitted from <see cref="Items"/> (shown as "+N more").</summary>
    public int HiddenCount { get; }

    /// <summary>
    /// Computes layout for <paramref name="moons"/>.
    /// An empty moon array produces an empty layout with <see cref="HiddenCount"/> = 0.
    /// </summary>
    public MoonLayout(Moon[] moons)
    {
        if (moons.Length == 0)
        {
            Items       = [];
            HiddenCount = 0;
            return;
        }

        HiddenCount = Math.Max(0, moons.Length - MaxDisplayed);
        var shown   = moons.Length <= MaxDisplayed ? moons : moons[..MaxDisplayed];

        float logMin = MathF.Log10(MathF.Max(shown[0].OrbitParentRadii * 0.75f, 0.5f));
        float logMax = MathF.Log10(shown[^1].OrbitParentRadii * 1.30f);
        if (logMax <= logMin) logMax = logMin + 1f;
        float logRange = logMax - logMin;

        float Norm(float orb) =>
            (MathF.Log10(MathF.Max(orb, 0.01f)) - logMin) / logRange;

        var items = new List<MoonLayoutItem>(shown.Length);
        foreach (var moon in shown)
        {
            float normX   = Norm(moon.OrbitParentRadii);
            // Radius: clamp to [3, 13] px using the 800 km ≈ 9 px scale
            float radius  = Math.Clamp(moon.RadiusKm / 800f * 9f, 3f, 13f);
            var   color   = SystemVisuals.MoonRgb(moon.Type);
            string label  = moon.Name.Split('-').Last();
            string radLbl = moon.RadiusKm >= 1000f
                ? $"{moon.RadiusKm / 1000f:F1}k"
                : $"{moon.RadiusKm:F0}";
            items.Add(new MoonLayoutItem(moon, normX, radius, color, label, radLbl));
        }
        Items = items;
    }
}
