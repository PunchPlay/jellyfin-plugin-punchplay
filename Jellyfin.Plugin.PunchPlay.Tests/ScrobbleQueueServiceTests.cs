using Jellyfin.Plugin.PunchPlay.Tests.Support;

namespace Jellyfin.Plugin.PunchPlay.Tests;

public class ScrobbleQueueServiceTests
{
    [Fact]
    public async Task EnqueueAsync_PersistsEntry()
    {
        using var pluginContext = new TestPluginContext();
        var diagnostics = new PluginDiagnosticsService();
        var queueService = CreateQueueService(
            new DelegateHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))),
            diagnostics);

        await queueService.EnqueueAsync("progress", "user-1", CreatePayload(), CancellationToken.None);

        var entries = await ReadQueueEntriesAsync(pluginContext);
        Assert.Single(entries);
        Assert.Equal("progress", entries[0].Action);
        Assert.Equal("user-1", entries[0].JellyfinUserId);
        Assert.Equal(1, diagnostics.QueuedScrobbleCount);
        Assert.Equal(0, diagnostics.HighestQueuedRetryCount);
        Assert.NotNull(diagnostics.OldestQueuedScrobbleAtUtc);
        Assert.NotNull(diagnostics.NextQueuedRetryAtUtc);
    }

    [Fact]
    public async Task StartAsync_RetriesDueEntriesAutomaticallyAndRemovesOnSuccess()
    {
        using var pluginContext = new TestPluginContext();
        pluginContext.Plugin.SetUserToken("user-1", "token-1", "user-one");
        await WriteQueueEntriesAsync(pluginContext, CreateQueueEntry("entry-1", "start", "user-1", retryCount: 0, nextAttemptAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1)));

        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        var diagnostics = new PluginDiagnosticsService();
        var queueService = CreateQueueService(handler, diagnostics);

        await queueService.StartAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await ReadQueueEntriesAsync(pluginContext)).Count == 0);
        await queueService.StopAsync(CancellationToken.None);

        Assert.Empty(await ReadQueueEntriesAsync(pluginContext));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, diagnostics.QueuedScrobbleCount);
    }

    [Fact]
    public async Task StartAsync_DoesNotRetryFutureEntriesWhenOnlyDueEntriesAreAllowed()
    {
        using var pluginContext = new TestPluginContext();
        pluginContext.Plugin.SetUserToken("user-1", "token-1", "user-one");
        await WriteQueueEntriesAsync(pluginContext, CreateQueueEntry("entry-1", "progress", "user-1", retryCount: 0, nextAttemptAtUtc: DateTimeOffset.UtcNow.AddMinutes(5)));

        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        var diagnostics = new PluginDiagnosticsService();
        var queueService = CreateQueueService(handler, diagnostics);

        await queueService.StartAsync(CancellationToken.None);
        queueService.RequestFlush();
        await Task.Delay(250);
        await queueService.StopAsync(CancellationToken.None);

        var entries = await ReadQueueEntriesAsync(pluginContext);
        Assert.Single(entries);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(1, diagnostics.QueuedScrobbleCount);
    }

    [Fact]
    public async Task RetryNowAsync_UnauthorizedClearsTokenAndQueuedEntriesForUser()
    {
        using var pluginContext = new TestPluginContext();
        pluginContext.Plugin.SetUserToken("user-1", "token-1", "user-one");
        await WriteQueueEntriesAsync(pluginContext, CreateQueueEntry("entry-1", "pause", "user-1", retryCount: 0, nextAttemptAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1)));

        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(CreateJsonResponse(HttpStatusCode.Unauthorized, new { error = "revoked" })));
        var queueService = CreateQueueService(handler, new PluginDiagnosticsService());

        await queueService.RetryNowAsync(CancellationToken.None);

        Assert.Null(pluginContext.Plugin.GetUserToken("user-1"));
        Assert.Empty(await ReadQueueEntriesAsync(pluginContext));
    }

    [Fact]
    public async Task RetryNowAsync_RefreshesExpiredTokenAndRetriesSuccessfully()
    {
        using var pluginContext = new TestPluginContext();
        pluginContext.Plugin.SetUserToken("user-1", "stale-token", "user-one", "refresh-token-1");
        await WriteQueueEntriesAsync(pluginContext, CreateQueueEntry("entry-1", "pause", "user-1", retryCount: 0, nextAttemptAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1)));

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
        var queueService = CreateQueueService(handler, diagnostics);

        await queueService.RetryNowAsync(CancellationToken.None);

        Assert.Empty(await ReadQueueEntriesAsync(pluginContext));
        Assert.Equal("fresh-token", pluginContext.Plugin.GetUserToken("user-1"));
        Assert.Equal("refresh-token-2", pluginContext.Plugin.GetUserTokenRecord("user-1")?.RefreshToken);
        Assert.Equal(0, diagnostics.QueuedScrobbleCount);
    }

    [Fact]
    public async Task RetryNowAsync_ClearsTokenWhenRefreshTokenIsRejected()
    {
        using var pluginContext = new TestPluginContext();
        pluginContext.Plugin.SetUserToken("user-1", "stale-token", "user-one", "dead-refresh-token");
        await WriteQueueEntriesAsync(pluginContext, CreateQueueEntry("entry-1", "pause", "user-1", retryCount: 0, nextAttemptAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1)));

        var handler = new DelegateHttpMessageHandler((request, _) =>
        {
            return request.RequestUri!.AbsolutePath == "/api/auth/refresh"
                ? Task.FromResult(CreateJsonResponse(HttpStatusCode.Unauthorized, new { error = "invalid_grant" }))
                : Task.FromResult(CreateJsonResponse(HttpStatusCode.Unauthorized, new { error = "expired" }));
        });
        var queueService = CreateQueueService(handler, new PluginDiagnosticsService());

        await queueService.RetryNowAsync(CancellationToken.None);

        Assert.Null(pluginContext.Plugin.GetUserToken("user-1"));
        Assert.Empty(await ReadQueueEntriesAsync(pluginContext));
    }

    [Fact]
    public async Task RetryNowAsync_RetriesFutureEntriesWhenForced()
    {
        using var pluginContext = new TestPluginContext();
        pluginContext.Plugin.SetUserToken("user-1", "token-1", "user-one");
        await WriteQueueEntriesAsync(pluginContext, CreateQueueEntry("entry-1", "pause", "user-1", retryCount: 0, nextAttemptAtUtc: DateTimeOffset.UtcNow.AddMinutes(10)));

        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        var diagnostics = new PluginDiagnosticsService();
        var queueService = CreateQueueService(handler, diagnostics);

        await queueService.RetryNowAsync(CancellationToken.None);

        Assert.Empty(await ReadQueueEntriesAsync(pluginContext));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, diagnostics.QueuedScrobbleCount);
        Assert.Null(diagnostics.OldestQueuedScrobbleAtUtc);
        Assert.Null(diagnostics.NextQueuedRetryAtUtc);
    }

    [Fact]
    public async Task RetryNowAsync_RetryableFailureIncrementsRetryCountAndNextRetryTime()
    {
        using var pluginContext = new TestPluginContext();
        pluginContext.Plugin.SetUserToken("user-1", "token-1", "user-one");
        var before = DateTimeOffset.UtcNow;
        await WriteQueueEntriesAsync(pluginContext, CreateQueueEntry("entry-1", "progress", "user-1", retryCount: 0, nextAttemptAtUtc: before.AddMinutes(-1)));

        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(CreateJsonResponse(HttpStatusCode.ServiceUnavailable, new { error = "offline" })));
        var diagnostics = new PluginDiagnosticsService();
        var queueService = CreateQueueService(handler, diagnostics);

        await queueService.RetryNowAsync(CancellationToken.None);

        var entries = await ReadQueueEntriesAsync(pluginContext);
        var entry = Assert.Single(entries);
        Assert.Equal(1, entry.RetryCount);
        Assert.True(entry.NextAttemptAtUtc > before);
        Assert.Equal(1, diagnostics.QueuedScrobbleCount);
        Assert.Equal(1, diagnostics.HighestQueuedRetryCount);
        Assert.NotNull(diagnostics.NextQueuedRetryAtUtc);
    }

    [Fact]
    public async Task RetryNowAsync_DropsEntryAfterMaxRetryAttempts()
    {
        using var pluginContext = new TestPluginContext();
        pluginContext.Plugin.SetUserToken("user-1", "token-1", "user-one");
        await WriteQueueEntriesAsync(pluginContext, CreateQueueEntry("entry-1", "progress", "user-1", retryCount: 9, nextAttemptAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1)));

        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(CreateJsonResponse(HttpStatusCode.ServiceUnavailable, new { error = "offline" })));
        var diagnostics = new PluginDiagnosticsService();
        var queueService = CreateQueueService(handler, diagnostics);

        await queueService.RetryNowAsync(CancellationToken.None);

        Assert.Empty(await ReadQueueEntriesAsync(pluginContext));
        Assert.Equal(0, diagnostics.QueuedScrobbleCount);
        Assert.Null(diagnostics.OldestQueuedScrobbleAtUtc);
        Assert.Null(diagnostics.NextQueuedRetryAtUtc);
        Assert.Equal(0, diagnostics.HighestQueuedRetryCount);
    }

    [Fact]
    public async Task ClearUserAsync_RemovesOnlyMatchingUserEntries()
    {
        using var pluginContext = new TestPluginContext();
        await WriteQueueEntriesAsync(
            pluginContext,
            CreateQueueEntry("entry-1", "start", "user-1", retryCount: 0, nextAttemptAtUtc: DateTimeOffset.UtcNow),
            CreateQueueEntry("entry-2", "start", "user-2", retryCount: 2, nextAttemptAtUtc: DateTimeOffset.UtcNow));

        var diagnostics = new PluginDiagnosticsService();
        var queueService = CreateQueueService(
            new DelegateHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))),
            diagnostics);

        await queueService.ClearUserAsync("user-1", CancellationToken.None);

        var entries = await ReadQueueEntriesAsync(pluginContext);
        var remaining = Assert.Single(entries);
        Assert.Equal("user-2", remaining.JellyfinUserId);
        Assert.Equal(1, diagnostics.QueuedScrobbleCount);
    }

    [Fact]
    public async Task RetryNowAsync_DropsEntriesWithoutCurrentUserTokenWithoutSending()
    {
        using var pluginContext = new TestPluginContext();
        await WriteQueueEntriesAsync(pluginContext, CreateQueueEntry("entry-1", "start", "missing-user", retryCount: 0, nextAttemptAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1)));

        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        var diagnostics = new PluginDiagnosticsService();
        var queueService = CreateQueueService(handler, diagnostics);

        await queueService.RetryNowAsync(CancellationToken.None);

        Assert.Empty(await ReadQueueEntriesAsync(pluginContext));
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, diagnostics.QueuedScrobbleCount);
    }

    [Fact]
    public async Task ClearAsync_ResetsQueueSnapshotDiagnostics()
    {
        using var pluginContext = new TestPluginContext();
        await WriteQueueEntriesAsync(
            pluginContext,
            CreateQueueEntry("entry-1", "start", "user-1", retryCount: 1, nextAttemptAtUtc: DateTimeOffset.UtcNow.AddMinutes(2)),
            CreateQueueEntry("entry-2", "progress", "user-2", retryCount: 3, nextAttemptAtUtc: DateTimeOffset.UtcNow.AddMinutes(4)));

        var diagnostics = new PluginDiagnosticsService();
        var queueService = CreateQueueService(
            new DelegateHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))),
            diagnostics);

        await queueService.ClearAsync(CancellationToken.None);

        Assert.Empty(await ReadQueueEntriesAsync(pluginContext));
        Assert.Equal(0, diagnostics.QueuedScrobbleCount);
        Assert.Null(diagnostics.OldestQueuedScrobbleAtUtc);
        Assert.Null(diagnostics.NextQueuedRetryAtUtc);
        Assert.Equal(0, diagnostics.HighestQueuedRetryCount);
    }

    private static ScrobbleQueueService CreateQueueService(DelegateHttpMessageHandler handler, PluginDiagnosticsService diagnostics)
    {
        var factory = new TestHttpClientFactory(handler);
        var transport = new PunchPlayTransport(factory, NullLogger<PunchPlayTransport>.Instance);
        var authService = new PunchPlayAuthService(factory, NullLogger<PunchPlayAuthService>.Instance);
        return new ScrobbleQueueService(transport, authService, diagnostics, NullLogger<ScrobbleQueueService>.Instance);
    }

    private static ScrobblePayload CreatePayload()
    {
        return new ScrobblePayload
        {
            MediaType = "movie",
            Title = "Queued Movie",
            PositionSeconds = 120
        };
    }

    private static QueueEntryDocument CreateQueueEntry(string id, string action, string jellyfinUserId, int retryCount, DateTimeOffset nextAttemptAtUtc)
    {
        return new QueueEntryDocument
        {
            Id = id,
            Action = action,
            JellyfinUserId = jellyfinUserId,
            Payload = CreatePayload(),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            NextAttemptAtUtc = nextAttemptAtUtc,
            RetryCount = retryCount
        };
    }

    private static async Task WriteQueueEntriesAsync(TestPluginContext pluginContext, params QueueEntryDocument[] entries)
    {
        Directory.CreateDirectory(pluginContext.Plugin.StateDirectoryPath);
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(pluginContext.Plugin.QueueFilePath, json);
    }

    private static async Task<List<QueueEntryDocument>> ReadQueueEntriesAsync(TestPluginContext pluginContext)
    {
        if (!File.Exists(pluginContext.Plugin.QueueFilePath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(pluginContext.Plugin.QueueFilePath);
        return JsonSerializer.Deserialize<List<QueueEntryDocument>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, object body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            if (await predicate().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for the queue to flush.");
    }

    private sealed class QueueEntryDocument
    {
        public string Id { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string JellyfinUserId { get; set; } = string.Empty;

        public ScrobblePayload Payload { get; set; } = new();

        public DateTimeOffset CreatedAtUtc { get; set; }

        public DateTimeOffset NextAttemptAtUtc { get; set; }

        public int RetryCount { get; set; }
    }
}
