using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UniverseGenerator
{
    public static class Generators
    {
        public static List<Color> StarColors = new List<Color>() { Color.Red, Color.Orange, Color.Yellow, Color.Blue };

        public static List<SolarObjects.Star> GalacticSectorStarGen(int seed, int sectorSize, int galaxyX, int galaxyY, int galaxyZ, int density)
        {
            var lehRnd = new LehmerRandom2(seed);
            var starList = new List<SolarObjects.Star>();
            for (int x = 0; x < sectorSize; x++)
            {
                for (int y = 0; y < sectorSize; y++)
                {
                    for (int z = 0; z < sectorSize; z++)
                    {
                        var starSeed = ((x + galaxyX) & 0xFFFF) << 16 | ((y + galaxyY) & 0xFFFF) << 16 | ((z + galaxyZ) & 0xFFFF) << 16;
                        if (Generators.RndBoolSeeded(starSeed, Math.Max(density, 1)))
                        {
                            starList.Add(new SolarObjects.Star(new SolarObjects.Pos(galaxyX, galaxyY, galaxyZ), new SolarObjects.Pos(x, y, z), starSeed));
                        }
                    }
                }
            }

            return starList;
        }

        public static List<SolarObjects.Planet> RndPlanetsSeeded(int seed)
        {
            var lehRnd = new LehmerRandom2(seed);

            var planetCount = Math.Max(lehRnd.rndInt(-1, 10), 0);

            if (planetCount == 0)
                return new List<SolarObjects.Planet>();

            var planetList = new List<SolarObjects.Planet>();

            for (int i = 0; i < planetCount; i++)
            {
                var newPlanet = new SolarObjects.Planet();
                newPlanet.Distance = lehRnd.rndInt(20, 200);
                newPlanet.Size = lehRnd.rndInt(5, 12);
                newPlanet.Name = RndStarNameSeeded(seed + lehRnd.rndInt(100,200));
                newPlanet.Moons = new List<SolarObjects.Moon>();
                var moonCount = Math.Max(lehRnd.rndInt(-3, 7), 0);
                for (int j = 0; j < moonCount; j++)
                {
                    var newMoon = new SolarObjects.Moon();
                    newMoon.Distance = lehRnd.rndInt(20, 200);
                    newMoon.Size = lehRnd.rndInt(5, 12);
                    newMoon.Name = RndStarNameSeeded(seed + lehRnd.rndInt(100, 200));
                    newPlanet.Moons.Add(newMoon);
                }
                planetList.Add(newPlanet);
            }

            return planetList;
        }

        public static bool RndBoolSeeded(int seed, int chance)
        {
            var lehRnd = new LehmerRandom2(seed);
            return (lehRnd.rndInt(0, chance) == 1);
        }

        public static Color RndStarColorSeeded(int seed)
        {
            var lehRnd = new LehmerRandom2(seed);
            var starColor = (int)Math.Floor((double)(lehRnd.rndInt(0, StarColors.Count * 1000))/1000);

            return StarColors[starColor];
        }

        public static int RndSizeSeeded(int seed)
        {
            var lehRnd = new LehmerRandom2(seed);
            return lehRnd.rndInt(5, 12);
        }

        public static string RndStarNameSeeded(int seed)
        {
            var lehRnd = new LehmerRandom2(seed);
            string[] consonants = { "b", "c", "d", "f", "g", "h", "j", "k", "l", "m", "n", "p", "q", "r", "s", "t", "v", "w", "x", "y", "z" };
            string[] vowels = { "a", "e", "i", "o", "u" };

            string word = "";
            int requestedLength = lehRnd.rndInt(5, 12);

            if (requestedLength == 1)
            {
                word = GetRandomLetter(vowels, seed + word.Length);
            }
            else
            {
                int consenantCount = 0;
                for (int i = 0; i < requestedLength; i++)
                {
                    if (lehRnd.rndInt(consenantCount, 10) > 5)
                    {
                        word += GetRandomLetter(vowels, seed + word.Length);
                        consenantCount = 0;
                    }
                    else
                    {
                        word += GetRandomLetter(consonants, seed + word.Length);
                        consenantCount++;
                    }
                }

                word = word.Replace("q", "qu").Substring(0, requestedLength);
            }

            return word;
        }

        private static string GetRandomLetter(string[] letters, int seed)
        {
            var lehRnd = new LehmerRandom2(seed);
            return letters[lehRnd.rndInt(0, letters.Length - 1)];
        }
    }
}
