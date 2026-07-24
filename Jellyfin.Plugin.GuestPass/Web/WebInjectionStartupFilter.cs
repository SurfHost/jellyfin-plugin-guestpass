using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GuestPass.Web;

/// <summary>
/// Injects the GuestPass client script into Jellyfin Web's index.html by
/// rewriting the HTTP response, never the file on disk.
/// <para>
/// Upstream patched index.html on disk at startup. That cannot work on a
/// container whose web root is root-owned while the server runs as a non-root
/// user, which is the normal case for the official image with a PUID, for
/// linuxserver.io images, and for TrueNAS SCALE apps. Reading the web root
/// always works, because the server is already serving those files, so this
/// reads index.html, inserts the script tag, and writes the result itself.
/// </para>
/// <para>
/// Deliberately short-circuits rather than wrapping the downstream response.
/// Wrapping would inherit a real bug: Jellyfin serves index.html with
/// Cache-Control: no-cache, so browsers always revalidate, and the ETag never
/// changes because the file on disk is untouched. A revalidating browser would
/// get a 304 passed straight through and would never see the script.
/// </para>
/// </summary>
public sealed class WebInjectionStartupFilter : IStartupFilter
{
    private const string ScriptPath = "/GuestPass/ClientScript";
    private const string Begin = "<!-- GuestPass:begin -->";
    private const string End = "<!-- GuestPass:end -->";

    private readonly IServerApplicationPaths _paths;
    private readonly IServerConfigurationManager _config;
    private readonly WebInjectionStatus _status;
    private readonly ILogger<WebInjectionStartupFilter> _logger;

    private CachedPage? _cache;

    /// <summary>Initializes a new instance of the <see cref="WebInjectionStartupFilter"/> class.</summary>
    public WebInjectionStartupFilter(
        IServerApplicationPaths paths,
        IServerConfigurationManager config,
        WebInjectionStatus status,
        ILogger<WebInjectionStartupFilter> logger)
    {
        _paths = paths;
        _config = config;
        _status = status;
        _logger = logger;
    }

    private string IndexPath => Path.Combine(_paths.WebPath, "index.html");

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        _status.FilterInstalled = true;
        _status.WebPath = _paths.WebPath;
        _logger.LogInformation(
            "GuestPass: client script injection active, serving from {Path}.",
            _paths.WebPath);

        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            if (!IsIndexRequest(context))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (Plugin.Instance?.Configuration.InjectionEnabled == false)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var body = RenderIndex();
            if (body is null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = body.Length;

            // Jellyfin normally sets this in StaticFileOptions.OnPrepareResponse,
            // which we are bypassing entirely. No ETag is emitted on purpose: the
            // file on disk never changes, so a stable ETag would let a browser
            // revalidate its way back to an unpatched cached copy.
            context.Response.Headers.CacheControl = "no-cache";

            await context.Response.Body.WriteAsync(body).ConfigureAwait(false);

            _status.ServedCount++;
            _status.LastServedUtc = DateTimeOffset.UtcNow;
            _status.LastError = null;

            // Deliberately does not call next(): this short-circuits ahead of
            // UseDefaultFiles and UseStaticFiles.
        }
        catch (Exception ex)
        {
            // This middleware runs OUTSIDE Jellyfin's ExceptionMiddleware, which
            // lives inside app.Map(BaseUrl, ...). An escaping exception here is a
            // raw Kestrel 500 on /web/ with nothing in the Jellyfin log, i.e. the
            // whole web UI down. Never let that happen: fall through to the normal
            // static file pipeline and record why.
            _status.LastError = ex.Message;
            _logger.LogError(ex, "GuestPass: failed to serve a patched index.html, falling back to the static file.");

            if (!context.Response.HasStarted)
            {
                await next(context).ConfigureAwait(false);
            }
        }
    }

    private bool IsIndexRequest(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        // This filter wraps the entire pipeline, so it sits outside
        // app.Map(BaseUrl, ...) and Request.Path still carries any configured
        // base URL prefix. It also runs before UseDefaultFiles, so "/web/" is
        // never rewritten to "/web/index.html" for us and both must be matched.
        //
        // Bare "/web" is deliberately NOT matched: Jellyfin 301-redirects it to
        // "/web/", and index.html references its bundles with relative paths, so
        // serving the SPA at "/web" would resolve every script and stylesheet one
        // directory too high and 404 them all.
        var baseUrl = GetBaseUrl();
        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return string.Equals(path, baseUrl + "/web/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, baseUrl + "/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    private string GetBaseUrl()
    {
        try
        {
            return (_config.GetNetworkConfiguration().BaseUrl ?? string.Empty).TrimEnd('/');
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Returns the patched page, or null if index.html cannot be read at all, in
    /// which case the caller falls back to the normal static file pipeline.
    /// </summary>
    private byte[]? RenderIndex()
    {
        var path = IndexPath;
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            _status.IndexReadable = false;
            _status.LastError = "index.html not found at " + path;
            return null;
        }

        var baseUrl = GetBaseUrl();

        // Re-render only when the web client actually changed on disk, which in
        // practice means a Jellyfin upgrade, or when the base URL changed.
        var cached = _cache;
        if (cached is not null
            && cached.LastWriteUtc == info.LastWriteTimeUtc
            && cached.Length == info.Length
            && string.Equals(cached.BaseUrl, baseUrl, StringComparison.Ordinal))
        {
            return cached.Body;
        }

        var html = File.ReadAllText(path);
        _status.IndexReadable = true;

        // Guard on the script path rather than on the marker comment. A server
        // that was previously patched by hand, or by the upstream plugin, may
        // already carry a different marker around the very same tag.
        if (html.IndexOf(ScriptPath, StringComparison.OrdinalIgnoreCase) < 0)
        {
            var snippet = "\n" + Begin
                + "\n<script src=\"" + baseUrl + ScriptPath + "\" defer></script>\n"
                + End + "\n";

            var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = bodyIndex >= 0 ? html.Insert(bodyIndex, snippet) : html + snippet;
        }

        var body = Encoding.UTF8.GetBytes(html);
        _cache = new CachedPage(info.LastWriteTimeUtc, info.Length, baseUrl, body);
        return body;
    }

    private sealed record CachedPage(DateTime LastWriteUtc, long Length, string BaseUrl, byte[] Body);
}
