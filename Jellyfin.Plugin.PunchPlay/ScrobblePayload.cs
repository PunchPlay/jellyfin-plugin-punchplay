using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Payload sent to PunchPlay scrobble endpoints.
/// </summary>
public class ScrobblePayload
{
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("episode_title")]
    public string? EpisodeTitle { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("tmdb_id")]
    public int? TmdbId { get; set; }

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("tvdb_id")]
    public int? TvdbId { get; set; }

    [JsonPropertyName("season")]
    public int? Season { get; set; }

    [JsonPropertyName("episode")]
    public int? Episode { get; set; }

    [JsonPropertyName("position_seconds")]
    public int PositionSeconds { get; set; }

    [JsonPropertyName("duration_seconds")]
    public int? DurationSeconds { get; set; }

    [JsonPropertyName("progress_percent")]
    public double? ProgressPercent { get; set; }

    [JsonPropertyName("progress")]
    public double? Progress { get; set; }

    [JsonPropertyName("watched")]
    public bool? Watched { get; set; }

    [JsonPropertyName("watched_threshold")]
    public double? WatchedThreshold { get; set; }

    [JsonPropertyName("client")]
    public string Client { get; set; } = "jellyfin";

    [JsonPropertyName("client_version")]
    public string? ClientVersion { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("device_name")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("playback_session_id")]
    public string? PlaybackSessionId { get; set; }

    [JsonPropertyName("event_created_at")]
    public long? EventCreatedAt { get; set; }

    [JsonPropertyName("server_name")]
    public string? ServerName { get; set; }

    [JsonPropertyName("jellyfin_user_id")]
    public string? JellyfinUserId { get; set; }
}
