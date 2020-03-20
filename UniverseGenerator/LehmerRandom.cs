using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UniverseGenerator
{
    public class LehmerRandom
    {
        private const int a = 16807;
        private const int m = 2147483647;
        private const int q = 127773;
        private const int r = 2836;
        private int seed;

        public LehmerRandom(int seed)
        {
            this.seed = seed;
        }


        public double Next()
        {
            int hi = seed / q;
            int lo = seed % q;
            seed = (a * lo) - (r * hi);
            if (seed <= 0)
                seed = seed + m;
            return (seed * 1.0) / m;
        }
    }

    public class LehmerRandom2
    {
        UInt32 nProcGen = 0;

        public LehmerRandom2(int seed)
        {
            nProcGen = (UInt32)seed;
        }

        public double rndDouble(double min, double max)
        {
            return ((double)rnd() / (double)(0x7FFFFFFF)) * (max - min) + min;
        }

        public int rndInt(int min, int max)
        {
            return (int)((rnd() % (max - min)) + min);
        }

        public UInt32 rnd()
        {
            nProcGen += 0xe120fc15;
            UInt64 tmp;
            tmp = (UInt64)nProcGen * 0x4a39b70d;
            UInt32 m1 = (UInt32)((tmp >> 32) ^ tmp);
            tmp = (UInt64)m1 * 0x12fad5c9;
            UInt32 m2 = (UInt32)((tmp >> 32) ^ tmp);
            return m2;
        }

    }

}
