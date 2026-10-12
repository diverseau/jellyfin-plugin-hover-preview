using Jellyfin.Plugin.HoverPreview.Injection;
using Jellyfin.Plugin.HoverPreview.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.HoverPreview;

/// <summary>Registers the plugin's services with Jellyfin's container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ClipPlanService>();
        serviceCollection.AddHostedService<FileTransformationRegistrar>();
        serviceCollection.AddTransient<IStartupFilter, FallbackInjectionStartupFilter>();
    }
}
