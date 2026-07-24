using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.GuestPass.Configuration;

/// <summary>
/// Plugin configuration persisted by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the plugin is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the default share expiry in hours.</summary>
    public int DefaultExpiryHours { get; set; } = 24;

    /// <summary>Gets or sets the maximum allowed share expiry in hours.</summary>
    public int MaxExpiryHours { get; set; } = 720;

    /// <summary>
    /// Gets or sets an override for the public base URL used when building
    /// absolute share links. Empty means "derive from the incoming request".
    /// </summary>
    public string PublicBaseUrlOverride { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the prefix used when creating guest user names.
    /// </summary>
    public string GuestUsernamePrefix { get; set; } = "guest-";

    /// <summary>Gets or sets a value indicating whether shares may transcode.</summary>
    public bool AllowTranscoding { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether shares may remux.</summary>
    public bool AllowRemuxing { get; set; } = true;

    /// <summary>Gets or sets the cleanup interval, in minutes.</summary>
    public int CleanupIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether links default to one use. Retained
    /// for configuration compatibility; one-use enforcement was removed so that a
    /// link works every time until it expires or is revoked. Defaults to false.
    /// </summary>
    public bool OneUseDefault { get; set; }

    /// <summary>Gets or sets a value indicating whether guest-mode lockdown is enabled.</summary>
    public bool GuestModeLockdownEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a comma-separated list of CSS selectors that are hidden from guest
    /// sessions in the web client. Used to suppress other plugins' injected UI (search
    /// bars, floating buttons) so a guest only sees the shared title. Empty by default.
    /// </summary>
    public string GuestHiddenSelectors { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the client script is injected into
    /// the web client's index.html. Turning this off disables the context-menu
    /// button without uninstalling the plugin, which is the escape hatch if a
    /// future Jellyfin release ever breaks the injection.
    /// </summary>
    public bool InjectionEnabled { get; set; } = true;
}
