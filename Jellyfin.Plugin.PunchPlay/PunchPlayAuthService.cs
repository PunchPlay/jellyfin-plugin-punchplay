using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Exchanges a stored refresh token for a new access token when a scrobble hits 401.
/// Access tokens expire after 1 hour by design; refresh tokens are long-lived and rotate
/// on every use, so refreshes are single-flighted per Jellyfin user to avoid two concurrent
/// callers each presenting the same (about-to-be-stale) refresh token.
/// </summary>
public class PunchPlayAuthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PunchPlayAuthService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public PunchPlayAuthService(IHttpClientFactory httpClientFactory, ILogger<PunchPlayAuthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Refreshes the access token for a Jellyfin user and classifies failures so callers only
    /// unlink users when the backend definitively rejects the stored refresh token.
    /// </summary>
    public async Task<PunchPlayTokenRefreshResult> RefreshAccessTokenAsync(
        string jellyfinUserId,
        string staleAccessToken,
        CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(jellyfinUserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is null)
                return PunchPlayTokenRefreshResult.RetryableFailure("Plugin instance is unavailable.");

            // Another caller may have refreshed while we were waiting on the gate — reuse that
            // result instead of rotating the refresh chain (and invalidating it) a second time.
            var current = plugin.GetUserToken(jellyfinUserId);
            if (!string.Equals(current, staleAccessToken, StringComparison.Ordinal))
            {
                return current is null
                    ? PunchPlayTokenRefreshResult.Rejected("The user is no longer linked.")
                    : PunchPlayTokenRefreshResult.Refreshed(current);
            }

            var refreshToken = plugin.GetUserTokenRecord(jellyfinUserId)?.RefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
                return PunchPlayTokenRefreshResult.Rejected("No refresh token is stored for the user.");

            try
            {
                var client = _httpClientFactory.CreateClient("PunchPlay");
                using var response = await client.PostAsJsonAsync(
                    $"{plugin.ApiBase}/api/auth/refresh",
                    new { refresh_token = refreshToken },
                    ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var message = $"Token refresh returned {(int)response.StatusCode} {response.StatusCode}.";
                    if (IsDefinitiveRejection(response.StatusCode))
                    {
                        _logger.LogWarning(
                            "[PunchPlay] Token refresh rejected for user {UserId}: {StatusCode}",
                            jellyfinUserId, response.StatusCode);
                        return PunchPlayTokenRefreshResult.Rejected(message);
                    }

                    _logger.LogWarning(
                        "[PunchPlay] Token refresh temporarily failed for user {UserId}: {StatusCode}",
                        jellyfinUserId, response.StatusCode);
                    return PunchPlayTokenRefreshResult.RetryableFailure(message);
                }

                var body = await response.Content.ReadFromJsonAsync<RefreshResponse>(cancellationToken: ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body?.AccessToken) || string.IsNullOrWhiteSpace(body.RefreshToken))
                {
                    const string message = "Token refresh returned an invalid response.";
                    _logger.LogWarning("[PunchPlay] {Message} User: {UserId}", message, jellyfinUserId);
                    return PunchPlayTokenRefreshResult.RetryableFailure(message);
                }

                plugin.UpdateUserTokens(jellyfinUserId, body.AccessToken, body.RefreshToken);
                _logger.LogDebug("[PunchPlay] Refreshed access token for user {UserId}", jellyfinUserId);
                return PunchPlayTokenRefreshResult.Refreshed(body.AccessToken);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = $"Token refresh request failed: {ex.Message}";
                _logger.LogWarning(ex, "[PunchPlay] Token refresh request failed for user {UserId}", jellyfinUserId);
                return PunchPlayTokenRefreshResult.RetryableFailure(message);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsDefinitiveRejection(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest
        or HttpStatusCode.Unauthorized
        or HttpStatusCode.Forbidden;

    private class RefreshResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    }
}

/// <summary>
/// Result of attempting to refresh a PunchPlay access token.
/// </summary>
public sealed class PunchPlayTokenRefreshResult
{
    private PunchPlayTokenRefreshResult(PunchPlayTokenRefreshOutcome outcome, string? accessToken, string? message)
    {
        Outcome = outcome;
        AccessToken = accessToken;
        Message = message;
    }

    public PunchPlayTokenRefreshOutcome Outcome { get; }

    public string? AccessToken { get; }

    public string? Message { get; }

    public static PunchPlayTokenRefreshResult Refreshed(string accessToken) =>
        new(PunchPlayTokenRefreshOutcome.Refreshed, accessToken, null);

    public static PunchPlayTokenRefreshResult Rejected(string? message) =>
        new(PunchPlayTokenRefreshOutcome.Rejected, null, message);

    public static PunchPlayTokenRefreshResult RetryableFailure(string? message) =>
        new(PunchPlayTokenRefreshOutcome.RetryableFailure, null, message);
}

/// <summary>
/// High-level classification of a PunchPlay token refresh attempt.
/// </summary>
public enum PunchPlayTokenRefreshOutcome
{
    Refreshed,
    Rejected,
    RetryableFailure
}
