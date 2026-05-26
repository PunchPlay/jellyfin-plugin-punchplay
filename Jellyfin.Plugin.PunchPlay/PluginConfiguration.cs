using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Per-user PunchPlay token, keyed by Jellyfin user ID in <see cref="PluginConfiguration.UserTokens"/>.
/// </summary>
public class UserToken
{
    /// <summary>Jellyfin user ID (GUID string) this token belongs to.</summary>
    public string JellyfinUserId { get; set; } = string.Empty;

    /// <summary>Bearer token obtained via device auth flow.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>PunchPlay username shown in settings after login.</summary>
    public string PunchPlayUsername { get; set; } = string.Empty;

    /// <summary>UTC timestamp of when the token was issued.</summary>
    public DateTime? ConnectedAt { get; set; }
}

/// <summary>
/// Persisted plugin configuration (stored as XML by Jellyfin).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Per-user tokens keyed by Jellyfin user ID.</summary>
    public List<UserToken> UserTokens { get; set; } = new();

    /// <summary>Base URL of the PunchPlay API (allows self-hosted overrides).</summary>
    public string PunchPlayUrl { get; set; } = "https://punchplay.tv";

    /// <summary>Progress heartbeat interval in seconds. Lower = more frequent API calls.</summary>
    public int ProgressIntervalSeconds { get; set; } = 15;

    /// <summary>Stable device ID sent to PunchPlay to identify this server.</summary>
    public string ServerId { get; set; } = string.Empty;
}
