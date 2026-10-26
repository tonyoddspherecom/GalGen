using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UniverseGen.Service;
using UniverseGen.Service.Http;

namespace UniverseGen.Tests;

/// <summary>GalGen over HTTP: the binary protocol through a web server, and the readable JSON and PNG endpoints.</summary>
public sealed class HttpTests : IAsyncLifetime
{
    private readonly GalaxyHost _host = RemoteTests.Host();
    private WebApplication _web = null!;
    private HttpClient _http = null!;
    private Uri _base = null!;

    public async Task InitializeAsync() =>
        (_web, _base) = await Serve(new GalaxyRequestHandler(_host, new GalaxyServerOptions { CostPerSecond = 2000, CostBurst = 2000 }));

    /// A web server on a free port, serving a handler.
    private async Task<(WebApplication Web, Uri Base)> Serve(GalaxyRequestHandler handler)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication web = builder.Build();
        web.MapGalaxyService(handler);
        await web.StartAsync();
        string address = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http ??= new HttpClient { BaseAddress = new Uri(address + "/") };
        return (web, new Uri(address + "/"));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _web.StopAsync();
        await _web.DisposeAsync();
    }

    private async Task<JsonElement> Get(string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        HttpResponseMessage response = await _http.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task The_binary_protocol_over_http_answers_as_the_galaxy_does()
    {
        await using var client = new GalaxyClient(new HttpGalaxyTransport(new Uri(_base, "galaxy/rpc")));
        await RemoteTests.AssertSameAnswers(_host["small"], await client.OpenAsync("small"));
    }

    [Fact]
    public async Task The_readable_endpoints_answer_in_json()
    {
        JsonElement list = await Get("galaxy");
        Assert.Equal("small", list[0].GetProperty("id").GetString());
        JsonElement info = await Get("galaxy/small");
        Assert.Equal(_host["small"].Info.Fingerprint, info.GetProperty("fingerprint").GetString());
        Assert.Equal("Spiral", info.GetProperty("params").GetProperty("type").GetString());

        LocalGalaxy galaxy = _host["small"];
        StarRef home = galaxy.PickStar(new StarPick { Seed = 3, MinGalacticRadius = 0.3f })!;
        JsonElement picked = await Get("galaxy/small/pick?seed=3&minRadius=0.3");
        Assert.Equal(home.Id.ToString(), picked.GetProperty("id").GetString());
        Assert.Equal(home.Designation, picked.GetProperty("designation").GetString());

        // In light years: 40 ly round the home star.
        Vector3 ly = home.Star.Position * galaxy.Info.RadiusLightYears;
        string at = FormattableString.Invariant($"x={ly.X}&y={ly.Y}&z={ly.Z}");
        JsonElement stars = await Get($"galaxy/small/stars?{at}&radius=40&unit=ly&limit=50&classes=G,K,M&exclude=binary");
        var expected = galaxy.FindStars(StarQuery.Sphere(home.Star.Position, galaxy.Info.ToNormalised(40f),
            new StarFilter { Classes = StarFilter.ClassMask(Models.SpectralClass.G, Models.SpectralClass.K, Models.SpectralClass.M), ExcludedFlags = Models.StarFlags.Binary }, 50));
        Assert.Equal(expected.Stars.Select(s => s.Id.ToString()), stars.GetProperty("stars").EnumerateArray().Select(s => s.GetProperty("id").GetString()));

        JsonElement star = await Get($"galaxy/small/stars/{home.Id}");
        Assert.Equal(home.Designation, star.GetProperty("designation").GetString());
        JsonElement system = await Get($"galaxy/small/stars/{home.Id}/system");
        Assert.Equal(galaxy.GetSystem(home.Id)!.Bodies.Length, system.GetProperty("bodies").GetArrayLength());
        await Get($"galaxy/small/stars/{home.Id}/system/bodies/0/moons");
        await Get($"galaxy/small/stars/{home.Id with { Index = 9999 }}", HttpStatusCode.NotFound);

        JsonElement chunk = await Get($"galaxy/small/chunks/{home.Id.X}/{home.Id.Y}/{home.Id.Z}");
        Assert.Equal(galaxy.GetChunk(home.Id.Chunk).Stars.Length, chunk.GetProperty("stars").GetArrayLength());
        JsonElement place = await Get($"galaxy/small/describe?{at}&unit=ly");
        Assert.Equal(galaxy.Describe(home.Star.Position).PoeticName, place.GetProperty("poeticName").GetString());
        await Get("galaxy/small/clusters");
    }

    [Fact]
    public async Task Maps_come_as_png_or_raw_pixels()
    {
        HttpResponseMessage png = await _http.GetAsync("galaxy/small/map?width=128&height=128");
        Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);
        byte[] image = await png.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x89, image[0]);
        byte[] raw = await _http.GetByteArrayAsync("galaxy/small/map?view=edgeon&width=64&height=16&format=uint16");
        Assert.Equal(_host["small"].GetMap(MapRequest.EdgeOn(64, 16, MapFormat.UInt16)).Data, raw);
    }

    [Fact]
    public async Task Mistakes_get_the_right_status()
    {
        await Get("galaxy/nowhere", HttpStatusCode.NotFound);
        await Get("galaxy/small/stars?x=0&y=0&z=0&radius=1", HttpStatusCode.RequestEntityTooLarge);
        await Get("galaxy/small/stars?radius=banana", HttpStatusCode.BadRequest);
        await Get("galaxy/small/stars?radius=0.01&classes=Q", HttpStatusCode.BadRequest);
        await Get("galaxy/small/stars/not-a-star", HttpStatusCode.BadRequest);
        await Get("galaxy/small/map?format=gif", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unchanged_answer_is_not_sent_twice()
    {
        HttpResponseMessage first = await _http.GetAsync("galaxy/small/describe?x=0.3&z=0.1");
        EntityTagHeaderValue tag = first.Headers.ETag!;
        var again = new HttpRequestMessage(HttpMethod.Get, "galaxy/small/describe?x=0.3&z=0.1");
        again.Headers.IfNoneMatch.Add(tag);
        Assert.Equal(HttpStatusCode.NotModified, (await _http.SendAsync(again)).StatusCode);
        Assert.Contains("max-age", first.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task A_client_over_its_limit_is_told_when_to_come_back()
    {
        // A burst of 300 that refills at 1 a second: one 4-megapixel map (256 units) fits, a second doesn't.
        var (web, address) = await Serve(new GalaxyRequestHandler(_host, new GalaxyServerOptions { CostPerSecond = 1, CostBurst = 300 }));
        await using (web)
        {
            using var http = new HttpClient { BaseAddress = address };
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("galaxy/small/map?width=2048&height=2048&format=byte")).StatusCode);
            HttpResponseMessage refused = await http.GetAsync("galaxy/small/map?width=2048&height=2048&format=byte&x=0.5");
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.True(refused.Headers.RetryAfter!.Delta > TimeSpan.FromSeconds(100));
            await web.StopAsync();
        }
    }
}
