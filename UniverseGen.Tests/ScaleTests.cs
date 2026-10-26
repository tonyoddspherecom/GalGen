using System.Numerics;
using UniverseGen.Generation;
using UniverseGen.Models;

namespace UniverseGen.Tests;

/// <summary>The galaxy's scale: its size in light years, the disk's thickness, and how many stars it holds.</summary>
public class ScaleTests
{
    private static long CountStars(GalaxyParams p, StarGenerator generator, IEnumerable<ChunkCoord> chunks) =>
        chunks.Sum(c => (long)generator.Generate(p, c).Stars.Length);

    /// A slab of chunks through an arm, from well below the plane to well above it.
    private static IEnumerable<ChunkCoord> Slab(int height = 30)
    {
        for (long x = 1490; x < 1510; x++) for (long y = -height; y <= height; y++) for (long z = 590; z < 610; z++)
            yield return new ChunkCoord(x, y, z);
    }

    [Fact]
    public void A_chunk_is_the_same_number_of_light_years_at_any_galaxy_size()
    {
        Assert.Equal(new StarGenerator().ChunkSize, StarGenerator.For(new GalaxyParams()).ChunkSize);
        var small = new GalaxyParams { RadiusLightYears = 3000f };
        Assert.Equal(10f, small.ToLightYears(StarGenerator.For(small).ChunkSize), 3);
        Assert.Equal(0.5f, small.ToNormalised(1500f));
    }

    [Fact]
    public void Every_star_is_in_the_chunk_its_position_names()
    {
        foreach (var p in new[] { new GalaxyParams(), new GalaxyParams { Model = GalaxyModel.Classic } })
        {
            var generator = new StarGenerator();
            foreach (ChunkCoord c in PortTests.Chunks().Take(400))
                foreach (Star star in generator.Generate(p, c).Stars)
                    Assert.Equal(c, generator.WorldToChunk(star.Position));
        }
    }

    [Fact]
    public void A_thinner_disk_keeps_its_stars_closer_to_the_plane()
    {
        var thick = new GalaxyParams();
        var thin = new GalaxyParams { DiskThickness = 0.1f };
        var generator = new StarGenerator();
        // Stars 1,000 ly (0.02) above the plane, for each star in it: the default disk is thick enough to
        // have nearly as many there, the thin one hardly any. Out at two-thirds of the radius, past the
        // bulge, which is a sphere whatever the disk does.
        long Layer(GalaxyParams p, long y) => CountStars(p, generator, Slab(0).Select(c => new ChunkCoord(c.X + 1500, y, c.Z + 900)));
        double Raised(GalaxyParams p) => (double)Layer(p, 100) / Layer(p, 0);
        Assert.True(Raised(thick) > 0.1, $"thick {Raised(thick)}");
        Assert.True(Raised(thin) < 0.01, $"thin {Raised(thin)}");
        Assert.Equal(new GalaxySampler(thick).DiskHeight * 0.1f, new GalaxySampler(thin).DiskHeight, 6);
        // Without a bulge (a sphere, whatever the disk does), the space a query must search shrinks with the disk.
        var flatThick = new GalaxyParams { BulgeStrength = 0f, HaloStrength = 0f };
        var flatThin = new GalaxyParams { BulgeStrength = 0f, HaloStrength = 0f, DiskThickness = 0.1f };
        Assert.Equal(new GalaxySampler(flatThick).VerticalExtent * 0.1f, new GalaxySampler(flatThin).VerticalExtent, 5);
        // Edge-on, the thin disk's light is pulled into the middle rows.
        var generatorMaps = new GalaxyGenerator();
        float[] thickSide = generatorMaps.GenerateSideView(thick, 64, 64).DensityMap;
        float[] thinSide = generatorMaps.GenerateSideView(thin, 64, 64).DensityMap;
        float OffPlane(float[] map) => Enumerable.Range(0, 64 * 64).Where(i => Math.Abs(i / 64 - 32) > 6).Sum(i => map[i]);
        Assert.True(OffPlane(thinSide) < OffPlane(thickSide));
    }

    [Fact]
    public void Nothing_lies_beyond_the_vertical_extent()
    {
        foreach (var p in new[] { new GalaxyParams(), new GalaxyParams { DiskThickness = 0.05f, StarDensity = 3f }, PortTests.Galaxy("Elliptical") })
        {
            var generator = new StarGenerator();
            float extent = new GalaxySampler(p).VerticalExtent;
            long beyond = (long)MathF.Ceiling(extent / generator.ChunkSize) + 1;
            // Chunks just past the extent, above the centre (the bulge) and above an arm.
            foreach (long x in new long[] { 0, 3, -2, 1500, -900 })
            foreach (long z in new long[] { 0, -4, 600, 2000 })
            foreach (long y in new[] { beyond, -beyond, beyond + 7 })
                Assert.Empty(generator.Generate(p, new ChunkCoord(x, y, z)).Stars);
            // ...and the plane itself has stars inside it, so the test means something.
            Assert.NotEmpty(generator.Generate(p, new ChunkCoord(0, 0, 0)).Stars);
        }
    }

    [Fact]
    public void Star_density_scales_the_count_and_a_sparse_galaxy_still_has_stars()
    {
        var generator = new StarGenerator();
        long normal = CountStars(new GalaxyParams(), generator, Slab());
        long sparse = CountStars(new GalaxyParams { StarDensity = 0.01f }, generator, Slab());
        long dense = CountStars(new GalaxyParams { StarDensity = 3f }, generator, Slab());
        Assert.InRange(sparse, normal * 0.007, normal * 0.013);
        Assert.InRange(dense, normal * 2.8, normal * 3.2);
        // Each chunk expects 0.002 stars or more, far under one: rounding would have left none at all.
        long faint = CountStars(new GalaxyParams { StarDensity = 0.0005f }, generator, Slab(2));
        Assert.True(faint > 0);
    }

    [Fact]
    public void A_dense_chunk_can_hold_more_than_the_base_count()
    {
        var p = new GalaxyParams { StarDensity = 4f };
        var generator = new StarGenerator();
        StarChunk centre = generator.Generate(p, new ChunkCoord(0, 0, 0));
        Assert.True(centre.Stars.Length > generator.MaxStarsPerChunk);
        Assert.True(centre.Stars.Length <= new GalaxySampler(p).ChunkCapacity(generator.MaxStarsPerChunk));
        // Every star is still inside its chunk, and none is repeated.
        float half = generator.ChunkSize * 0.5f;
        Assert.All(centre.Stars, s => Assert.True(MathF.Abs(s.X) <= half && MathF.Abs(s.Y) <= half && MathF.Abs(s.Z) <= half));
        Assert.Equal(centre.Stars.Length, centre.Stars.Distinct().Count());
    }

    [Fact]
    public void Generation_is_the_same_on_any_thread()
    {
        var p = new GalaxyParams { StarDensity = 0.3f, DiskThickness = 0.5f, Seed = 77 };
        var chunks = Slab(4).ToArray();
        var generator = new StarGenerator();
        Star[][] serial = chunks.Select(c => generator.Generate(p, c).Stars).ToArray();
        var parallel = new Star[chunks.Length][];
        var shared = new StarGenerator();
        Parallel.For(0, chunks.Length, i => parallel[i] = shared.Generate(p, chunks[i]).Stars);
        for (int i = 0; i < chunks.Length; i++)
            Assert.Equal(serial[i], parallel[i]);
    }

    [Fact]
    public void A_generator_follows_a_change_to_the_params_it_is_given()
    {
        var p = new GalaxyParams();
        var generator = new StarGenerator();
        var coord = new ChunkCoord(1500, 0, 600);
        Star[] before = generator.Generate(p, coord).Stars;
        p.Seed = 999;
        Assert.NotEqual(before, generator.Generate(p, coord).Stars);
        p.Seed = new GalaxyParams().Seed;
        Assert.Equal(before, generator.Generate(p, coord).Stars);
    }

    [Fact]
    public void Codes_carry_the_model_and_scale_and_old_codes_still_work()
    {
        // A Classic galaxy at the default scale has the short code every code shared before had.
        var classic = new GalaxyParams { Seed = 4242, Arms = 3, Model = GalaxyModel.Classic };
        string code = GalaxyCode.Encode(classic);
        Assert.Equal(23, code.Length);
        Assert.Equal(classic.Fingerprint(), GalaxyCode.Decode(code).Fingerprint());
        // An old code is a Classic galaxy, so its stars are what they always were.
        Assert.Equal(GalaxyModel.Classic, GalaxyCode.Decode("AAEEMhkUDygjOTAAAAAAAAA").Model);

        var realistic = new GalaxyParams
        {
            Seed = 4242, Arms = 3, RadiusLightYears = 3000f, DiskThickness = 0.08f, StarDensity = 0.02f, FeatureDensity = 60,
            BulgeThickness = 0.2f, HaloStrength = 0f,
        };
        string realisticCode = GalaxyCode.Encode(realistic);
        Assert.Equal(54, realisticCode.Length);
        GalaxyParams back = GalaxyCode.Decode(realisticCode);
        Assert.Equal((GalaxyModel.Realistic, 3000f, 0.08f, 0.02f, 60, 0.2f, 0f),
            (back.Model, back.RadiusLightYears, back.DiskThickness, back.StarDensity, back.FeatureDensity, back.BulgeThickness, back.HaloStrength));
        Assert.Equal(realistic.Fingerprint(), back.Fingerprint());
        Assert.Equal(54, GalaxyCode.Encode(new GalaxyParams()).Length);
        // A code cut short in its extension is refused, rather than read as a different galaxy.
        Assert.Throws<FormatException>(() => GalaxyCode.Decode(realisticCode[..40]));
    }

    [Fact]
    public void The_fingerprint_names_exactly_one_galaxy()
    {
        var a = new GalaxyParams();
        Assert.Equal(a.Fingerprint(), new GalaxyParams().Fingerprint());
        Assert.Equal(16, a.Fingerprint().Length);
        var changes = new Action<GalaxyParams>[]
        {
            p => p.Seed++, p => p.Arms++, p => p.ArmWidth += 0.01f, p => p.Type = GalaxyType.Irregular,
            p => p.RadiusLightYears = 3000f, p => p.DiskThickness = 0.5f, p => p.StarDensity = 0.5f, p => p.FeatureDensity = 50,
            p => p.Model = GalaxyModel.Classic, p => p.BulgeThickness = 0.3f, p => p.HaloStrength = 0f,
        };
        var seen = new HashSet<string> { a.Fingerprint() };
        foreach (var change in changes)
        {
            var b = new GalaxyParams();
            change(b);
            Assert.True(seen.Add(b.Fingerprint()));
        }
    }

    [Fact]
    public void Settings_that_would_break_generation_are_refused()
    {
        new GalaxyParams().Validate();
        foreach (var bad in new Action<GalaxyParams>[]
        {
            p => p.Arms = 0, p => p.ArmWidth = 0f, p => p.ArmTightness = float.NaN, p => p.RadiusLightYears = 0f,
            p => p.DiskThickness = -1f, p => p.StarDensity = -0.1f, p => p.BarStrength = 2f, p => p.Type = (GalaxyType)9,
            p => p.BulgeThickness = 0f, p => p.HaloStrength = -1f, p => p.Model = (GalaxyModel)7,
        })
        {
            var p = new GalaxyParams();
            bad(p);
            Assert.Throws<ArgumentException>(p.Validate);
        }
    }

    [Fact]
    public void The_sampler_agrees_with_the_generator()
    {
        var p = PortTests.Galaxy("BarredYoung");
        var sampler = new GalaxySampler(p);
        var generator = new StarGenerator();
        foreach (ChunkCoord c in PortTests.Chunks().Take(300))
            Assert.Equal(generator.Generate(p, c).Region, sampler.RegionAt(c.Centre(generator.ChunkSize)));
        // It's a copy: changing the params afterwards doesn't change it.
        RegionType before = sampler.RegionAt(new Vector3(0.3f, 0f, 0.12f));
        p.Seed++;
        Assert.Equal(before, sampler.RegionAt(new Vector3(0.3f, 0f, 0.12f)));
    }
}
