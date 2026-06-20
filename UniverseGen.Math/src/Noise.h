#pragma once
#include <cstdint>

class SimplexNoise
{
public:
    explicit SimplexNoise(uint64_t seed);
    float Noise2D(float x, float y) const;
    float Octave2D(float x, float y, int octaves, float persistence) const;

private:
    int m_perm[512];
    static float Grad2D(int hash, float x, float y);
};
