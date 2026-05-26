using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Builds scrobble payloads from Jellyfin playback events.
/// </summary>
public class ScrobblePayloadFactory
{
    private const double WatchedThreshold = 0.85;

    private readonly ILogger<ScrobblePayloadFactory> _logger;

    public ScrobblePayloadFactory(ILogger<ScrobblePayloadFactory> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Builds a PunchPlay payload for the supplied playback event.
    /// </summary>
    public ScrobblePayload? Create(
        PlaybackProgressEventArgs e,
        string action,
        string jellyfinUserId,
        string serverId,
        string serverName,
        string clientVersion,
        bool playedToCompletion = false,
        DateTimeOffset? eventCreatedAt = null)
    {
        if (e.Item is null)
            return null;

        var item = e.Item;
        var createdAt = eventCreatedAt ?? DateTimeOffset.UtcNow;
        var eventCreatedAtMs = createdAt.ToUnixTimeMilliseconds();
        var positionSeconds = ToSeconds(e.PlaybackPositionTicks);
        var durationSeconds = ToSeconds(item.RunTimeTicks);
        double? progressPercent = durationSeconds > 0
            ? Math.Round((double)positionSeconds / durationSeconds * 100d, 2)
            : null;
        double? progress = durationSeconds > 0
            ? Math.Round((double)positionSeconds / durationSeconds, 4)
            : null;
        bool? watched = action == "stop" && (playedToCompletion || MeetsWatchedThreshold(positionSeconds, durationSeconds))
            ? true
            : null;
        double? watchedThreshold = watched == true ? WatchedThreshold : null;
        var playbackSessionId = BuildPlaybackSessionId(e);
        var eventId = BuildEventId(playbackSessionId, action, eventCreatedAtMs, positionSeconds);

        if (item is Movie movie)
        {
            if (string.IsNullOrWhiteSpace(movie.Name) && ParseTmdbId(movie.ProviderIds.GetValueOrDefault("Tmdb")) is null)
            {
                _logger.LogInformation("[PunchPlay] Skipping movie {ItemId}: no TMDB ID or title metadata available", movie.Id);
                return null;
            }

            return new ScrobblePayload
            {
                EventId = eventId,
                MediaType = "movie",
                Title = movie.Name,
                Year = movie.ProductionYear,
                TmdbId = ParseTmdbId(movie.ProviderIds.GetValueOrDefault("Tmdb")),
                ImdbId = NormalizeProviderId(movie.ProviderIds.GetValueOrDefault("Imdb")),
                TvdbId = ParseTvdbId(movie.ProviderIds.GetValueOrDefault("Tvdb")),
                PositionSeconds = positionSeconds,
                DurationSeconds = durationSeconds > 0 ? durationSeconds : null,
                ProgressPercent = progressPercent,
                Progress = progress,
                Watched = watched,
                WatchedThreshold = watchedThreshold,
                ClientVersion = clientVersion,
                DeviceId = serverId,
                DeviceName = e.Session?.DeviceName ?? e.DeviceName,
                PlaybackSessionId = playbackSessionId,
                EventCreatedAt = eventCreatedAtMs,
                ServerName = serverName,
                JellyfinUserId = jellyfinUserId
            };
        }

        if (item is Episode episode)
        {
            var series = episode.Series;
            var showTitle = episode.SeriesName ?? series?.Name;
            var showTmdbId = ParseTmdbId(series?.ProviderIds.GetValueOrDefault("Tmdb"));
            var showImdbId = NormalizeProviderId(series?.ProviderIds.GetValueOrDefault("Imdb"))
                ?? NormalizeProviderId(episode.ProviderIds.GetValueOrDefault("Imdb"));
            var showTvdbId = ParseTvdbId(series?.ProviderIds.GetValueOrDefault("Tvdb"))
                ?? ParseTvdbId(episode.ProviderIds.GetValueOrDefault("Tvdb"));

            if (string.IsNullOrWhiteSpace(showTitle) && showTmdbId is null && string.IsNullOrWhiteSpace(showImdbId) && showTvdbId is null)
            {
                _logger.LogInformation("[PunchPlay] Skipping episode {ItemId}: no show-level identifiers or title metadata available", episode.Id);
                return null;
            }

            if (episode.ParentIndexNumber is null || episode.IndexNumber is null)
            {
                _logger.LogInformation("[PunchPlay] Skipping episode {ItemId}: season/episode numbers are missing", episode.Id);
                return null;
            }

            return new ScrobblePayload
            {
                EventId = eventId,
                MediaType = "episode",
                Title = showTitle,
                EpisodeTitle = episode.Name,
                Year = series?.ProductionYear ?? episode.ProductionYear,
                TmdbId = showTmdbId,
                ImdbId = showImdbId,
                TvdbId = showTvdbId,
                Season = episode.ParentIndexNumber,
                Episode = episode.IndexNumber,
                PositionSeconds = positionSeconds,
                DurationSeconds = durationSeconds > 0 ? durationSeconds : null,
                ProgressPercent = progressPercent,
                Progress = progress,
                Watched = watched,
                WatchedThreshold = watchedThreshold,
                ClientVersion = clientVersion,
                DeviceId = serverId,
                DeviceName = e.Session?.DeviceName ?? e.DeviceName,
                PlaybackSessionId = playbackSessionId,
                EventCreatedAt = eventCreatedAtMs,
                ServerName = serverName,
                JellyfinUserId = jellyfinUserId
            };
        }

        _logger.LogDebug("[PunchPlay] Skipping unsupported item type {ItemType} for item {ItemId}", item.GetType().Name, item.Id);
        return null;
    }

    /// <summary>
    /// Builds a stable in-memory session key used for duplicate suppression.
    /// </summary>
    public static string BuildSessionKey(PlaybackProgressEventArgs e)
    {
        var itemId = e.Item?.Id.ToString() ?? "unknown";
        var sessionUserId = e.Session?.UserId.ToString() ?? "unknown-user";
        var sessionId = NormalizeIdentifier(e.Session?.Id);
        if (!string.IsNullOrWhiteSpace(sessionId))
            return $"{sessionUserId}:session:{sessionId}:{itemId}";

        var deviceId = NormalizeIdentifier(e.Session?.DeviceId) ?? NormalizeIdentifier(e.DeviceId);
        if (!string.IsNullOrWhiteSpace(deviceId))
            return $"{sessionUserId}:device:{deviceId}:{itemId}";

        var deviceOrClientName = NormalizeIdentifier(e.Session?.DeviceName)
            ?? NormalizeIdentifier(e.DeviceName)
            ?? NormalizeIdentifier(e.Session?.Client)
            ?? NormalizeIdentifier(e.ClientName)
            ?? "unknown-device";

        return $"{sessionUserId}:name:{deviceOrClientName}:{itemId}";
    }

    /// <summary>
    /// Builds the backend playback session identifier used for watching-now and continue-watching state.
    /// </summary>
    public static string BuildPlaybackSessionId(PlaybackProgressEventArgs e) => BuildSessionKey(e);

    /// <summary>
    /// Returns true if the playback position is far enough through the item to count as watched.
    /// </summary>
    public static bool MeetsWatchedThreshold(int positionSeconds, int durationSeconds)
    {
        if (durationSeconds <= 0)
            return false;

        return (double)positionSeconds / durationSeconds >= WatchedThreshold;
    }

    private static int ToSeconds(long? ticks) =>
        ticks.HasValue && ticks.Value > 0
            ? (int)(ticks.Value / 10_000_000)
            : 0;

    private static int? ParseTmdbId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value, out var id) && id > 0 ? id : null;
    }

    private static int? ParseTvdbId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value, out var id) && id > 0 ? id : null;
    }

    private static string? NormalizeProviderId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeIdentifier(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildEventId(string playbackSessionId, string action, long eventCreatedAtMs, int positionSeconds) =>
        $"{playbackSessionId}:{action}:{eventCreatedAtMs}:{positionSeconds}";
}
