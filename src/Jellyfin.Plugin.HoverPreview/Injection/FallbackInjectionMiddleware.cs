using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.HoverPreview.Injection;

/// <summary>Puts <see cref="FallbackInjectionMiddleware"/> at the front of the request pipeline.</summary>
public sealed class FallbackInjectionStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<FallbackInjectionMiddleware>();
        next(app);
    };
}

/// <summary>
/// Built-in injection used only when File Transformation is not installed: rewrites jellyfin-web's
/// index.html in memory as it is served. Nothing on disk is touched.
/// </summary>
public sealed class FallbackInjectionMiddleware
{
    private const int MaxPageBytes = 4 * 1024 * 1024;
    private readonly RequestDelegate _next;

    /// <summary>Initializes a new instance of the <see cref="FallbackInjectionMiddleware"/> class.</summary>
    /// <param name="next">Next middleware.</param>
    public FallbackInjectionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>Handles a request.</summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var plugin = Plugin.Instance;
        if (plugin is null
            || !plugin.Configuration.Enabled
            || InjectionState.ViaFileTransformation
            || !HttpMethods.IsGet(context.Request.Method)
            || !IsIndexPath(context.Request.Path.Value))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask for an uncompressed, unconditional response we can edit.
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);
        context.Request.Headers.Remove(HeaderNames.IfNoneMatch);
        context.Request.Headers.Remove(HeaderNames.IfModifiedSince);

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var isHtml = context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ?? false;
        var encoded = context.Response.Headers.ContainsKey(HeaderNames.ContentEncoding);
        if (context.Response.StatusCode != StatusCodes.Status200OK || !isHtml || encoded || buffer.Length > MaxPageBytes)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        var bytes = Encoding.UTF8.GetBytes(IndexHtmlInjector.Inject(html, FileTransformationRegistrar.BaseUrl, plugin.CacheToken));

        context.Response.Headers.Remove(HeaderNames.ETag);
        context.Response.Headers.Remove(HeaderNames.LastModified);
        context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
        context.Response.ContentLength = bytes.Length;
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>Matches /web, /web/, /web/index.html (optionally under a base URL).</summary>
    internal static bool IsIndexPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }
}
