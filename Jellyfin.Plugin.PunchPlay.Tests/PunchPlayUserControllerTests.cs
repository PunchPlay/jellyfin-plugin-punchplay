using Jellyfin.Plugin.PunchPlay.Tests.Support;

namespace Jellyfin.Plugin.PunchPlay.Tests;

public class PunchPlayUserControllerTests
{
    [Fact]
    public async Task StartAsync_BindsPendingSessionToTargetUser()
    {
        using var pluginContext = new TestPluginContext();
        const string targetUserId = "target-user-1";
        var deviceAuthService = CreateDeviceAuthService(CreateDeviceAuthHandler(targetUserId));

        var start = Assert.IsType<DeviceAuthService.StartResult>(
            await deviceAuthService.StartAsync(targetUserId, "PopcornHead", CancellationToken.None));
        Assert.Equal(targetUserId, deviceAuthService.GetBoundUserId(start.SessionId));
    }

    [Fact]
    public async Task PollAuth_ByBoundUser_Completes()
    {
        using var pluginContext = new TestPluginContext();
        var targetUserId = Guid.NewGuid();
        var deviceAuthService = CreateDeviceAuthService(CreateDeviceAuthHandler(targetUserId.ToString()));
        var start = Assert.IsType<DeviceAuthService.StartResult>(
            await deviceAuthService.StartAsync(targetUserId.ToString(), "PopcornHead", CancellationToken.None));

        var currentUser = CreateUser(targetUserId, isAdmin: false);
        var controller = CreateController(deviceAuthService, currentUser);

        var result = await controller.PollAuth(start.SessionId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(pluginContext.Plugin.GetUserToken(targetUserId.ToString()));
    }

    [Fact]
    public async Task PollAuth_ByAdmin_Completes()
    {
        using var pluginContext = new TestPluginContext();
        var targetUserId = Guid.NewGuid();
        var deviceAuthService = CreateDeviceAuthService(CreateDeviceAuthHandler(targetUserId.ToString()));
        var start = Assert.IsType<DeviceAuthService.StartResult>(
            await deviceAuthService.StartAsync(targetUserId.ToString(), "PopcornHead", CancellationToken.None));

        var adminUser = CreateUser(Guid.NewGuid(), isAdmin: true);
        var controller = CreateController(deviceAuthService, adminUser);

        var result = await controller.PollAuth(start.SessionId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(pluginContext.Plugin.GetUserToken(targetUserId.ToString()));
    }

    [Fact]
    public async Task PollAuth_ByAnotherNonAdminUser_ReturnsForbidden()
    {
        using var pluginContext = new TestPluginContext();
        var targetUserId = Guid.NewGuid();
        var deviceAuthService = CreateDeviceAuthService(CreateDeviceAuthHandler(targetUserId.ToString()));
        var start = Assert.IsType<DeviceAuthService.StartResult>(
            await deviceAuthService.StartAsync(targetUserId.ToString(), "PopcornHead", CancellationToken.None));

        var otherUser = CreateUser(Guid.NewGuid(), isAdmin: false);
        var controller = CreateController(deviceAuthService, otherUser);

        var result = await controller.PollAuth(start.SessionId, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Null(pluginContext.Plugin.GetUserToken(targetUserId.ToString()));
    }

    [Fact]
    public async Task PollAuth_UnknownSession_ReturnsGone()
    {
        using var pluginContext = new TestPluginContext();
        var userId = Guid.NewGuid();
        var deviceAuthService = CreateDeviceAuthService(CreateDeviceAuthHandler("target-user-1"));
        var controller = CreateController(deviceAuthService, CreateUser(userId, isAdmin: false));

        var result = await controller.PollAuth("missing-session", CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, objectResult.StatusCode);
    }

    [Fact]
    public async Task Disconnect_ClearsUserTokenAndQueuedScrobbles()
    {
        using var pluginContext = new TestPluginContext();
        var userId = Guid.NewGuid();
        pluginContext.Plugin.SetUserToken(userId.ToString(), "token-1", "tester");
        await WriteQueueEntriesAsync(pluginContext, new QueueEntryDocument
        {
            Id = "entry-1",
            Action = "stop",
            JellyfinUserId = userId.ToString(),
            Payload = new ScrobblePayload { MediaType = "movie", Title = "Queued Movie", PositionSeconds = 30 },
            CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            NextAttemptAtUtc = DateTimeOffset.UtcNow,
            RetryCount = 0
        });

        var deviceAuthService = CreateDeviceAuthService(CreateDeviceAuthHandler("target-user-1"));
        var controller = CreateController(deviceAuthService, CreateUser(userId, isAdmin: false));

        var result = await controller.Disconnect(null);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(pluginContext.Plugin.GetUserToken(userId.ToString()));
        Assert.Empty(await ReadQueueEntriesAsync(pluginContext));
    }

    private static DeviceAuthService CreateDeviceAuthService(DelegateHttpMessageHandler handler)
    {
        return new DeviceAuthService(new TestHttpClientFactory(handler), NullLogger<DeviceAuthService>.Instance);
    }

    private static PunchPlayUserController CreateController(DeviceAuthService deviceAuthService, User currentUser)
    {
        var authContext = new Mock<IAuthorizationContext>();
        authContext
            .Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpRequest>()))
            .ReturnsAsync(new AuthorizationInfo
            {
                IsAuthenticated = true,
                User = currentUser
            });

        var authorizationService = new Mock<IAuthorizationService>();
        authorizationService
            .Setup(service => service.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<object?>(),
                "RequiresElevation"))
            .ReturnsAsync(isAdmin(currentUser)
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed());

        var testHttpClientFactory = new TestHttpClientFactory(new DelegateHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));
        var queueService = new ScrobbleQueueService(
            new PunchPlayTransport(testHttpClientFactory, NullLogger<PunchPlayTransport>.Instance),
            new PunchPlayAuthService(testHttpClientFactory, NullLogger<PunchPlayAuthService>.Instance),
            new PluginDiagnosticsService(),
            NullLogger<ScrobbleQueueService>.Instance);

        var controller = new PunchPlayUserController(deviceAuthService, authContext.Object, authorizationService.Object, queueService, NullLogger<PunchPlayUserController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, currentUser.Id.ToString()),
                        new Claim(ClaimTypes.Name, currentUser.Username),
                        new Claim(ClaimTypes.Role, isAdmin(currentUser) ? "Administrator" : "User")
                    ], "TestAuth"))
                }
            }
        };

        return controller;
    }

    private static bool isAdmin(User user) => user.HasPermission(PermissionKind.IsAdministrator);

    private static User CreateUser(Guid userId, bool isAdmin)
    {
        var user = new User("tester", "local", "local")
        {
            Id = userId
        };

        if (isAdmin)
        {
            user.SetPermission(PermissionKind.IsAdministrator, true);
        }

        return user;
    }

    private static DelegateHttpMessageHandler CreateDeviceAuthHandler(string expectedLinkedUserId, string expectedLinkedUsername = "PopcornHead")
    {
        return new DelegateHttpMessageHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/auth/device/code", StringComparison.Ordinal))
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("jellyfin", body.RootElement.GetProperty("client_type").GetString());
                Assert.Equal(typeof(Plugin).Assembly.GetName().Version?.ToString(), body.RootElement.GetProperty("client_version").GetString());

                return CreateJsonResponse(HttpStatusCode.OK, new
                {
                    user_code = "ABCD-EFGH",
                    device_code = "device-code-123",
                    verification_uri_qr = "data:image/png;base64,AAA",
                    expires_in = 900
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/api/auth/device/token", StringComparison.Ordinal))
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("device-code-123", body.RootElement.GetProperty("device_code").GetString());
                Assert.Equal("jellyfin", body.RootElement.GetProperty("client_type").GetString());
                Assert.Equal(typeof(Plugin).Assembly.GetName().Version?.ToString(), body.RootElement.GetProperty("client_version").GetString());
                Assert.Equal("test-server-id", body.RootElement.GetProperty("device_id").GetString());
                Assert.StartsWith("Jellyfin", body.RootElement.GetProperty("device_name").GetString(), StringComparison.Ordinal);
                Assert.Equal(expectedLinkedUserId, body.RootElement.GetProperty("linked_user_id").GetString());
                Assert.Equal(expectedLinkedUsername, body.RootElement.GetProperty("linked_username").GetString());

                return CreateJsonResponse(HttpStatusCode.OK, new
                {
                    access_token = "access-token-123",
                    username = "punchplay-user"
                });
            }

            throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}");
        });
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
