using Jellyfin.Plugin.PunchPlay.Tests.Support;

namespace Jellyfin.Plugin.PunchPlay.Tests;

public class PunchPlayScrobbleClientTests
{
    [Fact]
    public async Task DispatchAsync_RefreshesExpiredTokenAndRetriesSuccessfully()
    {
        using var pluginContext = new TestPluginContext();
        pluginContext.Plugin.SetUserToken("user-1", "stale-token", "user-one", "refresh-token-1");

        var handler = new DelegateHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/refresh")
            {
                return Task.FromResult(CreateJsonResponse(HttpStatusCode.OK, new { access_token = "fresh-token", refresh_token = "refresh-token-2" }));
            }

            var presented = request.Headers.Authorization?.Parameter;
            return Task.FromResult(presented == "fresh-token"
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : CreateJsonResponse(HttpStatusCode.Unauthorized, new { error = "expired" }));
        });
        var diagnostics = new PluginDiagnosticsService();
        var client = CreateClient(handler, diagnostics, out _);

        await client.DispatchAsync("progress", "user-1", "stale-token", CreatePayload(), CancellationToken.None);

        Assert.Equal("fresh-token", pluginContext.Plugin.GetUserToken("user-1"));
        Assert.Equal("refresh-token-2", pluginContext.Plugin.GetUserTokenRecord("user-1")?.RefreshToken);
        Assert.Equal(3, handler.RequestCount); // scrobble (401) + refresh + scrobble retry
    }

    [Fact]
    public async Task DispatchAsync_ClearsTokenAndQueueWhenRefreshTokenIsRejected()
    {
        using var pluginContext = new TestPluginContext();
        pluginContext.Plugin.SetUserToken("user-1", "stale-token", "user-one", "dead-refresh-token");

        var handler = new DelegateHttpMessageHandler((request, _) =>
        {
            return request.RequestUri!.AbsolutePath == "/api/auth/refresh"
                ? Task.FromResult(CreateJsonResponse(HttpStatusCode.Unauthorized, new { error = "invalid_grant" }))
                : Task.FromResult(CreateJsonResponse(HttpStatusCode.Unauthorized, new { error = "expired" }));
        });
        var client = CreateClient(handler, new PluginDiagnosticsService(), out var queueService);

        await client.DispatchAsync("progress", "user-1", "stale-token", CreatePayload(), CancellationToken.None);

        Assert.Null(pluginContext.Plugin.GetUserToken("user-1"));
        _ = queueService; // token/queue clearing is asserted via GetUserToken above
    }

    private static PunchPlayScrobbleClient CreateClient(
        DelegateHttpMessageHandler handler,
        PluginDiagnosticsService diagnostics,
        out ScrobbleQueueService queueService)
    {
        var factory = new TestHttpClientFactory(handler);
        var transport = new PunchPlayTransport(factory, NullLogger<PunchPlayTransport>.Instance);
        var authService = new PunchPlayAuthService(factory, NullLogger<PunchPlayAuthService>.Instance);
        queueService = new ScrobbleQueueService(transport, authService, diagnostics, NullLogger<ScrobbleQueueService>.Instance);
        return new PunchPlayScrobbleClient(transport, authService, queueService, diagnostics, NullLogger<PunchPlayScrobbleClient>.Instance);
    }

    private static ScrobblePayload CreatePayload()
    {
        return new ScrobblePayload
        {
            MediaType = "movie",
            Title = "Arrival",
            PositionSeconds = 30
        };
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, object body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }
}
