namespace UniverseGen.Service;

/// <summary>
/// Somewhere a <see cref="GalaxyClient"/> keeps answers between runs. Every answer about a galaxy is
/// fixed by its fingerprint, so a stored answer never goes stale: a game can keep its home region's map
/// and stars on disk and open its map instantly, even offline. Keys are short hex strings.
/// Implementations must be safe to call from any thread; failures should be swallowed (it's a cache).
/// </summary>
public interface IResponseStore
{
    bool TryGet(string key, out byte[] value);
    void Put(string key, byte[] value);
}

/// <summary>A response store in memory, up to a size.</summary>
public sealed class MemoryResponseStore : IResponseStore
{
    private readonly LruCache<string, byte[]> _entries;

    public MemoryResponseStore(int maxEntries = 4096) => _entries = new LruCache<string, byte[]>(maxEntries);

    public bool TryGet(string key, out byte[] value) => _entries.TryGet(key, out value);
    public void Put(string key, byte[] value) => _entries.Set(key, value);
}

/// <summary>
/// A response store in a folder, one file per answer, trimmed oldest-first when it grows past
/// <c>maxBytes</c>. Several processes can share a folder: files are written whole, then renamed.
/// </summary>
public sealed class FileResponseStore : IResponseStore
{
    private readonly string _directory;
    private readonly long _maxBytes;
    private long _approxBytes = -1;
    private int _trimming;

    public FileResponseStore(string directory, long maxBytes = 512L << 20)
    {
        _directory = directory;
        _maxBytes = maxBytes;
        Directory.CreateDirectory(directory);
    }

    private string PathOf(string key)
    {
        if (key.Length == 0 || key.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Store keys are hex.", nameof(key));
        return Path.Combine(_directory, key + ".bin");
    }

    public bool TryGet(string key, out byte[] value)
    {
        try
        {
            string path = PathOf(key);
            value = File.ReadAllBytes(path);
            // Its age is how long since it was last used, for trimming.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            value = [];
            return false;
        }
    }

    public void Put(string key, byte[] value)
    {
        try
        {
            string path = PathOf(key), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, value);
            File.Move(temp, path, overwrite: true);
            if (Interlocked.Read(ref _approxBytes) < 0) Interlocked.Exchange(ref _approxBytes, Measure());
            if (Interlocked.Add(ref _approxBytes, value.Length) > _maxBytes) Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private long Measure() => new DirectoryInfo(_directory).EnumerateFiles("*.bin").Sum(f => f.Length);

    /// Deletes the least recently used answers until the folder is back to three-quarters of its size.
    private void Trim()
    {
        if (Interlocked.Exchange(ref _trimming, 1) == 1) return;
        try
        {
            var files = new DirectoryInfo(_directory).EnumerateFiles("*.bin").OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (FileInfo file in files)
            {
                if (total <= _maxBytes * 3 / 4) break;
                try { file.Delete(); total -= file.Length; }
                catch (IOException) { }
            }
            Interlocked.Exchange(ref _approxBytes, total);
        }
        finally { Interlocked.Exchange(ref _trimming, 0); }
    }
}
