using UniverseGen.Generation;
using UniverseGen.Models;
using UniverseGen.Service;

namespace UniverseGen.Tests;

/// <summary>A star's name is the same on every runtime: the one it's always had.</summary>
public class StarNamesTests
{
    // Taken on .NET 8, where these names have always come from: stars on every side of the centre.
    [Theory]
    [InlineData(0.31f, 0.004f, 0.27f, "SAO 464653")]
    [InlineData(-0.25f, -0.01f, -0.3f, "HIP 348863")]
    [InlineData(-0.4f, -0.002f, -0.12f, "HIP 720821")]
    [InlineData(0.5f, -0.003f, -0.2f, "TYC 615112")]
    public void A_star_keeps_its_name_wherever_it_is(float x, float y, float z, string name)
    {
        Assert.Equal(name, StarNames.Designation(new Star(x, y, z, SpectralClass.G, 128, StarFlags.None)));
    }

    [Fact]
    public void Stars_below_and_behind_the_centre_have_names_of_their_own()
    {
        var galaxy = LocalGalaxyTests.Galaxy();
        var stars = galaxy.FindStars(StarQuery.Sphere(new System.Numerics.Vector3(-0.3f, -0.001f, -0.3f), 0.02f, limit: 200)).Stars;
        Assert.True(stars.Count > 50);
        Assert.True(stars.Select(s => s.Designation).Distinct().Count() > stars.Count * 0.95);
    }
}
