using UniverseGen.Generation;
using UniverseGen.Models;
using UniverseGen.Service;
using Xunit.Abstractions;

namespace UniverseGen.Tests;

/// <summary>
/// The Realistic model's Milky Way against the real one: how many stars, how dense round the Sun, how
/// thin the disk, how much of it is bulge. Ranges, not points: the galaxy is procedural and the real
/// figures are themselves uncertain by a factor of two.
/// </summary>
public class RealismTests(ITestOutputHelper output)
{
    private static readonly GalaxyParams MilkyWay = new() { Arms = 4, BarStrength = 0.4f };

    private static long Estimate(GalaxyParams p) => GalaxyCensus.Estimate(p, StarGenerator.For(p).ChunkSize, 200);

    /// Stars per cubic light year in the plane at the Sun's distance (26,000 ly), averaged round the galaxy.
    private static double LocalDensity(GalaxyParams p)
    {
        var generator = StarGenerator.For(p);
        float r = p.ToNormalised(26_000f);
        long stars = 0;
        const int samples = 2000;
        for (int i = 0; i < samples; i++)
        {
            float angle = i * MathF.Tau / samples;
            var at = new System.Numerics.Vector3(r * MathF.Cos(angle), 0f, r * MathF.Sin(angle));
            stars += generator.Generate(p, generator.WorldToChunk(at)).Stars.Length;
        }
        float chunkLy = p.ToLightYears(generator.ChunkSize);
        return stars / (double)samples / (chunkLy * chunkLy * chunkLy);
    }

    [Fact]
    public void A_milky_way_has_a_few_hundred_billion_stars()
    {
        long stars = Estimate(MilkyWay);
        output.WriteLine($"Milky Way: {stars:N0} stars (Classic: {Estimate(new GalaxyParams { Arms = 4, BarStrength = 0.4f, Model = GalaxyModel.Classic }):N0})");
        Assert.InRange(stars, 1e11, 5e11);
    }

    [Fact]
    public void Round_the_sun_there_are_a_few_thousandths_of_a_star_a_cubic_light_year()
    {
        double density = LocalDensity(MilkyWay);
        output.WriteLine($"Local density: {density:0.0000} stars/ly³ (real: about 0.004; Classic: {LocalDensity(new GalaxyParams { Arms = 4, BarStrength = 0.4f, Model = GalaxyModel.Classic }):0.0000})");
        Assert.InRange(density, 0.002, 0.012);
    }

    [Fact]
    public void The_disk_is_thin_and_the_bulge_a_fraction_of_the_stars()
    {
        var sampler = new GalaxySampler(MilkyWay);
        // A sech² scale of 600 ly is an exponential scale height of about 300 ly, the real thin disk's.
        Assert.InRange(MilkyWay.ToLightYears(sampler.DiskHeight), 450f, 800f);
        long all = Estimate(MilkyWay);
        long noBulge = Estimate(new GalaxyParams { Arms = 4, BarStrength = 0.4f, BulgeStrength = 0f });
        double share = 1.0 - (double)noBulge / all;
        output.WriteLine($"Bulge share: {share:P1}");
        Assert.InRange(share, 0.08, 0.35);
    }

    [Fact]
    public void A_flat_galaxy_keeps_its_stars_near_the_plane()
    {
        var flat = new GalaxyParams { RadiusLightYears = 3000f, DiskThickness = 0.3f, BulgeThickness = 0.1f, HaloStrength = 0f };
        var generator = StarGenerator.For(flat);
        // Every star in a slab through the galaxy, out to 300 ly either side of the plane.
        var heights = new List<float>();
        for (long x = -280; x <= 280; x += 7)
            for (long z = -280; z <= 280; z += 7)
                for (long y = -30; y <= 30; y++)
                    heights.AddRange(generator.Generate(flat, new ChunkCoord(x, y, z)).Stars.Select(s => MathF.Abs(flat.ToLightYears(s.Y))));
        heights.Sort();
        float p99 = heights[(int)(heights.Count * 0.99)], p999 = heights[(int)(heights.Count * 0.999)];
        float extent = flat.ToLightYears(new GalaxySampler(flat).VerticalExtent);
        output.WriteLine($"Flat galaxy: {heights.Count:N0} stars sampled, 99% within {p99:0} ly of the plane, 99.9% within {p999:0}; none beyond {extent:0} ly; ~{Estimate(flat):N0} stars in all");
        // 6,000 ly across and its stars within 3% of its radius of the plane: the thin disk, and the thick disk's tail.
        Assert.True(p99 < 90f, $"{p99} ly");
        Assert.True(p999 < 150f, $"{p999} ly");
        Assert.True(extent < 300f, $"{extent} ly");
    }
}
