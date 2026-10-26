using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UniverseGen.Models;
using UniverseGen.Service.Protocol;

namespace UniverseGen.Service.Http;

/// <summary>How the HTTP endpoints identify clients, and how long answers may be cached.</summary>
public sealed record HttpGalaxyOptions
{
    /// Which client a request is from, for rate limits and authorisation: its IP address by default.
    /// Use an account or API key instead when there's one.
    public Func<HttpContext, string> ClientKey { get; init; } = context => context.Connection.RemoteIpAddress?.ToString() ?? "";
    /// Called the first time a client is seen: set <see cref="GalaxyConnection.Tag"/> (an account, a role) for
    /// <see cref="GalaxyServerOptions.Authorize"/> to check.
    public Action<HttpContext, GalaxyConnection>? OnConnect { get; init; }
    /// How long a client's rate-limit state is kept after its last request.
    public TimeSpan ClientIdle { get; init; } = TimeSpan.FromMinutes(10);
    /// How long browsers and caches may keep an answer. Answers only change if the server's galaxies do.
    public TimeSpan CacheFor { get; init; } = TimeSpan.FromHours(1);
    /// The readable JSON and PNG endpoints, beside the binary one. Off leaves only {prefix}/rpc.
    public bool ReadableEndpoints { get; init; } = true;
}

/// <summary>
/// GalGen over HTTP, for ASP.NET Core:
/// <code>app.MapGalaxyService(new GalaxyRequestHandler(host));</code>
///
/// <c>POST {prefix}/rpc</c> carries the binary protocol (for <see cref="HttpGalaxyTransport"/>).
/// Beside it, readable endpoints answer in JSON (and maps in PNG) for web pages, tools and curl:
/// <list type="bullet">
/// <item><c>GET {prefix}</c>: the galaxies</item>
/// <item><c>GET {prefix}/{id}</c>: one galaxy's info</item>
/// <item><c>GET {prefix}/{id}/map?view=faceon|region|edgeon&amp;width=&amp;height=&amp;x=&amp;y=&amp;w=&amp;h=&amp;format=png|byte|uint16|float32</c></item>
/// <item><c>GET {prefix}/{id}/stars?x=&amp;y=&amp;z=&amp;radius=&amp;unit=ly&amp;limit=&amp;order=nearest|brightest|none&amp;classes=G,K&amp;regions=&amp;exclude=binary&amp;minPlanets=&amp;habitable=</c></item>
/// <item><c>GET {prefix}/{id}/stars/{x:y:z:i}</c>, <c>…/system</c>, <c>…/system/bodies/{n}/moons</c></item>
/// <item><c>GET {prefix}/{id}/chunks/{x}/{y}/{z}</c></item>
/// <item><c>GET {prefix}/{id}/describe?x=&amp;y=&amp;z=&amp;unit=ly</c></item>
/// <item><c>GET {prefix}/{id}/pick?seed=&amp;minRadius=&amp;maxRadius=&amp;classes=&amp;minPlanets=&amp;habitable=</c></item>
/// <item><c>GET {prefix}/{id}/clusters</c></item>
/// </list>
/// Every endpoint goes through the same <see cref="GalaxyRequestHandler"/>, so its limits, rate limits,
/// authorisation and logging apply to all of them alike. Returns the route group, for adding CORS,
/// authentication or ASP.NET's own rate limiting.
/// </summary>
public static class GalaxyEndpoints
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true,   // Vector3's X, Y and Z are fields
        Converters = { new JsonStringEnumConverter(), new StarIdConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static RouteGroupBuilder MapGalaxyService(this IEndpointRouteBuilder app, GalaxyRequestHandler handler,
                                                     string prefix = "/galaxy", HttpGalaxyOptions? options = null)
    {
        var api = new Api(handler, options ?? new HttpGalaxyOptions());
        RouteGroupBuilder group = app.MapGroup(prefix);
        // Cast so it binds as a route handler whose result is written, not a RequestDelegate that drops it.
        group.MapPost("/rpc", (Delegate)api.Rpc);
        if (!api.Options.ReadableEndpoints) return group;

        group.MapGet("/", (Delegate)((HttpContext c) => api.Call(c, GalaxyRequestKind.List, _ => { }, r =>
        {
            var list = new GalaxyInfo[r.Count(20)];
            for (int i = 0; i < list.Length; i++) list[i] = Codec.ReadInfo(r);
            return list;
        })));
        group.MapGet("/{id}", (HttpContext c, string id) => api.Call(c, GalaxyRequestKind.Info, w => w.Str(id), Codec.ReadInfo));
        group.MapGet("/{id}/map", api.Map);
        group.MapGet("/{id}/stars", api.Stars);
        group.MapGet("/{id}/stars/{star}", (HttpContext c, string id, string star) =>
            WithStar(star, s => api.Call(c, GalaxyRequestKind.Star, w => { w.Str(id); Codec.Write(w, s); },
                r => r.Bool() ? Codec.ReadStarRef(r) : null)));
        group.MapGet("/{id}/stars/{star}/system", (HttpContext c, string id, string star) =>
            WithStar(star, s => api.Call(c, GalaxyRequestKind.System, w => { w.Str(id); Codec.Write(w, s); },
                r => r.Bool() ? Codec.ReadSystem(r) : null)));
        group.MapGet("/{id}/stars/{star}/system/bodies/{body:int}/moons", (HttpContext c, string id, string star, int body) =>
            WithStar(star, s => api.Call(c, GalaxyRequestKind.Moons, w => { w.Str(id); Codec.Write(w, s); w.VarI(body); }, r =>
            {
                var moons = new Moon[r.Count(14)];
                for (int i = 0; i < moons.Length; i++) moons[i] = Codec.ReadMoon(r);
                return moons;
            })));
        group.MapGet("/{id}/chunks/{x:long}/{y:long}/{z:long}", (HttpContext c, string id, long x, long y, long z) =>
            api.Call(c, GalaxyRequestKind.Chunks, w => { w.Str(id); w.VarU(1); Codec.Write(w, new ChunkCoord(x, y, z)); }, r =>
            {
                r.Count(6);
                return Codec.ReadChunk(r);
            }));
        group.MapGet("/{id}/describe", api.Describe);
        group.MapGet("/{id}/pick", api.Pick);
        group.MapGet("/{id}/clusters", (HttpContext c, string id) => api.Call(c, GalaxyRequestKind.Clusters, w => w.Str(id), r =>
        {
            var clusters = new GalaxyCluster[r.Count(23)];
            for (int i = 0; i < clusters.Length; i++) clusters[i] = Codec.ReadCluster(r);
            return clusters;
        }));
        return group;
    }

    private static async Task<IResult> WithStar(string text, Func<StarId, Task<IResult>> then) =>
        StarId.TryParse(text, out StarId id) ? await then(id) : Problem(GalaxyError.BadRequest, $"'{text}' isn't a star id (x:y:z:index).");

    internal static IResult Problem(GalaxyError error, string message, TimeSpan retryAfter = default)
    {
        int status = error switch
        {
            GalaxyError.BadRequest or GalaxyError.VersionMismatch => StatusCodes.Status400BadRequest,
            GalaxyError.UnknownGalaxy => StatusCodes.Status404NotFound,
            GalaxyError.LimitExceeded => StatusCodes.Status413PayloadTooLarge,
            GalaxyError.RateLimited => StatusCodes.Status429TooManyRequests,
            GalaxyError.Unsupported => StatusCodes.Status403Forbidden,
            GalaxyError.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };
        var body = new { error = error.ToString(), message, retryAfterSeconds = retryAfter > TimeSpan.Zero ? Math.Ceiling(retryAfter.TotalSeconds) : (double?)null };
        IResult result = Results.Json(body, Json, statusCode: status);
        return retryAfter > TimeSpan.Zero ? new WithHeader(result, "Retry-After", Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture)) : result;
    }

    /// The endpoints' shared state: the handler, and one connection per client.
    private sealed class Api(GalaxyRequestHandler handler, HttpGalaxyOptions options)
    {
        private readonly ConcurrentDictionary<string, (GalaxyConnection Connection, long LastUsed)> _clients = new();
        private int _sincePrune;

        public HttpGalaxyOptions Options { get; } = options;

        private GalaxyConnection Connection(HttpContext context)
        {
            string key = Options.ClientKey(context);
            long now = Environment.TickCount64;
            var entry = _clients.AddOrUpdate(key,
                _ =>
                {
                    GalaxyConnection connection = handler.Connect(key);
                    Options.OnConnect?.Invoke(context, connection);
                    return (connection, now);
                },
                (_, existing) => (existing.Connection, now));
            if (Interlocked.Increment(ref _sincePrune) % 1024 == 0)
            {
                long idle = (long)Options.ClientIdle.TotalMilliseconds;
                foreach (var (k, v) in _clients)
                    if (now - v.LastUsed > idle) _clients.TryRemove(k, out _);
            }
            return entry.Connection;
        }

        public async Task<IResult> Rpc(HttpContext context)
        {
            long? length = context.Request.ContentLength;
            if (length > handler.Options.MaxRequestBytes)
                return Problem(GalaxyError.LimitExceeded, $"Requests can't be over {handler.Options.MaxRequestBytes} bytes.");
            using var body = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                body.Write(buffer, 0, read);
                if (body.Length > handler.Options.MaxRequestBytes)
                    return Problem(GalaxyError.LimitExceeded, $"Requests can't be over {handler.Options.MaxRequestBytes} bytes.");
            }
            byte[] response = await handler.HandleAsync(Connection(context), body.ToArray(), context.RequestAborted);
            return Results.Bytes(response, HttpGalaxyTransport.ContentType);
        }

        /// One protocol request, made on the caller's behalf, its answer turned into JSON or another result.
        public async Task<IResult> Call<T>(HttpContext context, GalaxyRequestKind kind, Action<WireWriter> body, Func<WireReader, T> read,
                                           Func<T, IResult>? present = null)
        {
            WireWriter frame = GalaxyProtocol.Request(kind, 1);
            body(frame);
            byte[] response = await handler.HandleAsync(Connection(context), frame.ToArray(), context.RequestAborted);
            T value;
            try { value = read(GalaxyProtocol.OpenResponse(response, kind)); }
            catch (GalaxyServiceException e) { return Problem(e.Error, e.Message, e.RetryAfter); }
            if (value == null) return Results.Json(new { error = "NotFound", message = "There's nothing there." }, Json, statusCode: StatusCodes.Status404NotFound);

            // The same answer has the same tag, so a cache can check rather than fetch again.
            string tag = "\"" + Convert.ToHexString(SHA256.HashData(response.AsSpan(GalaxyProtocol.ResponseHeaderSize)))[..24] + "\"";
            context.Response.Headers.ETag = tag;
            context.Response.Headers.CacheControl = $"public, max-age={(int)Options.CacheFor.TotalSeconds}";
            if (context.Request.Headers.IfNoneMatch == tag) return Results.StatusCode(StatusCodes.Status304NotModified);
            return present != null ? present(value) : Results.Json(value, Json);
        }

        private async Task<GalaxyInfo?> Info(HttpContext context, string id)
        {
            WireWriter frame = GalaxyProtocol.Request(GalaxyRequestKind.Info, 1);
            frame.Str(id);
            byte[] response = await handler.HandleAsync(Connection(context), frame.ToArray(), context.RequestAborted);
            try { return Codec.ReadInfo(GalaxyProtocol.OpenResponse(response, GalaxyRequestKind.Info)); }
            catch (GalaxyServiceException) { return null; }
        }

        public async Task<IResult> Map(HttpContext context, string id)
        {
            var q = new Query(context);
            var view = q.Enum("view", MapView.FaceOn);
            string format = (q.Text("format") ?? "png").ToLowerInvariant();
            var request = new MapRequest
            {
                View = view,
                Width = q.Int("width", 512),
                Height = q.Int("height", view == MapView.EdgeOn ? 128 : 512),
                Region = new RegionF(q.Float("x", 0f), q.Float("y", 0f), q.Float("w", 1f), q.Float("h", 1f)),
                Format = format switch { "png" or "byte" => MapFormat.Byte, "uint16" => MapFormat.UInt16, "float32" => MapFormat.Float32, _ => (MapFormat)255 },
            };
            if (q.Error != null || !Enum.IsDefined(request.Format)) return Problem(GalaxyError.BadRequest, q.Error ?? $"'{format}' isn't png, byte, uint16 or float32.");
            return await Call(context, GalaxyRequestKind.Map, w => { w.Str(id); Codec.Write(w, request); }, Codec.ReadImage,
                image => format == "png" ? Results.Bytes(image.ToPng(), "image/png") : Results.Bytes(image.Data, "application/octet-stream"));
        }

        public async Task<IResult> Stars(HttpContext context, string id)
        {
            var q = new Query(context);
            float scale = 1f;
            if (q.Text("unit") == "ly")
            {
                GalaxyInfo? info = await Info(context, id);
                if (info == null) return Problem(GalaxyError.UnknownGalaxy, $"There's no galaxy '{id}'.");
                scale = 1f / info.RadiusLightYears;
            }
            var query = StarQuery.Sphere(
                new Vector3(q.Float("x", 0f), q.Float("y", 0f), q.Float("z", 0f)) * scale, q.Float("radius", 0f) * scale,
                q.Filter(), q.Int("limit", 1000), q.Enum("order", StarOrder.Nearest));
            if (q.Error != null) return Problem(GalaxyError.BadRequest, q.Error);
            return await Call(context, GalaxyRequestKind.FindStars, w => { w.Str(id); Codec.Write(w, query); }, Codec.ReadQueryResult);
        }

        public async Task<IResult> Describe(HttpContext context, string id)
        {
            var q = new Query(context);
            float scale = 1f;
            if (q.Text("unit") == "ly")
            {
                GalaxyInfo? info = await Info(context, id);
                if (info == null) return Problem(GalaxyError.UnknownGalaxy, $"There's no galaxy '{id}'.");
                scale = 1f / info.RadiusLightYears;
            }
            var at = new Vector3(q.Float("x", 0f), q.Float("y", 0f), q.Float("z", 0f)) * scale;
            if (q.Error != null) return Problem(GalaxyError.BadRequest, q.Error);
            return await Call(context, GalaxyRequestKind.Describe, w => { w.Str(id); w.Vec3(at); }, Codec.ReadLocation);
        }

        public async Task<IResult> Pick(HttpContext context, string id)
        {
            var q = new Query(context);
            var pick = new StarPick
            {
                Seed = q.ULong("seed", 0), Filter = q.Filter(),
                MinGalacticRadius = q.Float("minRadius", 0f), MaxGalacticRadius = q.Float("maxRadius", 1f),
                MaxAttempts = q.Int("attempts", 64),
            };
            if (q.Error != null) return Problem(GalaxyError.BadRequest, q.Error);
            return await Call(context, GalaxyRequestKind.Pick, w => { w.Str(id); Codec.Write(w, pick); },
                r => r.Bool() ? Codec.ReadStarRef(r) : null);
        }
    }

    /// Query-string parsing that collects the first problem rather than throwing.
    private sealed class Query(HttpContext context)
    {
        public string? Error { get; private set; }

        public string? Text(string name) => context.Request.Query.TryGetValue(name, out var v) ? v.ToString() : null;

        private T Parse<T>(string name, T fallback, Func<string, (bool, T)> parse)
        {
            string? text = Text(name);
            if (string.IsNullOrEmpty(text)) return fallback;
            var (ok, value) = parse(text);
            if (!ok) Error ??= $"'{text}' isn't a valid {name}.";
            return ok ? value : fallback;
        }

        public float Float(string name, float fallback) =>
            Parse(name, fallback, t => (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v), v));
        public int Int(string name, int fallback) =>
            Parse(name, fallback, t => (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v), v));
        public ulong ULong(string name, ulong fallback) =>
            Parse(name, fallback, t => (ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out ulong v), v));
        public T Enum<T>(string name, T fallback) where T : struct, Enum =>
            Parse(name, fallback, t => (System.Enum.TryParse(t, ignoreCase: true, out T v) && System.Enum.IsDefined(v), v));

        private int Mask<T>(string name, int all) where T : struct, Enum
        {
            string? text = Text(name);
            if (string.IsNullOrEmpty(text) || text == "all") return all;
            int mask = 0;
            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (System.Enum.TryParse(part, ignoreCase: true, out T v) && System.Enum.IsDefined(v)) mask |= 1 << Convert.ToInt32(v);
                else Error ??= $"'{part}' isn't a {typeof(T).Name}.";
            }
            return mask;
        }

        public StarFilter Filter() => new()
        {
            Classes = (ushort)Mask<SpectralClass>("classes", StarFilter.AllClasses),
            Regions = (byte)Mask<RegionType>("regions", StarFilter.AllRegions),
            ExcludedFlags = (StarFlags)Mask<FlagBit>("exclude", 0),
            RequiredFlags = (StarFlags)Mask<FlagBit>("require", 0),
            MinLuminosity = (byte)Math.Clamp(Int("minLuminosity", 0), 0, 255),
            MinPlanets = Int("minPlanets", 0),
            HabitableOnly = Text("habitable") is "true" or "1",
        };

        // StarFlags by bit number, for parsing "binary,giant".
        private enum FlagBit { Binary = 0, Giant = 1, Supergiant = 2 }
    }

    /// Star ids in JSON as their "x:y:z:index" text.
    private sealed class StarIdConverter : JsonConverter<StarId>
    {
        public override StarId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            StarId.TryParse(reader.GetString(), out StarId id) ? id : throw new JsonException("Not a star id.");

        public override void Write(Utf8JsonWriter writer, StarId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private sealed class WithHeader(IResult inner, string name, string value) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers[name] = value;
            return inner.ExecuteAsync(httpContext);
        }
    }
}
