using System;
using System.Net;
using System.Net.Http;
using Jellyfin.Plugin.AniWorld.Extractors;
using Jellyfin.Plugin.AniWorld.Helpers;
using Jellyfin.Plugin.AniWorld.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld;

/// <summary>
/// Registers plugin services with the DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    private const int HttpClientTimeoutSeconds = 50;

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient("AniWorld", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler);
        serviceCollection.AddHttpClient("STO", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler);
        serviceCollection.AddHttpClient("Filmo", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler);
        serviceCollection.AddHttpClient("FilmPalast", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler);
        serviceCollection.AddHttpClient("MegaKino", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler);
        // Moflix's JSON API sits behind Cloudflare and fingerprints non-browser TLS
        // clients, so it is served through curl-impersonate (a real Chrome
        // fingerprint) when the native library is available; otherwise it falls
        // back to the regular handler like the other providers.
        serviceCollection.AddHttpClient("Moflix", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureMoflixHandler);
        serviceCollection.AddSingleton<AniWorldService>();
        serviceCollection.AddSingleton<StoService>();
        serviceCollection.AddSingleton<FilmoService>();
        serviceCollection.AddSingleton<FilmPalastService>();
        serviceCollection.AddSingleton<MegaKinoService>();
        serviceCollection.AddSingleton<MoflixService>();
        serviceCollection.AddSingleton<DownloadHistoryService>();
        serviceCollection.AddSingleton<DownloadService>();
        serviceCollection.AddSingleton<IStreamExtractor, VoeExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, VidozaExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, VidmolyExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, FilemoonExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, MegaKinoExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, MoflixClickExtractor>();
    }

    private static HttpMessageHandler ConfigureMoflixHandler(IServiceProvider services)
    {
        var proxyUrl = Plugin.Instance?.Configuration?.ProxyUrl;
        var logger = services.GetService<ILogger<CurlImpersonateHandler>>();
        var impersonate = CurlImpersonateHandler.TryCreate(proxyUrl, logger);
        if (impersonate != null)
        {
            return impersonate;
        }

        return ConfigureHandler(services);
    }

    private static HttpMessageHandler ConfigureHandler(IServiceProvider _)
    {
        var proxyUrl = Plugin.Instance?.Configuration?.ProxyUrl;
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            var proxyUri = new Uri(proxyUrl);
            var isSocks = proxyUri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase);

            if (isSocks)
            {
                return new SocketsHttpHandler
                {
                    Proxy = new WebProxy(proxyUri),
                    UseProxy = true,
                };
            }

            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxyUri),
                UseProxy = true,
            };
            return handler;
        }

        return new HttpClientHandler();
    }
}
