using Jellyfin.Plugin.GuestPass.Lifecycle;
using Jellyfin.Plugin.GuestPass.Services;
using Jellyfin.Plugin.GuestPass.Storage;
using Jellyfin.Plugin.GuestPass.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
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

        // Injects the client script by rewriting the index.html RESPONSE, so no
        // write access to the web root is needed. Must be Singleton: startup
        // filters are resolved from the root provider, and a Scoped registration
        // throws under scope validation.
        serviceCollection.AddSingleton<WebInjectionStatus>();
        serviceCollection.AddSingleton<IStartupFilter, WebInjectionStartupFilter>();

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
