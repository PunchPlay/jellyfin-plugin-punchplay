using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Coordinates live scrobble delivery, retry queueing, and token invalidation.
/// </summary>
public class PunchPlayScrobbleClient
{
    private readonly PunchPlayTransport _transport;
    private readonly PunchPlayAuthService _authService;
    private readonly ScrobbleQueueService _queueService;
    private readonly PluginDiagnosticsService _diagnostics;
    private readonly ILogger<PunchPlayScrobbleClient> _logger;

    public PunchPlayScrobbleClient(
        PunchPlayTransport transport,
        PunchPlayAuthService authService,
        ScrobbleQueueService queueService,
        PluginDiagnosticsService diagnostics,
        ILogger<PunchPlayScrobbleClient> logger)
    {
        _transport = transport;
        _authService = authService;
        _queueService = queueService;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    /// <summary>
    /// Sends a live scrobble to PunchPlay and queues it for retry if the failure is transient.
    /// </summary>
    public async Task DispatchAsync(
        string action,
        string jellyfinUserId,
        string accessToken,
        ScrobblePayload payload,
        CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
            return;

        var result = await _transport.SendAsync(action, accessToken, payload, ct).ConfigureAwait(false);

        if (result.Outcome == PunchPlayTransportOutcome.Unauthorized)
        {
            var refreshedToken = await _authService.RefreshAccessTokenAsync(jellyfinUserId, accessToken, ct).ConfigureAwait(false);
            if (refreshedToken is not null)
            {
                _logger.LogDebug("[PunchPlay] Retrying {Action} for user {UserId} after token refresh", action, jellyfinUserId);
                result = await _transport.SendAsync(action, refreshedToken, payload, ct).ConfigureAwait(false);
            }
        }

        switch (result.Outcome)
        {
            case PunchPlayTransportOutcome.Success:
                _diagnostics.MarkSuccess();
                _queueService.RequestFlush();
                return;

            case PunchPlayTransportOutcome.Unauthorized:
                plugin.ClearUserToken(jellyfinUserId);
                await _queueService.ClearUserAsync(jellyfinUserId, ct).ConfigureAwait(false);
                _diagnostics.MarkFailure(result.Message);
                _logger.LogWarning("[PunchPlay] Token revoked for user {UserId}. Clearing stored credentials.", jellyfinUserId);
                return;

            case PunchPlayTransportOutcome.RetryableFailure:
                await _queueService.EnqueueAsync(action, jellyfinUserId, payload, ct).ConfigureAwait(false);
                _diagnostics.MarkFailure(result.Message);
                _logger.LogWarning("[PunchPlay] Queued transiently failed scrobble {Action} for user {UserId}: {Error}",
                    action, jellyfinUserId, result.Message);
                return;

            case PunchPlayTransportOutcome.PermanentFailure:
                _diagnostics.MarkFailure(result.Message);
                _logger.LogWarning("[PunchPlay] Dropping non-retryable scrobble {Action} for user {UserId}: {Error}",
                    action, jellyfinUserId, result.Message);
                return;
        }
    }
}
