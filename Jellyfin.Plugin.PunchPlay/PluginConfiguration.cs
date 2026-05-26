using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Persisted plugin configuration (stored as XML by Jellyfin).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Bearer token obtained via device auth flow.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>PunchPlay username shown in settings after login.</summary>
    public string PunchPlayUsername { get; set; } = string.Empty;

    /// <summary>UTC timestamp of when the token was issued.</summary>
    public DateTime? ConnectedAt { get; set; }

    /// <summary>Base URL of the PunchPlay API (allows self-hosted overrides).</summary>
    public string PunchPlayUrl { get; set; } = "https://punchplay.tv";

    /// <summary>Progress heartbeat interval in seconds. Lower = more frequent API calls.</summary>
    public int ProgressIntervalSeconds { get; set; } = 15;

    /// <summary>Stable device ID sent to PunchPlay to identify this server.</summary>
    public string ServerId { get; set; } = string.Empty;
}
