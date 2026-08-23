using System.Collections.Concurrent;
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
    /// Refreshes the access token for a Jellyfin user. Returns the new access token on success,
    /// or null if there's no refresh token on file, or the backend rejected it (expired/revoked) —
    /// in that case the caller should treat the user as unlinked.
    /// </summary>
    public async Task<string?> RefreshAccessTokenAsync(string jellyfinUserId, string staleAccessToken, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(jellyfinUserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is null) return null;

            // Another caller may have refreshed while we were waiting on the gate — reuse that
            // result instead of rotating the refresh chain (and invalidating it) a second time.
            var current = plugin.GetUserToken(jellyfinUserId);
            if (!string.Equals(current, staleAccessToken, StringComparison.Ordinal))
                return current;

            var refreshToken = plugin.GetUserTokenRecord(jellyfinUserId)?.RefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
                return null;

            HttpResponseMessage response;
            try
            {
                var client = _httpClientFactory.CreateClient("PunchPlay");
                response = await client.PostAsJsonAsync(
                    $"{plugin.ApiBase}/api/auth/refresh",
                    new { refresh_token = refreshToken },
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PunchPlay] Token refresh request failed for user {UserId}", jellyfinUserId);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[PunchPlay] Token refresh rejected for user {UserId}: {StatusCode}",
                    jellyfinUserId, response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<RefreshResponse>(cancellationToken: ct).ConfigureAwait(false);
            if (body?.AccessToken is null || body.RefreshToken is null)
                return null;

            plugin.UpdateUserTokens(jellyfinUserId, body.AccessToken, body.RefreshToken);
            _logger.LogDebug("[PunchPlay] Refreshed access token for user {UserId}", jellyfinUserId);
            return body.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private class RefreshResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    }
}
