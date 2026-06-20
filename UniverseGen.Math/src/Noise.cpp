#include "Noise.h"
#include <cmath>
#include <algorithm>

SimplexNoise::SimplexNoise(uint64_t seed)
{
    for (int i = 0; i < 256; i++) m_perm[i] = i;

    // xorshift64 seeded shuffle
    uint64_t s = seed ^ 0x123456789ABCDEF0ULL;
    for (int i = 255; i > 0; i--)
    {
        s ^= s << 13; s ^= s >> 7; s ^= s << 17;
        int j = (int)(s % (uint64_t)(i + 1));
        std::swap(m_perm[i], m_perm[j]);
    }
    for (int i = 0; i < 256; i++) m_perm[256 + i] = m_perm[i];
}

float SimplexNoise::Grad2D(int hash, float x, float y)
{
    static const float g[8][2] =
        { {1,1},{-1,1},{1,-1},{-1,-1},{1,0},{-1,0},{0,1},{0,-1} };
    int h = hash & 7;
    return g[h][0] * x + g[h][1] * y;
}

float SimplexNoise::Noise2D(float x, float y) const
{
    static const float F2 = 0.5f * (sqrtf(3.0f) - 1.0f);
    static const float G2 = (3.0f - sqrtf(3.0f)) / 6.0f;

    float s  = (x + y) * F2;
    int   i  = (int)floorf(x + s);
    int   j  = (int)floorf(y + s);
    float t  = (float)(i + j) * G2;
    float x0 = x - (i - t);
    float y0 = y - (j - t);

    int i1 = (x0 > y0) ? 1 : 0;
    int j1 = (x0 > y0) ? 0 : 1;

    float x1 = x0 - i1 + G2;
    float y1 = y0 - j1 + G2;
    float x2 = x0 - 1.0f + 2.0f * G2;
    float y2 = y0 - 1.0f + 2.0f * G2;

    int ii = i & 255, jj = j & 255;
    float n = 0.0f;

    float t0 = 0.5f - x0*x0 - y0*y0;
    if (t0 > 0) { t0 *= t0; n += t0*t0 * Grad2D(m_perm[ii + m_perm[jj]], x0, y0); }

    float t1 = 0.5f - x1*x1 - y1*y1;
    if (t1 > 0) { t1 *= t1; n += t1*t1 * Grad2D(m_perm[ii + i1 + m_perm[jj + j1]], x1, y1); }

    float t2 = 0.5f - x2*x2 - y2*y2;
    if (t2 > 0) { t2 *= t2; n += t2*t2 * Grad2D(m_perm[ii + 1 + m_perm[jj + 1]], x2, y2); }

    return 70.0f * n;
}

float SimplexNoise::Octave2D(float x, float y, int octaves, float persistence) const
{
    float value = 0.0f, amplitude = 1.0f, frequency = 1.0f, maxValue = 0.0f;
    for (int i = 0; i < octaves; i++)
    {
        value    += Noise2D(x * frequency, y * frequency) * amplitude;
        maxValue += amplitude;
        amplitude *= persistence;
        frequency *= 2.0f;
    }
    return value / maxValue;
}
    