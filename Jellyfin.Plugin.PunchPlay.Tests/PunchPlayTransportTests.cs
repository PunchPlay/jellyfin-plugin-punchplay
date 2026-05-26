using Jellyfin.Plugin.PunchPlay.Tests.Support;

namespace Jellyfin.Plugin.PunchPlay.Tests;

public class PunchPlayTransportTests
{
    [Fact]
    public async Task SendAsync_TreatsAll2xxResponsesAsSuccess()
    {
        using var pluginContext = new TestPluginContext();
        var handler = new DelegateHttpMessageHandler((request, _) =>
        {
            Assert.Equal("https://punchplay.example/api/scrobble/start", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        });
        var transport = CreateTransport(handler);

        var result = await transport.SendAsync("start", "token", CreatePayload(), CancellationToken.None);

        Assert.Equal(PunchPlayTransportOutcome.Success, result.Outcome);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SendAsync_AllowsResumeAction()
    {
        using var pluginContext = new TestPluginContext();
        var handler = new DelegateHttpMessageHandler((request, _) =>
        {
            Assert.Equal("https://punchplay.example/api/scrobble/resume", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var transport = CreateTransport(handler);

        var result = await transport.SendAsync("resume", "token", CreatePayload(), CancellationToken.None);

        Assert.Equal(PunchPlayTransportOutcome.Success, result.Outcome);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SendAsync_ReturnsUnauthorizedFor401()
    {
        using var pluginContext = new TestPluginContext();
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(CreateJsonResponse(HttpStatusCode.Unauthorized, new { error = "revoked" })));
        var transport = CreateTransport(handler);

        var result = await transport.SendAsync("pause", "token", CreatePayload(), CancellationToken.None);

        Assert.Equal(PunchPlayTransportOutcome.Unauthorized, result.Outcome);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task SendAsync_ReturnsRetryableFailureForRetryableStatuses(int statusCode)
    {
        using var pluginContext = new TestPluginContext();
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(CreateJsonResponse((HttpStatusCode)statusCode, new { error = "retry later" })));
        var transport = CreateTransport(handler);

        var result = await transport.SendAsync("progress", "token", CreatePayload(), CancellationToken.None);

        Assert.Equal(PunchPlayTransportOutcome.RetryableFailure, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_ReturnsPermanentFailureFor400()
    {
        using var pluginContext = new TestPluginContext();
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(CreateJsonResponse(HttpStatusCode.BadRequest, new { error = "bad payload" })));
        var transport = CreateTransport(handler);

        var result = await transport.SendAsync("stop", "token", CreatePayload(), CancellationToken.None);

        Assert.Equal(PunchPlayTransportOutcome.PermanentFailure, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_RejectsInvalidActionBeforeMakingHttpRequest()
    {
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        var transport = CreateTransport(handler);

        var result = await transport.SendAsync("rewind", "token", CreatePayload(), CancellationToken.None);

        Assert.Equal(PunchPlayTransportOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(0, handler.RequestCount);
    }

    private static PunchPlayTransport CreateTransport(DelegateHttpMessageHandler handler)
    {
        return new PunchPlayTransport(new TestHttpClientFactory(handler), NullLogger<PunchPlayTransport>.Instance);
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
