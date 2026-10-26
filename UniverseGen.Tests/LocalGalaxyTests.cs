using System.Numerics;
using UniverseGen.Generation;
using UniverseGen.Models;
using UniverseGen.Service;

namespace UniverseGen.Tests;

/// <summary>A galaxy served from this process: its queries checked against brute force, its maps, places, picks and limits.</summary>
public class LocalGalaxyTests
{
    /// A small galaxy with no halo: quick to search, and to count, exhaustively.
    internal static GalaxyParams Small(ulong seed = 31337) => new()
    {
        Seed = seed, RadiusLightYears = 1000f, StarDensity = 0.5f, BulgeFactor = 0.08f, HaloStrength = 0f, Arms = 3,
    };

    internal static LocalGalaxy Galaxy(GalaxyParams? p = null, LocalGalaxyOptions? options = null) =>
        new("small", p ?? Small(), options);

    /// Every star in the chunks a query could touch, and one chunk further in every direction, filtered by hand.
    private static List<StarRef> BruteForce(LocalGalaxy galaxy, StarQuery query)
    {
        float s = galaxy.Info.ChunkSize;
        (Vector3 lo, Vector3 hi) = query.Bounds;
        var found = new List<StarRef>();
        for (long x = (long)MathF.Floor(lo.X / s) - 2; x <= (long)MathF.Ceiling(hi.X / s) + 2; x++)
        for (long y = (long)MathF.Floor(lo.Y / s) - 2; y <= (long)MathF.Ceiling(hi.Y / s) + 2; y++)
        for (long z = (long)MathF.Floor(lo.Z / s) - 2; z <= (long)MathF.Ceiling(hi.Z / s) + 2; z++)
        {
            StarChunk chunk = galaxy.GetChunk(new ChunkCoord(x, y, z));
            for (int i = 0; i < chunk.Stars.Length; i++)
                if (query.Contains(chunk.Stars[i].Position) && query.Filter.Accepts(chunk.Stars[i], chunk.Region))
                    found.Add(new StarRef(new StarId(chunk.Coord, i), chunk.Stars[i], StarNames.Designation(chunk.Stars[i]), chunk.Region));
        }
        return found;
    }

    private static Vector3 InAnArm(LocalGalaxy galaxy) => galaxy.PickStar(new StarPick { Seed = 5, MinGalacticRadius = 0.3f })!.Star.Position;

    [Fact]
    public void A_sphere_query_finds_exactly_the_stars_inside_it_nearest_first()
    {
        LocalGalaxy galaxy = Galaxy();
        Vector3 centre = InAnArm(galaxy);
        var query = StarQuery.Sphere(centre, galaxy.Info.ToNormalised(60f), limit: 20_000);
        StarQueryResult result = galaxy.FindStars(query);
        List<StarRef> expected = BruteForce(galaxy, query);

        Assert.NotEmpty(expected);
        Assert.False(result.Truncated);
        Assert.Equal(expected.Select(s => s.Id).ToHashSet(), result.Stars.Select(s => s.Id).ToHashSet());
        float[] distances = result.Stars.Select(s => Vector3.Distance(s.Star.Position, centre)).ToArray();
        Assert.Equal(distances.Order(), distances);
        Assert.All(result.Stars, s => Assert.Equal(StarNames.Designation(s.Star), s.Designation));
    }

    [Fact]
    public void A_limit_keeps_the_nearest_and_says_there_were_more()
    {
        LocalGalaxy galaxy = Galaxy();
        Vector3 centre = InAnArm(galaxy);
        var all = galaxy.FindStars(StarQuery.Sphere(centre, galaxy.Info.ToNormalised(60f), limit: 20_000)).Stars;
        StarQueryResult some = galaxy.FindStars(StarQuery.Sphere(centre, galaxy.Info.ToNormalised(60f), limit: 7));
        Assert.True(all.Count > 7);
        Assert.True(some.Truncated);
        Assert.Equal(all.Take(7).Select(s => s.Id), some.Stars.Select(s => s.Id));
    }

    [Fact]
    public void A_box_query_with_a_filter_and_each_order()
    {
        LocalGalaxy galaxy = Galaxy();
        Vector3 c = InAnArm(galaxy);
        float r = galaxy.Info.ToNormalised(50f);
        var filter = new StarFilter { Classes = StarFilter.ClassMask(SpectralClass.G, SpectralClass.K, SpectralClass.M), ExcludedFlags = StarFlags.Binary };
        var box = StarQuery.Box(c - new Vector3(r), c + new Vector3(r, r * 0.5f, r), filter, limit: 20_000, order: StarOrder.None);
        List<StarRef> expected = BruteForce(galaxy, box);
        StarQueryResult unordered = galaxy.FindStars(box);
        Assert.Equal(expected.Select(s => s.Id).ToHashSet(), unordered.Stars.Select(s => s.Id).ToHashSet());
        Assert.All(unordered.Stars, s => Assert.True(s.Star.Class is SpectralClass.G or SpectralClass.K or SpectralClass.M && !s.Star.IsBinary));

        var brightest = galaxy.FindStars(box with { Order = StarOrder.Brightest, Limit = 20 }).Stars;
        Assert.Equal(expected.Select(s => (int)s.Star.Luminosity).OrderDescending().Take(20), brightest.Select(s => (int)s.Star.Luminosity));
        // In chunk order a limit is the first stars found, and the same ones every time.
        var first = galaxy.FindStars(box with { Limit = 5 });
        Assert.True(first.Truncated);
        Assert.Equal(unordered.Stars.Take(5).Select(s => s.Id), first.Stars.Select(s => s.Id));
    }

    [Fact]
    public void A_planet_filter_keeps_only_systems_that_have_them()
    {
        LocalGalaxy galaxy = Galaxy();
        var query = StarQuery.Sphere(InAnArm(galaxy), galaxy.Info.ToNormalised(80f), new StarFilter { MinPlanets = 6 }, limit: 1000);
        StarQueryResult result = galaxy.FindStars(query);
        Assert.NotEmpty(result.Stars);
        Assert.All(result.Stars, s => Assert.True(galaxy.GetSystem(s.Id)!.PlanetCount >= 6));
        int any = galaxy.FindStars(query with { Filter = StarFilter.Any }).Stars.Count;
        Assert.True(result.Stars.Count < any);
    }

    [Fact]
    public void Stars_systems_and_moons_are_the_generators_own()
    {
        LocalGalaxy galaxy = Galaxy();
        StarRef star = galaxy.FindStars(StarQuery.Sphere(InAnArm(galaxy), galaxy.Info.ToNormalised(30f), new StarFilter { Classes = StarFilter.ClassMask(SpectralClass.G, SpectralClass.K) })).Stars[0];
        Assert.Equal(star, galaxy.GetStar(star.Id));
        Assert.Null(galaxy.GetStar(star.Id with { Index = 100_000 }));

        StarSystem system = galaxy.GetSystem(star.Id)!;
        StarSystem direct = new StarSystemGenerator().Generate(star.Star, galaxy.Info.Params.Age, galaxy.Info.Params.Seed, star.Designation);
        Assert.Equal(direct.Designation, system.Designation);
        Assert.Equal(direct.Bodies.Select(b => (b.Name, b.Type, b.OrbitAu, b.MoonCount)), system.Bodies.Select(b => (b.Name, b.Type, b.OrbitAu, b.MoonCount)));
        Assert.Same(system, galaxy.GetSystem(star.Id));

        int withMoons = Array.FindIndex(system.Bodies, b => b.MoonCount > 0);
        if (withMoons >= 0)
            Assert.Equal(system.Bodies[withMoons].MoonCount, galaxy.GetMoons(star.Id, withMoons).Count);
        var bad = Assert.Throws<GalaxyServiceException>(() => galaxy.GetMoons(star.Id, 99));
        Assert.Equal(GalaxyError.BadRequest, bad.Error);
    }

    [Fact]
    public void A_place_is_described_as_its_stars_are()
    {
        LocalGalaxy galaxy = Galaxy();
        StarRef star = galaxy.PickStar(new StarPick { Seed = 11 })!;
        LocationInfo place = galaxy.Describe(star.Star.Position);
        Assert.Equal(star.Id.Chunk, place.Chunk);
        Assert.Equal(star.Region, place.Region);
        Assert.Equal(RegionNameGenerator.PoeticName(place.Chunk.X, place.Chunk.Y, place.Chunk.Z, place.Region, galaxy.Info.Params.Seed), place.PoeticName);
        Assert.InRange(place.Density, 0f, 1f);
    }

    [Fact]
    public void A_pick_is_the_same_star_every_time_and_follows_its_rules()
    {
        LocalGalaxy galaxy = Galaxy();
        var pick = new StarPick
        {
            Seed = 2024, MinGalacticRadius = 0.4f, MaxGalacticRadius = 0.7f,
            Filter = new StarFilter { Classes = StarFilter.ClassMask(SpectralClass.G), MinPlanets = 3 },
        };
        StarRef first = galaxy.PickStar(pick)!;
        Assert.Equal(first.Id, Galaxy().PickStar(pick)!.Id);
        Assert.Equal(SpectralClass.G, first.Star.Class);
        Assert.True(galaxy.GetSystem(first.Id)!.PlanetCount >= 3);
        float r = new Vector2(first.Star.X, first.Star.Z).Length();
        Assert.InRange(r, 0.35f, 0.75f);
        // Different seeds scatter.
        var picks = Enumerable.Range(0, 20).Select(i => galaxy.PickStar(pick with { Seed = (ulong)i })?.Id).ToHashSet();
        Assert.True(picks.Count > 15);
    }

    [Fact]
    public void Maps_are_the_generators_and_shared_between_callers()
    {
        GalaxyParams p = Small();
        LocalGalaxy galaxy = Galaxy(p);
        DensityImage face = galaxy.GetMap(MapRequest.FaceOn(128, MapFormat.Float32));
        Assert.Equal(new GalaxyGenerator().Generate(p, 128).DensityMap, face.ToFloats());
        DensityImage bytes = galaxy.GetMap(MapRequest.FaceOn(128));
        Assert.Equal(128 * 128, bytes.Data.Length);
        Assert.Equal(face[60, 70], bytes[60, 70], 2);
        Assert.Same(bytes, galaxy.GetMap(MapRequest.FaceOn(128)));

        var region = new RegionF(0.5f, 0.5f, 0.1f, 0.1f);
        Assert.Equal(new GalaxyGenerator().GenerateZoomedView(p, region, 1, 64).DensityMap, galaxy.GetMap(MapRequest.Zoomed(region, 64, MapFormat.Float32)).ToFloats());
        Assert.Equal(new GalaxyGenerator().GenerateSideView(p, 64, 16).DensityMap, galaxy.GetMap(MapRequest.EdgeOn(64, 16, MapFormat.Float32)).ToFloats());

        byte[] png = bytes.ToPng();
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
    }

    [Fact]
    public void Requests_past_the_limits_or_that_make_no_sense_are_refused()
    {
        LocalGalaxy galaxy = Galaxy(options: new LocalGalaxyOptions { MaxQueryChunks = 1000, MaxChunksPerRequest = 10, MaxMapPixels = 100 * 100 });
        GalaxyError Error(Action act) => Assert.Throws<GalaxyServiceException>(act).Error;

        Assert.Equal(GalaxyError.LimitExceeded, Error(() => galaxy.FindStars(StarQuery.Sphere(Vector3.Zero, 0.5f))));
        Assert.Equal(GalaxyError.LimitExceeded, Error(() => galaxy.GetChunks(Enumerable.Range(0, 11).Select(i => new ChunkCoord(i, 0, 0)).ToList())));
        Assert.Equal(GalaxyError.LimitExceeded, Error(() => galaxy.GetMap(MapRequest.FaceOn(101))));
        Assert.Equal(GalaxyError.BadRequest, Error(() => galaxy.FindStars(StarQuery.Sphere(new Vector3(float.NaN), 0.01f))));
        Assert.Equal(GalaxyError.BadRequest, Error(() => galaxy.FindStars(StarQuery.Sphere(Vector3.Zero, -1f))));
        Assert.Equal(GalaxyError.BadRequest, Error(() => galaxy.GetMap(MapRequest.Zoomed(new RegionF(0, 0, 0, 0.1f), 64))));
        Assert.Equal(GalaxyError.BadRequest, Error(() => galaxy.PickStar(new StarPick { MinGalacticRadius = 0.5f, MaxGalacticRadius = 0.5f })));
        Assert.Equal(GalaxyError.BadRequest, Error(() => galaxy.Describe(new Vector3(1e9f, 0, 0))));
        // Space above the disk costs nothing to search: it's known to be empty.
        Assert.Equal(0, galaxy.QueryChunkCount(StarQuery.Sphere(new Vector3(0.5f, 0.9f, 0f), 0.05f)));
    }

    [Fact]
    public void The_census_estimate_is_close_to_the_true_count()
    {
        foreach (GalaxyParams p in new[] { Small(), Small(7), new GalaxyParams { RadiusLightYears = 600f, StarDensity = 0.3f, DiskThickness = 0.2f, BulgeFactor = 0.1f, Seed = 3 } })
        {
            float chunk = p.ToNormalised(10f);
            long exact = GalaxyCensus.Count(p, chunk, 200);
            long estimate = GalaxyCensus.Estimate(p, chunk, 200);
            Assert.True(exact > 1000, $"{exact} stars");
            Assert.InRange(estimate, exact * 0.9, exact * 1.1);
        }
        Assert.Throws<InvalidOperationException>(() => GalaxyCensus.Count(new GalaxyParams(), 0.0002f, 200, maxChunks: 1_000_000));
    }

    [Fact]
    public void A_host_serves_galaxies_by_id()
    {
        var host = new GalaxyHost();
        host.Add("alpha", Small(1), "Alpha");
        host.Add("beta", Small(2));
        Assert.Throws<ArgumentException>(() => host.Add("alpha", Small(3)));
        Assert.Throws<ArgumentException>(() => host.Add("not ok!", Small(3)));
        Assert.Equal(["alpha", "beta"], host.Galaxies.Select(g => g.Info.Id));
        Assert.NotEqual(host["alpha"].Info.Fingerprint, host["beta"].Info.Fingerprint);
        Assert.Equal(GalaxyError.UnknownGalaxy, Assert.Throws<GalaxyServiceException>(() => host["gamma"]).Error);
        Assert.True(host.Remove("beta"));
        Assert.Single(host.Galaxies);
    }

    [Fact]
    public void The_same_params_served_with_different_chunks_are_a_different_galaxy()
    {
        GalaxyParams p = Small();
        Assert.Equal(Galaxy(p).Info.Fingerprint, Galaxy(p).Info.Fingerprint);
        Assert.NotEqual(Galaxy(p).Info.Fingerprint, Galaxy(p, new LocalGalaxyOptions { ChunkLightYears = 20f }).Info.Fingerprint);
        Assert.NotEqual(Galaxy(p).Info.Fingerprint, Galaxy(p, new LocalGalaxyOptions { MaxStarsPerChunk = 100 }).Info.Fingerprint);
    }
}
