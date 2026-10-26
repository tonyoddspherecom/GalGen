using System.Collections.Concurrent;
using UniverseGen.Models;

namespace UniverseGen.Service;

/// <summary>
/// The galaxies a process generates, by id: a game server's one galaxy, a season server's current and
/// past ones, or a hosting service's many. Galaxies can be added and removed while it serves.
/// Hand it to a <see cref="GalaxyRequestHandler"/> to serve it over any transport.
/// </summary>
public sealed class GalaxyHost : IGalaxyCatalog
{
    private readonly ConcurrentDictionary<string, LocalGalaxy> _galaxies = new(StringComparer.Ordinal);

    /// What every galaxy added without its own options uses.
    public LocalGalaxyOptions DefaultOptions { get; }

    public GalaxyHost(LocalGalaxyOptions? defaultOptions = null) => DefaultOptions = defaultOptions ?? new LocalGalaxyOptions();

    public IReadOnlyCollection<LocalGalaxy> Galaxies => _galaxies.Values.OrderBy(g => g.Info.Id, StringComparer.Ordinal).ToList();

    /// Adds a galaxy. Throws if the id is taken or the params are invalid.
    public LocalGalaxy Add(string id, GalaxyParams parms, string name = "", string description = "", LocalGalaxyOptions? options = null) =>
        Add(new LocalGalaxy(id, parms, options ?? DefaultOptions, name, description));

    public LocalGalaxy Add(LocalGalaxy galaxy)
    {
        if (!_galaxies.TryAdd(galaxy.Info.Id, galaxy))
            throw new ArgumentException($"There's already a galaxy '{galaxy.Info.Id}'.");
        return galaxy;
    }

    public bool Remove(string id) => _galaxies.TryRemove(id, out _);

    public LocalGalaxy? Find(string id) => _galaxies.TryGetValue(id, out LocalGalaxy? galaxy) ? galaxy : null;

    public LocalGalaxy this[string id] =>
        Find(id) ?? throw new GalaxyServiceException(GalaxyError.UnknownGalaxy, $"There's no galaxy '{id}'.");

    public ValueTask<IReadOnlyList<GalaxyInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        new(Galaxies.Select(g => g.Info).ToList());

    public ValueTask<IGalaxy> OpenAsync(string id, CancellationToken cancellationToken = default) => new(this[id]);

    /// <summary>Galaxy ids: 1–64 letters, digits, '-', '_' or '.', so they're safe in URLs, file names and logs.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static void ValidateId(string id)
    {
        if (!IsValidId(id))
            throw new ArgumentException($"'{id}' isn't a galaxy id: 1–64 letters, digits, '-', '_' or '.'.", nameof(id));
    }
}
