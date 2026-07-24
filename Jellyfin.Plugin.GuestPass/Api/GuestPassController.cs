using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.GuestPass.Configuration;
using Jellyfin.Plugin.GuestPass.Models;
using Jellyfin.Plugin.GuestPass.Services;
using Jellyfin.Plugin.GuestPass.Storage;
using Jellyfin.Plugin.GuestPass.Web;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GuestPass.Api;

/// <summary>Request body for GuestPass admin creation.</summary>
public sealed class ShareLinkCreateRequest
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets an optional expiry in hours.</summary>
    public int? ExpiryHours { get; set; }

    /// <summary>Gets or sets whether the link may be redeemed once only.</summary>
    public bool? OneUse { get; set; }
}

/// <summary>Admin response for a created GuestPass record.</summary>
public sealed class ShareLinkCreateResponse
{
    /// <summary>Gets or sets the raw share URL.</summary>
    public string ShareUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the created record snapshot.</summary>
    public ShareLinkAdminRecordDto Record { get; set; } = new();
}

/// <summary>DTO returned by admin list and revoke endpoints.</summary>
public sealed class ShareLinkAdminRecordDto
{
    public Guid Id { get; set; }

    public string ItemId { get; set; } = string.Empty;

    public string ItemNameSnapshot { get; set; } = string.Empty;

    public string? LibraryId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? RedeemedAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public ShareLinkStatus Status { get; set; }

    public Guid? GuestUserId { get; set; }

    public string? GuestUserName { get; set; }

    public string? AllowedTag { get; set; }

    public bool OneUse { get; set; }

    public bool MetadataTouched { get; set; }

    public int CleanupAttempts { get; set; }

    public string? CleanupError { get; set; }

    public string? ShareUrl { get; set; }
}

/// <summary>Guest session state returned to the web client.</summary>
public sealed class ShareLinkGuestStateDto
{
    public bool IsGuest { get; set; }

    public string? AllowedItemId { get; set; }

    public Guid? ShareId { get; set; }

    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public bool LockdownEnabled { get; set; }

    public string? HiddenSelectors { get; set; }
}

/// <summary>
/// Reports whether the client-script injection is actually working. The whole
/// reason this plugin exists is that upstream's injection failure was invisible,
/// so it is reported here rather than only in the server log.
/// </summary>
public sealed class InjectionStatusDto
{
    /// <summary>Gets or sets a value indicating whether injection is enabled in configuration.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the startup filter was registered and ran.</summary>
    public bool FilterInstalled { get; set; }

    /// <summary>Gets or sets a value indicating whether index.html could be read.</summary>
    public bool IndexReadable { get; set; }

    /// <summary>Gets or sets a value indicating whether everything is working.</summary>
    public bool Healthy { get; set; }

    /// <summary>Gets or sets how many patched pages have been served since startup.</summary>
    public long ServedCount { get; set; }

    /// <summary>Gets or sets when a patched page was last served.</summary>
    public DateTimeOffset? LastServedUtc { get; set; }

    /// <summary>Gets or sets the resolved Jellyfin web root.</summary>
    public string? WebPath { get; set; }

    /// <summary>Gets or sets the last error, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets a human-readable summary for the configuration page.</summary>
    public string? Summary { get; set; }
}

/// <summary>GuestPass API surface.</summary>
[ApiController]
[Route("GuestPass")]
public sealed class GuestPassController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ShareLinkCreationService _creationService;
    private readonly ShareLinkCleanupService _cleanupService;
    private readonly ShareLinkRedemptionService _redemptionService;
    private readonly ShareLinkStore _store;
    private readonly WebInjectionStatus _injectionStatus;
    private readonly ILogger<GuestPassController> _logger;

    /// <summary>Initializes a new instance of the <see cref="GuestPassController"/> class.</summary>
    public GuestPassController(
        ILibraryManager libraryManager,
        ShareLinkCreationService creationService,
        ShareLinkCleanupService cleanupService,
        ShareLinkRedemptionService redemptionService,
        ShareLinkStore store,
        WebInjectionStatus injectionStatus,
        ILogger<GuestPassController> logger)
    {
        _libraryManager = libraryManager;
        _creationService = creationService;
        _cleanupService = cleanupService;
        _redemptionService = redemptionService;
        _store = store;
        _injectionStatus = injectionStatus;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>Serves the client-side GuestPass script.</summary>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    public ActionResult ClientScript()
    {
        SetNoStoreHeaders();

        var assembly = typeof(GuestPassController).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".Web.guestpass.js", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return NotFound();
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Content(reader.ReadToEnd(), "application/javascript; charset=utf-8");
    }

    /// <summary>Creates a new share link for an item.</summary>
    [HttpPost("Admin/Create")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult<ShareLinkCreateResponse>> Create([FromBody] ShareLinkCreateRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var config = Config;
        if (!config.Enabled)
        {
            return StatusCode(503, new { error = "GuestPass is disabled." });
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ItemId))
        {
            _logger.LogWarning("GuestPass: create rejected, missing itemId.");
            return BadRequest(new { error = "Missing itemId." });
        }

        if (!Guid.TryParse(request.ItemId!.Trim(), out var itemId))
        {
            _logger.LogWarning("GuestPass: create rejected, itemId {ItemId} is not a GUID.", request.ItemId);
            return BadRequest(new { error = "Invalid itemId." });
        }

        var expiryHours = request.ExpiryHours ?? config.DefaultExpiryHours;
        if (expiryHours <= 0)
        {
            return BadRequest(new { error = "Expiry must be positive." });
        }

        var effectiveMaxExpiryHours = Math.Max(config.MaxExpiryHours, 720);
        if (expiryHours > effectiveMaxExpiryHours)
        {
            return BadRequest(new { error = $"Expiry exceeds the configured maximum of {effectiveMaxExpiryHours} hours." });
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogWarning("GuestPass: create rejected, item {ItemId} not found.", itemId);
            return NotFound(new { error = "Item not found." });
        }

        if (item.IsFolder && item is not Series && item is not Season)
        {
            _logger.LogWarning("GuestPass: create rejected, item {ItemId} \"{ItemName}\" is a folder or library, not shareable media.", itemId, item.Name);
            return BadRequest(new { error = "Only a movie, episode, series or season can be shared, not a library or collection. Open the title's page and try again." });
        }

        try
        {
            var creatorUserId = GetCurrentUserId();
            var oneUse = request.OneUse ?? config.OneUseDefault;
            var creation = await _creationService.CreateAsync(item, creatorUserId, expiryHours, oneUse, cancellationToken).ConfigureAwait(false);
            var shareUrl = BuildShareUrl(Request, creation.RawToken);
            creation.Record.ShareUrl = shareUrl;
            await _store.UpdateAsync(creation.Record, cancellationToken).ConfigureAwait(false);
            return Ok(new ShareLinkCreateResponse
            {
                ShareUrl = shareUrl,
                Record = ToDto(creation.Record)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GuestPass: create failed for item {ItemId}.", itemId);
            return StatusCode(500, new { error = "Failed to create share link." });
        }
    }

    /// <summary>Lists all share links for administrators.</summary>
    [HttpGet("Admin/List")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult<IEnumerable<ShareLinkAdminRecordDto>>> List(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(records.Select(ToDto).ToArray());
    }

    /// <summary>Revokes a share link and triggers cleanup.</summary>
    [HttpPost("Admin/Revoke/{id:guid}")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult<ShareLinkAdminRecordDto>> Revoke(Guid id, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var record = await _cleanupService.RevokeAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(new { error = "Share link not found." });
        }

        return Ok(ToDto(record));
    }

    /// <summary>Revokes a share link and then permanently removes its record.</summary>
    [HttpPost("Admin/Delete/{id:guid}")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var deleted = await _cleanupService.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return NotFound(new { error = "Share link not found." });
        }

        return Ok(new { deleted = true });
    }

    /// <summary>Reports whether the client-script injection is working.</summary>
    [HttpGet("Admin/InjectionStatus")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public ActionResult<InjectionStatusDto> InjectionStatus()
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var enabled = Config.InjectionEnabled;
        var healthy = enabled
            && _injectionStatus.FilterInstalled
            && _injectionStatus.LastError is null
            && _injectionStatus.ServedCount > 0;

        string summary;
        if (!enabled)
        {
            summary = "Injection is turned off in the settings below, so the GuestPass entry will not appear in the context menu.";
        }
        else if (!_injectionStatus.FilterInstalled)
        {
            summary = "The injection middleware is not installed. Restart Jellyfin: it is only wired up when the server process starts.";
        }
        else if (_injectionStatus.LastError is not null)
        {
            summary = "Injection failed: " + _injectionStatus.LastError;
        }
        else if (_injectionStatus.ServedCount == 0)
        {
            summary = "Ready, but no page has been served yet. Reload the Jellyfin web client once, then refresh this page.";
        }
        else
        {
            summary = "Working. The client script is being injected into the web client in memory; index.html on disk is untouched.";
        }

        return Ok(new InjectionStatusDto
        {
            Enabled = enabled,
            FilterInstalled = _injectionStatus.FilterInstalled,
            IndexReadable = _injectionStatus.IndexReadable,
            Healthy = healthy,
            ServedCount = _injectionStatus.ServedCount,
            LastServedUtc = _injectionStatus.LastServedUtc,
            WebPath = _injectionStatus.WebPath,
            LastError = _injectionStatus.LastError,
            Summary = summary
        });
    }

    /// <summary>Returns the guest session state for the current authenticated user.</summary>
    [HttpGet("GuestState")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult<ShareLinkGuestStateDto>> GuestState(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        var config = Config;
        var currentUserId = GetCurrentUserId();
        var currentUserName = GetCurrentUserName();

        if (currentUserId != Guid.Empty || !string.IsNullOrWhiteSpace(currentUserName))
        {
            var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            var match = records.FirstOrDefault(record =>
                !IsExpired(record) &&
                IsGuestSessionStatus(record.Status) &&
                (
                    (currentUserId != Guid.Empty && record.GuestUserId.HasValue && record.GuestUserId.Value == currentUserId) ||
                    (!string.IsNullOrWhiteSpace(currentUserName) &&
                     !string.IsNullOrWhiteSpace(record.GuestUserName) &&
                     string.Equals(record.GuestUserName, currentUserName, StringComparison.OrdinalIgnoreCase))
                ));

            if (match is not null)
            {
                return Ok(new ShareLinkGuestStateDto
                {
                    IsGuest = true,
                    AllowedItemId = match.ItemId,
                    ShareId = match.Id,
                    ExpiresAtUtc = match.ExpiresAtUtc,
                    LockdownEnabled = config.GuestModeLockdownEnabled,
                    HiddenSelectors = config.GuestHiddenSelectors
                });
            }
        }

        return Ok(new ShareLinkGuestStateDto
        {
            IsGuest = false,
            LockdownEnabled = config.GuestModeLockdownEnabled,
            HiddenSelectors = config.GuestHiddenSelectors
        });
    }

    /// <summary>Redeems a share link token and returns the bootstrap login page.</summary>
    [HttpGet("Redeem")]
    [AllowAnonymous]
    public async Task<ActionResult> Redeem([FromQuery(Name = "t")] string? token, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (string.IsNullOrWhiteSpace(token))
        {
            return LinkUnavailablePage(Request);
        }

        var html = await _redemptionService.RedeemAsync(token, Request, cancellationToken).ConfigureAwait(false);
        if (html is null)
        {
            return LinkUnavailablePage(Request);
        }

        return Content(html, "text/html; charset=utf-8");
    }

    private static ContentResult LinkUnavailablePage(HttpRequest request)
    {
        var pathBase = request.PathBase.Value ?? string.Empty;
        var redirectUrl = $"{pathBase}/web/";

        var redirectUrlJson = JsonSerializer.Serialize(redirectUrl);
        var redirectUrlHtml = System.Net.WebUtility.HtmlEncode(redirectUrl);

        var html = $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta http-equiv="refresh" content="6;url={{redirectUrlHtml}}">
  <title>Link unavailable</title>
  <style>
    body { font-family: system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center; background: #111827; color: #e5e7eb; }
    main { max-width: 36rem; padding: 2rem; }
    .muted { color: #9ca3af; }
    a { color: #60a5fa; }
  </style>
</head>
<body>
<main>
  <div>This share link is no longer valid.</div>
  <div class="muted">Taking you to the home page...</div>
  <p><a href="{{redirectUrlHtml}}">Open Jellyfin</a></p>
</main>
<script>
setTimeout(function () { window.location.replace({{redirectUrlJson}}); }, 4000);
</script>
</body>
</html>
""";

        return new ContentResult
        {
            StatusCode = StatusCodes.Status404NotFound,
            ContentType = "text/html; charset=utf-8",
            Content = html
        };
    }

    private static ShareLinkAdminRecordDto ToDto(ShareLinkRecord record)
    {
        return new ShareLinkAdminRecordDto
        {
            Id = record.Id,
            ItemId = record.ItemId,
            ItemNameSnapshot = record.ItemNameSnapshot,
            LibraryId = record.LibraryId,
            CreatedByUserId = record.CreatedByUserId,
            CreatedAtUtc = record.CreatedAtUtc,
            RedeemedAtUtc = record.RedeemedAtUtc,
            ExpiresAtUtc = record.ExpiresAtUtc,
            Status = record.Status,
            GuestUserId = record.GuestUserId,
            GuestUserName = record.GuestUserName,
            AllowedTag = record.AllowedTag,
            OneUse = record.OneUse,
            MetadataTouched = record.MetadataTouched,
            CleanupAttempts = record.CleanupAttempts,
            CleanupError = record.CleanupError,
            ShareUrl = record.ShareUrl
        };
    }

    private Guid GetCurrentUserId()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
    }

    private string? GetCurrentUserName()
    {
        return User.FindFirst("Jellyfin-UserName")?.Value
            ?? User.FindFirst(ClaimTypes.Name)?.Value
            ?? User.Identity?.Name;
    }

    private static bool IsExpired(ShareLinkRecord record)
    {
        return record.ExpiresAtUtc <= DateTimeOffset.UtcNow;
    }

    private static bool IsGuestSessionStatus(ShareLinkStatus status)
    {
        return status is ShareLinkStatus.Active or ShareLinkStatus.Redeeming or ShareLinkStatus.Redeemed;
    }

    private void SetNoStoreHeaders()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
    }

    private static string BuildShareUrl(Microsoft.AspNetCore.Http.HttpRequest request, string rawToken)
    {
        var config = Config;
        var baseUrl = string.IsNullOrWhiteSpace(config.PublicBaseUrlOverride)
            ? $"{request.Scheme}://{request.Host}{request.PathBase}"
            : config.PublicBaseUrlOverride.TrimEnd('/');

        return $"{baseUrl.TrimEnd('/')}/GuestPass/Redeem?t={Uri.EscapeDataString(rawToken)}";
    }
}
