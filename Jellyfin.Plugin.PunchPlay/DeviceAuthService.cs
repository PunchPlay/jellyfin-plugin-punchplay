using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Manages in-progress device auth sessions (code → token polling).
/// </summary>
public class DeviceAuthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeviceAuthService> _logger;

    // sessionId → (device_code, expiry, target Jellyfin user)
    private readonly ConcurrentDictionary<string, PendingSession> _pending = new();

    public DeviceAuthService(IHttpClientFactory httpClientFactory, ILogger<DeviceAuthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Starts a device auth flow and returns the user-facing code and QR image.
    /// </summary>
    public async Task<StartResult?> StartAsync(string jellyfinUserId, CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return null;

        var client = _httpClientFactory.CreateClient("PunchPlay");
        HttpResponseMessage response;
        try
        {
            var codeRequest = JsonContent.Create(new { client_type = "jellyfin" });
            response = await client.PostAsync(
                $"{plugin.ApiBase}/api/auth/device/code",
                codeRequest, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PunchPlay] Failed to start device auth");
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken: ct).ConfigureAwait(false);
        if (body is null) return null;

        var sessionId = Guid.NewGuid().ToString("N");
        var expiry = DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn);
        _pending[sessionId] = new PendingSession(body.DeviceCode, expiry, jellyfinUserId);

        // Clean stale sessions
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryGetValue(key, out var s) && s.Expiry < DateTimeOffset.UtcNow)
                _pending.TryRemove(key, out _);
        }

        return new StartResult(sessionId, body.UserCode, body.VerificationUriQr, body.ExpiresIn);
    }

    /// <summary>
    /// Polls PunchPlay for token completion. On success, stores the token for the Jellyfin user bound at auth start.
    /// </summary>
    public async Task<PollResult> PollAsync(string sessionId, CancellationToken ct)
    {
        if (!_pending.TryGetValue(sessionId, out var session))
            return PollResult.Expired;

        if (session.Expiry < DateTimeOffset.UtcNow)
        {
            _pending.TryRemove(sessionId, out _);
            return PollResult.Expired;
        }

        var plugin = Plugin.Instance;
        if (plugin is null) return PollResult.Expired;

        var client = _httpClientFactory.CreateClient("PunchPlay");
        HttpResponseMessage response;
        try
        {
            var payload = JsonContent.Create(new
            {
                device_code = session.DeviceCode,
                client_type = "jellyfin",
                device_id = plugin.EnsureServerId(),
                device_name = plugin.FriendlyServerName
            });
            response = await client.PostAsync($"{plugin.ApiBase}/api/auth/device/token", payload, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PunchPlay] Poll request failed");
            return PollResult.Pending;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken: ct).ConfigureAwait(false);
            return errBody?.Error switch
            {
                "authorization_pending" => PollResult.Pending,
                "expired" => PollResult.Expired,
                _ => PollResult.Pending
            };
        }

        var tokenBody = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct).ConfigureAwait(false);
        if (tokenBody?.AccessToken is null) return PollResult.Pending;

        _pending.TryRemove(sessionId, out _);

        plugin.SetUserToken(session.TargetJellyfinUserId, tokenBody.AccessToken, tokenBody.Username ?? string.Empty);

        return PollResult.Complete;
    }

    /// <summary>
    /// Returns the Jellyfin user bound to a pending device-auth session, if any.
    /// </summary>
    public string? GetBoundUserId(string sessionId)
    {
        if (!_pending.TryGetValue(sessionId, out var session))
            return null;

        return session.Expiry < DateTimeOffset.UtcNow ? null : session.TargetJellyfinUserId;
    }

    private record PendingSession(string DeviceCode, DateTimeOffset Expiry, string TargetJellyfinUserId);

    private class DeviceCodeResponse
    {
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = string.Empty;
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = string.Empty;
        [JsonPropertyName("verification_uri_qr")] public string VerificationUriQr { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
    }

    private class ErrorBody
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    public record StartResult(string SessionId, string UserCode, string QrDataUri, int ExpiresIn);

    public enum PollResult { Pending, Complete, Expired }
}
