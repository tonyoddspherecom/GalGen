using System.Drawing;
using System.Drawing.Imaging;
using UniverseGen.Generation;
using UniverseGen.Models;

namespace UniverseGen.Rendering;

public class GalaxyRenderer
{
    /// Fraction of pixels that become stars at density = 1.0, at zoom level 0.
    /// Lower = sparser field at the galaxy overview.
    public float StarThreshold { get; set; } = 0.18f;

    /// Density above which every pixel is drawn solid — representing the
    /// unresolvable stellar mass of the galactic core.
    public float SolidCoreThreshold { get; set; } = 0.82f;

    /// How many times more stars become visible per zoom level.
    /// Each zoom level multiplies the draw probability by this factor, simulating
    /// the resolution of previously-averaged structure into individual stars.
    /// Stars visible at level N always remain visible at level N+1 because
    /// the draw probability only ever increases (world-space hash is deterministic).
    public float StarScalePerLevel { get; set; } = 4f;

    public Bitmap Render(Galaxy galaxy, GalaxyClusterMap? clusterMap = null)
    {
        var bmp  = new Bitmap(galaxy.Width, galaxy.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* pixels = (byte*)data.Scan0;
            int   stride = data.Stride;

            new Span<byte>(pixels, galaxy.Height * stride).Clear();

            ulong seed = galaxy.Params.Seed;

            float      bulgeFactor  = galaxy.Params.BulgeFactor;
            GalaxyAge  age          = galaxy.Params.Age;
            GalaxyType galaxyType   = galaxy.Params.Type;
            bool       isOld        = age == GalaxyAge.Old;
            bool       isElliptical = galaxyType == GalaxyType.Elliptical;
            bool       isIrregular  = galaxyType == GalaxyType.Irregular;

            // Pre-compute region extents for world-space coordinate mapping.
            // Using (width-1) so the last pixel maps exactly to the far edge of the region.
            float rx = galaxy.Region.X,  rw = galaxy.Region.Width;
            float ry = galaxy.Region.Y,  rh = galaxy.Region.Height;
            float invW = galaxy.Width  > 1 ? 1f / (galaxy.Width  - 1) : 0f;
            float invH = galaxy.Height > 1 ? 1f / (galaxy.Height - 1) : 0f;

            for (int y = 0; y < galaxy.Height; y++)
            {
                for (int x = 0; x < galaxy.Width; x++)
                {
                    float d = galaxy.GetDensity(x, y);
                    if (d <= 0f) continue;

                    // Smoothly blend draw probability from dot-scatter (outer) to
                    // fully solid (core). Transition spans 0.25 density units so
                    // there's no visible hard edge between the blob and the stars.
                    float blendStart = SolidCoreThreshold - 0.08f;
                    float solidFrac  = Math.Clamp((d - blendStart) / (SolidCoreThreshold - blendStart), 0f, 1f);
                    solidFrac = solidFrac * solidFrac * (3f - 2f * solidFrac); // smoothstep

                    // At zoom level k, reveal more stars by raising the scatter threshold.
                    // Cap the threshold at 1 BEFORE multiplying by d so that the draw
                    // probability is always bounded by d — a sparse region (d=0.01) can
                    // never become solid no matter how deep the zoom.
                    float levelScale        = MathF.Pow(StarScalePerLevel, galaxy.ZoomLevel);
                    float effectiveThreshold = Math.Min(StarThreshold * levelScale, 1f);
                    float drawProb           = solidFrac + (1f - solidFrac) * d * effectiveThreshold;

                    // World-space position in [0,1] — identical for the same galaxy location
                    // regardless of zoom level, so Hash() produces zoom-independent results.
                    int wx = (int)((rx + x * invW * rw) * 65536f);
                    int wy = (int)((ry + y * invH * rh) * 65536f);

                    float r1 = (Hash(wx, wy, seed) & 0xFFFFFF) / (float)0xFFFFFF;
                    if (r1 >= drawProb) continue;

                    // Normalised radial position in galaxy space [-1,1] — used for colour split.
                    // Derived from world coords so it matches the C++ density computation.
                    float nx       = 2f * (rx + x * invW * rw) - 1f;
                    float ny       = 2f * (ry + y * invH * rh) - 1f;
                    float rn       = MathF.Sqrt(nx * nx + ny * ny);
                    float bulgeFrac = MathF.Exp(-rn * rn / (2f * bulgeFactor * bulgeFactor));

                    float variation = ((Hash(wx + 7919, wy + 6271, seed) & 0xFF) / 255f) * 0.2f - 0.1f;
                    float colorD    = Math.Clamp(0.4f + d * 0.6f + variation, 0f, 1f);

                    // HII region: rare bright blue-white knots along arms in spiral/lenticular young/mature only
                    float hiiR  = (Hash(wx + 31337, wy + 31337, seed) & 0xFFFF) / (float)0xFFFF;
                    bool  isHII = !isOld && !isElliptical && bulgeFrac < 0.3f && hiiR < 0.018f;

                    Color c;
                    if (isHII)
                    {
                        c = Color.FromArgb(210, 235, 255);
                    }
                    else if (isElliptical)
                    {
                        // Ellipticals are pure old stellar population — no age distinction
                        c = Interpolate(colorD, OldStops);
                    }
                    else if (isIrregular)
                    {
                        // Irregulars skew young/blue with a warm core mix
                        var armColor   = StarColor(colorD, GalaxyAge.Young);
                        var bulgeColor = Interpolate(colorD, MatureStops);
                        c = Blend(armColor, bulgeColor, bulgeFrac);
                    }
                    else
                    {
                        // Spiral / Lenticular: arms use age ramp; bulge is always old population
                        var armColor   = StarColor(colorD, age);
                        var bulgeColor = Interpolate(colorD, OldStops);
                        c = Blend(armColor, bulgeColor, bulgeFrac);
                    }

                    SetPixel(pixels, stride, x, y, galaxy.Width, galaxy.Height, c);
                }
            }
        }
        bmp.UnlockBits(data);

        // Draw cluster/nebula markers on top (only on full-galaxy view)
        if (clusterMap != null && galaxy.ZoomLevel == 0)
            DrawClusterMarkers(bmp, clusterMap, galaxy.Width, galaxy.Height);

        // Draw SMBH at galactic centre (only on full-galaxy, top-level view)
        if (galaxy.BlackHole != null && galaxy.ZoomLevel == 0)
            DrawSmbhMarker(bmp, galaxy.BlackHole, galaxy.Width, galaxy.Height);

        return bmp;
    }

    private static void DrawClusterMarkers(Bitmap bmp, GalaxyClusterMap clusterMap, int w, int h)
    {
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        foreach (var c in clusterMap.Clusters)
        {
            // Galaxy normalised coords → pixel coords
            // X axis → image X,  Z axis → image Y
            float px = (c.Position.X + 1f) * 0.5f * (w - 1);
            float py = (c.Position.Z + 1f) * 0.5f * (h - 1);

            switch (c.Type)
            {
                case ClusterType.Nebula:
                    // Soft pink/magenta glow rings
                    using (var b1 = new SolidBrush(Color.FromArgb(18, 255, 100, 180)))
                    using (var b2 = new SolidBrush(Color.FromArgb(40, 255, 120, 200)))
                    using (var b3 = new SolidBrush(Color.FromArgb(80, 255, 160, 225)))
                    using (var bc = new SolidBrush(Color.FromArgb(180, 255, 200, 245)))
                    {
                        g.FillEllipse(b1, px - 9f,  py - 9f,  18f, 18f);
                        g.FillEllipse(b2, px - 5f,  py - 5f,  10f, 10f);
                        g.FillEllipse(b3, px - 3f,  py - 3f,   6f,  6f);
                        g.FillEllipse(bc, px - 1.5f,py - 1.5f, 3f,  3f);
                    }
                    break;

                case ClusterType.OpenCluster:
                    // Bright yellow-white dot with a faint ring
                    using (var bd = new SolidBrush(Color.FromArgb(200, 255, 255, 180)))
                    using (var rp = new Pen(Color.FromArgb(100, 255, 235, 100), 0.8f))
                    {
                        g.FillEllipse(bd, px - 2f, py - 2f, 4f, 4f);
                        g.DrawEllipse(rp, px - 4f, py - 4f, 8f, 8f);
                    }
                    break;

                case ClusterType.GlobularCluster:
                    // Gold concentric rings — dense core
                    using (var bc2 = new SolidBrush(Color.FromArgb(210, 255, 210, 80)))
                    using (var rp1 = new Pen(Color.FromArgb(130, 255, 185, 50), 0.9f))
                    using (var rp2 = new Pen(Color.FromArgb(65,  255, 210, 80), 1.3f))
                    {
                        g.FillEllipse(bc2, px - 2f, py - 2f, 4f,  4f);
                        g.DrawEllipse(rp1, px - 4f, py - 4f, 8f,  8f);
                        g.DrawEllipse(rp2, px - 6f, py - 6f, 12f, 12f);
                    }
                    break;
            }
        }
    }

    private static void DrawSmbhMarker(Bitmap bmp, GalacticBlackHole bh, int w, int h)
    {
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // SMBH is always at the galactic centre → image centre pixel
        float cx = w / 2f;
        float cy = h / 2f;

        float act = bh.ActivityLevel;

        // Multi-ring accretion glow — orange-amber, intensity scales with activity
        (float R, int A, int r, int gn, int b)[] rings =
        [
            (24f, (int)(12 + act * 18), 160,  75, 15),   // outer corona
            (15f, (int)(25 + act * 35), 210, 115, 25),   // mid glow
            ( 9f, (int)(50 + act * 70), 250, 165, 50),   // inner glow
            ( 5f, (int)(90 + act * 95), 255, 230, 120),  // hot inner ring
        ];

        foreach (var (R, A, r, gn, b) in rings)
        {
            using var brush = new SolidBrush(Color.FromArgb(A, r, gn, b));
            g.FillEllipse(brush, cx - R, cy - R, R * 2, R * 2);
        }

        // Event horizon shadow — opaque black disc punched into the glow
        const float ShadowR = 3.5f;
        using (var shadow = new SolidBrush(Color.Black))
            g.FillEllipse(shadow, cx - ShadowR, cy - ShadowR, ShadowR * 2, ShadowR * 2);

        // White-hot accretion point only when sufficiently active
        if (act > 0.12f)
        {
            int coreA = (int)(act * 230);
            using var core = new SolidBrush(Color.FromArgb(coreA, 255, 255, 235));
            g.FillEllipse(core, cx - 1.5f, cy - 1.5f, 3f, 3f);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static unsafe void SetPixel(byte* pixels, int stride, int x, int y, int w, int h, Color c)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        int offset       = y * stride + x * 4;
        pixels[offset]   = c.B;
        pixels[offset+1] = c.G;
        pixels[offset+2] = c.R;
        pixels[offset+3] = 255;
    }

    private static Color Blend(Color a, Color b, float t) =>
        Color.FromArgb(
            (int)(a.R + t * (b.R - a.R)),
            (int)(a.G + t * (b.G - a.G)),
            (int)(a.B + t * (b.B - a.B)));

    /// 64-bit finaliser hash — good distribution, no allocations.
    private static ulong Hash(int x, int y, ulong seed)
    {
        ulong h = (ulong)(uint)x * 0x9e3779b97f4a7c15UL
                ^ (ulong)(uint)y * 0x6c62272e07bb0142UL
                ^ seed;
        h = (h ^ (h >> 30)) * 0xbf58476d1ce4e5b9UL;
        h = (h ^ (h >> 27)) * 0x94d049bb133111ebUL;
        return h ^ (h >> 31);
    }

    // ── colour ramps ─────────────────────────────────────────────────────────

    private static readonly (float d, int r, int g, int b)[] YoungStops =
    [
        (0.00f,  80, 120, 255),
        (0.40f, 160, 200, 255),
        (0.75f, 220, 235, 255),
        (1.00f, 255, 255, 255),
    ];

    private static readonly (float d, int r, int g, int b)[] MatureStops =
    [
        (0.00f, 140,  80, 220),
        (0.40f, 190, 160, 255),
        (0.75f, 230, 220, 255),
        (1.00f, 255, 255, 255),
    ];

    private static readonly (float d, int r, int g, int b)[] OldStops =
    [
        (0.00f, 200,  80,  20),
        (0.40f, 240, 160,  60),
        (0.75f, 255, 220, 140),
        (1.00f, 255, 255, 220),
    ];

    private static Color StarColor(float d, GalaxyAge age)
    {
        d = Math.Clamp(d, 0f, 1f);
        return age switch
        {
            GalaxyAge.Young => Interpolate(d, YoungStops),
            GalaxyAge.Old   => Interpolate(d, OldStops),
            _               => Interpolate(d, MatureStops),
        };
    }

    private static Color Interpolate(float d, (float d, int r, int g, int b)[] stops)
    {
        for (int i = 1; i < stops.Length; i++)
        {
            if (d <= stops[i].d)
            {
                float t = (d - stops[i - 1].d) / (stops[i].d - stops[i - 1].d);
                return Color.FromArgb(
                    (int)(stops[i - 1].r + t * (stops[i].r - stops[i - 1].r)),
                    (int)(stops[i - 1].g + t * (stops[i].g - stops[i - 1].g)),
                    (int)(stops[i - 1].b + t * (stops[i].b - stops[i - 1].b)));
            }
        }
        var last = stops[^1];
        return Color.FromArgb(last.r, last.g, last.b);
    }
}
