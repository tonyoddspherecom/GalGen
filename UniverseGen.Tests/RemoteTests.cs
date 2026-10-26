using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using UniverseGen.Models;
using UniverseGen.Service;
using UniverseGen.Service.Protocol;

namespace UniverseGen.Tests;

/// <summary>Galaxies over the wire: the protocol, the client, the TCP server, and everything that can go wrong between them.</summary>
public class RemoteTests
{
    internal static GalaxyHost Host(params (string Id, ulong Seed)[] galaxies)
    {
        var host = new GalaxyHost();
        foreach (var (id, seed) in galaxies.Length > 0 ? galaxies : [("small", 31337UL)])
            host.Add(id, LocalGalaxyTests.Small(seed), name: id.ToUpperInvariant(), description: "a test galaxy");
        return host;
    }

    private static GalaxyClient Loopback(GalaxyRequestHandler handler, GalaxyClientOptions? options = null) =>
        new(new LoopbackGalaxyTransport(handler), options);

    private static GalaxyClient Loopback(GalaxyHost host, GalaxyClientOptions? options = null) =>
        Loopback(new GalaxyRequestHandler(host), options);

    /// Asks a local and a remote galaxy the same of everything, and checks they answer the same.
    internal static async Task AssertSameAnswers(IGalaxy local, IGalaxy remote)
    {
        Assert.Equal(local.Info, remote.Info with { Params = local.Info.Params });
        Assert.Equal(local.Info.Params.Fingerprint(), remote.Info.Params.Fingerprint());

        DensityImage localMap = await local.GetMapAsync(MapRequest.FaceOn(64)), remoteMap = await remote.GetMapAsync(MapRequest.FaceOn(64));
        Assert.Equal(localMap.Data, remoteMap.Data);
        var edge = MapRequest.EdgeOn(64, 16, MapFormat.UInt16);
        Assert.Equal((await local.GetMapAsync(edge)).Data, (await remote.GetMapAsync(edge)).Data);

        StarRef home = (await local.PickStarAsync(new StarPick { Seed = 99, MinGalacticRadius = 0.3f }))!;
        Assert.Equal(home, await remote.PickStarAsync(new StarPick { Seed = 99, MinGalacticRadius = 0.3f }));

        var query = StarQuery.Sphere(home.Star.Position, local.Info.ToNormalised(40f), new StarFilter { ExcludedFlags = StarFlags.Giant }, limit: 500);
        StarQueryResult localStars = await local.FindStarsAsync(query), remoteStars = await remote.FindStarsAsync(query);
        Assert.Equal(localStars.Stars, remoteStars.Stars);
        Assert.Equal((localStars.Truncated, localStars.ChunksSearched), (remoteStars.Truncated, remoteStars.ChunksSearched));

        ChunkCoord at = home.Id.Chunk;
        var coords = Enumerable.Range(-3, 7).Select(dx => new ChunkCoord(at.X + dx, at.Y, at.Z)).ToList();
        var localChunks = await local.GetChunksAsync(coords);
        var remoteChunks = await remote.GetChunksAsync(coords);
        for (int i = 0; i < coords.Count; i++)
        {
            Assert.Equal(localChunks[i].Coord, remoteChunks[i].Coord);
            Assert.Equal(localChunks[i].Stars, remoteChunks[i].Stars);
            Assert.Equal((localChunks[i].Region, localChunks[i].Density), (remoteChunks[i].Region, remoteChunks[i].Density));
        }

        Assert.Equal(home, await remote.GetStarAsync(home.Id));
        Assert.Null(await remote.GetStarAsync(home.Id with { Index = 9999 }));
        StarSystem localSystem = (await local.GetSystemAsync(home.Id))!, remoteSystem = (await remote.GetSystemAsync(home.Id))!;
        Assert.Equal(Describe(localSystem), Describe(remoteSystem));
        for (int b = 0; b < localSystem.Bodies.Length; b++)
            Assert.Equal(await local.GetMoonsAsync(home.Id, b), await remote.GetMoonsAsync(home.Id, b));

        Assert.Equal(await local.DescribeAsync(home.Star.Position), await remote.DescribeAsync(home.Star.Position));
        Assert.Equal(await local.GetClustersAsync(), await remote.GetClustersAsync());
    }

    private static string Describe(StarSystem s) =>
        $"{s.Designation} {s.StarClass} {s.LuminositySol} {s.HzInnerAu} {s.HzOuterAu} {s.SnowLineAu} {s.HasOortCloud} " +
        string.Join(";", s.Companions.Select(c => $"{c}")) + " | " +
        string.Join(";", s.Bodies.Select(b => $"{b.Name},{b.Type},{b.Atmosphere},{b.Surface},{b.OrbitAu},{b.MassEarth},{b.RadiusEarth},{b.TemperatureK},{b.GravitySurface},{b.MoonCount},{b.HasRings},{b.InHabitableZone}"));

    [Fact]
    public async Task A_remote_galaxy_answers_exactly_as_the_local_one_does()
    {
        GalaxyHost host = Host();
        await using GalaxyClient client = Loopback(host);
        await AssertSameAnswers(host["small"], await client.OpenAsync("small"));
    }

    [Fact]
    public async Task The_server_says_who_it_is_and_lists_its_galaxies()
    {
        GalaxyHost host = Host(("alpha", 1), ("beta", 2));
        await using GalaxyClient client = Loopback(new GalaxyRequestHandler(host, new GalaxyServerOptions { Name = "Test server", Limits = new LocalGalaxyOptions { MaxResults = 77 } }));
        ServerHello hello = await client.HelloAsync();
        Assert.Equal(("Test server", (int)GalaxyProtocol.Version, 77), (hello.Name, hello.ProtocolVersion, hello.MaxResults));
        var list = await client.ListAsync();
        Assert.Equal(["alpha", "beta"], list.Select(g => g.Id));
        Assert.Equal("ALPHA", list[0].Name);
        Assert.Equal(host["beta"].Info.Fingerprint, list[1].Fingerprint);
        var stats = await client.StatsAsync();
        Assert.Equal(2, stats.Count);
    }

    [Fact]
    public async Task The_servers_errors_reach_the_client_as_they_were()
    {
        await using GalaxyClient client = Loopback(Host());
        var unknown = await Assert.ThrowsAsync<GalaxyServiceException>(async () => await client.OpenAsync("nowhere"));
        Assert.Equal(GalaxyError.UnknownGalaxy, unknown.Error);
        RemoteGalaxy galaxy = await client.OpenAsync("small");
        var tooBig = await Assert.ThrowsAsync<GalaxyServiceException>(async () => await galaxy.FindStarsAsync(StarQuery.Sphere(Vector3.Zero, 2f)));
        Assert.Equal(GalaxyError.LimitExceeded, tooBig.Error);
        var bad = await Assert.ThrowsAsync<GalaxyServiceException>(async () => await galaxy.GetMoonsAsync(new StarId(0, 0, 0, 0), -5));
        Assert.Equal(GalaxyError.BadRequest, bad.Error);
    }

    [Fact]
    public async Task Garbage_never_breaks_the_server()
    {
        var handler = new GalaxyRequestHandler(Host());
        GalaxyConnection connection = handler.Connect("fuzzer");
        var random = new Random(1234);
        var valid = GalaxyProtocol.Request(GalaxyRequestKind.FindStars, 7);
        valid.Str("small");
        Codec.Write(valid, StarQuery.Sphere(new Vector3(0.3f, 0, 0.1f), 0.02f));
        byte[] good = valid.ToArray();
        for (int i = 0; i < 3000; i++)
        {
            byte[] frame;
            if (i % 2 == 0)
            {
                frame = new byte[random.Next(0, 64)];
                random.NextBytes(frame);
                if (frame.Length > 1 && i % 4 == 0) { frame[0] = GalaxyProtocol.Version; frame[1] = (byte)random.Next(1, 14); }
            }
            else
            {
                // A real request, damaged: a byte flipped, or cut short.
                frame = (byte[])good.Clone();
                if (i % 3 == 0) frame = frame[..random.Next(0, frame.Length)];
                else frame[random.Next(2, frame.Length)] ^= (byte)random.Next(1, 256);
            }
            byte[] response = await handler.HandleAsync(connection, frame);
            Assert.True(response.Length >= GalaxyProtocol.ResponseHeaderSize);
            try { GalaxyProtocol.OpenResponse(response, (GalaxyRequestKind)response[2]); }
            catch (GalaxyServiceException e) { Assert.NotEqual(GalaxyError.Internal, e.Error); }
        }
    }

    [Fact]
    public async Task A_client_on_another_version_is_told_so()
    {
        var handler = new GalaxyRequestHandler(Host());
        byte[] frame = [(byte)(GalaxyProtocol.Version + 1), (byte)GalaxyRequestKind.Hello, 1, 0, 0, 0, 0];
        byte[] response = await handler.HandleAsync(handler.Connect(), frame);
        var e = Assert.Throws<GalaxyServiceException>(() => GalaxyProtocol.OpenResponse(response, GalaxyRequestKind.Hello));
        Assert.Equal(GalaxyError.VersionMismatch, e.Error);
    }

    [Fact]
    public async Task A_busy_client_is_slowed_and_its_retries_get_through()
    {
        var handler = new GalaxyRequestHandler(Host(), new GalaxyServerOptions { CostPerSecond = 40, CostBurst = 40 });
        await using GalaxyClient patient = Loopback(handler, new GalaxyClientOptions { MaxRetries = 20 });
        RemoteGalaxy galaxy = await patient.OpenAsync("small");
        // 30 chunks a request: the second has to wait for the bucket to refill.
        for (int i = 0; i < 3; i++)
            await galaxy.GetChunksAsync(Enumerable.Range(0, 30).Select(x => new ChunkCoord(x, 0, i)).ToList());

        await using GalaxyClient impatient = Loopback(handler, new GalaxyClientOptions { MaxRetries = 0 });
        RemoteGalaxy other = await impatient.OpenAsync("small");
        await other.GetChunksAsync(Enumerable.Range(0, 40).Select(x => new ChunkCoord(x, 0, 50)).ToList());
        var e = await Assert.ThrowsAsync<GalaxyServiceException>(async () =>
            await other.GetChunksAsync(Enumerable.Range(0, 40).Select(x => new ChunkCoord(x, 0, 51)).ToList()));
        Assert.Equal(GalaxyError.RateLimited, e.Error);
        Assert.True(e.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task A_galaxy_a_client_may_not_see_is_as_good_as_absent()
    {
        var handler = new GalaxyRequestHandler(Host(("public", 1), ("secret", 2)), new GalaxyServerOptions
        {
            Authorize = (connection, kind, galaxy) => galaxy != "secret" || Equals(connection.Tag, "admin"),
        });
        await using GalaxyClient client = Loopback(handler);
        Assert.Equal(["public"], (await client.ListAsync()).Select(g => g.Id));
        var e = await Assert.ThrowsAsync<GalaxyServiceException>(async () => await client.OpenAsync("secret"));
        Assert.Equal(GalaxyError.UnknownGalaxy, e.Error);
    }

    [Fact]
    public async Task Callers_asking_for_the_same_chunks_share_one_request_and_big_asks_are_split()
    {
        var host = Host();
        var handler = new GalaxyRequestHandler(host, new GalaxyServerOptions { Limits = new LocalGalaxyOptions { MaxChunksPerRequest = 50 } });
        int requests = 0;
        handler.RequestHandled += log =>
        {
            if (log.Kind == GalaxyRequestKind.Chunks) Interlocked.Increment(ref requests);
        };
        await using GalaxyClient client = Loopback(handler);
        RemoteGalaxy galaxy = await client.OpenAsync("small");
        var many = Enumerable.Range(0, 120).Select(x => new ChunkCoord(x, 0, 0)).ToList();
        var a = galaxy.GetChunksAsync(many).AsTask();
        var b = galaxy.GetChunksAsync(many.Take(60).ToList()).AsTask();
        await Task.WhenAll(a, b);
        Assert.Equal(many.Select(c => host["small"].GetChunk(c).Stars.Length), a.Result.Select(c => c.Stars.Length));
        // 120 chunks in batches of 50 is three requests, and the second caller's 60 were already on their way.
        Assert.Equal(3, requests);
        await galaxy.GetChunksAsync(many);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task A_stored_answer_needs_no_server()
    {
        string folder = Path.Combine(Path.GetTempPath(), "galgen-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            GalaxyHost host = Host();
            var store = new FileResponseStore(folder);
            StarQuery query;
            StarQueryResult online;
            await using (GalaxyClient client = Loopback(host, new GalaxyClientOptions { Store = store }))
            {
                RemoteGalaxy galaxy = await client.OpenAsync("small");
                query = StarQuery.Sphere(new Vector3(0.3f, 0f, 0.2f), galaxy.Info.ToNormalised(30f));
                online = await galaxy.FindStarsAsync(query);
                await galaxy.GetMapAsync(MapRequest.FaceOn(64));
            }
            // The server's gone. A new client with the same store, and the galaxy's info from before (a game
            // would keep it with its save), still has every answer it asked for, and nothing else.
            var offline = new MessageGalaxyTransport((_, _) => throw new GalaxyServiceException(GalaxyError.Unavailable, "offline"));
            await using GalaxyClient cut = new(offline, new GalaxyClientOptions { Store = new FileResponseStore(folder), MaxRetries = 0 });
            var galaxyOffline = new RemoteGalaxy(cut, host["small"].Info);
            Assert.Equal(online.Stars, (await galaxyOffline.FindStarsAsync(query)).Stars);
            Assert.Equal(64 * 64, (await galaxyOffline.GetMapAsync(MapRequest.FaceOn(64))).Data.Length);
            await Assert.ThrowsAsync<GalaxyServiceException>(async () => await galaxyOffline.GetMapAsync(MapRequest.FaceOn(32)));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Answers_can_come_back_in_any_order_on_a_message_channel()
    {
        var handler = new GalaxyRequestHandler(Host());
        GalaxyConnection connection = handler.Connect();
        var outbox = new ConcurrentQueue<ReadOnlyMemory<byte>>();
        MessageGalaxyTransport? transport = null;
        transport = new MessageGalaxyTransport((frame, _) => { outbox.Enqueue(frame.ToArray()); return ValueTask.CompletedTask; });
        await using GalaxyClient client = new(transport);
        RemoteGalaxy galaxy;
        {
            Task<RemoteGalaxy> opening = client.OpenAsync("small").AsTask();
            while (!opening.IsCompleted)
            {
                // Deliver everything sent so far, newest first.
                var batch = new List<ReadOnlyMemory<byte>>();
                while (outbox.TryDequeue(out var f)) batch.Add(f);
                batch.Reverse();
                foreach (var f in batch) transport.Receive(await handler.HandleAsync(connection, f));
                await Task.Delay(1);
            }
            galaxy = await opening;
        }
        var asks = Enumerable.Range(0, 10).Select(i => galaxy.DescribeAsync(new Vector3(i * 0.05f, 0, 0.1f)).AsTask()).ToList();
        while (outbox.Count < 10) await Task.Delay(1);
        var frames = new List<ReadOnlyMemory<byte>>();
        while (outbox.TryDequeue(out var f)) frames.Add(f);
        frames.Reverse();
        foreach (var f in frames) Assert.True(transport.Receive(await handler.HandleAsync(connection, f)));
        var places = await Task.WhenAll(asks);
        for (int i = 0; i < 10; i++)
            Assert.Equal(new Vector3(i * 0.05f, 0, 0.1f), places[i].Position);
        // An answer nobody's waiting for is ignored.
        Assert.False(transport.Receive(await handler.HandleAsync(connection, frames[0])));
    }

    [Fact]
    public async Task Over_tcp_with_many_requests_at_once_and_a_server_restart()
    {
        GalaxyHost host = Host();
        var handler = new GalaxyRequestHandler(host);
        var options = new TcpGalaxyServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) };
        var server = new TcpGalaxyServer(handler, options);
        server.Start();
        int port = server.LocalEndPoint!.Port;
        await using GalaxyClient client = GalaxyClient.Tcp("127.0.0.1", port, new GalaxyClientOptions { MaxRetries = 5 });
        RemoteGalaxy galaxy = await client.OpenAsync("small");
        await AssertSameAnswers(host["small"], galaxy);

        var many = Enumerable.Range(0, 60).Select(i => galaxy.DescribeAsync(new Vector3(i * 0.01f, 0f, -0.2f)).AsTask()).ToList();
        var places = await Task.WhenAll(many);
        for (int i = 0; i < places.Length; i++)
            Assert.Equal(host["small"].Describe(new Vector3(i * 0.01f, 0f, -0.2f)), places[i]);
        Assert.Equal(1, server.ConnectionCount);

        // The server goes down and comes back on the same port: the client reconnects by itself.
        await server.StopAsync();
        await using var again = new TcpGalaxyServer(handler, options with { EndPoint = new IPEndPoint(IPAddress.Loopback, port) });
        again.Start();
        LocationInfo after = await galaxy.DescribeAsync(new Vector3(0.123f, 0f, 0.2f));
        Assert.Equal(host["small"].Describe(new Vector3(0.123f, 0f, 0.2f)), after);
    }

    [Fact]
    public async Task A_client_can_serve_as_a_relay_for_another_server()
    {
        GalaxyHost origin = Host();
        await using GalaxyClient upstream = Loopback(origin);
        // A relay serves what the origin has, caching as it goes, to its own clients.
        await using GalaxyClient downstream = Loopback(new GalaxyRequestHandler(upstream));
        await AssertSameAnswers(origin["small"], await downstream.OpenAsync("small"));
    }

    [Fact]
    public void Star_ids_read_back_as_they_were_written()
    {
        var id = new StarId(-12, 3, 4_000_000_000L, 17);
        Assert.Equal("-12:3:4000000000:17", id.ToString());
        Assert.Equal(id, StarId.Parse(id.ToString()));
        Assert.False(StarId.TryParse("1:2:3", out _));
        Assert.False(StarId.TryParse("1:2:3:-4", out _));
    }
}
