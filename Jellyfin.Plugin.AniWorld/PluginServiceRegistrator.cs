using System;
using System.Net;
using System.Net.Http;
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
    private const int HttpClientTimeoutSeconds = 50;

    /// <summary>
    /// Shared cookie jar across every named HttpClient. Lets cookies obtained on
    /// one site (or via FlareSolverr) flow into subsequent requests on related
    /// hosts — DDoS-Guard's __ddg* cookies get scoped by domain and are reused
    /// by every extractor that hits the same host afterwards.
    /// </summary>
    private static readonly CookieContainer SharedCookies = new();

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient("AniWorld", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler);
        serviceCollection.AddHttpClient("STO", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler);
        serviceCollection.AddHttpClient("HiAnime", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler);

        // FlareSolverr typically runs on the same Docker network and shouldn't
        // be routed through the same outbound proxy — give it a plain handler.
        serviceCollection.AddHttpClient("FlareSolverr", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(120);
        });

        serviceCollection.AddSingleton<AniWorldService>();
        serviceCollection.AddSingleton<StoService>();
        serviceCollection.AddSingleton<HiAnimeService>();
        serviceCollection.AddSingleton<DownloadHistoryService>();
        serviceCollection.AddSingleton<DownloadService>();
        serviceCollection.AddSingleton<FlareSolverrClient>();
        serviceCollection.AddSingleton<CaptchaSessionService>();
        serviceCollection.AddSingleton<IStreamExtractor, VoeExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, VidozaExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, VidmolyExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, FilemoonExtractor>();
    }

    /// <summary>
    /// Gets the shared cookie container used by every named HttpClient.
    /// </summary>
    internal static CookieContainer Cookies => SharedCookies;

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
                    UseCookies = true,
                    CookieContainer = SharedCookies,
                    AllowAutoRedirect = true,
                };
            }

            return new HttpClientHandler
            {
                Proxy = new WebProxy(proxyUri),
                UseProxy = true,
                UseCookies = true,
                CookieContainer = SharedCookies,
                AllowAutoRedirect = true,
            };
        }

        return new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = SharedCookies,
            AllowAutoRedirect = true,
        };
    }
}
