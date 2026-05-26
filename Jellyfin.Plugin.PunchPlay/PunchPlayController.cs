using System.Security.Claims;
using MediaBrowser.Controller.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Admin-only endpoints — server-wide settings. Requires Jellyfin admin.
/// </summary>
[ApiController]
[Route("PunchPlay")]
[Authorize(Policy = "RequiresElevation")]
public class PunchPlayController : ControllerBase
{
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

    public class SaveSettingsRequest
    {
        public string? PunchPlayUrl { get; set; }
        public int? ProgressIntervalSeconds { get; set; }
    }
}

/// <summary>
/// Per-user endpoints — each Jellyfin user links their own PunchPlay account. No admin required.
/// </summary>
[ApiController]
[Route("PunchPlay/user")]
[Authorize]
public class PunchPlayUserController : ControllerBase
{
    private readonly DeviceAuthService _deviceAuth;

    public PunchPlayUserController(DeviceAuthService deviceAuth)
    {
        _deviceAuth = deviceAuth;
    }

    private string? GetCurrentUserId() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    /// <summary>Returns the current Jellyfin user's PunchPlay connection status.</summary>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Status()
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return StatusCode(500);

        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return StatusCode(401);

        var token = plugin.GetUserTokenRecord(userId);
        var connected = token is not null && !string.IsNullOrWhiteSpace(token.AccessToken);

        return Ok(new
        {
            connected,
            username = connected ? token!.PunchPlayUsername : null,
            connectedAt = connected ? token!.ConnectedAt : null
        });
    }

    /// <summary>Starts a device auth flow for the current user.</summary>
    [HttpPost("auth/start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> StartAuth(CancellationToken ct)
    {
        var result = await _deviceAuth.StartAsync(ct).ConfigureAwait(false);
        if (result is null) return StatusCode(502, new { error = "Could not reach PunchPlay. Check the URL in plugin settings." });

        return Ok(new
        {
            sessionId = result.SessionId,
            userCode = result.UserCode,
            qrDataUri = result.QrDataUri,
            expiresIn = result.ExpiresIn
        });
    }

    /// <summary>Polls for device auth completion for the current user. Returns 202 while pending, 200 on success, 410 if expired.</summary>
    [HttpGet("auth/poll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> PollAuth([FromQuery] string sessionId, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return StatusCode(401);

        var result = await _deviceAuth.PollAsync(sessionId, userId, ct).ConfigureAwait(false);

        return result switch
        {
            DeviceAuthService.PollResult.Complete => Ok(new { status = "complete" }),
            DeviceAuthService.PollResult.Expired => StatusCode(410, new { status = "expired" }),
            _ => StatusCode(202, new { status = "pending" })
        };
    }

    /// <summary>Disconnects the current user's PunchPlay token.</summary>
    [HttpDelete("auth/disconnect")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Disconnect()
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return StatusCode(500);

        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return StatusCode(401);

        plugin.ClearUserToken(userId);
        return NoContent();
    }
}
