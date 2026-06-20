#include "../include/UniverseGenMath.h"
#include "Noise.h"
#include <cmath>
#include <algorithm>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

// ── viewport helpers ─────────────────────────────────────────────────────────
//
// All face-on generators accept a viewport (vx,vy,vw,vh) in normalised [0,1]
// galaxy space.  Full-galaxy call passes (0,0,1,1).  A zoomed call maps the
// same output grid onto the requested sub-region.
//
// Noise scale uses base/vw (linear with zoom factor) so that the coarsest
// noise octave always spans roughly the same fraction of the viewport,
// producing visible sub-structure at every zoom depth rather than a smooth blob.
//
// Domain warp amplitude is clamped to a fraction of the viewport size so the
// macro arm structure remains legible at high zoom — without this the warp
// exceeds the viewport width and every pixel sees a different random arm phase.

static inline float vNx(int x, int w, float vx, float vw)
{
    float u = (w > 1) ? (float)x / (w - 1) : 0.5f;
    return 2.0f * (vx + u * vw) - 1.0f;
}
static inline float vNy(int y, int h, float vy, float vh)
{
    float v = (h > 1) ? (float)y / (h - 1) : 0.5f;
    return 2.0f * (vy + v * vh) - 1.0f;
}

// ── shared primitives ────────────────────────────────────────────────────────

static float SpiralArmDensity(float nx, float ny, int arms, float tightness, float armWidth)
{
    float r = sqrtf(nx*nx + ny*ny);
    if (r < 0.005f) return 1.0f;
    float theta = atan2f(ny, nx);
    float minAngDist = (float)M_PI;
    for (int i = 0; i < arms; i++)
    {
        float armBase     = (2.0f * (float)M_PI * i) / arms;
        float spiralTheta = logf(r) / tightness + armBase;
        float dTheta      = fmodf(theta - spiralTheta, 2.0f * (float)M_PI);
        if (dTheta < -(float)M_PI) dTheta += 2.0f * (float)M_PI;
        if (dTheta >  (float)M_PI) dTheta -= 2.0f * (float)M_PI;
        float dist = fabsf(dTheta);
        if (dist < minAngDist) minAngDist = dist;
    }
    float scaledDist = minAngDist * r;
    return expf(-scaledDist * scaledDist / (2.0f * armWidth * armWidth));
}

static float BulgeDensity(float nx, float ny, float bulgeRadius, float bulgeStrength)
{
    float r2 = nx*nx + ny*ny, br2 = bulgeRadius * bulgeRadius;
    return bulgeStrength * expf(-r2 / (2.0f * br2));
}

static float BarDensity(float nx, float ny, float barLength, float barAngle, float barStrength)
{
    if (barStrength <= 0.0f) return 0.0f;
    float barWidth = barLength * 0.22f;
    float cosA = cosf(barAngle), sinA = sinf(barAngle);
    float bx = nx*cosA + ny*sinA, by = -nx*sinA + ny*cosA;
    float r2 = (bx/barLength)*(bx/barLength) + (by/barWidth)*(by/barWidth);
    if (r2 >= 1.0f) return 0.0f;
    return barStrength * (1.0f - r2) * (1.0f - r2);
}

static float BarredSpiralDensity(float nx, float ny, int numArms, float tightness,
                                  float armWidth, float barLength, float barAngle)
{
    float maxD = 0.0f;
    int   perTip = std::max(1, numArms / 2);
    for (int tip = 0; tip < 2; tip++)
    {
        float tipAngle = barAngle + tip * (float)M_PI;
        float ox = barLength * cosf(tipAngle), oy = barLength * sinf(tipAngle);
        float dx = nx - ox, dy = ny - oy;
        float r  = sqrtf(dx*dx + dy*dy);
        if (r < 0.005f) { maxD = 1.0f; continue; }
        float theta = atan2f(dy, dx);
        for (int i = 0; i < perTip; i++)
        {
            float armBase     = tipAngle + (float)M_PI * 0.5f + (2.0f * (float)M_PI * i) / perTip;
            float spiralTheta = logf(r + 0.001f) / tightness + armBase;
            float dTheta      = fmodf(theta - spiralTheta, 2.0f * (float)M_PI);
            if (dTheta < -(float)M_PI) dTheta += 2.0f * (float)M_PI;
            if (dTheta >  (float)M_PI) dTheta -= 2.0f * (float)M_PI;
            maxD = std::max(maxD, expf(-(fabsf(dTheta)*r)*(fabsf(dTheta)*r) / (2.0f*armWidth*armWidth)));
        }
    }
    return maxD;
}

// ── face-on density generators ───────────────────────────────────────────────

static void GenerateSpiral(const GalaxyMathParams* p, float* out, int w, int h,
                            float vx, float vy, float vw, float vh)
{
    SimplexNoise noise(p->seed);
    // base/vw keeps ~3 coarse-octave features visible per viewport at every zoom depth.
    float noiseScale  = ((p->age == 2) ? 2.5f : 3.0f) / vw;
    float dissipNoise = p->dissipation * 0.4f;
    float barAngle    = (float)(p->seed & 0xFFFF) / 65536.0f * (float)M_PI;

    // Clamp warp amplitude to a fraction of the viewport so the arm structure
    // remains legible when zoomed deep. At vw >= 0.04 it's at full strength.
    float warpAmp = p->dissipation * 0.8f * std::min(1.0f, vw / 0.04f);

    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
    {
        float nx = vNx(x, w, vx, vw), ny = vNy(y, h, vy, vh);
        float r  = sqrtf(nx*nx + ny*ny);
        if (r > 1.0f) { out[y*w+x] = 0.0f; continue; }

        float n  = (noise.Octave2D(nx*noiseScale, ny*noiseScale, 4, 0.5f) + 1.0f) * 0.5f;
        float n2 = noise.Noise2D(nx*noiseScale + 100.0f, ny*noiseScale);
        float warpX = nx + warpAmp * (n  - 0.5f);
        float warpY = ny + warpAmp * (n2 - 0.5f);

        float spiral = (p->barStrength > 0.0f)
            ? SpiralArmDensity(warpX, warpY, p->arms, p->armTightness, p->armWidth) * (1.0f-p->barStrength)
              + BarredSpiralDensity(warpX, warpY, p->arms, p->armTightness, p->armWidth, p->barLength, barAngle) * p->barStrength
            : SpiralArmDensity(warpX, warpY, p->arms, p->armTightness, p->armWidth);

        float bulge = BulgeDensity(nx, ny, p->bulgeFactor, p->bulgeStrength);
        float bar   = BarDensity(nx, ny, p->barLength, barAngle, p->barStrength);
        float halo  = 0.055f * expf(-r / 0.55f);

        float d = std::max({ spiral*p->armDensity, bulge*p->coreDensity, bar*p->coreDensity, halo });
        d += n * dissipNoise;
        d  = std::min(d, 1.0f);
        float fade = 1.0f - r*r;
        out[y*w+x] = d * fade * fade;
    }
}

static void GenerateElliptical(const GalaxyMathParams* p, float* out, int w, int h,
                                float vx, float vy, float vw, float vh)
{
    SimplexNoise noise(p->seed);
    float eFlat  = (float)((p->seed >> 16) & 0xFF) / 510.0f;
    float eAngle = (float)(p->seed & 0xFFFF) / 65536.0f * (float)M_PI;
    float cosE = cosf(eAngle), sinE = sinf(eAngle);
    float axisB = 1.0f - eFlat + 0.01f;
    float noiseScale = 2.5f / vw;

    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
    {
        float nx = vNx(x, w, vx, vw), ny = vNy(y, h, vy, vh);

        float ex =  nx*cosE + ny*sinE;
        float ey = (-nx*sinE + ny*cosE) / axisB;
        float re = sqrtf(ex*ex + ey*ey);
        if (re > 1.0f) { out[y*w+x] = 0.0f; continue; }

        float d = expf(-4.0f*re*re) + 0.35f*expf(-1.8f*re);
        d = std::min(d, 1.0f);
        float n = (noise.Octave2D(nx*noiseScale, ny*noiseScale, 3, 0.5f) + 1.0f) * 0.5f;
        d += n * p->dissipation * 0.06f;
        out[y*w+x] = std::min(d, 1.0f);
    }
}

static void GenerateLenticular(const GalaxyMathParams* p, float* out, int w, int h,
                                float vx, float vy, float vw, float vh)
{
    SimplexNoise noise(p->seed);
    float noiseScale = 2.5f / vw;

    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
    {
        float nx = vNx(x, w, vx, vw), ny = vNy(y, h, vy, vh);
        float r  = sqrtf(nx*nx + ny*ny);
        if (r > 1.0f) { out[y*w+x] = 0.0f; continue; }

        float disk  = p->armDensity * expf(-r / 0.42f);
        float bulge = BulgeDensity(nx, ny, p->bulgeFactor, p->bulgeStrength);
        float halo  = 0.04f * expf(-r / 0.55f);
        float n = (noise.Octave2D(nx*noiseScale, ny*noiseScale, 3, 0.5f) + 1.0f) * 0.5f;
        float d = std::max({ disk, bulge*p->coreDensity, halo });
        d += n * p->dissipation * 0.08f;
        d  = std::min(d, 1.0f);
        float fade = 1.0f - r*r;
        out[y*w+x] = d * fade;
    }
}

static void GenerateIrregular(const GalaxyMathParams* p, float* out, int w, int h,
                               float vx, float vy, float vw, float vh)
{
    SimplexNoise noise1(p->seed);
    SimplexNoise noise2(p->seed ^ 0xABCDEF9876543210ULL);
    float s1 = 1.8f / vw, s2 = 5.5f / vw;

    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
    {
        float nx = vNx(x, w, vx, vw), ny = vNy(y, h, vy, vh);
        float r  = sqrtf(nx*nx + ny*ny);
        if (r > 1.0f) { out[y*w+x] = 0.0f; continue; }

        float n1 = (noise1.Octave2D(nx*s1 + 0.4f, ny*s1 - 0.25f, 5, 0.65f) + 1.0f) * 0.5f;
        float n2 = (noise2.Octave2D(nx*s2,         ny*s2,          3, 0.55f) + 1.0f) * 0.5f;
        float d  = n1*0.65f + n2*0.35f;
        d *= std::max(0.0f, 1.0f - r*r*0.9f);
        out[y*w+x] = std::min(d, 1.0f);
    }
}

// ── side-view density generators ─────────────────────────────────────────────
// Side views always render the full extent — no viewport needed.

static void GenerateSpiralSideView(const GalaxyMathParams* p, float* out, int w, int h)
{
    SimplexNoise noise(p->seed ^ 0xDEADBEEFCAFEBABEULL);
    float diskHeight = 0.04f + (p->age * 0.02f) + p->bulgeFactor * 0.04f;
    float diskRadius = 0.75f;
    float bulgeR     = p->bulgeFactor * 0.6f + 0.04f;

    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
    {
        float r = (2.0f*x/(w-1)) - 1.0f;
        float z = (2.0f*y/(h-1)) - 1.0f;

        float sech  = 1.0f / coshf(z / diskHeight);
        float disk  = expf(-fabsf(r) / diskRadius) * sech * sech;
        float bDist = sqrtf(r*r + (z/0.6f)*(z/0.6f));
        float bulge = p->bulgeStrength * expf(-bDist / bulgeR);
        float n     = (noise.Octave2D(r*5.0f, z*5.0f, 3, 0.5f) + 1.0f) * 0.5f;

        float d = std::max(disk*p->armDensity, bulge*p->coreDensity);
        d += n * p->dissipation * 0.12f;
        d  = std::min(d, 1.0f);

        float dustLane  = expf(-z*z / (2.0f * 0.013f * 0.013f));
        float bulgeMask = expf(-bDist / bulgeR);
        d *= 1.0f - dustLane * (1.0f - bulgeMask) * 0.82f;

        float fade = 1.0f - r*r;
        out[y*w+x] = d * fade * fade;
    }
}

static void GenerateEllipticalSideView(const GalaxyMathParams* p, float* out, int w, int h)
{
    float eFlat = (float)((p->seed >> 16) & 0xFF) / 510.0f;
    float axisB = 1.0f - eFlat * 0.6f + 0.01f;

    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
    {
        float r = (2.0f*x/(w-1)) - 1.0f;
        float z = (2.0f*y/(h-1)) - 1.0f;
        float re = sqrtf(r*r + (z/axisB)*(z/axisB));
        if (re > 1.0f) { out[y*w+x] = 0.0f; continue; }
        float d = expf(-4.0f*re*re) + 0.35f*expf(-1.8f*re);
        out[y*w+x] = std::min(d, 1.0f);
    }
}

static void GenerateLenticularSideView(const GalaxyMathParams* p, float* out, int w, int h)
{
    float diskHeight = 0.07f + p->bulgeFactor * 0.05f;
    float diskRadius = 0.70f;
    float bulgeR     = p->bulgeFactor * 0.55f + 0.05f;

    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
    {
        float r = (2.0f*x/(w-1)) - 1.0f;
        float z = (2.0f*y/(h-1)) - 1.0f;
        float sech  = 1.0f / coshf(z / diskHeight);
        float disk  = expf(-fabsf(r) / diskRadius) * sech * sech;
        float bDist = sqrtf(r*r + (z/0.7f)*(z/0.7f));
        float bulge = p->bulgeStrength * expf(-bDist / bulgeR);
        float d = std::max(disk*p->armDensity, bulge*p->coreDensity);
        float fade = 1.0f - r*r;
        out[y*w+x] = std::min(d, 1.0f) * fade;
    }
}

static void GenerateIrregularSideView(const GalaxyMathParams* p, float* out, int w, int h)
{
    SimplexNoise noise(p->seed ^ 0xFF00FF00FF00FF00ULL);

    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
    {
        float r = (2.0f*x/(w-1)) - 1.0f;
        float z = (2.0f*y/(h-1)) - 1.0f;
        float n = (noise.Octave2D(r*2.5f, z*2.5f, 4, 0.6f) + 1.0f) * 0.5f;
        float vertFade = expf(-z*z / (0.18f * 0.18f));
        float radFade  = 1.0f - r*r;
        out[y*w+x] = std::min(n * vertFade * radFade, 1.0f);
    }
}

// ── point-sample density (full-galaxy scale) ─────────────────────────────────
// These mirror the grid generators but evaluate at a single (nx, nz) point.
// Always uses vw=1 (full-galaxy noise scale) — appropriate for chunk sampling.

static float PointSampleSpiral(const GalaxyMathParams* p, float nx, float nz)
{
    float r = sqrtf(nx*nx + nz*nz);
    if (r > 1.0f) return 0.0f;

    SimplexNoise noise(p->seed);
    float noiseScale = (p->age == 2) ? 2.5f : 3.0f;
    float warpAmp    = p->dissipation * 0.8f;
    float barAngle   = (float)(p->seed & 0xFFFF) / 65536.0f * (float)M_PI;

    float n  = (noise.Octave2D(nx*noiseScale, nz*noiseScale, 4, 0.5f) + 1.0f) * 0.5f;
    float n2 = noise.Noise2D(nx*noiseScale + 100.0f, nz*noiseScale);
    float wx = nx + warpAmp * (n  - 0.5f);
    float wz = nz + warpAmp * (n2 - 0.5f);

    float spiral = (p->barStrength > 0.0f)
        ? SpiralArmDensity(wx, wz, p->arms, p->armTightness, p->armWidth) * (1.0f - p->barStrength)
          + BarredSpiralDensity(wx, wz, p->arms, p->armTightness, p->armWidth, p->barLength, barAngle) * p->barStrength
        : SpiralArmDensity(wx, wz, p->arms, p->armTightness, p->armWidth);

    float bulge = BulgeDensity(nx, nz, p->bulgeFactor, p->bulgeStrength);
    float bar   = BarDensity(nx, nz, p->barLength, barAngle, p->barStrength);
    float halo  = 0.055f * expf(-r / 0.55f);

    float d = std::max({ spiral*p->armDensity, bulge*p->coreDensity, bar*p->coreDensity, halo });
    d += n * p->dissipation * 0.4f;
    d  = std::min(d, 1.0f);
    float fade = 1.0f - r*r;
    return d * fade * fade;
}

static float PointSampleElliptical(const GalaxyMathParams* p, float nx, float nz)
{
    float eFlat  = (float)((p->seed >> 16) & 0xFF) / 510.0f;
    float eAngle = (float)(p->seed & 0xFFFF) / 65536.0f * (float)M_PI;
    float cosE = cosf(eAngle), sinE = sinf(eAngle);
    float axisB = 1.0f - eFlat + 0.01f;
    float ex =  nx*cosE + nz*sinE;
    float ez = (-nx*sinE + nz*cosE) / axisB;
    float re = sqrtf(ex*ex + ez*ez);
    if (re > 1.0f) return 0.0f;
    float d = expf(-4.0f*re*re) + 0.35f*expf(-1.8f*re);
    return std::min(d, 1.0f);
}

static float PointSampleLenticular(const GalaxyMathParams* p, float nx, float nz)
{
    float r = sqrtf(nx*nx + nz*nz);
    if (r > 1.0f) return 0.0f;
    float disk  = p->armDensity * expf(-r / 0.42f);
    float bulge = BulgeDensity(nx, nz, p->bulgeFactor, p->bulgeStrength);
    float halo  = 0.04f * expf(-r / 0.55f);
    float d = std::max({ disk, bulge*p->coreDensity, halo });
    float fade = 1.0f - r*r;
    return std::min(d, 1.0f) * fade;
}

static float PointSampleIrregular(const GalaxyMathParams* p, float nx, float nz)
{
    float r = sqrtf(nx*nx + nz*nz);
    if (r > 1.0f) return 0.0f;
    SimplexNoise noise1(p->seed);
    SimplexNoise noise2(p->seed ^ 0xABCDEF9876543210ULL);
    float n1 = (noise1.Octave2D(nx*1.8f + 0.4f, nz*1.8f - 0.25f, 5, 0.65f) + 1.0f) * 0.5f;
    float n2 = (noise2.Octave2D(nx*5.5f,         nz*5.5f,          3, 0.55f) + 1.0f) * 0.5f;
    float d  = n1*0.65f + n2*0.35f;
    d *= std::max(0.0f, 1.0f - r*r*0.9f);
    return std::min(d, 1.0f);
}

static float SampleDensityAt(const GalaxyMathParams* p, float nx, float nz)
{
    switch (p->galaxyType)
    {
        case 1:  return PointSampleElliptical(p, nx, nz);
        case 2:  return PointSampleLenticular(p, nx, nz);
        case 3:  return PointSampleIrregular (p, nx, nz);
        default: return PointSampleSpiral    (p, nx, nz);
    }
}

// ── region classifier ─────────────────────────────────────────────────────────
// Returns RegionType for the chunk centred at (cx, cy, cz) in normalised space.

static uint8_t ClassifyRegion(const GalaxyMathParams* p, float cx, float cy, float cz)
{
    float r2d  = sqrtf(cx*cx + cz*cz);
    float r3d  = sqrtf(cx*cx + cy*cy + cz*cz);
    float diskH = 0.04f + (float)p->age * 0.02f + p->bulgeFactor * 0.04f;

    // Halo: significantly above/below the disk plane, or high radius + off-plane
    if (fabsf(cy) > diskH * 4.0f || (r2d > 0.55f && fabsf(cy) > diskH * 1.5f))
        return 3; // REGION_HALO

    // Bulge: central 3-D sphere
    float bulgeR = p->bulgeFactor * 0.6f + 0.04f;
    if (r3d < bulgeR * 1.8f)
        return 2; // REGION_BULGE

    // Sample 2-D density at this XZ position
    float d2d = SampleDensityAt(p, cx, cz);

    // Void: galactic plane but very sparse
    if (d2d < 0.06f)
        return 5; // REGION_VOID

    // Nursery: peak-density arm region with a young-star noise trigger.
    // Uses a different noise seed so nurseries don't just echo the arm shape.
    if (d2d > 0.60f && r2d > 0.08f)
    {
        SimplexNoise nursNoise(p->seed ^ 0x3C4D5E6F7A8B9C0DULL);
        float ns = nursNoise.Noise2D(cx * 14.0f, cz * 14.0f);
        if (ns > 0.38f)
            return 4; // REGION_NURSERY
    }

    // Arm vs inter-arm based on 2-D density threshold
    return (d2d > 0.28f) ? 0 : 1;  // REGION_DISK_ARM : REGION_DISK_INTER
}

// ── star chunk generator ──────────────────────────────────────────────────────

static uint64_t StarHash(int64_t cx, int64_t cy, int64_t cz, int idx, uint64_t seed)
{
    uint64_t h = (uint64_t)(uint32_t)cx * 0x9e3779b97f4a7c15ULL
               ^ (uint64_t)(uint32_t)cy * 0x6c62272e07bb0142ULL
               ^ (uint64_t)(uint32_t)cz * 0x517cc1b727220a95ULL
               ^ (uint64_t)(uint32_t)idx * 0xd9c4a9a0ab96cceaULL
               ^ seed;
    h = (h ^ (h >> 30)) * 0xbf58476d1ce4e5b9ULL;
    h = (h ^ (h >> 27)) * 0x94d049bb133111ebULL;
    return h ^ (h >> 31);
}

// ── per-region spectral class distributions ───────────────────────────────────
// All arrays: cumulative probability, O B A F G K M WD NS BH order.

// Spiral arm — standard Salpeter IMF
static const float kCumulArm[] =
    { 0.000006f, 0.001306f, 0.007306f, 0.037306f, 0.113306f, 0.234306f, 0.999306f, 0.999956f, 0.999991f, 1.0f };

// Inter-arm disk — slightly older; fewer O/B, more K/M
static const float kCumulInterArm[] =
    { 0.000002f, 0.000400f, 0.003500f, 0.022000f, 0.085000f, 0.210000f, 0.999200f, 0.999960f, 0.999992f, 1.0f };

// Bulge — old, metal-rich; K/M dominant, almost no O/B
static const float kCumulBulge[] =
    { 0.0000005f, 0.000060f, 0.000500f, 0.004000f, 0.038000f, 0.165000f, 0.999000f, 0.999700f, 0.999950f, 1.0f };

// Halo — ancient, metal-poor; K/M dominant, no O/B, elevated WD fraction
static const float kCumulHalo[] =
    { 0.0000001f, 0.000020f, 0.000200f, 0.002000f, 0.025000f, 0.130000f, 0.994000f, 0.999600f, 0.999940f, 1.0f };

// Nursery / star-forming — hot young OB stars overrepresented
static const float kCumulNursery[] =
    { 0.003000f, 0.032000f, 0.085000f, 0.155000f, 0.255000f, 0.380000f, 0.978000f, 0.992000f, 0.998000f, 1.0f };

// Void — very sparse old field stars; K/M heavy, almost no O/B
static const float kCumulVoid[] =
    { 0.0000005f, 0.000050f, 0.000400f, 0.003500f, 0.035000f, 0.155000f, 0.998000f, 0.999700f, 0.999950f, 1.0f };

static const float* kRegionCumul[] =
    { kCumulArm, kCumulInterArm, kCumulBulge, kCumulHalo, kCumulNursery, kCumulVoid };

static uint8_t PickSpectralClass(float r, uint8_t regionType)
{
    const float* cumul = kRegionCumul[regionType < 6 ? regionType : 0];
    for (int i = 0; i < 10; i++)
        if (r < cumul[i]) return (uint8_t)i;
    return 9;
}

// Base luminosity centre per spectral class (O B A F G K M WD NS BH)
static const float kBaseLum[] = { 0.95f, 0.80f, 0.62f, 0.48f, 0.36f, 0.26f, 0.10f,
                                   0.18f, 0.22f, 0.30f };

// ── exported entry points ────────────────────────────────────────────────────

UGMATH_API void GenerateGalaxyDensityMap(
    const GalaxyMathParams* p, float* outDensity, int width, int height)
{
    switch (p->galaxyType)
    {
        case 1:  GenerateElliptical(p, outDensity, width, height, 0,0,1,1); break;
        case 2:  GenerateLenticular(p, outDensity, width, height, 0,0,1,1); break;
        case 3:  GenerateIrregular (p, outDensity, width, height, 0,0,1,1); break;
        default: GenerateSpiral    (p, outDensity, width, height, 0,0,1,1); break;
    }
}

UGMATH_API void GenerateGalaxyDensityMapRegion(
    const GalaxyMathParams* p, float* outDensity, int width, int height,
    float regionX, float regionY, float regionW, float regionH)
{
    switch (p->galaxyType)
    {
        case 1:  GenerateElliptical(p, outDensity, width, height, regionX, regionY, regionW, regionH); break;
        case 2:  GenerateLenticular(p, outDensity, width, height, regionX, regionY, regionW, regionH); break;
        case 3:  GenerateIrregular (p, outDensity, width, height, regionX, regionY, regionW, regionH); break;
        default: GenerateSpiral    (p, outDensity, width, height, regionX, regionY, regionW, regionH); break;
    }
}

UGMATH_API void GenerateGalaxySideViewDensityMap(
    const GalaxyMathParams* p, float* outDensity, int width, int height)
{
    switch (p->galaxyType)
    {
        case 1:  GenerateEllipticalSideView(p, outDensity, width, height); break;
        case 2:  GenerateLenticularSideView(p, outDensity, width, height); break;
        case 3:  GenerateIrregularSideView (p, outDensity, width, height); break;
        default: GenerateSpiralSideView    (p, outDensity, width, height); break;
    }
}

UGMATH_API int GenerateStarChunk(
    const GalaxyMathParams* p,
    int64_t chunkX, int64_t chunkY, int64_t chunkZ,
    float   chunkSize,
    int     maxStars,
    StarDataC* outStars)
{
    if (maxStars <= 0 || !outStars) return 0;

    // Centre of this chunk in galaxy normalised space
    float cx = (float)chunkX * chunkSize;
    float cy = (float)chunkY * chunkSize;
    float cz = (float)chunkZ * chunkSize;

    // 2-D density at (cx, cz) — galactic plane
    float density2d = SampleDensityAt(p, cx, cz);

    // Vertical sech² disk profile: disk height grows with age and bulge size
    float diskH = 0.04f + (float)p->age * 0.02f + p->bulgeFactor * 0.04f;
    float sech  = 1.0f / coshf(cy / diskH);
    float d3d   = density2d * sech * sech;

    uint8_t regionType = ClassifyRegion(p, cx, cy, cz);

    // Spherical bulge contributes everywhere within the central sphere
    float r3 = sqrtf(cx*cx + cy*cy + cz*cz);
    float bulgeSph = p->bulgeStrength * p->coreDensity
                   * expf(-r3*r3 / (2.0f * p->bulgeFactor * p->bulgeFactor));
    d3d = std::max(d3d, bulgeSph);

    if (d3d < 0.002f) return 0;

    int count = std::min((int)(d3d * (float)maxStars + 0.5f), maxStars);
    if (count <= 0) return 0;

    float half = chunkSize * 0.5f;

    for (int i = 0; i < count; i++)
    {
        uint64_t h0 = StarHash(chunkX, chunkY, chunkZ, i*4+0, p->seed);
        uint64_t h1 = StarHash(chunkX, chunkY, chunkZ, i*4+1, p->seed);
        uint64_t h2 = StarHash(chunkX, chunkY, chunkZ, i*4+2, p->seed);
        uint64_t h3 = StarHash(chunkX, chunkY, chunkZ, i*4+3, p->seed);

        // Position randomly scattered inside the chunk
        float px = cx + (((h0      ) & 0xFFFFFF) / (float)0xFFFFFF - 0.5f) * chunkSize;
        float py = cy + (((h1      ) & 0xFFFFFF) / (float)0xFFFFFF - 0.5f) * chunkSize;
        float pz = cz + (((h2      ) & 0xFFFFFF) / (float)0xFFFFFF - 0.5f) * chunkSize;

        // Spectral class from initial mass function
        float classR = ((h0 >> 24) & 0xFFFF) / (float)0x10000;
        uint8_t sc   = PickSpectralClass(classR, regionType);

        // Luminosity: base + small random variation
        float lumBase = kBaseLum[sc];
        float lumVar  = (((h1 >> 24) & 0xFF) / 255.0f - 0.5f) * 0.15f;
        float lum     = std::max(0.0f, std::min(1.0f, lumBase + lumVar));

        // Flags
        uint8_t flags = 0;
        if (((h2 >> 24) & 0xFF) < 128) flags |= 1;             // ~50% binary
        float gR = ((h3) & 0xFFFF) / (float)0x10000;
        // Giant / supergiant rates vary by region
        float giantRate     = (regionType == 4) ? 0.06f : (regionType >= 2) ? 0.05f : 0.03f;
        float supergiantRate= (regionType == 4) ? 0.008f: (regionType >= 2) ? 0.000f : 0.001f;
        if (sc >= 4 && sc <= 6 && gR < giantRate)
            { flags |= 2; lum = std::min(1.0f, lum * 3.0f); }
        if (sc <= 2 && gR < supergiantRate)
            { flags |= 4; lum = 1.0f; }

        outStars[i] = { px, py, pz, sc, (uint8_t)(lum * 255.0f), flags, 0 };
    }
    return count;
}

UGMATH_API uint8_t GetChunkRegionType(
    const GalaxyMathParams* p,
    int64_t chunkX, int64_t chunkY, int64_t chunkZ,
    float   chunkSize)
{
    float cx = (float)chunkX * chunkSize;
    float cy = (float)chunkY * chunkSize;
    float cz = (float)chunkZ * chunkSize;
    return ClassifyRegion(p, cx, cy, cz);
}
