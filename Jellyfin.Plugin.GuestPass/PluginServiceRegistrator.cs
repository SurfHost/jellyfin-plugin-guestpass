using Jellyfin.Plugin.GuestPass.Lifecycle;
using Jellyfin.Plugin.GuestPass.Services;
using Jellyfin.Plugin.GuestPass.Storage;
using Jellyfin.Plugin.GuestPass.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.GuestPass;

/// <summary>
/// Registers the foundational GuestPass services used by later API and web
/// workers.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;

        serviceCollection.AddHostedService<WebInjectionHostedService>();
        serviceCollection.AddSingleton<ShareLinkStore>();
        serviceCollection.AddSingleton<ShareTokenService>();
        serviceCollection.AddSingleton<ItemTagService>();
        serviceCollection.AddSingleton<JellyfinGuestUserService>();
        serviceCollection.AddSingleton<ShareLinkCreationService>();
        serviceCollection.AddSingleton<ShareLinkRedemptionService>();
        serviceCollection.AddSingleton<ShareLinkCleanupService>();
        serviceCollection.AddSingleton<IShareLinkCleanupService>(provider => provider.GetRequiredService<ShareLinkCleanupService>());
        serviceCollection.AddHostedService<StartupCleanupHostedService>();
    }
}
