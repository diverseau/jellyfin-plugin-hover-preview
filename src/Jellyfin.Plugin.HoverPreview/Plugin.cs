using System;
using System.Collections.Generic;
using Jellyfin.Plugin.HoverPreview.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.HoverPreview;

/// <summary>Netflix-style hover preview cards for jellyfin-web.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin id. Never change it: Jellyfin keys installs and settings on it.</summary>
    public const string PluginGuid = "4f7ecbe9-9f13-44e5-814d-63e5ad579f8c";

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running instance (Jellyfin creates exactly one).</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Hover Preview";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public override string Description => "Netflix-style pop-out cards with muted preview clips when you hover a card.";

    /// <summary>Gets a token that changes with the plugin version, used to bust browser caches of the client files.</summary>
    public string CacheToken => Version.ToString();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        };
    }
}
