using System.Net;
using System.Net.Http.Headers;

namespace UniverseGen.Service;

/// <summary>
/// The GalGen protocol over HTTP: each request frame is POSTed to a server's rpc endpoint (see
/// UniverseGen.Service.Http) and the response frame comes back as the body. For clients that can't
/// open a TCP connection (a browser, behind a proxy) or servers that would rather sit behind a web
/// server, a CDN or a load balancer.
/// </summary>
public sealed class HttpGalaxyTransport : IGalaxyTransport
{
    public const string ContentType = "application/x-galgen";

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly bool _ownsClient;

    /// <param name="endpoint">The rpc endpoint, e.g. https://example.com/galaxy/rpc.</param>
    /// <param name="http">An HttpClient to use (with any auth headers it should send); null makes one.</param>
    public HttpGalaxyTransport(Uri endpoint, HttpClient? http = null)
    {
        _endpoint = endpoint;
        _http = http ?? new HttpClient();
        _ownsClient = http == null;
    }

    public async ValueTask<ReadOnlyMemory<byte>> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        using var content = new ReadOnlyMemoryContent(request);
        content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
        HttpResponseMessage response;
        try { response = await _http.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException e)
        {
            throw new GalaxyServiceException(GalaxyError.Unavailable, $"Couldn't reach the galaxy server at {_endpoint}: {e.Message}", inner: e);
        }
        using (response)
        {
            // Protocol errors come back as error frames with 200; anything else is the web in between.
            if (response.StatusCode != HttpStatusCode.OK)
                throw new GalaxyServiceException(
                    response.StatusCode is HttpStatusCode.TooManyRequests ? GalaxyError.RateLimited : GalaxyError.Unavailable,
                    $"The galaxy server answered {(int)response.StatusCode} {response.ReasonPhrase}.",
                    response.Headers.RetryAfter?.Delta ?? TimeSpan.Zero);
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
