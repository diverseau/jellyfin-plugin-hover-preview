using System;
using System.IO;
using System.Net.Mime;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.HoverPreview.Services;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.HoverPreview.Api;

/// <summary>Serves the browser client and clip plans.</summary>
[ApiController]
[Route("HoverPreview")]
public class HoverPreviewController : ControllerBase
{
    private static readonly Lazy<string> ClientScript = new(() => ReadResource("Web.hover-preview.js"));
    private static readonly Lazy<string> ClientStyles = new(() => ReadResource("Web.hover-preview.css"));

    private readonly ClipPlanService _clipPlanService;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly INetworkManager _networkManager;

    /// <summary>Initializes a new instance of the <see cref="HoverPreviewController"/> class.</summary>
    public HoverPreviewController(ClipPlanService clipPlanService, IAuthorizationContext authorizationContext, INetworkManager networkManager)
    {
        _clipPlanService = clipPlanService;
        _authorizationContext = authorizationContext;
        _networkManager = networkManager;
    }

    /// <summary>
    /// The browser client with its (non-secret) UI settings prepended. Anonymous because a
    /// &lt;script src&gt; tag cannot send Jellyfin's auth header; it contains no per-user data.
    /// </summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("client.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var config = plugin.Configuration;
        var settings = JsonSerializer.Serialize(new
        {
            enabled = config.Enabled,
            delayMs = config.ClampedDelayMs,
            video = config.EnableVideo,
            clipSeconds = config.ClampedClipSeconds,
            startMuted = config.StartMuted,
            areas = new
            {
                home = config.EnableOnHome,
                libraries = config.EnableOnLibraries,
                search = config.EnableOnSearch,
                details = config.EnableOnDetails,
            },
            version = plugin.CacheToken,
        });

        // JSON from JsonSerializer escapes '<', '>' and '&', so it is safe to embed in a script.
        return Cacheable($"window.HoverPreviewSettings = {settings};\n{ClientScript.Value}", "application/javascript; charset=utf-8");
    }

    /// <summary>The client stylesheet. Anonymous for the same reason as the script.</summary>
    /// <returns>CSS.</returns>
    [HttpGet("client.css")]
    [AllowAnonymous]
    [Produces("text/css")]
    public ActionResult GetClientStyles() => Cacheable(ClientStyles.Value, "text/css; charset=utf-8");

    /// <summary>
    /// Gets the clip plan for a card's item. Respects the user's library access, parental controls,
    /// playback/transcoding permissions and remote bitrate limit; hidden and missing items both 404.
    /// </summary>
    /// <param name="itemId">Item id from the card.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan.</returns>
    [HttpGet("Clips/{itemId}")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClipPlanDto>> GetClips([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || itemId == Guid.Empty)
        {
            return NotFound();
        }

        var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (!auth.IsAuthenticated)
        {
            return Unauthorized();
        }

        // A user token must resolve to a user; only admin API keys may query without one.
        if (auth.User is null && !auth.IsApiKey)
        {
            return Forbid();
        }

        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        var isLocal = remoteIp is not null && _networkManager.IsInLocalNetwork(remoteIp);
        var plan = await _clipPlanService.GetPlanAsync(itemId, auth.User, isLocal, plugin.Configuration, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return NotFound();
        }

        // The client keeps plans for the page's lifetime; the browser must not reuse stale ones after a settings change.
        Response.Headers[HeaderNames.CacheControl] = "private, no-cache";
        return plan;
    }

    private static string ReadResource(string relativeName)
    {
        var assembly = typeof(HoverPreviewController).Assembly;
        var name = typeof(Plugin).Namespace + "." + relativeName;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing embedded resource " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private ActionResult Cacheable(string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes), 0, 8).ToLowerInvariant() + "\"";
        Response.Headers[HeaderNames.ETag] = etag;
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        if (Request.Headers.IfNoneMatch.ToString() == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return File(bytes, contentType);
    }
}
