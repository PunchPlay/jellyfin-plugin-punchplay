using System.Collections.Concurrent;
using System.Net.Http.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Subscribes to Jellyfin playback events and scrobbles to PunchPlay.
/// </summary>
public sealed class PlaybackScrobbler : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PlaybackScrobbler> _logger;

    // userId:progressKey → last progress send time (rate-limit heartbeats)
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastProgress = new();

    public PlaybackScrobbler(
        ISessionManager sessionManager,
        IHttpClientFactory httpClientFactory,
        ILogger<PlaybackScrobbler> logger)
    {
        _sessionManager = sessionManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        return Task.CompletedTask;
    }

    private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        => await SendAsync(e, "start").ConfigureAwait(false);

    private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.IsPaused)
        {
            await SendAsync(e, "pause").ConfigureAwait(false);
            return;
        }

        // Throttle progress events to the configured interval
        var plugin = Plugin.Instance;
        if (plugin is null) return;
        var intervalSeconds = Math.Max(10, plugin.Configuration.ProgressIntervalSeconds);

        var key = $"{e.Session.UserId}:{BuildProgressKey(e.Item)}";
        var now = DateTimeOffset.UtcNow;
        if (_lastProgress.TryGetValue(key, out var last) && (now - last).TotalSeconds < intervalSeconds)
            return;

        _lastProgress[key] = now;
        await SendAsync(e, "progress").ConfigureAwait(false);
    }

    private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        var action = "stop";
        await SendAsync(e, action, e.PlayedToCompletion).ConfigureAwait(false);

        // Clear throttle entry
        var key = $"{e.Session.UserId}:{BuildProgressKey(e.Item)}";
        _lastProgress.TryRemove(key, out _);
    }

    private async Task SendAsync(PlaybackProgressEventArgs e, string action, bool? playedToCompletion = null)
    {
        var plugin = Plugin.Instance;
        if (plugin?.AccessToken is null) return;
        if (e.Item is null) return;

        var payload = BuildPayload(e, action, playedToCompletion);
        if (payload is null) return;

        var client = _httpClientFactory.CreateClient("PunchPlay");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", plugin.AccessToken);

        try
        {
            var response = await client.PostAsJsonAsync(
                $"{plugin.ApiBase}/api/scrobble/{action}", payload).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("[PunchPlay] Token revoked — clearing stored credentials");
                var cfg = plugin.Configuration;
                cfg.AccessToken = string.Empty;
                cfg.ConnectedAt = null;
                plugin.SaveConfiguration(cfg);
            }
            else if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[PunchPlay] Scrobble {Action} returned {Status}", action, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PunchPlay] Scrobble {Action} failed", action);
        }
    }

    private static ScrobblePayload? BuildPayload(PlaybackProgressEventArgs e, string action, bool? playedToCompletion)
    {
        var item = e.Item;
        var positionTicks = e.PlaybackPositionTicks ?? 0;
        var positionSeconds = (int)(positionTicks / 10_000_000);
        var durationSeconds = item.RunTimeTicks.HasValue
            ? (int)(item.RunTimeTicks.Value / 10_000_000)
            : 0;

        if (item is Movie movie)
        {
            var tmdbId = ParseTmdbId(movie.ProviderIds.GetValueOrDefault("Tmdb"));
            if (tmdbId is null && string.IsNullOrWhiteSpace(movie.Name)) return null;

            return new ScrobblePayload
            {
                MediaType = "movie",
                Title = movie.Name,
                Year = movie.ProductionYear,
                TmdbId = tmdbId,
                PositionSeconds = positionSeconds,
                DurationSeconds = durationSeconds > 0 ? durationSeconds : null,
                Watched = playedToCompletion,
                WatchedThreshold = playedToCompletion is null ? null : 0.85,
                DeviceName = e.Session.DeviceName
            };
        }

        if (item is Episode episode)
        {
            var showTmdbId = ParseTmdbId(episode.Series?.ProviderIds.GetValueOrDefault("Tmdb"))
                ?? ParseTmdbId(episode.ProviderIds.GetValueOrDefault("Tmdb"));
            var seasonNumber = episode.ParentIndexNumber;
            var episodeNumber = episode.IndexNumber;
            var showName = episode.SeriesName ?? episode.Series?.Name;

            if (showTmdbId is null && string.IsNullOrWhiteSpace(showName)) return null;
            if (seasonNumber is null || episodeNumber is null) return null;

            return new ScrobblePayload
            {
                MediaType = "episode",
                Title = showName,
                Year = episode.ProductionYear ?? episode.Series?.ProductionYear,
                TmdbId = showTmdbId,
                Season = seasonNumber,
                Episode = episodeNumber,
                PositionSeconds = positionSeconds,
                DurationSeconds = durationSeconds > 0 ? durationSeconds : null,
                Watched = playedToCompletion,
                WatchedThreshold = playedToCompletion is null ? null : 0.85,
                DeviceName = e.Session.DeviceName
            };
        }

        return null;
    }

    private static string BuildProgressKey(BaseItem? item) => item switch
    {
        Movie m => $"movie:{m.ProviderIds.GetValueOrDefault("Tmdb") ?? m.Name}",
        Episode ep => $"episode:{ep.Series?.ProviderIds.GetValueOrDefault("Tmdb") ?? ep.SeriesName}:{ep.ParentIndexNumber}:{ep.IndexNumber}",
        _ => item?.Id.ToString() ?? "unknown"
    };

    private static int? ParseTmdbId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return int.TryParse(value, out var id) && id > 0 ? id : null;
    }

    /// <inheritdoc />
    public void Dispose() => _lastProgress.Clear();

    private class ScrobblePayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("media_type")]
        public string MediaType { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string? Title { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("year")]
        public int? Year { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("tmdb_id")]
        public int? TmdbId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("season")]
        public int? Season { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("episode")]
        public int? Episode { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("position_seconds")]
        public int PositionSeconds { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("duration_seconds")]
        public int? DurationSeconds { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("watched")]
        public bool? Watched { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("watched_threshold")]
        public double? WatchedThreshold { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("client_version")]
        public string ClientVersion { get; set; } = "1.0.0";

        [System.Text.Json.Serialization.JsonPropertyName("device_id")]
        public string? DeviceName { get; set; }
    }
}
