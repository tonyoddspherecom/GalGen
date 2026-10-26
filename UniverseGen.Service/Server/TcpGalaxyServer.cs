using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace UniverseGen.Service;

/// <summary>Where and how a <see cref="TcpGalaxyServer"/> listens.</summary>
public sealed record TcpGalaxyServerOptions
{
    /// Port 0 picks a free one (see <see cref="TcpGalaxyServer.LocalEndPoint"/>).
    public IPEndPoint EndPoint { get; init; } = new(IPAddress.Any, TcpGalaxyServer.DefaultPort);
    public int MaxConnections { get; init; } = 1_000;
    /// Requests one connection may have in flight at once; more wait their turn.
    public int MaxInFlightPerConnection { get; init; } = 8;
    /// A connection that sends nothing for this long is closed.
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Serves the GalGen protocol over TCP with no other dependency: each frame is a little-endian u32
/// length and then the frame. Requests on one connection are answered concurrently (up to
/// <see cref="TcpGalaxyServerOptions.MaxInFlightPerConnection"/>), so a slow map doesn't hold up a quick
/// star. Pair it with <see cref="TcpGalaxyTransport"/> on the client.
/// </summary>
public sealed class TcpGalaxyServer : IAsyncDisposable
{
    public const int DefaultPort = 24700;

    private readonly GalaxyRequestHandler _handler;
    private readonly ConcurrentDictionary<GalaxyConnection, TcpClient> _clients = new();
    private readonly CancellationTokenSource _stopping = new();
    private TcpListener? _listener;
    private Task? _accepting;

    public TcpGalaxyServerOptions Options { get; }
    public IPEndPoint? LocalEndPoint => _listener?.LocalEndpoint as IPEndPoint;
    public int ConnectionCount => _clients.Count;

    public event Action<GalaxyConnection>? Connected;
    public event Action<GalaxyConnection>? Disconnected;

    public TcpGalaxyServer(GalaxyRequestHandler handler, TcpGalaxyServerOptions? options = null)
    {
        _handler = handler;
        Options = options ?? new TcpGalaxyServerOptions();
    }

    /// Starts listening. Connections are accepted and served in the background until <see cref="StopAsync"/>.
    public void Start()
    {
        if (_listener != null) throw new InvalidOperationException("The server has already started.");
        _listener = new TcpListener(Options.EndPoint);
        _listener.Start();
        _accepting = AcceptLoop(_stopping.Token);
    }

    private async Task AcceptLoop(CancellationToken stopping)
    {
        var serving = new List<Task>();
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client = await _listener!.AcceptTcpClientAsync(stopping).ConfigureAwait(false);
                if (_clients.Count >= Options.MaxConnections)
                {
                    client.Dispose();
                    continue;
                }
                serving.RemoveAll(t => t.IsCompleted);
                serving.Add(Serve(client, stopping));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        await Task.WhenAll(serving).ConfigureAwait(false);
    }

    private async Task Serve(TcpClient client, CancellationToken stopping)
    {
        client.NoDelay = true;
        GalaxyConnection connection = _handler.Connect(client.Client.RemoteEndPoint?.ToString() ?? "");
        _clients[connection] = client;
        Connected?.Invoke(connection);
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var writing = new SemaphoreSlim(1, 1);
        var inFlight = new SemaphoreSlim(Options.MaxInFlightPerConnection, Options.MaxInFlightPerConnection);
        var answering = new List<Task>();
        try
        {
            NetworkStream stream = client.GetStream();
            var header = new byte[4];
            while (!closing.IsCancellationRequested)
            {
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(closing.Token))
                {
                    idle.CancelAfter(Options.IdleTimeout);
                    if (!await ReadExactly(stream, header, idle.Token).ConfigureAwait(false)) break;
                }
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                // Past the handler's own limit there's no point reading it; and the stream can't be trusted after.
                if (length < 0 || length > _handler.Options.MaxRequestBytes) break;
                var frame = new byte[length];
                if (!await ReadExactly(stream, frame, closing.Token).ConfigureAwait(false)) break;

                await inFlight.WaitAsync(closing.Token).ConfigureAwait(false);
                answering.RemoveAll(t => t.IsCompleted);
                answering.Add(Answer(connection, stream, frame, writing, inFlight, closing));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally
        {
            closing.Cancel();
            try { await Task.WhenAll(answering).ConfigureAwait(false); } catch { }
            _clients.TryRemove(connection, out _);
            client.Dispose();
            Disconnected?.Invoke(connection);
        }
    }

    private async Task Answer(GalaxyConnection connection, NetworkStream stream, byte[] frame,
                              SemaphoreSlim writing, SemaphoreSlim inFlight, CancellationTokenSource closing)
    {
        try
        {
            byte[] response = await _handler.HandleAsync(connection, frame, closing.Token).ConfigureAwait(false);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, response.Length);
            await writing.WaitAsync(closing.Token).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(header, closing.Token).ConfigureAwait(false);
                await stream.WriteAsync(response, closing.Token).ConfigureAwait(false);
            }
            finally { writing.Release(); }
        }
        catch (Exception) when (closing.IsCancellationRequested) { }
        catch (IOException) { closing.Cancel(); }
        catch (SocketException) { closing.Cancel(); }
        finally { inFlight.Release(); }
    }

    /// Fills the buffer, or returns false if the other end closed first.
    internal static async Task<bool> ReadExactly(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    /// Stops listening, closes every connection, and waits for requests in progress to finish.
    public async Task StopAsync()
    {
        if (_stopping.IsCancellationRequested) return;
        _stopping.Cancel();
        _listener?.Stop();
        foreach (TcpClient client in _clients.Values) client.Dispose();
        if (_accepting != null) await _accepting.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
