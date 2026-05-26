namespace Jellyfin.Plugin.PunchPlay.Tests.Support;

internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public TestHttpClientFactory(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler, disposeHandler: false);
    }

    public HttpClient CreateClient(string name) => _client;
}

internal sealed class DelegateHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
    private int _requestCount;

    public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public int RequestCount => _requestCount;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        return await _handler(request, cancellationToken).ConfigureAwait(false);
    }
}
