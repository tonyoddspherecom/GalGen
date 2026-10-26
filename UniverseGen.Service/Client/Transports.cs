using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using UniverseGen.Service.Protocol;

namespace UniverseGen.Service;

/// <summary>
/// Carries GalGen frames between a <see cref="GalaxyClient"/> and a server. Implement it to put the
/// protocol on any channel; or derive from <see cref="MessageGalaxyTransport"/>, which does the matching
/// of answers to questions for any channel that sends and receives whole messages.
/// </summary>
public interface IGalaxyTransport : IAsyncDisposable
{
    /// Sends one request frame and returns its response frame. Must allow many requests at once.
    ValueTask<ReadOnlyMemory<byte>> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken);
}

/// <summary>
/// A transport for any channel that carries whole messages: a game's own UDP or ENet connection, a
/// WebSocket, a message queue. Give it a way to send; call <see cref="Receive"/> with each response that
/// arrives. It matches answers to questions by the request id in every frame, so any number can be in
/// flight and they can arrive in any order.
/// </summary>
public class MessageGalaxyTransport : IGalaxyTransport
{
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? _send;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<ReadOnlyMemory<byte>>> _pending = new();

    /// <param name="send">Sends a request frame on the channel. Must be safe to call from any thread.</param>
    public MessageGalaxyTransport(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send) => _send = send;

    /// For a subclass that overrides <see cref="SendFrameAsync"/>.
    protected MessageGalaxyTransport() { }

    /// Requests awaiting an answer.
    public int Pending => _pending.Count;

    protected virtual ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) =>
        _send!(frame, cancellationToken);

    public async ValueTask<ReadOnlyMemory<byte>> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        if (!GalaxyProtocol.TryReadRequestId(request.Span, out uint id))
            throw new ArgumentException("That isn't a request frame.", nameof(request));
        var answer = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, answer))
            throw new InvalidOperationException($"Request {id} is already in flight.");
        try
        {
            using (cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken)))
            {
                await SendFrameAsync(request, cancellationToken).ConfigureAwait(false);
                return await answer.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
            OnFinished(id);
        }
    }

    /// Called when a request has its answer, failed or was cancelled.
    protected virtual void OnFinished(uint id) { }

    /// Fails one request in flight.
    protected void Fail(uint id, Exception reason)
    {
        if (_pending.TryGetValue(id, out var answer)) answer.TrySetException(reason);
    }

    /// <summary>
    /// Hands over a response frame that arrived on the channel. Returns false if nothing was waiting for
    /// it (it came too late, or was never asked for), which is safe to ignore.
    /// </summary>
    public bool Receive(ReadOnlyMemory<byte> response)
    {
        if (!GalaxyProtocol.TryReadResponseId(response.Span, out uint id) || !_pending.TryGetValue(id, out var answer))
            return false;
        return answer.TrySetResult(response);
    }

    /// Fails every request in flight: the channel is gone.
    public void FailAll(Exception reason)
    {
        foreach (var answer in _pending.Values) answer.TrySetException(reason);
    }

    public virtual ValueTask DisposeAsync()
    {
        FailAll(new GalaxyServiceException(GalaxyError.Unavailable, "The connection was closed."));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The GalGen protocol over TCP, to a <see cref="TcpGalaxyServer"/>. One connection carries any number
/// of requests at once. If the connection drops, requests in flight fail with
/// <see cref="GalaxyError.Unavailable"/> and the next request reconnects.
/// </summary>
public sealed class TcpGalaxyTransport : MessageGalaxyTransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _connecting = new(1, 1);
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly int _maxResponseBytes;
    // Which connection each request in flight went out on, so a dropped connection fails only its own.
    private readonly ConcurrentDictionary<uint, NetworkStream> _sentOn = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _reading;
    private bool _disposed;

    public TcpGalaxyTransport(string host, int port = TcpGalaxyServer.DefaultPort, int maxResponseBytes = 256 << 20)
    {
        _host = host;
        _port = port;
        _maxResponseBytes = maxResponseBytes;
    }

    public bool Connected => _client?.Connected == true;

    /// Connects now, rather than on the first request.
    public async Task ConnectAsync(CancellationToken cancellationToken = default) => await Stream(cancellationToken).ConfigureAwait(false);

    private async ValueTask<NetworkStream> Stream(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream != null && _client?.Connected == true) return _stream;
        await _connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stream != null && _client?.Connected == true) return _stream;
            Drop();
            var client = new TcpClient { NoDelay = true };
            try { await client.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false); }
            catch (SocketException e)
            {
                client.Dispose();
                throw new GalaxyServiceException(GalaxyError.Unavailable, $"Couldn't reach the galaxy server at {_host}:{_port}: {e.Message}", inner: e);
            }
            _client = client;
            _stream = client.GetStream();
            _reading = new CancellationTokenSource();
            _ = ReadLoop(_stream, _reading.Token);
            return _stream;
        }
        finally { _connecting.Release(); }
    }

    protected override async ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        NetworkStream stream = await Stream(cancellationToken).ConfigureAwait(false);
        GalaxyProtocol.TryReadRequestId(frame.Span, out uint id);
        _sentOn[id] = stream;
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, frame.Length);
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            throw new GalaxyServiceException(GalaxyError.Unavailable, "The connection to the galaxy server was lost.", inner: e);
        }
        finally { _writing.Release(); }
    }

    private async Task ReadLoop(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        Exception reason = new GalaxyServiceException(GalaxyError.Unavailable, "The galaxy server closed the connection.");
        try
        {
            while (await TcpGalaxyServer.ReadExactly(stream, header, cancellationToken).ConfigureAwait(false))
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length < 0 || length > _maxResponseBytes)
                {
                    reason = new GalaxyServiceException(GalaxyError.LimitExceeded, $"The server sent a {length}-byte response; the most allowed is {_maxResponseBytes}.");
                    break;
                }
                var frame = new byte[length];
                if (!await TcpGalaxyServer.ReadExactly(stream, frame, cancellationToken).ConfigureAwait(false)) break;
                Receive(frame);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            reason = new GalaxyServiceException(GalaxyError.Unavailable, "The connection to the galaxy server was lost.", inner: e);
        }
        // This connection is finished: everything sent on it fails, and the next request reconnects.
        if (ReferenceEquals(stream, _stream)) Drop();
        foreach (var (id, on) in _sentOn)
            if (ReferenceEquals(on, stream)) Fail(id, reason);
    }

    protected override void OnFinished(uint id) => _sentOn.TryRemove(id, out _);

    private void Drop()
    {
        _reading?.Cancel();
        _client?.Dispose();
        _client = null;
        _stream = null;
    }

    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _connecting.WaitAsync().ConfigureAwait(false);
        try { Drop(); }
        finally { _connecting.Release(); }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// The protocol without a network: frames go straight to a <see cref="GalaxyRequestHandler"/> in this
/// process. For tests, and for a game that runs its own galaxy server in-process but wants its client
/// code to be exactly what it'll be against a remote one.
/// </summary>
public sealed class LoopbackGalaxyTransport : IGalaxyTransport
{
    private readonly GalaxyRequestHandler _handler;
    private readonly GalaxyConnection _connection;

    public LoopbackGalaxyTransport(GalaxyRequestHandler handler, string remote = "loopback")
    {
        _handler = handler;
        _connection = handler.Connect(remote);
    }

    public async ValueTask<ReadOnlyMemory<byte>> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken) =>
        await _handler.HandleAsync(_connection, request, cancellationToken).ConfigureAwait(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
