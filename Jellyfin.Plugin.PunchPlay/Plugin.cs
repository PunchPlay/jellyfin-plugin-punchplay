using System.Reflection;
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
    public static readonly Guid PluginId = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
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

    /// <summary>Convenience accessor for the saved token.</summary>
    public string? AccessToken =>
        string.IsNullOrWhiteSpace(Configuration.AccessToken) ? null : Configuration.AccessToken;

    /// <summary>Base URL without trailing slash.</summary>
    public string ApiBase =>
        Configuration.PunchPlayUrl.TrimEnd('/');
}
