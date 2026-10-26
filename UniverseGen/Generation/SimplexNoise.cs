namespace UniverseGen.Generation;

/// <summary>
/// 2-D simplex noise with a seeded permutation table.
/// Ported line for line from the original C++ (Noise.cpp) so every galaxy comes out exactly as it did.
/// Immutable once built, so one instance can be shared by any number of threads.
/// </summary>
internal sealed class SimplexNoise
{
    private static readonly float F2 = 0.5f * (MathF.Sqrt(3.0f) - 1.0f);
    private static readonly float G2 = (3.0f - MathF.Sqrt(3.0f)) / 6.0f;
    private static readonly float[] GradX = { 1, -1, 1, -1, 1, -1, 0, 0 };
    private static readonly float[] GradY = { 1, 1, -1, -1, 0, 0, 1, -1 };

    private readonly int[] _perm = new int[512];

    public SimplexNoise(ulong seed)
    {
        for (int i = 0; i < 256; i++) _perm[i] = i;

        // xorshift64 seeded shuffle
        ulong s = seed ^ 0x123456789ABCDEF0UL;
        for (int i = 255; i > 0; i--)
        {
            s ^= s << 13; s ^= s >> 7; s ^= s << 17;
            int j = (int)(s % (ulong)(i + 1));
            (_perm[i], _perm[j]) = (_perm[j], _perm[i]);
        }
        for (int i = 0; i < 256; i++) _perm[256 + i] = _perm[i];
    }

    private static float Grad2D(int hash, float x, float y)
    {
        int h = hash & 7;
        return GradX[h] * x + GradY[h] * y;
    }

    public float Noise2D(float x, float y)
    {
        float s  = (x + y) * F2;
        int   i  = (int)MathF.Floor(x + s);
        int   j  = (int)MathF.Floor(y + s);
        float t  = (i + j) * G2;
        float x0 = x - (i - t);
        float y0 = y - (j - t);

        int i1 = x0 > y0 ? 1 : 0;
        int j1 = x0 > y0 ? 0 : 1;

        float x1 = x0 - i1 + G2;
        float y1 = y0 - j1 + G2;
        float x2 = x0 - 1.0f + 2.0f * G2;
        float y2 = y0 - 1.0f + 2.0f * G2;

        int ii = i & 255, jj = j & 255;
        float n = 0.0f;

        float t0 = 0.5f - x0*x0 - y0*y0;
        if (t0 > 0) { t0 *= t0; n += t0*t0 * Grad2D(_perm[ii + _perm[jj]], x0, y0); }

        float t1 = 0.5f - x1*x1 - y1*y1;
        if (t1 > 0) { t1 *= t1; n += t1*t1 * Grad2D(_perm[ii + i1 + _perm[jj + j1]], x1, y1); }

        float t2 = 0.5f - x2*x2 - y2*y2;
        if (t2 > 0) { t2 *= t2; n += t2*t2 * Grad2D(_perm[ii + 1 + _perm[jj + 1]], x2, y2); }

        return 70.0f * n;
    }

    public float Octave2D(float x, float y, int octaves, float persistence)
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
}
