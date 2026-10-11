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
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.GuestPass.Web;

/// <summary>
/// Injects the GuestPass client script into Jellyfin Web's index.html by
/// rewriting the HTTP response, never the file on disk.
/// <para>
/// Upstream patched index.html on disk at startup. That cannot work on a
/// container whose web root is root-owned while the server runs as a non-root
/// user, which is the normal case for the official image with a PUID, for
/// linuxserver.io images, and for TrueNAS SCALE apps.
/// </para>
/// <para>
/// This wraps the normal static-file response instead of answering on its own,
/// so it composes with other plugins that inject into the same page (MeTube,
/// Jellyfin Enhanced, JavaScript Injector): whichever runs first, the next one
/// sees and patches the already-patched page. Up to 0.2.3 this filter served
/// index.html itself and never called the rest of the pipeline, which hid every
/// other plugin's script. The trap that made it short-circuit is handled
/// directly instead: Jellyfin serves index.html with Cache-Control: no-cache and
/// an ETag that never changes (the file on disk is untouched), so a revalidating
/// browser would get a 304 with no body to patch. The conditional and range
/// request headers are therefore removed before the request continues, and the
/// patched response carries no ETag or Last-Modified, so the browser cannot
/// revalidate its way back to an unpatched copy. Accept-Encoding is removed too,
/// so the body arrives uncompressed.
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

    /// <summary>Inserts the script tag before the last &lt;/body&gt;, unless it is already there.</summary>
    internal static string Patch(string html, string baseUrl)
    {
        // Guard on the script path rather than on the marker comment. A server
        // that was previously patched by hand, or by the upstream plugin, may
        // already carry a different marker around the very same tag.
        if (html.Contains(ScriptPath, StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var snippet = "\n" + Begin
            + "\n<script src=\"" + baseUrl + ScriptPath + "\" defer></script>\n"
            + End + "\n";

        var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyIndex >= 0 ? html.Insert(bodyIndex, snippet) : html + snippet;
    }

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
        if (!IsIndexRequest(context) || Plugin.Instance?.Configuration.InjectionEnabled == false)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var headers = context.Request.Headers;
        headers.Remove(HeaderNames.AcceptEncoding);
        headers.Remove(HeaderNames.Range);
        headers.Remove(HeaderNames.IfRange);
        headers.Remove(HeaderNames.IfNoneMatch);
        headers.Remove(HeaderNames.IfModifiedSince);

        var response = context.Response;
        var originalBody = response.Body;
        var buffer = new MemoryStream();
        await using (buffer.ConfigureAwait(false))
        {
            response.Body = buffer;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                response.Body = originalBody;
            }

            byte[]? patched = null;
            try
            {
                if (response.StatusCode == StatusCodes.Status200OK
                    && (response.ContentType ?? string.Empty).StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrEmpty(response.Headers.ContentEncoding))
                {
                    var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                    patched = Encoding.UTF8.GetBytes(Patch(html, GetBaseUrl()));
                    _status.IndexReadable = true;
                }
                else
                {
                    _status.IndexReadable = false;
                    _status.LastError = FormattableString.Invariant(
                        $"index.html came back as {response.StatusCode} {response.ContentType}, so it was passed through unpatched.");
                }
            }
            catch (Exception ex)
            {
                // This middleware runs OUTSIDE Jellyfin's ExceptionMiddleware, which
                // lives inside app.Map(BaseUrl, ...). An escaping exception here is a
                // raw Kestrel 500 on /web/ with nothing in the Jellyfin log, i.e. the
                // whole web UI down. Serve the page unpatched instead and record why.
                _status.LastError = ex.Message;
                _logger.LogError(ex, "GuestPass: failed to patch index.html, serving it unchanged.");
                patched = null;
            }

            if (patched is null)
            {
                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            // No ETag or Last-Modified on purpose: the file on disk never changes,
            // so either would let a browser revalidate back to an unpatched copy.
            response.Headers.Remove(HeaderNames.ETag);
            response.Headers.Remove(HeaderNames.LastModified);
            response.Headers.CacheControl = "no-cache";
            response.ContentLength = patched.Length;
            await originalBody.WriteAsync(patched, context.RequestAborted).ConfigureAwait(false);

            _status.IncrementServed();
            _status.LastServedUtc = DateTimeOffset.UtcNow;
            _status.LastError = null;
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
        // "/web/", so there is no page to patch on that request.
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
}
