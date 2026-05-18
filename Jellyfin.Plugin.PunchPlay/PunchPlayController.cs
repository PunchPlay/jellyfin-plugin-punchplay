using Jellyfin.Plugin.PunchPlay;
using MediaBrowser.Controller.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// REST endpoints used by the plugin config page.
/// All endpoints require Jellyfin admin authentication.
/// </summary>
[ApiController]
[Route("PunchPlay")]
[Authorize(Policy = "RequiresElevation")]
public class PunchPlayController : ControllerBase
{
    private readonly DeviceAuthService _deviceAuth;

    public PunchPlayController(DeviceAuthService deviceAuth)
    {
        _deviceAuth = deviceAuth;
    }

    /// <summary>Returns current connection status.</summary>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Status()
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return StatusCode(500);

        var connected = plugin.AccessToken is not null;
        return Ok(new
        {
            connected,
            username = connected ? plugin.Configuration.PunchPlayUsername : null,
            connectedAt = connected ? plugin.Configuration.ConnectedAt : null,
            punchPlayUrl = plugin.Configuration.PunchPlayUrl
        });
    }

    /// <summary>Starts a device auth flow and returns the user code + QR image.</summary>
    [HttpPost("auth/start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> StartAuth(CancellationToken ct)
    {
        var result = await _deviceAuth.StartAsync(ct).ConfigureAwait(false);
        if (result is null) return StatusCode(502, new { error = "Could not reach PunchPlay. Check your server URL in plugin settings." });

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
    public async Task<IActionResult> PollAuth([FromQuery] string sessionId, CancellationToken ct)
    {
        var result = await _deviceAuth.PollAsync(sessionId, ct).ConfigureAwait(false);

        return result switch
        {
            DeviceAuthService.PollResult.Complete => Ok(new { status = "complete" }),
            DeviceAuthService.PollResult.Expired => StatusCode(410, new { status = "expired" }),
            _ => StatusCode(202, new { status = "pending" })
        };
    }

    /// <summary>Disconnects the current token.</summary>
    [HttpDelete("auth/disconnect")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Disconnect()
    {
        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            var cfg = plugin.Configuration;
            cfg.AccessToken = string.Empty;
            cfg.PunchPlayUsername = string.Empty;
            cfg.ConnectedAt = null;
            plugin.SaveConfiguration(cfg);
        }

        return NoContent();
    }
}
