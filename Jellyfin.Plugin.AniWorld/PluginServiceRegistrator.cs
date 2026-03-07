using Jellyfin.Plugin.AniWorld.Extractors;
using Jellyfin.Plugin.AniWorld.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AniWorld;

/// <summary>
/// Registers plugin services with the DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient("AniWorld");
        serviceCollection.AddHttpClient("STO");
        serviceCollection.AddHttpClient("HiAnime");
        serviceCollection.AddSingleton<AniWorldService>();
        serviceCollection.AddSingleton<StoService>();
        serviceCollection.AddSingleton<HiAnimeService>();
        serviceCollection.AddSingleton<DownloadHistoryService>();
        serviceCollection.AddSingleton<DownloadService>();
        serviceCollection.AddSingleton<IStreamExtractor, VoeExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, VidozaExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, VidmolyExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, FilemoonExtractor>();
    }
}
