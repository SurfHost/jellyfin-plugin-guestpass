using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.Plugin.GuestPass.Configuration;

namespace Jellyfin.Plugin.GuestPass;

/// <summary>
/// GuestPass plugin. Creates expiring guest-share links for Jellyfin items
/// without persisting raw tokens.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "GuestPass";

    /// <inheritdoc />
    public override string Description =>
        "Secure expiring share links for Jellyfin items with guest-user lockdown.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("9c80a3c0-a449-47eb-8a61-56dfd672896b");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "GuestPass",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html"
        }
    };
}
