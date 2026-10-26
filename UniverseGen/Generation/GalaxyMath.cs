using UniverseGen.Models;

namespace UniverseGen.Generation;

/// <summary>
/// The numbers the galaxy maths reads from <see cref="GalaxyParams"/>, frozen at one moment so a
/// generator can tell whether the galaxy it was built for has changed.
/// </summary>
internal readonly record struct GalaxyMathParams(
    int   Arms,
    float ArmTightness,
    float ArmWidth,
    float BulgeFactor,
    float BulgeStrength,
    float Dissipation,
    float CoreDensity,
    float ArmDensity,
    int   Age,
    ulong Seed,
    float BarStrength,
    float BarLength,
    int   GalaxyType,
    float DiskThickness,
    float StarDensity,
    GalaxyModel Model,
    float BulgeThickness,
    float HaloStrength)
{
    internal static GalaxyMathParams From(GalaxyParams p) => new(
        p.Arms, p.ArmTightness, p.ArmWidth, p.BulgeFactor, p.BulgeStrength, p.Dissipation,
        p.CoreDensity, p.ArmDensity, (int)p.Age, p.Seed, p.BarStrength, p.BarLength, (int)p.Type,
        p.DiskThickness, p.StarDensity, p.Model, p.BulgeThickness, p.HaloStrength);
}

/// <summary>
/// The galaxy's shape and stars: density maps (face-on, zoomed, edge-on), the stellar environment of
/// any point, and the stars in any chunk of space.
///
/// Ported line for line from the original C++ (GalaxyMath.cpp, UniverseGen.Math) so that every
/// galaxy, every map and every star comes out exactly as it did: the tests check it against
/// fingerprints taken from the C++. Managed code runs anywhere .NET does (Windows, Linux, macOS,
/// a Steam Deck), with no native library to build or ship.
///
/// An instance holds the galaxy's noise tables, built once rather than for every sample (the C++
/// built two or three per chunk). It's immutable, so it can be shared by any number of threads.
///
/// Two models of the third dimension (see <see cref="GalaxyModel"/>). Classic is the original, kept
/// exactly. Realistic keeps every face-on shape and map but gives the galaxy a real structure in depth
/// and real numbers of stars: a thin disk (scale height about 300 ly in a Milky Way) and a thick one,
/// a compact flattened bulge, and a faint spherical halo, calibrated so a Milky Way has a few hundred
/// billion stars and the Sun's neighbourhood about the density it really has.
/// </summary>
internal sealed class GalaxyMath
{
    // ── the Realistic model's constants ──────────────────────────────────────
    // Calibrated against the Milky Way (the tests hold them to it): about 2 × 10¹¹ stars, a few
    // thousandths of a star per cubic light year round the Sun, and a bulge of a sixth or so of the stars.

    /// Stars at density 1 as a share of Classic's 200 a chunk: the local density, not Classic's fifty times too many.
    private const float RealDensityScale = 0.2f;
    /// <summary>
    /// A real disk thins out exponentially from the centre; the face-on shapes barely do. So the disk's
    /// stars are weighted by e^(−r/RadialScale), normalised to keep the disk's total: denser inside,
    /// sparser out where the Sun is.
    /// </summary>
    private const float RadialScale = 0.25f, RadialNorm = 4.56f;
    /// The thick disk: its density in the plane as a share of the thin disk's, and its scale height as a multiple.
    private const float ThickShare = 0.12f, ThickScale = 3.5f;
    /// The bulge's 3-D size as a share of BulgeFactor (which also sizes its glow on the face-on map), and its peak density.
    private const float BulgeRadiusShare = 0.35f, BulgePeak = 2.2f;
    /// The stellar halo: its peak density and its scale length, normalised.
    private const float HaloPeak = 0.0003f, HaloScale = 0.3f;
    /// Below this density there are no stars at all (well under one star a chunk).
    private const float RealEmptyBelow = 1e-5f;
    /// Bumped whenever a change to the maths would give a different galaxy from the same params, so
    /// anything that caches generated data (a server, a client's disk cache) knows to throw it away.
    public const int AlgorithmVersion = 1;

    private const float Pi = MathF.PI;

    public GalaxyMathParams P { get; }

    /// Scale height of the disk (normalised units), after <see cref="GalaxyParams.DiskThickness"/>.
    public float DiskHeight { get; }

    private readonly SimplexNoise _noise;          // the galaxy's own: arms, warp, dissipation
    private readonly SimplexNoise _irregular2;     // an irregular galaxy's second layer
    private readonly SimplexNoise _nursery;        // star-forming regions, apart from the arm shape
    private readonly SimplexNoise _spiralSide;     // edge-on views
    private readonly SimplexNoise _irregularSide;
    private readonly float        _barAngle;

    public GalaxyMath(GalaxyMathParams p)
    {
        P              = p;
        _noise         = new SimplexNoise(p.Seed);
        _irregular2    = new SimplexNoise(p.Seed ^ 0xABCDEF9876543210UL);
        _nursery       = new SimplexNoise(p.Seed ^ 0x3C4D5E6F7A8B9C0DUL);
        _spiralSide    = new SimplexNoise(p.Seed ^ 0xDEADBEEFCAFEBABEUL);
        _irregularSide = new SimplexNoise(p.Seed ^ 0xFF00FF00FF00FF00UL);
        _barAngle      = (p.Seed & 0xFFFF) / 65536.0f * Pi;
        Realistic      = p.Model == GalaxyModel.Realistic;
        DiskHeight     = (Realistic ? RealDiskHeight(p.Age, p.BulgeFactor) : BaseDiskHeight(p)) * p.DiskThickness;
        Empty          = Realistic ? RealEmptyBelow : MinChunkDensity;
    }

    public static GalaxyMath For(GalaxyParams p) => new(GalaxyMathParams.From(p));

    public bool Realistic { get; }

    /// Below this 3-D density a chunk is empty.
    public float Empty { get; }

    /// The disk's scale height before DiskThickness: it grows with age and bulge size.
    internal static float BaseDiskHeight(GalaxyMathParams p) => 0.04f + p.Age * 0.02f + p.BulgeFactor * 0.04f;

    /// The Realistic thin disk's sech² scale height before DiskThickness: 0.012 (600 ly, a 300 ly
    /// exponential scale height) for a mature Milky Way, a little more for older or bulgier galaxies.
    internal static float RealDiskHeight(int age, float bulgeFactor) => 0.008f + age * 0.002f + bulgeFactor * 0.01f;

    /// <summary>
    /// How high above the plane globular clusters must sit to be clear of the disk: Classic's disk height,
    /// or the Realistic thick disk's.
    /// </summary>
    internal static float ClusterDiskHeight(GalaxyParams p) => p.Model == GalaxyModel.Realistic
        ? RealDiskHeight((int)p.Age, p.BulgeFactor) * ThickScale * p.DiskThickness
        : (0.04f + (int)p.Age * 0.02f + p.BulgeFactor * 0.04f) * p.DiskThickness;

    private static float Sech2(float x)
    {
        float sech = 1.0f / MathF.Cosh(x);
        return sech * sech;
    }

    /// The Realistic bulge's 3-D Gaussian width.
    private float BulgeSigma => P.BulgeFactor * BulgeRadiusShare;
    private float BulgeAmplitude => P.BulgeStrength * P.CoreDensity * BulgePeak;
    private float HaloAmplitude => P.HaloStrength * HaloPeak;

    // std::max and std::min exactly, ties and signed zeros included, so results match the C++ bit for bit.
    private static float Max(float a, float b) => a < b ? b : a;
    private static float Min(float a, float b) => b < a ? b : a;
    private static float Max(float a, float b, float c) => Max(Max(a, b), c);
    private static float Max(float a, float b, float c, float d) => Max(Max(Max(a, b), c), d);

    // ── viewport helpers ─────────────────────────────────────────────────────
    //
    // All face-on generators take a viewport (vx,vy,vw,vh) in normalised [0,1] galaxy space; the
    // full galaxy is (0,0,1,1). Noise scale is base/vw, so the coarsest octave spans about the same
    // share of any viewport and each zoom shows real sub-structure rather than a smooth blob. The
    // domain warp is clamped to a share of the viewport so the arms stay legible deep in.

    private static float VNx(int x, int w, float vx, float vw)
    {
        float u = w > 1 ? (float)x / (w - 1) : 0.5f;
        return 2.0f * (vx + u * vw) - 1.0f;
    }

    private static float VNy(int y, int h, float vy, float vh)
    {
        float v = h > 1 ? (float)y / (h - 1) : 0.5f;
        return 2.0f * (vy + v * vh) - 1.0f;
    }

    // ── shared primitives ────────────────────────────────────────────────────

    private static float SpiralArmDensity(float nx, float ny, int arms, float tightness, float armWidth)
    {
        float r = MathF.Sqrt(nx*nx + ny*ny);
        if (r < 0.005f) return 1.0f;
        float theta = MathF.Atan2(ny, nx);
        float minAngDist = Pi;
        for (int i = 0; i < arms; i++)
        {
            float armBase     = 2.0f * Pi * i / arms;
            float spiralTheta = MathF.Log(r) / tightness + armBase;
            float dTheta      = (theta - spiralTheta) % (2.0f * Pi);
            if (dTheta < -Pi) dTheta += 2.0f * Pi;
            if (dTheta >  Pi) dTheta -= 2.0f * Pi;
            float dist = MathF.Abs(dTheta);
            if (dist < minAngDist) minAngDist = dist;
        }
        float scaledDist = minAngDist * r;
        return MathF.Exp(-scaledDist * scaledDist / (2.0f * armWidth * armWidth));
    }

    private static float BulgeDensity(float nx, float ny, float bulgeRadius, float bulgeStrength)
    {
        float r2 = nx*nx + ny*ny, br2 = bulgeRadius * bulgeRadius;
        return bulgeStrength * MathF.Exp(-r2 / (2.0f * br2));
    }

    private static float BarDensity(float nx, float ny, float barLength, float barAngle, float barStrength)
    {
        if (barStrength <= 0.0f) return 0.0f;
        float barWidth = barLength * 0.22f;
        float cosA = MathF.Cos(barAngle), sinA = MathF.Sin(barAngle);
        float bx = nx*cosA + ny*sinA, by = -nx*sinA + ny*cosA;
        float r2 = (bx/barLength)*(bx/barLength) + (by/barWidth)*(by/barWidth);
        if (r2 >= 1.0f) return 0.0f;
        return barStrength * (1.0f - r2) * (1.0f - r2);
    }

    private static float BarredSpiralDensity(float nx, float ny, int numArms, float tightness,
                                             float armWidth, float barLength, float barAngle)
    {
        float maxD   = 0.0f;
        int   perTip = Math.Max(1, numArms / 2);
        for (int tip = 0; tip < 2; tip++)
        {
            float tipAngle = barAngle + tip * Pi;
            float ox = barLength * MathF.Cos(tipAngle), oy = barLength * MathF.Sin(tipAngle);
            float dx = nx - ox, dy = ny - oy;
            float r  = MathF.Sqrt(dx*dx + dy*dy);
            if (r < 0.005f) { maxD = 1.0f; continue; }
            float theta = MathF.Atan2(dy, dx);
            for (int i = 0; i < perTip; i++)
            {
                float armBase     = tipAngle + Pi * 0.5f + 2.0f * Pi * i / perTip;
                float spiralTheta = MathF.Log(r + 0.001f) / tightness + armBase;
                float dTheta      = (theta - spiralTheta) % (2.0f * Pi);
                if (dTheta < -Pi) dTheta += 2.0f * Pi;
                if (dTheta >  Pi) dTheta -= 2.0f * Pi;
                maxD = Max(maxD, MathF.Exp(-(MathF.Abs(dTheta)*r)*(MathF.Abs(dTheta)*r) / (2.0f*armWidth*armWidth)));
            }
        }
        return maxD;
    }

    private float Spiral(float x, float y) => P.BarStrength > 0.0f
        ? SpiralArmDensity(x, y, P.Arms, P.ArmTightness, P.ArmWidth) * (1.0f - P.BarStrength)
          + BarredSpiralDensity(x, y, P.Arms, P.ArmTightness, P.ArmWidth, P.BarLength, _barAngle) * P.BarStrength
        : SpiralArmDensity(x, y, P.Arms, P.ArmTightness, P.ArmWidth);

    // ── face-on density maps ─────────────────────────────────────────────────

    /// Fills <paramref name="output"/> (row-major, width × height) with the face-on density of the
    /// viewport (vx,vy,vw,vh) in normalised [0,1] galaxy space; (0,0,1,1) is the whole galaxy.
    public void DensityMap(Span<float> output, int width, int height, float vx = 0, float vy = 0, float vw = 1, float vh = 1)
    {
        if (output.Length < width * height) throw new ArgumentException("The output is smaller than width × height.", nameof(output));
        switch (P.GalaxyType)
        {
            case 1:  Elliptical(output, width, height, vx, vy, vw, vh); break;
            case 2:  Lenticular(output, width, height, vx, vy, vw, vh); break;
            case 3:  Irregular (output, width, height, vx, vy, vw, vh); break;
            default: Spiral    (output, width, height, vx, vy, vw, vh); break;
        }
    }

    private void Spiral(Span<float> o, int w, int h, float vx, float vy, float vw, float vh)
    {
        float noiseScale  = (P.Age == 2 ? 2.5f : 3.0f) / vw;
        float dissipNoise = P.Dissipation * 0.4f;
        float warpAmp     = P.Dissipation * 0.8f * Min(1.0f, vw / 0.04f);

        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float nx = VNx(x, w, vx, vw), ny = VNy(y, h, vy, vh);
            float r  = MathF.Sqrt(nx*nx + ny*ny);
            if (r > 1.0f) { o[y*w+x] = 0.0f; continue; }

            float n  = (_noise.Octave2D(nx*noiseScale, ny*noiseScale, 4, 0.5f) + 1.0f) * 0.5f;
            float n2 = _noise.Noise2D(nx*noiseScale + 100.0f, ny*noiseScale);
            float spiral = Spiral(nx + warpAmp * (n - 0.5f), ny + warpAmp * (n2 - 0.5f));

            float bulge = BulgeDensity(nx, ny, P.BulgeFactor, P.BulgeStrength);
            float bar   = BarDensity(nx, ny, P.BarLength, _barAngle, P.BarStrength);
            float halo  = 0.055f * MathF.Exp(-r / 0.55f);

            float d = Max(spiral*P.ArmDensity, bulge*P.CoreDensity, bar*P.CoreDensity, halo);
            d += n * dissipNoise;
            d  = Min(d, 1.0f);
            float fade = 1.0f - r*r;
            o[y*w+x] = d * fade * fade;
        }
    }

    private (float cos, float sin, float axisB) EllipseShape()
    {
        float eFlat  = ((P.Seed >> 16) & 0xFF) / 510.0f;
        float eAngle = (P.Seed & 0xFFFF) / 65536.0f * Pi;
        return (MathF.Cos(eAngle), MathF.Sin(eAngle), 1.0f - eFlat + 0.01f);
    }

    private void Elliptical(Span<float> o, int w, int h, float vx, float vy, float vw, float vh)
    {
        var (cosE, sinE, axisB) = EllipseShape();
        float noiseScale = 2.5f / vw;

        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float nx = VNx(x, w, vx, vw), ny = VNy(y, h, vy, vh);

            float ex =  nx*cosE + ny*sinE;
            float ey = (-nx*sinE + ny*cosE) / axisB;
            float re = MathF.Sqrt(ex*ex + ey*ey);
            if (re > 1.0f) { o[y*w+x] = 0.0f; continue; }

            float d = MathF.Exp(-4.0f*re*re) + 0.35f*MathF.Exp(-1.8f*re);
            d = Min(d, 1.0f);
            float n = (_noise.Octave2D(nx*noiseScale, ny*noiseScale, 3, 0.5f) + 1.0f) * 0.5f;
            d += n * P.Dissipation * 0.06f;
            o[y*w+x] = Min(d, 1.0f);
        }
    }

    private void Lenticular(Span<float> o, int w, int h, float vx, float vy, float vw, float vh)
    {
        float noiseScale = 2.5f / vw;

        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float nx = VNx(x, w, vx, vw), ny = VNy(y, h, vy, vh);
            float r  = MathF.Sqrt(nx*nx + ny*ny);
            if (r > 1.0f) { o[y*w+x] = 0.0f; continue; }

            float disk  = P.ArmDensity * MathF.Exp(-r / 0.42f);
            float bulge = BulgeDensity(nx, ny, P.BulgeFactor, P.BulgeStrength);
            float halo  = 0.04f * MathF.Exp(-r / 0.55f);
            float n = (_noise.Octave2D(nx*noiseScale, ny*noiseScale, 3, 0.5f) + 1.0f) * 0.5f;
            float d = Max(disk, bulge*P.CoreDensity, halo);
            d += n * P.Dissipation * 0.08f;
            d  = Min(d, 1.0f);
            float fade = 1.0f - r*r;
            o[y*w+x] = d * fade;
        }
    }

    private void Irregular(Span<float> o, int w, int h, float vx, float vy, float vw, float vh)
    {
        float s1 = 1.8f / vw, s2 = 5.5f / vw;

        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float nx = VNx(x, w, vx, vw), ny = VNy(y, h, vy, vh);
            float r  = MathF.Sqrt(nx*nx + ny*ny);
            if (r > 1.0f) { o[y*w+x] = 0.0f; continue; }

            float n1 = (_noise.Octave2D     (nx*s1 + 0.4f, ny*s1 - 0.25f, 5, 0.65f) + 1.0f) * 0.5f;
            float n2 = (_irregular2.Octave2D(nx*s2,        ny*s2,         3, 0.55f) + 1.0f) * 0.5f;
            float d  = n1*0.65f + n2*0.35f;
            d *= Max(0.0f, 1.0f - r*r*0.9f);
            o[y*w+x] = Min(d, 1.0f);
        }
    }

    // ── edge-on density maps ─────────────────────────────────────────────────
    // Side views always show the whole galaxy: width is the radial axis, height the vertical.

    public void SideView(Span<float> output, int width, int height)
    {
        if (output.Length < width * height) throw new ArgumentException("The output is smaller than width × height.", nameof(output));
        switch (P.GalaxyType)
        {
            case 1:  EllipticalSide(output, width, height); break;
            case 2:  LenticularSide(output, width, height); break;
            case 3:  IrregularSide (output, width, height); break;
            default: SpiralSide    (output, width, height); break;
        }
    }

    private void SpiralSide(Span<float> o, int w, int h)
    {
        float diskHeight = DiskHeight;
        float diskRadius = 0.75f;
        // Realistic: a compact bulge flattened by BulgeThickness, and a dust lane as thin as the disk.
        float bulgeR     = Realistic ? BulgeSigma * 2.0f : P.BulgeFactor * 0.6f + 0.04f;
        float bulgeFlat  = Realistic ? P.BulgeThickness : 0.6f;
        float lane       = Realistic ? diskHeight * 0.5f : 0.013f;

        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float r = 2.0f*x/(w-1) - 1.0f;
            float z = 2.0f*y/(h-1) - 1.0f;

            float sech  = 1.0f / MathF.Cosh(z / diskHeight);
            float layer = Realistic ? Min(Sech2(z / diskHeight) + ThickShare * Sech2(z / (diskHeight * ThickScale)), 1.0f) : sech * sech;
            float disk  = Realistic ? MathF.Exp(-MathF.Abs(r) / diskRadius) * layer : MathF.Exp(-MathF.Abs(r) / diskRadius) * sech * sech;
            float bDist = MathF.Sqrt(r*r + (z/bulgeFlat)*(z/bulgeFlat));
            float bulge = P.BulgeStrength * MathF.Exp(-bDist / bulgeR);
            float n     = (_spiralSide.Octave2D(r*5.0f, z*5.0f, 3, 0.5f) + 1.0f) * 0.5f;

            float d = Max(disk*P.ArmDensity, bulge*P.CoreDensity);
            // Realistic: the patchy glow only where there are stars, not filling the space above the disk.
            d += n * P.Dissipation * 0.12f * (Realistic ? Min(layer * 4.0f, 1.0f) : 1.0f);
            d  = Min(d, 1.0f);

            float dustLane  = MathF.Exp(-z*z / (2.0f * lane * lane));
            float bulgeMask = MathF.Exp(-bDist / bulgeR);
            d *= 1.0f - dustLane * (1.0f - bulgeMask) * 0.82f;

            float fade = 1.0f - r*r;
            o[y*w+x] = d * fade * fade;
        }
    }

    private void EllipticalSide(Span<float> o, int w, int h)
    {
        float eFlat = ((P.Seed >> 16) & 0xFF) / 510.0f;
        float axisB = 1.0f - eFlat * 0.6f + 0.01f;

        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float r = 2.0f*x/(w-1) - 1.0f;
            float z = 2.0f*y/(h-1) - 1.0f;
            float re = MathF.Sqrt(r*r + (z/axisB)*(z/axisB));
            if (re > 1.0f) { o[y*w+x] = 0.0f; continue; }
            float d = MathF.Exp(-4.0f*re*re) + 0.35f*MathF.Exp(-1.8f*re);
            o[y*w+x] = Min(d, 1.0f);
        }
    }

    private void LenticularSide(Span<float> o, int w, int h)
    {
        // Realistic: a lenticular's disk is thicker than a spiral's, its bulge bigger.
        float diskHeight = Realistic ? DiskHeight * 1.5f : (0.07f + P.BulgeFactor * 0.05f) * P.DiskThickness;
        float diskRadius = 0.70f;
        float bulgeR     = Realistic ? BulgeSigma * 1.8f : P.BulgeFactor * 0.55f + 0.05f;
        float bulgeFlat  = Realistic ? P.BulgeThickness * 1.15f : 0.7f;

        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float r = 2.0f*x/(w-1) - 1.0f;
            float z = 2.0f*y/(h-1) - 1.0f;
            float sech  = 1.0f / MathF.Cosh(z / diskHeight);
            float layer = Realistic ? Min(Sech2(z / diskHeight) + ThickShare * Sech2(z / (diskHeight * ThickScale)), 1.0f) : sech * sech;
            float disk  = Realistic ? MathF.Exp(-MathF.Abs(r) / diskRadius) * layer : MathF.Exp(-MathF.Abs(r) / diskRadius) * sech * sech;
            float bDist = MathF.Sqrt(r*r + (z/bulgeFlat)*(z/bulgeFlat));
            float bulge = P.BulgeStrength * MathF.Exp(-bDist / bulgeR);
            float d = Max(disk*P.ArmDensity, bulge*P.CoreDensity);
            float fade = 1.0f - r*r;
            o[y*w+x] = Min(d, 1.0f) * fade;
        }
    }

    private void IrregularSide(Span<float> o, int w, int h)
    {
        float spread = 0.18f * P.DiskThickness;

        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float r = 2.0f*x/(w-1) - 1.0f;
            float z = 2.0f*y/(h-1) - 1.0f;
            float n = (_irregularSide.Octave2D(r*2.5f, z*2.5f, 4, 0.6f) + 1.0f) * 0.5f;
            float vertFade = MathF.Exp(-z*z / (spread * spread));
            float radFade  = 1.0f - r*r;
            o[y*w+x] = Min(n * vertFade * radFade, 1.0f);
        }
    }

    // ── point samples (full-galaxy scale) ────────────────────────────────────
    // The face-on maps at one point, with full-galaxy noise scale: what star chunks sample.

    /// Face-on density (0–1) at a point in the galactic plane, in normalised coordinates.
    public float SampleDensity(float nx, float nz) => P.GalaxyType switch
    {
        1 => SampleElliptical(nx, nz),
        2 => SampleLenticular(nx, nz),
        3 => SampleIrregular(nx, nz),
        _ => SampleSpiral(nx, nz),
    };

    private float SampleSpiral(float nx, float nz)
    {
        float r = MathF.Sqrt(nx*nx + nz*nz);
        if (r > 1.0f) return 0.0f;

        float noiseScale = P.Age == 2 ? 2.5f : 3.0f;
        float warpAmp    = P.Dissipation * 0.8f;

        float n  = (_noise.Octave2D(nx*noiseScale, nz*noiseScale, 4, 0.5f) + 1.0f) * 0.5f;
        float n2 = _noise.Noise2D(nx*noiseScale + 100.0f, nz*noiseScale);
        float spiral = Spiral(nx + warpAmp * (n - 0.5f), nz + warpAmp * (n2 - 0.5f));

        float bulge = BulgeDensity(nx, nz, P.BulgeFactor, P.BulgeStrength);
        float bar   = BarDensity(nx, nz, P.BarLength, _barAngle, P.BarStrength);
        float halo  = 0.055f * MathF.Exp(-r / 0.55f);

        float d = Max(spiral*P.ArmDensity, bulge*P.CoreDensity, bar*P.CoreDensity, halo);
        d += n * P.Dissipation * 0.4f;
        d  = Min(d, 1.0f);
        float fade = 1.0f - r*r;
        return d * fade * fade;
    }

    private float SampleElliptical(float nx, float nz)
    {
        var (cosE, sinE, axisB) = EllipseShape();
        float ex =  nx*cosE + nz*sinE;
        float ez = (-nx*sinE + nz*cosE) / axisB;
        float re = MathF.Sqrt(ex*ex + ez*ez);
        if (re > 1.0f) return 0.0f;
        float d = MathF.Exp(-4.0f*re*re) + 0.35f*MathF.Exp(-1.8f*re);
        return Min(d, 1.0f);
    }

    private float SampleLenticular(float nx, float nz)
    {
        float r = MathF.Sqrt(nx*nx + nz*nz);
        if (r > 1.0f) return 0.0f;
        float disk  = P.ArmDensity * MathF.Exp(-r / 0.42f);
        float bulge = BulgeDensity(nx, nz, P.BulgeFactor, P.BulgeStrength);
        float halo  = 0.04f * MathF.Exp(-r / 0.55f);
        float d = Max(disk, bulge*P.CoreDensity, halo);
        float fade = 1.0f - r*r;
        return Min(d, 1.0f) * fade;
    }

    private float SampleIrregular(float nx, float nz)
    {
        float r = MathF.Sqrt(nx*nx + nz*nz);
        if (r > 1.0f) return 0.0f;
        float n1 = (_noise.Octave2D     (nx*1.8f + 0.4f, nz*1.8f - 0.25f, 5, 0.65f) + 1.0f) * 0.5f;
        float n2 = (_irregular2.Octave2D(nx*5.5f,        nz*5.5f,         3, 0.55f) + 1.0f) * 0.5f;
        float d  = n1*0.65f + n2*0.35f;
        d *= Max(0.0f, 1.0f - r*r*0.9f);
        return Min(d, 1.0f);
    }

    // ── region classifier ────────────────────────────────────────────────────

    /// The stellar environment at a point in normalised galaxy space.
    public RegionType ClassifyRegion(float cx, float cy, float cz)
    {
        float r2d   = MathF.Sqrt(cx*cx + cz*cz);
        float r3d   = MathF.Sqrt(cx*cx + cy*cy + cz*cz);
        float diskH = DiskHeight;

        if (Realistic)
        {
            // Halo: beyond the thick disk, or far out and off the thin one
            if (MathF.Abs(cy) > diskH * ThickScale * 3.0f || (r2d > 0.55f && MathF.Abs(cy) > diskH * 6.0f))
                return RegionType.Halo;
            // Bulge: within two widths of its flattened centre
            float q = P.BulgeThickness;
            float sigma = BulgeSigma;
            if (cx*cx + cz*cz + (cy/q)*(cy/q) < 4.0f * sigma * sigma)
                return RegionType.Bulge;
        }
        else
        {
            // Halo: well above or below the disk, or far out and off the plane
            if (MathF.Abs(cy) > diskH * 4.0f || (r2d > 0.55f && MathF.Abs(cy) > diskH * 1.5f))
                return RegionType.Halo;

            // Bulge: the central sphere
            float bulgeR = P.BulgeFactor * 0.6f + 0.04f;
            if (r3d < bulgeR * 1.8f)
                return RegionType.Bulge;
        }

        float d2d = SampleDensity(cx, cz);

        // Void: in the plane but very sparse
        if (d2d < 0.06f)
            return RegionType.Void;

        // Nursery: peak arm density with a young-star noise trigger, on its own seed so nurseries
        // don't just echo the arm shape
        if (d2d > 0.60f && r2d > 0.08f && _nursery.Noise2D(cx * 14.0f, cz * 14.0f) > 0.38f)
            return RegionType.Nursery;

        return d2d > 0.28f ? RegionType.DiskArm : RegionType.DiskInter;
    }

    // ── stars ────────────────────────────────────────────────────────────────

    private static ulong StarHash(long cx, long cy, long cz, int idx, ulong seed)
    {
        ulong h = (ulong)(uint)cx  * 0x9e3779b97f4a7c15UL
                ^ (ulong)(uint)cy  * 0x6c62272e07bb0142UL
                ^ (ulong)(uint)cz  * 0x517cc1b727220a95UL
                ^ (ulong)(uint)idx * 0xd9c4a9a0ab96cceaUL
                ^ seed;
        h = (h ^ (h >> 30)) * 0xbf58476d1ce4e5b9UL;
        h = (h ^ (h >> 27)) * 0x94d049bb133111ebUL;
        return h ^ (h >> 31);
    }

    // Per-region spectral class distributions, cumulative, in O B A F G K M WD NS BH order.
    private static readonly float[][] RegionCumulative =
    [
        // Spiral arm: standard Salpeter IMF
        [0.000006f, 0.001306f, 0.007306f, 0.037306f, 0.113306f, 0.234306f, 0.999306f, 0.999956f, 0.999991f, 1.0f],
        // Inter-arm disk: slightly older; fewer O/B, more K/M
        [0.000002f, 0.000400f, 0.003500f, 0.022000f, 0.085000f, 0.210000f, 0.999200f, 0.999960f, 0.999992f, 1.0f],
        // Bulge: old, metal-rich; K/M dominant, almost no O/B
        [0.0000005f, 0.000060f, 0.000500f, 0.004000f, 0.038000f, 0.165000f, 0.999000f, 0.999700f, 0.999950f, 1.0f],
        // Halo: ancient, metal-poor; K/M dominant, no O/B, more white dwarfs
        [0.0000001f, 0.000020f, 0.000200f, 0.002000f, 0.025000f, 0.130000f, 0.994000f, 0.999600f, 0.999940f, 1.0f],
        // Nursery: hot young OB stars over-represented
        [0.003000f, 0.032000f, 0.085000f, 0.155000f, 0.255000f, 0.380000f, 0.978000f, 0.992000f, 0.998000f, 1.0f],
        // Void: very sparse old field stars; K/M heavy, almost no O/B
        [0.0000005f, 0.000050f, 0.000400f, 0.003500f, 0.035000f, 0.155000f, 0.998000f, 0.999700f, 0.999950f, 1.0f],
    ];

    // Base luminosity per spectral class (O B A F G K M WD NS BH)
    private static readonly float[] BaseLuminosity = [0.95f, 0.80f, 0.62f, 0.48f, 0.36f, 0.26f, 0.10f, 0.18f, 0.22f, 0.30f];

    private static SpectralClass PickSpectralClass(float r, RegionType region)
    {
        float[] cumulative = RegionCumulative[(int)region < 6 ? (int)region : 0];
        for (int i = 0; i < 10; i++)
            if (r < cumulative[i]) return (SpectralClass)i;
        return SpectralClass.BlackHole;
    }

    /// Below this a chunk is empty: the far halo, beyond the disk's edge.
    private const float MinChunkDensity = 0.002f;

    /// The index whose hash rolls a sparse chunk's fractional star (see StarCount). No star uses it.
    private const int CountRollIndex = -1;

    /// The most stars a Classic chunk can hold at this StarDensity.
    public static int ChunkCapacity(int maxStars, float starDensity) =>
        starDensity <= 1.0f ? maxStars : (int)MathF.Ceiling(maxStars * starDensity);

    /// The stars a chunk of density 1 is expected to hold.
    public float StarsPerDensity(int maxStars) => maxStars * P.StarDensity * (Realistic ? RealDensityScale : 1.0f);

    /// The most stars any chunk of this galaxy can hold.
    public int Capacity(int maxStars) => Realistic
        ? (int)MathF.Ceiling(StarsPerDensity(maxStars) * ((1.0f + ThickShare) * RadialNorm + BulgeAmplitude + HaloAmplitude)) + 1
        : ChunkCapacity(maxStars, P.StarDensity);

    /// Whether counts round to the nearest (Classic at StarDensity 1 or more, as it always did) or roll for their fraction.
    public bool RoundsToNearest => !Realistic && P.StarDensity >= 1.0f;

    /// <summary>
    /// The stars in the chunk at (chunkX, chunkY, chunkZ), whose centre is (chunkX, chunkY, chunkZ) × chunkSize
    /// in normalised space (X and Z in the plane, Y vertical). <paramref name="maxStars"/> is a chunk's
    /// count at density 1, before StarDensity.
    /// </summary>
    public Star[] StarChunk(long chunkX, long chunkY, long chunkZ, float chunkSize, int maxStars, out RegionType region)
    {
        float cx = chunkX * chunkSize;
        float cy = chunkY * chunkSize;
        float cz = chunkZ * chunkSize;
        region = ClassifyRegion(cx, cy, cz);

        if (maxStars <= 0) return [];

        float d3d = ChunkDensity(cx, cy, cz);
        if (d3d < Empty) return [];

        int count = StarCount(d3d, chunkX, chunkY, chunkZ, maxStars);
        if (count <= 0) return [];

        var stars = new Star[count];
        for (int i = 0; i < count; i++)
        {
            ulong h0 = StarHash(chunkX, chunkY, chunkZ, i*4+0, P.Seed);
            ulong h1 = StarHash(chunkX, chunkY, chunkZ, i*4+1, P.Seed);
            ulong h2 = StarHash(chunkX, chunkY, chunkZ, i*4+2, P.Seed);
            ulong h3 = StarHash(chunkX, chunkY, chunkZ, i*4+3, P.Seed);

            // Scattered anywhere in the chunk
            float px = cx + ((h0 & 0xFFFFFF) / (float)0xFFFFFF - 0.5f) * chunkSize;
            float py = cy + ((h1 & 0xFFFFFF) / (float)0xFFFFFF - 0.5f) * chunkSize;
            float pz = cz + ((h2 & 0xFFFFFF) / (float)0xFFFFFF - 0.5f) * chunkSize;

            // Spectral class from the region's initial mass function
            float classR = ((h0 >> 24) & 0xFFFF) / (float)0x10000;
            SpectralClass sc = PickSpectralClass(classR, region);

            // Luminosity: the class's base, varied a little
            float lumVar = (((h1 >> 24) & 0xFF) / 255.0f - 0.5f) * 0.15f;
            float lum    = Max(0.0f, Min(1.0f, BaseLuminosity[(int)sc] + lumVar));

            var flags = StarFlags.None;
            if (((h2 >> 24) & 0xFF) < 128) flags |= StarFlags.Binary;   // ~50% binary
            float gR = (h3 & 0xFFFF) / (float)0x10000;
            // Giant and supergiant rates vary by region
            float giantRate      = region == RegionType.Nursery ? 0.06f  : region >= RegionType.Bulge ? 0.05f  : 0.03f;
            float supergiantRate = region == RegionType.Nursery ? 0.008f : region >= RegionType.Bulge ? 0.000f : 0.001f;
            if (sc >= SpectralClass.G && sc <= SpectralClass.M && gR < giantRate)
                { flags |= StarFlags.Giant; lum = Min(1.0f, lum * 3.0f); }
            if (sc <= SpectralClass.A && gR < supergiantRate)
                { flags |= StarFlags.Supergiant; lum = 1.0f; }

            stars[i] = new Star(px, py, pz, sc, (byte)(lum * 255.0f), flags);
        }
        return stars;
    }

    /// How many stars the chunk holds, without making them: for counting a whole galaxy.
    public int StarCount(long chunkX, long chunkY, long chunkZ, float chunkSize, int maxStars)
    {
        if (maxStars <= 0) return 0;
        float d3d = ChunkDensity(chunkX * chunkSize, chunkY * chunkSize, chunkZ * chunkSize);
        return d3d < Empty ? 0 : Math.Max(StarCount(d3d, chunkX, chunkY, chunkZ, maxStars), 0);
    }

    /// The 3-D density (0–1, before StarDensity) at a chunk's centre.
    private float ChunkDensity(float cx, float cy, float cz) => ChunkDensity(SampleDensity(cx, cz), cx, cy, cz);

    /// The same, given the plane's density beneath the point (for a whole column at once).
    internal float ChunkDensity(float density2d, float cx, float cy, float cz)
    {
        if (Realistic)
        {
            // Thin and thick disks over the plane's density, a flattened Gaussian bulge, and a
            // spherical halo: the components add, as stars do.
            float radial = RadialNorm * MathF.Exp(-MathF.Sqrt(cx*cx + cz*cz) / RadialScale);
            float disk = density2d * radial * (Sech2(cy / DiskHeight) + ThickShare * Sech2(cy / (DiskHeight * ThickScale)));
            float q = P.BulgeThickness, sigma = BulgeSigma;
            float rb2 = cx*cx + cz*cz + (cy/q)*(cy/q);
            float bulge = BulgeAmplitude * MathF.Exp(-rb2 / (2.0f * sigma * sigma));
            float halo = HaloAmplitude * MathF.Exp(-MathF.Sqrt(cx*cx + cy*cy + cz*cz) / HaloScale);
            return disk + bulge + halo;
        }

        // The disk's vertical sech² profile over the plane's density
        float sech  = 1.0f / MathF.Cosh(cy / DiskHeight);
        float d3d   = density2d * sech * sech;

        // The spherical bulge reaches out of the plane everywhere within it
        float r3 = MathF.Sqrt(cx*cx + cy*cy + cz*cz);
        float bulgeSph = P.BulgeStrength * P.CoreDensity
                       * MathF.Exp(-r3*r3 / (2.0f * P.BulgeFactor * P.BulgeFactor));
        return Max(d3d, bulgeSph);
    }

    /// The 3-D density at any point in the galaxy (before StarDensity); under <see cref="Empty"/>, no stars.
    public float DensityAt(float x, float y, float z) => ChunkDensity(x, y, z);

    /// <summary>
    /// How many stars a chunk of this density holds. At StarDensity 1 or more it's rounded to the
    /// nearest, as it always was. Below 1 (a sparse galaxy) rounding would empty every chunk that
    /// expects less than half a star, so instead each chunk rolls its fractional star, from its own hash.
    /// </summary>
    private int StarCount(float d3d, long chunkX, long chunkY, long chunkZ, int maxStars)
    {
        float expected = Realistic ? d3d * StarsPerDensity(maxStars) : d3d * maxStars * P.StarDensity;
        int count;
        if (RoundsToNearest)
            count = (int)(expected + 0.5f);
        else
        {
            count = (int)expected;
            float roll = (StarHash(chunkX, chunkY, chunkZ, CountRollIndex, P.Seed) & 0xFFFFFF) / (float)0x1000000;
            if (roll < expected - count) count++;
        }
        return Math.Min(count, Capacity(maxStars));
    }

    /// <summary>
    /// How far above or below the plane (normalised units) a chunk can hold stars at all: beyond it
    /// both the disk and the bulge are under the empty-chunk threshold. Lets a query skip empty space
    /// without generating it. Conservative: it never excludes a chunk that has stars.
    /// </summary>
    public float VerticalExtent()
    {
        if (Realistic)
        {
            // Each of the four parts under a quarter of the threshold, so their sum is under it. The
            // plane's density (with its radial weight) is at most RadialNorm, and |y| is no more than
            // any distance from the centre.
            float t = RealEmptyBelow / 4.0f;
            float thin = DiskHeight * MathF.Acosh(MathF.Sqrt(RadialNorm / t));
            float thick = ThickShare * RadialNorm > t ? DiskHeight * ThickScale * MathF.Acosh(MathF.Sqrt(ThickShare * RadialNorm / t)) : 0.0f;
            float bulgeTop = BulgeAmplitude > t ? P.BulgeThickness * BulgeSigma * MathF.Sqrt(2.0f * MathF.Log(BulgeAmplitude / t)) : 0.0f;
            float haloTop = HaloAmplitude > t ? HaloScale * MathF.Log(HaloAmplitude / t) : 0.0f;
            return MathF.Max(MathF.Max(thin, thick), MathF.Max(bulgeTop, haloTop)) * 1.01f;
        }

        // sech²(y/h) < MinChunkDensity once y > h·acosh(1/√MinChunkDensity); the plane's density is at most 1
        float disk  = DiskHeight * MathF.Acosh(1.0f / MathF.Sqrt(MinChunkDensity));
        float peak  = P.BulgeStrength * P.CoreDensity;
        float bulge = peak > MinChunkDensity
            ? MathF.Abs(P.BulgeFactor) * MathF.Sqrt(2.0f * MathF.Log(peak / MinChunkDensity))
            : 0.0f;
        // A little headroom for float rounding at the boundary
        return MathF.Max(disk, bulge) * 1.01f;
    }
}
