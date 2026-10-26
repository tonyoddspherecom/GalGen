using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using UniverseGen.Models;
using UniverseGen.Server;
using UniverseGen.Service;
using UniverseGen.Service.Http;
using UniverseGen.Service.Protocol;

// galgen-server: serves GalGen galaxies over TCP and/or HTTP, from a config file.
//   galgen-server [serve] [--config galgen.json]
//   galgen-server census <code> [--chunk-ly 10] [--max-stars 200] [--exact]
//   galgen-server inspect <code>
const string Usage = """
    galgen-server: serves GalGen galaxies over TCP and HTTP.

      galgen-server [serve] [--config <file>]   Serve the galaxies in a config file (galgen.json by default;
                                                 copy galgen.example.json to start one).
      galgen-server census <code> [--chunk-ly <n>] [--max-stars <n>] [--exact]
                                                 How many stars a galaxy has: an estimate, or an exact count.
      galgen-server inspect <code>               A galaxy code's settings, as config JSON.
    """;

try
{
    string command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "serve";
    string[] rest = command == "serve" && (args.Length == 0 || args[0].StartsWith('-')) ? args : args.Skip(1).ToArray();
    return command switch
    {
        "serve" => await Serve(Option(rest, "--config") ?? "galgen.json"),
        "census" => Census(rest),
        "inspect" => Inspect(rest),
        "help" or "--help" or "-h" => Help(),
        _ => Help(1),
    };
}
catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException or FileNotFoundException)
{
    Console.Error.WriteLine($"galgen-server: {e.Message}");
    return 1;
}

static int Help(int code = 0)
{
    Console.WriteLine(Usage);
    return code;
}

static string? Option(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string Code(string[] args) =>
    args.FirstOrDefault(a => !a.StartsWith('-')) ?? throw new ArgumentException("Give a galaxy code.");

static int Census(string[] args)
{
    GalaxyParams p = GalaxyCode.Decode(Code(args));
    float chunkLy = float.Parse(Option(args, "--chunk-ly") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
    int maxStars = int.Parse(Option(args, "--max-stars") ?? "200", System.Globalization.CultureInfo.InvariantCulture);
    float chunk = p.ToNormalised(chunkLy);
    var clock = Stopwatch.StartNew();
    Console.WriteLine($"Estimate: {GalaxyCensus.Estimate(p, chunk, maxStars):N0} stars ({clock.ElapsedMilliseconds} ms)");
    if (args.Contains("--exact"))
    {
        clock.Restart();
        var progress = new Progress<double>(f => Console.Write($"\rCounting… {f:P0}   "));
        long exact = GalaxyCensus.Count(p, chunk, maxStars, progress: progress);
        Console.WriteLine($"\rExact:    {exact:N0} stars ({clock.Elapsed.TotalSeconds:0.0} s)      ");
    }
    return 0;
}

static int Inspect(string[] args)
{
    GalaxyParams p = GalaxyCode.Decode(Code(args));
    Console.WriteLine(JsonSerializer.Serialize(p, ServerConfig.Json));
    Console.WriteLine($"Fingerprint {p.Fingerprint()}, radius {p.RadiusLightYears:N0} ly");
    return 0;
}

static async Task<int> Serve(string configPath)
{
    if (!File.Exists(configPath))
        throw new FileNotFoundException($"There's no config at {Path.GetFullPath(configPath)}. Copy galgen.example.json there and edit it.");
    ServerConfig config = ServerConfig.Load(configPath);
    Console.WriteLine($"{config.Name}: GalGen protocol {GalaxyProtocol.Version}");

    var host = new GalaxyHost(config.Limits);
    foreach (GalaxyConfig g in config.Galaxies)
    {
        LocalGalaxy galaxy = host.Add(g.Id, g.Resolve(), g.Name, g.Description, g.Options);
        GalaxyInfo info = galaxy.Info;
        Console.WriteLine($"  {info.Id,-16} {info.Name,-24} {info.Params.Type} {info.RadiusLightYears:N0} ly, ~{info.EstimatedStars:N0} stars, code {info.Code}, fingerprint {info.Fingerprint}");
    }

    var handler = new GalaxyRequestHandler(host, new GalaxyServerOptions
    {
        Name = config.Name,
        Limits = config.Limits,
        CostPerSecond = config.RateLimit.CostPerSecond,
        CostBurst = config.RateLimit.CostBurst,
        ExposeStats = config.ExposeStats,
    });
    var summary = new RequestSummary();
    handler.RequestHandled += log =>
    {
        summary.Add(log);
        if (config.LogRequests || log.Error == GalaxyError.Internal)
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {log.Connection.Remote} {log.Kind} {log.Galaxy} {log.Duration.TotalMilliseconds:0.0} ms {log.ResponseBytes} B{(log.Error != null ? $" {log.Error}: {log.Message}" : "")}");
    };

    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Cancel();

    TcpGalaxyServer? tcp = null;
    if (config.Tcp.Enabled)
    {
        tcp = new TcpGalaxyServer(handler, new TcpGalaxyServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Parse(config.Tcp.Address), config.Tcp.Port),
            MaxConnections = config.Tcp.MaxConnections,
        });
        tcp.Start();
        Console.WriteLine($"TCP on {tcp.LocalEndPoint}");
    }

    WebApplication? web = null;
    if (config.Http.Enabled)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls([.. config.Http.Urls]);
        if (config.Http.CorsOrigins.Count > 0)
            builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
                (config.Http.CorsOrigins.Contains("*") ? p.AllowAnyOrigin() : p.WithOrigins([.. config.Http.CorsOrigins])).AllowAnyHeader().AllowAnyMethod()));
        web = builder.Build();
        if (config.Http.CorsOrigins.Count > 0) web.UseCors();
        web.MapGet("/health", () => Results.Json(new { status = "ok", galaxies = host.Galaxies.Count, tcpConnections = tcp?.ConnectionCount ?? 0 }));
        web.MapGalaxyService(handler, config.Http.Prefix, new HttpGalaxyOptions { ReadableEndpoints = config.Http.ReadableEndpoints });
        await web.StartAsync(stopping.Token);
        Console.WriteLine($"HTTP on {string.Join(", ", web.Urls)}{config.Http.Prefix}");
    }

    Console.WriteLine("Serving. Ctrl+C stops.");
    try
    {
        while (!stopping.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stopping.Token);
            if (!config.LogRequests && summary.Take() is { } line) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
        }
    }
    catch (OperationCanceledException) { }

    Console.WriteLine("Stopping…");
    if (tcp != null) await tcp.DisposeAsync();
    if (web != null) await web.StopAsync();
    return 0;
}

/// <summary>Requests counted between console summaries.</summary>
sealed class RequestSummary
{
    private readonly ConcurrentDictionary<GalaxyRequestKind, int> _byKind = new();
    private int _errors;
    private long _bytes;

    public void Add(GalaxyRequestLog log)
    {
        _byKind.AddOrUpdate(log.Kind, 1, (_, n) => n + 1);
        if (log.Error != null) Interlocked.Increment(ref _errors);
        Interlocked.Add(ref _bytes, log.ResponseBytes);
    }

    /// The last minute's requests in a line, or null if there were none.
    public string? Take()
    {
        var counts = _byKind.ToArray();
        _byKind.Clear();
        int errors = Interlocked.Exchange(ref _errors, 0);
        long bytes = Interlocked.Exchange(ref _bytes, 0);
        if (counts.Length == 0) return null;
        return $"{counts.Sum(c => c.Value)} requests ({string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value}"))}), {errors} errors, {bytes / 1024.0:N0} KB sent";
    }
}
