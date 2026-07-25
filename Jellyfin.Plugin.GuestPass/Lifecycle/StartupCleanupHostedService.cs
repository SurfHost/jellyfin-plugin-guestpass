using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.GuestPass.Services;
using Jellyfin.Plugin.GuestPass.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GuestPass.Lifecycle;

/// <summary>
/// Runs one cleanup pass at startup so stale records do not linger forever.
/// </summary>
public sealed class StartupCleanupHostedService : BackgroundService
{
    private readonly IShareLinkCleanupService _cleanupService;
    private readonly ShareLinkStore _store;
    private readonly ILogger<StartupCleanupHostedService> _logger;

    /// <summary>Initializes a new instance of the <see cref="StartupCleanupHostedService"/> class.</summary>
    public StartupCleanupHostedService(
        IShareLinkCleanupService cleanupService,
        ShareLinkStore store,
        ILogger<StartupCleanupHostedService> logger)
    {
        _cleanupService = cleanupService;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Before anything else, drop raw share tokens left on disk by v0.2.2 and earlier.
        // Kept separate from cleanup so a cleanup failure cannot leave them in place.
        try
        {
            await _store.ScrubLegacyShareUrlsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GuestPass: could not scrub legacy share URLs from the store.");
        }

        try
        {
            await _cleanupService.CleanupAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GuestPass: startup cleanup failed.");
        }
    }
}
