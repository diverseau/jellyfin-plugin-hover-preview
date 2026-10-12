using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HoverPreview.Injection;

/// <summary>Shared state between the two injection paths.</summary>
internal static class InjectionState
{
    private static int _viaFileTransformation;

    /// <summary>Gets or sets a value indicating whether File Transformation owns index.html injection (the fallback middleware then stands down).</summary>
    public static bool ViaFileTransformation
    {
        get => Volatile.Read(ref _viaFileTransformation) == 1;
        set => Volatile.Write(ref _viaFileTransformation, value ? 1 : 0);
    }
}

/// <summary>Payload File Transformation hands to the callback (deserialized by File Transformation via Newtonsoft).</summary>
public sealed class TransformationPayload
{
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}

/// <summary>Callback target File Transformation invokes by reflection for every index.html it serves.</summary>
public static class TransformationCallbacks
{
    /// <summary>Adds the client tags to index.html.</summary>
    /// <param name="payload">The original page.</param>
    /// <returns>The page with tags added.</returns>
    public static string IndexHtml(TransformationPayload payload)
    {
        var html = payload.Contents ?? string.Empty;
        var plugin = Plugin.Instance;
        if (plugin is null || !plugin.Configuration.Enabled)
        {
            return html;
        }

        return IndexHtmlInjector.Inject(html, FileTransformationRegistrar.BaseUrl, plugin.CacheToken);
    }
}

/// <summary>
/// On startup, registers an index.html transformation with the File Transformation plugin when it is
/// installed. File Transformation is called by reflection only: it lives in its own load context and
/// may be absent, in which case <see cref="FallbackInjectionMiddleware"/> injects the tags instead.
/// </summary>
public sealed class FileTransformationRegistrar : IHostedService
{
    private static readonly Guid TransformationId = Guid.Parse("8b0d1f3e-5c2a-4e7b-9a61-2f4c8d9e7a10");

    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<FileTransformationRegistrar> _logger;

    /// <summary>Initializes a new instance of the <see cref="FileTransformationRegistrar"/> class.</summary>
    public FileTransformationRegistrar(IServerConfigurationManager configurationManager, ILogger<FileTransformationRegistrar> logger)
    {
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <summary>Gets the server's base URL, captured at startup for the static callback.</summary>
    internal static string BaseUrl { get; private set; } = string.Empty;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        BaseUrl = _configurationManager.GetNetworkConfiguration().BaseUrl ?? string.Empty;

        try
        {
            InjectionState.ViaFileTransformation = TryRegister();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hover Preview: File Transformation registration failed, using built-in injection");
            InjectionState.ViaFileTransformation = false;
        }

        _logger.LogInformation(
            "Hover Preview: client script injected via {Method}",
            InjectionState.ViaFileTransformation ? "File Transformation" : "built-in middleware");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool TryRegister()
    {
        var assembly = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(a => a.GetName().Name == "Jellyfin.Plugin.FileTransformation");
        var registerMethod = assembly?
            .GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")?
            .GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
        var payloadType = registerMethod?.GetParameters().FirstOrDefault()?.ParameterType;
        var parse = payloadType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
        if (registerMethod is null || parse is null)
        {
            return false;
        }

        // Build File Transformation's own JObject type so we don't need Newtonsoft ourselves.
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = TransformationId,
            // Must be exactly "index.html": File Transformation keys pipelines by this string and, for
            // index.html, only runs the pipeline whose key matches the file name exactly. A regex like
            // "index\\.html$" is silently skipped as soon as any other plugin registers "index.html".
            fileNamePattern = "index.html",
            callbackAssembly = typeof(TransformationCallbacks).Assembly.FullName,
            callbackClass = typeof(TransformationCallbacks).FullName,
            callbackMethod = nameof(TransformationCallbacks.IndexHtml),
        });
        var payload = parse.Invoke(null, new object[] { json });
        registerMethod.Invoke(null, new[] { payload });
        return true;
    }
}
