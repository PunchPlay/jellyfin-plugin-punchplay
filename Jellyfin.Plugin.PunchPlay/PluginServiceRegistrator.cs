using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Registers plugin services with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient("PunchPlay", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        serviceCollection.AddSingleton<PluginDiagnosticsService>();
        serviceCollection.AddSingleton<ScrobblePayloadFactory>();
        serviceCollection.AddSingleton<PunchPlayTransport>();
        serviceCollection.AddSingleton<PunchPlayAuthService>();
        serviceCollection.AddSingleton<ScrobbleQueueService>();
        serviceCollection.AddSingleton<PunchPlayScrobbleClient>();
        serviceCollection.AddSingleton<DeviceAuthService>();
        serviceCollection.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ScrobbleQueueService>());
        serviceCollection.AddHostedService<PlaybackScrobbler>();
    }
}
