using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Sends scrobble payloads to PunchPlay and classifies the outcome for retry handling.
/// </summary>
public class PunchPlayTransport
{
    private static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "start",
        "progress",
        "pause",
        "resume",
        "stop"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PunchPlayTransport> _logger;

    public PunchPlayTransport(IHttpClientFactory httpClientFactory, ILogger<PunchPlayTransport> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Sends a scrobble action to PunchPlay.
    /// </summary>
    public async Task<PunchPlayTransportResult> SendAsync(
        string action,
        string accessToken,
        ScrobblePayload payload,
        CancellationToken ct)
    {
        if (!AllowedActions.Contains(action))
        {
            return PunchPlayTransportResult.PermanentFailure($"Invalid scrobble action '{action}'.");
        }

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return PunchPlayTransportResult.RetryableFailure("Plugin instance is unavailable.");
        }

        var client = _httpClientFactory.CreateClient("PunchPlay");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync($"{plugin.ApiBase}/api/scrobble/{action}", payload, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex)
        {
            var error = ct.IsCancellationRequested
                ? $"Request for action '{action}' was canceled."
                : $"Request for action '{action}' timed out.";
            _logger.LogWarning(ex, "[PunchPlay] {Error}", error);
            return PunchPlayTransportResult.RetryableFailure(error);
        }
        catch (HttpRequestException ex)
        {
            var error = $"Network error while sending action '{action}': {ex.Message}";
            _logger.LogWarning(ex, "[PunchPlay] {Error}", error);
            return PunchPlayTransportResult.RetryableFailure(error);
        }
        catch (Exception ex)
        {
            var error = $"Unexpected error while sending action '{action}': {ex.Message}";
            _logger.LogError(ex, "[PunchPlay] {Error}", error);
            return PunchPlayTransportResult.RetryableFailure(error);
        }

        if (response.IsSuccessStatusCode)
            return PunchPlayTransportResult.Success();

        var snippet = await ReadResponseSnippetAsync(response, ct).ConfigureAwait(false);
        var message = $"Action '{action}' returned {(int)response.StatusCode} {response.StatusCode}" +
            (string.IsNullOrWhiteSpace(snippet) ? string.Empty : $": {snippet}");

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return PunchPlayTransportResult.Unauthorized(message);

        if (IsRetryableStatus(response.StatusCode))
            return PunchPlayTransportResult.RetryableFailure(message);

        return PunchPlayTransportResult.PermanentFailure(message);
    }

    private static bool IsRetryableStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
        or (HttpStatusCode)429
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    private static async Task<string?> ReadResponseSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            body = body.ReplaceLineEndings(" ").Trim();
            return body.Length <= 300 ? body : body[..300];
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Classified result of a PunchPlay transport attempt.
/// </summary>
public sealed class PunchPlayTransportResult
{
    private PunchPlayTransportResult(PunchPlayTransportOutcome outcome, string? message)
    {
        Outcome = outcome;
        Message = message;
    }

    public PunchPlayTransportOutcome Outcome { get; }

    public string? Message { get; }

    public static PunchPlayTransportResult Success() => new(PunchPlayTransportOutcome.Success, null);

    public static PunchPlayTransportResult RetryableFailure(string? message) => new(PunchPlayTransportOutcome.RetryableFailure, message);

    public static PunchPlayTransportResult Unauthorized(string? message) => new(PunchPlayTransportOutcome.Unauthorized, message);

    public static PunchPlayTransportResult PermanentFailure(string? message) => new(PunchPlayTransportOutcome.PermanentFailure, message);
}

/// <summary>
/// High-level delivery classification for a PunchPlay scrobble request.
/// </summary>
public enum PunchPlayTransportOutcome
{
    Success,
    RetryableFailure,
    Unauthorized,
    PermanentFailure
}
