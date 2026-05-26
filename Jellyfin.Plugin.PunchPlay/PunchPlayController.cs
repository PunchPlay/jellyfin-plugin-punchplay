using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Admin-only endpoints — server-wide settings. Requires Jellyfin admin.
/// </summary>
[ApiController]
[Route("PunchPlay")]
[Authorize(Policy = "RequiresElevation")]
public class PunchPlayController : ControllerBase
{
    private readonly PluginDiagnosticsService _diagnostics;
    private readonly ScrobbleQueueService _queueService;

    public PunchPlayController(PluginDiagnosticsService diagnostics, ScrobbleQueueService queueService)
    {
        _diagnostics = diagnostics;
        _queueService = queueService;
    }

    /// <summary>Returns server-wide settings.</summary>
    [HttpGet("settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetSettings()
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return StatusCode(500);

        return Ok(new
        {
            punchPlayUrl = plugin.Configuration.PunchPlayUrl,
            progressIntervalSeconds = plugin.Configuration.ProgressIntervalSeconds
        });
    }

    /// <summary>Saves server-wide settings.</summary>
    [HttpPost("settings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult SaveSettings([FromBody] SaveSettingsRequest body)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return StatusCode(500);

        var cfg = plugin.Configuration;
        if (!string.IsNullOrWhiteSpace(body.PunchPlayUrl))
            cfg.PunchPlayUrl = body.PunchPlayUrl.TrimEnd('/');
        if (body.ProgressIntervalSeconds.HasValue)
            cfg.ProgressIntervalSeconds = Math.Clamp(body.ProgressIntervalSeconds.Value, 5, 300);
        plugin.SaveConfiguration(cfg);

        return NoContent();
    }

    /// <summary>Returns runtime plugin diagnostics for admins.</summary>
    [HttpGet("settings/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStatus()
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return StatusCode(500);

        return Ok(new
        {
            punchPlayUrl = plugin.Configuration.PunchPlayUrl,
            pluginVersion = plugin.ClientVersion,
            serverName = plugin.FriendlyServerName,
            linkedUserCount = plugin.Configuration.UserTokens.Count,
            queuedScrobbleCount = _diagnostics.QueuedScrobbleCount,
            oldestQueuedScrobbleAt = _diagnostics.OldestQueuedScrobbleAtUtc,
            nextQueuedRetryAt = _diagnostics.NextQueuedRetryAtUtc,
            highestQueuedRetryCount = _diagnostics.HighestQueuedRetryCount,
            lastSuccessfulScrobbleAt = _diagnostics.LastSuccessfulScrobbleAtUtc,
            lastFailedScrobbleAt = _diagnostics.LastFailedScrobbleAtUtc,
            lastError = _diagnostics.LastError
        });
    }

    /// <summary>Retries the local scrobble queue immediately.</summary>
    [HttpPost("settings/queue/retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RetryQueue(CancellationToken ct)
    {
        await _queueService.RetryNowAsync(ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Clears the local scrobble retry queue.</summary>
    [HttpDelete("settings/queue")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearQueue(CancellationToken ct)
    {
        await _queueService.ClearAsync(ct).ConfigureAwait(false);
        return NoContent();
    }

    public class SaveSettingsRequest
    {
        public string? PunchPlayUrl { get; set; }
        public int? ProgressIntervalSeconds { get; set; }
    }
}

/// <summary>
/// Per-user endpoints — any authenticated Jellyfin user can link their own PunchPlay account.
/// Admins may additionally pass a <c>userId</c> query param to manage any user.
/// </summary>
[ApiController]
[Route("PunchPlay/user")]
[Authorize]
public class PunchPlayUserController : ControllerBase
{
    private readonly DeviceAuthService _deviceAuth;
    private readonly IAuthorizationContext _authContext;
    private readonly IAuthorizationService _authorizationService;
    private readonly ScrobbleQueueService _queueService;
    private readonly ILogger<PunchPlayUserController> _logger;

    public PunchPlayUserController(
        DeviceAuthService deviceAuth,
        IAuthorizationContext authContext,
        IAuthorizationService authorizationService,
        ScrobbleQueueService queueService,
        ILogger<PunchPlayUserController> logger)
    {
        _deviceAuth = deviceAuth;
        _authContext = authContext;
        _authorizationService = authorizationService;
        _queueService = queueService;
        _logger = logger;
    }

    /// <summary>
    /// Returns the effective target user ID.
    /// Admins may pass a different <paramref name="requestedUserId"/>; non-admins always get their own ID.
    /// </summary>
    private async Task<string?> GetTargetUserIdAsync(Guid? requestedUserId)
    {
        var currentContext = await GetCurrentAccessContextAsync().ConfigureAwait(false);
        if (currentContext is null) return null;

        var currentId = currentContext.UserId;

        // If no specific user requested, or same user — return current
        if (!requestedUserId.HasValue || requestedUserId.Value == Guid.Empty || requestedUserId.Value == currentId)
            return currentId.ToString();

        // Different user requested — only admins allowed
        if (currentContext.IsAdmin)
            return requestedUserId.Value.ToString();

        // Non-admin requesting another user — silently use their own ID
        return currentId.ToString();
    }

    private async Task<CurrentAccessContext?> GetCurrentAccessContextAsync()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (auth is null || auth.UserId == Guid.Empty)
            return null;

        var authResult = await _authorizationService.AuthorizeAsync(User, null, "RequiresElevation").ConfigureAwait(false);
        var isAdmin = authResult.Succeeded;
        return new CurrentAccessContext(auth.UserId, isAdmin);
    }

    /// <summary>Returns a Jellyfin user's PunchPlay connection status.</summary>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Status([FromQuery] Guid? userId = null)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return StatusCode(500);

        var targetId = await GetTargetUserIdAsync(userId).ConfigureAwait(false);
        if (string.IsNullOrEmpty(targetId)) return StatusCode(401);

        var token = plugin.GetUserTokenRecord(targetId);
        var connected = token is not null && !string.IsNullOrWhiteSpace(token.AccessToken);

        return Ok(new
        {
            connected,
            username = connected ? token!.PunchPlayUsername : null,
            connectedAt = connected ? token!.ConnectedAt : null
        });
    }

    /// <summary>Starts a device auth flow.</summary>
    [HttpPost("auth/start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> StartAuth(
        [FromQuery] Guid? userId = null,
        [FromQuery] string? jellyfinUsername = null,
        CancellationToken ct = default)
    {
        var targetId = await GetTargetUserIdAsync(userId).ConfigureAwait(false);
        if (string.IsNullOrEmpty(targetId)) return StatusCode(401);

        var linkedUsername = string.IsNullOrWhiteSpace(jellyfinUsername) ? null : jellyfinUsername.Trim();
        var result = await _deviceAuth.StartAsync(targetId, linkedUsername, ct).ConfigureAwait(false);
        if (result is null) return StatusCode(502, new { error = "Could not reach PunchPlay. Check the URL in plugin settings." });

        return Ok(new
        {
            sessionId = result.SessionId,
            userCode = result.UserCode,
            qrDataUri = result.QrDataUri,
            expiresIn = result.ExpiresIn
        });
    }

    /// <summary>Polls for device auth completion. Returns 202 while pending, 200 on success, 410 if expired.</summary>
    [HttpGet("auth/poll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PollAuth([FromQuery] string sessionId, CancellationToken ct = default)
    {
        var currentContext = await GetCurrentAccessContextAsync().ConfigureAwait(false);
        if (currentContext is null) return StatusCode(401);

        var boundUserId = _deviceAuth.GetBoundUserId(sessionId);
        if (!string.IsNullOrWhiteSpace(boundUserId)
            && !currentContext.IsAdmin
            && !string.Equals(boundUserId, currentContext.UserId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var result = await _deviceAuth.PollAsync(sessionId, ct).ConfigureAwait(false);

        return result switch
        {
            DeviceAuthService.PollResult.Complete => Ok(new { status = "complete" }),
            DeviceAuthService.PollResult.Expired => StatusCode(410, new { status = "expired" }),
            _ => StatusCode(202, new { status = "pending" })
        };
    }

    /// <summary>Disconnects a user's PunchPlay token.</summary>
    [HttpDelete("auth/disconnect")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Disconnect([FromQuery] Guid? userId = null, CancellationToken ct = default)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return StatusCode(500);

        var targetId = await GetTargetUserIdAsync(userId).ConfigureAwait(false);
        if (string.IsNullOrEmpty(targetId)) return StatusCode(401);

        plugin.ClearUserToken(targetId);
        await _queueService.ClearUserAsync(targetId, ct).ConfigureAwait(false);
        return NoContent();
    }
    private sealed record CurrentAccessContext(Guid UserId, bool IsAdmin);
}
