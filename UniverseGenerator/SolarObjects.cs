using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UniverseGenerator
{
    public class SolarObjects
    {
        public class Star
        {
            public Pos SectorLocation { get; set; }

            public Pos GalaxyLocation { get; set; }

            public int StarSeed { get; set; }

            public int Size { get; set; }

            public Color StarType { get; set; }

            public string Name { get; set; }

            public List<Planet> Planets { get; set; }

            public Star(Pos galaxyLocation, Pos sectorLocation, int starSeed)
            {
                SectorLocation = sectorLocation;
                GalaxyLocation = galaxyLocation;
                StarSeed = starSeed;
                StarType = Generators.RndStarColorSeeded(starSeed);
                Size = Generators.RndSizeSeeded(starSeed);
                Name = Generators.RndStarNameSeeded(starSeed);
                Planets = Generators.RndPlanetsSeeded(starSeed);
            }

        }

        public class Planet
        {
            public int Size { get; set; }
            public String Name { get; set; }
            public int Distance { get; set; }
            public List<Moon> Moons { get; set; }
        }

        public class Moon
        {
            public int Size { get; set; }
            public String Name { get; set; }
            public int Distance { get; set; }
        }

        public class Pos
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }

            public Pos(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public Pos Scaled(int displayScale)
            {
                return new Pos(X * displayScale, Y * displayScale, Z * displayScale);
            }
        }

    }
}
