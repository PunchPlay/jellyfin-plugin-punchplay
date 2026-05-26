using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// PunchPlay Jellyfin plugin — scrobbles playback to punchplay.tv.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly IApplicationPaths _applicationPaths;

    public static readonly Guid PluginId = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        _applicationPaths = applicationPaths;
        Instance = this;
        Directory.CreateDirectory(StateDirectoryPath);
    }

    /// <inheritdoc />
    public override string Name => "PunchPlay";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Scrobble playback to PunchPlay — track what you watch.";

    /// <summary>Singleton accessor used by controllers and services.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = "PunchPlay",
                EmbeddedResourcePath = $"{prefix}.Configuration.configPage.html",
                EnableInMainMenu = false
            }
        };
    }

    /// <summary>Base URL without trailing slash.</summary>
    public string ApiBase =>
        Configuration.PunchPlayUrl.TrimEnd('/');

    /// <summary>Stable plugin version reported to PunchPlay.</summary>
    public string ClientVersion =>
        GetType().Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>Plugin-owned state directory for queue and other local state.</summary>
    public string StateDirectoryPath =>
        Path.Combine(_applicationPaths.PluginConfigurationsPath, "PunchPlay");

    /// <summary>Queue file path used for persisted transient scrobble retries.</summary>
    public string QueueFilePath =>
        Path.Combine(StateDirectoryPath, "scrobble-queue.json");

    /// <summary>Returns a friendly server name for device auth and diagnostics.</summary>
    public string FriendlyServerName
    {
        get
        {
            try
            {
                return $"Jellyfin ({System.Net.Dns.GetHostName()})";
            }
            catch
            {
                return "Jellyfin";
            }
        }
    }

    /// <summary>Ensures the plugin has a stable PunchPlay server ID and returns it.</summary>
    public string EnsureServerId()
    {
        var cfg = Configuration;
        if (!string.IsNullOrWhiteSpace(cfg.ServerId))
            return cfg.ServerId;

        cfg.ServerId = Guid.NewGuid().ToString("N");
        SaveConfiguration(cfg);
        return cfg.ServerId;
    }

    /// <summary>Returns the access token for the given Jellyfin user ID, or null if not linked.</summary>
    public string? GetUserToken(string jellyfinUserId)
    {
        var token = Configuration.UserTokens
            .FirstOrDefault(t => string.Equals(t.JellyfinUserId, jellyfinUserId, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(token?.AccessToken) ? null : token.AccessToken;
    }

    /// <summary>Stores or replaces the token for a Jellyfin user.</summary>
    public void SetUserToken(string jellyfinUserId, string accessToken, string punchPlayUsername)
    {
        var cfg = Configuration;
        var existing = cfg.UserTokens
            .FirstOrDefault(t => string.Equals(t.JellyfinUserId, jellyfinUserId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.AccessToken = accessToken;
            existing.PunchPlayUsername = punchPlayUsername;
            existing.ConnectedAt = DateTime.UtcNow;
        }
        else
        {
            cfg.UserTokens.Add(new UserToken
            {
                JellyfinUserId = jellyfinUserId,
                AccessToken = accessToken,
                PunchPlayUsername = punchPlayUsername,
                ConnectedAt = DateTime.UtcNow
            });
        }

        SaveConfiguration(cfg);
    }

    /// <summary>Removes the token for a Jellyfin user.</summary>
    public void ClearUserToken(string jellyfinUserId)
    {
        var cfg = Configuration;
        cfg.UserTokens.RemoveAll(t =>
            string.Equals(t.JellyfinUserId, jellyfinUserId, StringComparison.OrdinalIgnoreCase));
        SaveConfiguration(cfg);
    }

    /// <summary>Returns the stored <see cref="UserToken"/> for a Jellyfin user, or null.</summary>
    public UserToken? GetUserTokenRecord(string jellyfinUserId) =>
        Configuration.UserTokens
            .FirstOrDefault(t => string.Equals(t.JellyfinUserId, jellyfinUserId, StringComparison.OrdinalIgnoreCase));
}
