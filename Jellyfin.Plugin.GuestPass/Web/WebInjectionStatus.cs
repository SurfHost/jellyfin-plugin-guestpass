using System;

namespace Jellyfin.Plugin.GuestPass.Web;

/// <summary>
/// Live status of the client-script injection, shared between the startup
/// filter that does the work and the admin API that reports it.
/// <para>
/// This exists because the failure this plugin was written to fix is invisible:
/// upstream swallowed its injection error into a single log warning, so the
/// plugin looked installed and healthy while the button never appeared. Anything
/// that can go wrong here is surfaced on the configuration page instead.
/// </para>
/// </summary>
public sealed class WebInjectionStatus
{
    /// <summary>Gets or sets a value indicating whether the startup filter was registered and ran.</summary>
    public bool FilterInstalled { get; set; }

    /// <summary>Gets or sets the resolved Jellyfin web root.</summary>
    public string? WebPath { get; set; }

    /// <summary>Gets or sets a value indicating whether index.html was found and could be read.</summary>
    public bool IndexReadable { get; set; }

    private long _servedCount;

    /// <summary>Gets the number of times a patched index.html has been served.</summary>
    public long ServedCount => System.Threading.Interlocked.Read(ref _servedCount);

    /// <summary>Atomically records one more served page. Called from concurrent requests.</summary>
    public void IncrementServed() => System.Threading.Interlocked.Increment(ref _servedCount);

    /// <summary>Gets or sets when a patched index.html was last served.</summary>
    public DateTimeOffset? LastServedUtc { get; set; }

    /// <summary>Gets or sets the last error, if any. Null means healthy.</summary>
    public string? LastError { get; set; }
}
