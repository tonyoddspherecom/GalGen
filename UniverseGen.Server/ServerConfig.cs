using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniverseGen.Models;
using UniverseGen.Service;

namespace UniverseGen.Server;

/// <summary>The server's config file (galgen.json): see galgen.example.json for every setting, explained.</summary>
public sealed class ServerConfig
{
    public string Name { get; set; } = "GalGen";
    public TcpConfig Tcp { get; set; } = new();
    public HttpConfig Http { get; set; } = new();
    /// Limits and cache sizes for every galaxy (a galaxy can have its own).
    public LocalGalaxyOptions Limits { get; set; } = new();
    public RateLimitConfig RateLimit { get; set; } = new();
    public bool ExposeStats { get; set; } = true;
    /// Every request on the console, rather than a summary each minute.
    public bool LogRequests { get; set; }
    public List<GalaxyConfig> Galaxies { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A misspelt setting is an error, not quietly ignored.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public static ServerConfig Load(string path)
    {
        ServerConfig config;
        try { config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), Json) ?? new ServerConfig(); }
        catch (JsonException e) { throw new FormatException($"{path}: {(e.Path is { Length: > 1 } at ? $"at {at}: " : "")}{e.Message}", e); }
        config.Check();
        return config;
    }

    public void Check()
    {
        if (!Tcp.Enabled && !Http.Enabled) throw new FormatException("Neither tcp nor http is enabled: the server would serve nothing.");
        if (Galaxies.Count == 0) throw new FormatException("There are no galaxies to serve.");
        if (Tcp.Enabled && !IPAddress.TryParse(Tcp.Address, out _)) throw new FormatException($"tcp.address '{Tcp.Address}' isn't an IP address.");
        foreach (GalaxyConfig galaxy in Galaxies)
        {
            if (!GalaxyHost.IsValidId(galaxy.Id)) throw new FormatException($"'{galaxy.Id}' isn't a galaxy id: 1–64 letters, digits, '-', '_' or '.'.");
            if ((galaxy.Code == null) == (galaxy.Params == null)) throw new FormatException($"Galaxy '{galaxy.Id}' needs a code or params (one, not both).");
        }
        if (Galaxies.GroupBy(g => g.Id).FirstOrDefault(g => g.Count() > 1) is { } twice) throw new FormatException($"Galaxy '{twice.Key}' is listed twice.");
    }
}

public sealed class TcpConfig
{
    public bool Enabled { get; set; } = true;
    public string Address { get; set; } = "0.0.0.0";
    public int Port { get; set; } = TcpGalaxyServer.DefaultPort;
    public int MaxConnections { get; set; } = 1000;
}

public sealed class HttpConfig
{
    public bool Enabled { get; set; }
    public List<string> Urls { get; set; } = ["http://0.0.0.0:24701"];
    public string Prefix { get; set; } = "/galaxy";
    /// Origins allowed to call it from a browser ("*" for any). Empty allows none.
    public List<string> CorsOrigins { get; set; } = [];
    /// The readable JSON and PNG endpoints, beside the binary one.
    public bool ReadableEndpoints { get; set; } = true;
}

public sealed class RateLimitConfig
{
    /// Cost units each client may spend a second, and save up. 0 means no limit.
    public double CostPerSecond { get; set; }
    public double CostBurst { get; set; }
}

public sealed class GalaxyConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// A galaxy code (from the GalGen demo, or `galgen-server inspect`)...
    public string? Code { get; set; }
    /// ...or its params in full.
    public GalaxyParams? Params { get; set; }
    /// Its own limits and chunking, instead of the server's.
    public LocalGalaxyOptions? Options { get; set; }

    public GalaxyParams Resolve() => Code != null ? GalaxyCode.Decode(Code) : Params!;
}
