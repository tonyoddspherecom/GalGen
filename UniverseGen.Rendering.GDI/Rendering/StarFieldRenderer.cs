using System.Drawing;
using System.Drawing.Imaging;
using UniverseGen.Models;

namespace UniverseGen.Rendering;

/// <summary>
/// Software 3-D renderer for star fields.
///
/// Every star is a crisp disc whose pixel radius scales with 1/depth, so the
/// star visibly grows as the camera approaches it.  No blur or alpha blending —
/// disc edges are hard pixel boundaries.
///
/// <see cref="Render"/> returns both the <see cref="Bitmap"/> and a
/// <see cref="ProjectedStar"/> array suitable for hover hit-testing.
/// </summary>
public sealed class StarFieldRenderer
{
    /// Maximum depth (galaxy normalised units) beyond which stars are not drawn.
    /// 0.0012 = 60 ly at the 50,000 ly/unit scale (6 chunks × 10 ly/chunk).
    public float MaxDepth { get; set; } = 0.0012f;

    /// Drives apparent disc size: radius_px = BaseStarSize * classMul / depth.
    /// 0.003 → G-star is 3 px at 50 ly, grows to a visible disc closer in.
    public float BaseStarSize { get; set; } = 0.003f;

    // Spectral colours are defined centrally in SystemVisuals to keep them
    // consistent across every renderer and widget in the project.
    // Cached as an indexed array for the hot render-loop (avoids a switch per star).
    private static readonly (byte R, byte G, byte B)[] ClassColor =
        Enum.GetValues<SpectralClass>()
            .OrderBy(c => (int)c)
            .Select(SystemVisuals.SpectralRgb)
            .ToArray();

    // Physical size multiplier per spectral class (relative to G=1).
    // Determines how quickly the disc grows as you close in.
    private static readonly float[] ClassSizeMul =
    [
        8.0f,  // O  supergiant-scale
        5.0f,  // B
        3.0f,  // A
        1.8f,  // F
        1.0f,  // G  (baseline)
        0.8f,  // K
        0.5f,  // M  red dwarf
        0.3f,  // WD very compact
        0.1f,  // NS tiny
        0.0f,  // BH (no disc — just a pixel; draw separately)
    ];

    // ── public render entry point ─────────────────────────────────────────────

    /// <summary>
    /// Renders <paramref name="chunks"/> from the point of view of
    /// <paramref name="camera"/> into <paramref name="reuseBitmap"/> (if it is the
    /// same size) or a freshly allocated <see cref="Bitmap"/>.
    ///
    /// <para>Pass the previous frame's back-buffer as <paramref name="reuseBitmap"/>
    /// to eliminate per-frame Large-Object-Heap allocations and the GC pauses they
    /// cause.  The caller must not read from that bitmap after calling this method
    /// until the returned bitmap has been swapped in as the display buffer.</para>
    ///
    /// <para>Only stars with depth ≤ <paramref name="maxHoverDepth"/> are included
    /// in the returned <see cref="ProjectedStar"/> array — keeping it small so the
    /// hover hit-test scan stays cheap regardless of how many stars are on screen.</para>
    /// </summary>
    public (Bitmap Bitmap, ProjectedStar[] Projected) Render(
        IEnumerable<StarChunk> chunks, Camera3D camera, int width, int height,
        Bitmap?  reuseBitmap   = null,
        float    maxHoverDepth = float.MaxValue)
    {
        // Reuse the supplied bitmap if it matches the current dimensions; this
        // eliminates the ~8 MB Large-Object-Heap allocation that would otherwise
        // trigger a Gen-2 GC and cause visible frame stutter.
        Bitmap bmp;
        if (reuseBitmap != null &&
            reuseBitmap.Width == width && reuseBitmap.Height == height)
        {
            bmp = reuseBitmap;
        }
        else
        {
            // Do NOT dispose reuseBitmap here — the caller owns its lifetime.
            // Simply allocate a fresh bitmap; the caller will dispose the old
            // back buffer once it detects the dimensions changed.
            bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, width, height),
                                ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        // Hover array: only nearby stars.  512 is a comfortable starting capacity —
        // the list never grows large because maxHoverDepth is typically ≤ 50 ly.
        var projected = new List<ProjectedStar>(512);

        unsafe
        {
            byte* pixels = (byte*)data.Scan0;
            int   stride = data.Stride;
            new Span<byte>(pixels, height * stride).Clear();

            // Two-pass: draw distant (small) stars first, then close (large) ones,
            // so large discs always appear on top.
            var largeStars = new List<(int ix, int iy, byte r, byte g, byte b, int radius, ProjectedStar ps)>();

            foreach (var chunk in chunks)
            foreach (ref readonly var star in chunk.Stars.AsSpan())
            {
                var (sx, sy, depth) = camera.Project(star.Position, width, height);
                if (float.IsNaN(sx) || depth > MaxDepth || depth <= 0f) continue;

                int ix = (int)sx, iy = (int)sy;
                if ((uint)ix >= (uint)width || (uint)iy >= (uint)height) continue;

                // Apparent radius: grows as 1/depth.  Giants and supergiants
                // get a further multiplier on top of the class size.
                float sizeMul = ClassSizeMul[(int)star.Class];
                if (star.IsSupergiant) sizeMul *= 6f;
                else if (star.IsGiant) sizeMul *= 2.5f;

                float radiusF  = BaseStarSize * sizeMul / depth;
                int   radius   = (int)radiusF;          // 0 = single pixel
                radius = Math.Min(radius, 48);          // hard cap so BH / OS never fill screen

                var ps = new ProjectedStar(sx, sy, depth, radius, star);
                // Only track for hover hit-testing when within the hover range.
                // Stars beyond that are still drawn but don't bloat the hover array.
                if (depth <= maxHoverDepth)
                    projected.Add(ps);

                var (r, g, b) = ClassColor[(int)star.Class];

                if (radius <= 1)
                {
                    // Single pixel — draw immediately (no ordering needed at this size)
                    SetPixel(pixels, stride, width, height, ix, iy, r, g, b);
                }
                else
                {
                    // Defer large discs for the second pass
                    largeStars.Add((ix, iy, r, g, b, radius, ps));
                }
            }

            // Second pass: large discs — painter's algorithm: farthest depth first so
            // closer discs always overdraw more distant ones, regardless of apparent size.
            largeStars.Sort((a, b) => b.ps.Depth.CompareTo(a.ps.Depth));
            foreach (var (ix, iy, r, g, b, radius, _) in largeStars)
                DrawDisc(pixels, stride, width, height, ix, iy, r, g, b, radius);
        }

        bmp.UnlockBits(data);
        return (bmp, projected.ToArray());
    }

    // ── pixel helpers ─────────────────────────────────────────────────────────

    /// Crisp filled circle — hard pixel boundary, no alpha blending.
    private static unsafe void DrawDisc(byte* px, int stride,
        int w, int h, int cx, int cy, byte r, byte g, byte b, int radius)
    {
        int r2 = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            if (dx * dx + dy * dy <= r2)
                SetPixel(px, stride, w, h, cx + dx, cy + dy, r, g, b);
        }
    }

    private static unsafe void SetPixel(byte* px, int stride,
        int w, int h, int x, int y, byte r, byte g, byte b)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        int off = y * stride + x * 4;
        px[off] = b; px[off+1] = g; px[off+2] = r; px[off+3] = 255;
    }
}
