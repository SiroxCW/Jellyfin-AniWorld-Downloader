using System.Collections.Generic;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AniWorld.Configuration;

/// <summary>
/// Plugin configuration for AniWorld Downloader.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── General settings ─────────────────────────────────────────

    /// <summary>
    /// Gets or sets the maximum concurrent downloads.
    /// </summary>
    public int MaxConcurrentDownloads { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum retry attempts for failed downloads.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Gets or sets whether to automatically scan the Jellyfin library
    /// when a download completes.
    /// </summary>
    public bool AutoScanLibrary { get; set; } = true;

    /// <summary>
    /// Gets or sets whether non-admin users can access the plugin UI.
    /// Requires the File Transformation plugin and a server restart.
    /// </summary>
    public bool EnableNonAdminAccess { get; set; } = false;

    /// <summary>
    /// Gets or sets whether maintenance mode is enabled.
    /// When enabled, new downloads are blocked and a message is displayed to users.
    /// Existing queued/active downloads continue to completion.
    /// </summary>
    public bool MaintenanceMode { get; set; } = false;

    /// <summary>
    /// Gets or sets the message displayed when maintenance mode is active.
    /// </summary>
    public string MaintenanceMessage { get; set; } = "The downloader is currently under maintenance and does not accept new downloads at this time.";

    /// <summary>
    /// Gets or sets the HTTP proxy URL for all network requests (e.g. "http://proxy:8080" or "socks5://proxy:1080").
    /// Leave empty to not use a proxy.
    /// </summary>
    public string ProxyUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a custom base URL for s.to (e.g. "https://serienstream.to").
    /// Leave empty to use the default "https://s.to".
    /// </summary>
    public string StoBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the dedicated download path for movies across all sources.
    /// Leave empty to use the normal per-site/per-language download paths.
    /// </summary>
    public string MovieDownloadPath { get; set; } = string.Empty;

    // ── Per-site configs ─────────────────────────────────────────

    /// <summary>
    /// Gets or sets the AniWorld (aniworld.to) downloader configuration.
    /// </summary>
    public SiteDownloaderConfig AniWorldConfig { get; set; } = new()
    {
        Enabled = true,
        PreferredProvider = "Vidmoly",
        FallbackProvider = "VOE",
    };

    /// <summary>
    /// Gets or sets the s.to (SerienStream) downloader configuration.
    /// </summary>
    public SiteDownloaderConfig StoConfig { get; set; } = new()
    {
        Enabled = true,
        PreferredProvider = "VOE",
    };

    /// <summary>
    /// Gets or sets the HiAnime (hianime.to) downloader configuration.
    /// </summary>
    // NOTE: HiAnime (hianime.to) has been shut down. Config is kept for potential future use.
    public SiteDownloaderConfig HiAnimeConfig { get; set; } = new()
    {
        Enabled = false,
        PreferredLanguage = "sub",
    };

    // ── Legacy flat properties (backward compat / used as AniWorld defaults) ──

    /// <summary>
    /// Gets or sets the download path for anime files.
    /// Kept for backward compatibility. Falls back to AniWorldConfig.DownloadPath.
    /// </summary>
    public string DownloadPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the preferred language.
    /// 1 = German Dub, 2 = English Sub, 3 = German Sub.
    /// </summary>
    public string PreferredLanguage { get; set; } = "1";

    /// <summary>
    /// Gets or sets the preferred provider (VOE, Filemoon, Vidoza, Vidmoly).
    /// </summary>
    public string PreferredProvider { get; set; } = "VOE";

    /// <summary>
    /// Gets or sets the fallback provider.
    /// </summary>
    public string FallbackProvider { get; set; } = string.Empty;

    /// <summary>
    /// Resolves the effective download path for a given source and language.
    /// Checks per-language paths first, then the general site path, then fallbacks.
    /// </summary>
    public string GetDownloadPath(string source, string? language = null, bool isMovie = false)
    {
        if (isMovie && !string.IsNullOrEmpty(MovieDownloadPath))
        {
            return MovieDownloadPath;
        }

        var siteConfig = GetSiteConfig(source);

        // 1. Per-language path for this site
        if (!string.IsNullOrEmpty(language))
        {
            var langPath = siteConfig.GetLanguagePath(language);
            if (!string.IsNullOrEmpty(langPath))
            {
                return langPath;
            }
        }

        // 2. General site path (backward compat)
        if (!string.IsNullOrEmpty(siteConfig.DownloadPath))
        {
            return siteConfig.DownloadPath;
        }

        // 3. HiAnime falls back to AniWorld's paths since both are anime
        if (string.Equals(source, "hianime", System.StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(language))
            {
                var awLangPath = AniWorldConfig.GetLanguagePath(language);
                if (!string.IsNullOrEmpty(awLangPath))
                {
                    return awLangPath;
                }
            }

            if (!string.IsNullOrEmpty(AniWorldConfig.DownloadPath))
            {
                return AniWorldConfig.DownloadPath;
            }
        }

        // 4. Legacy global path
        return DownloadPath;
    }

    /// <summary>
    /// Returns all distinct non-empty download paths explicitly configured for a source.
    /// No fallbacks — only returns paths set directly on this site's config.
    /// </summary>
    public List<string> GetAllDownloadPaths(string source)
    {
        var paths = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var siteConfig = GetSiteConfig(source);

        AddNonEmpty(paths, siteConfig.DownloadPath1);
        AddNonEmpty(paths, siteConfig.DownloadPath2);
        AddNonEmpty(paths, siteConfig.DownloadPath3);
        AddNonEmpty(paths, siteConfig.DownloadPathSub);
        AddNonEmpty(paths, siteConfig.DownloadPathDub);

        return paths.ToList();
    }

    private static void AddNonEmpty(HashSet<string> set, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            set.Add(value);
        }
    }

    /// <summary>
    /// Resolves the effective preferred language for a given source.
    /// </summary>
    public string GetPreferredLanguage(string source)
    {
        var siteConfig = GetSiteConfig(source);
        return !string.IsNullOrEmpty(siteConfig.PreferredLanguage) ? siteConfig.PreferredLanguage : PreferredLanguage;
    }

    /// <summary>
    /// Resolves the effective preferred provider for a given source.
    /// </summary>
    public string GetPreferredProvider(string source)
    {
        var siteConfig = GetSiteConfig(source);
        return !string.IsNullOrEmpty(siteConfig.PreferredProvider) ? siteConfig.PreferredProvider : PreferredProvider;
    }

    /// <summary>
    /// Resolves the effective fallback provider for a given source.
    /// </summary>
    public string GetFallbackProvider(string source)
    {
        var siteConfig = GetSiteConfig(source);
        return !string.IsNullOrEmpty(siteConfig.FallbackProvider) ? siteConfig.FallbackProvider : FallbackProvider;
    }

    /// <summary>
    /// Gets the site-specific config for a source.
    /// </summary>
    public SiteDownloaderConfig GetSiteConfig(string source)
    {
        return source?.ToLowerInvariant() switch
        {
            "sto" => StoConfig,
            "hianime" => HiAnimeConfig,
            _ => AniWorldConfig,
        };
    }
}

/// <summary>
/// Per-site downloader configuration.
/// </summary>
public class SiteDownloaderConfig
{
    /// <summary>Gets or sets whether this downloader is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the download path. Empty = use global DownloadPath. Kept for backward compatibility.</summary>
    public string DownloadPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the download path for language key "1" (German Dub for AniWorld/s.to).</summary>
    public string DownloadPath1 { get; set; } = string.Empty;

    /// <summary>Gets or sets the download path for language key "2" (English Sub for AniWorld, English Dub for s.to).</summary>
    public string DownloadPath2 { get; set; } = string.Empty;

    /// <summary>Gets or sets the download path for language key "3" (German Sub for AniWorld).</summary>
    public string DownloadPath3 { get; set; } = string.Empty;

    /// <summary>Gets or sets the download path for "sub" (English Sub for HiAnime).</summary>
    public string DownloadPathSub { get; set; } = string.Empty;

    /// <summary>Gets or sets the download path for "dub" (English Dub for HiAnime).</summary>
    public string DownloadPathDub { get; set; } = string.Empty;

    /// <summary>Looks up a per-language download path by language key. Returns empty string if not set.</summary>
    [XmlIgnore]
    public Dictionary<string, string> DownloadPaths
    {
        get
        {
            var dict = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(DownloadPath1)) dict["1"] = DownloadPath1;
            if (!string.IsNullOrEmpty(DownloadPath2)) dict["2"] = DownloadPath2;
            if (!string.IsNullOrEmpty(DownloadPath3)) dict["3"] = DownloadPath3;
            if (!string.IsNullOrEmpty(DownloadPathSub)) dict["sub"] = DownloadPathSub;
            if (!string.IsNullOrEmpty(DownloadPathDub)) dict["dub"] = DownloadPathDub;
            return dict;
        }
    }

    /// <summary>Gets the per-language download path for a specific language key.</summary>
    public string GetLanguagePath(string langKey)
    {
        return langKey switch
        {
            "1" => DownloadPath1,
            "2" => DownloadPath2,
            "3" => DownloadPath3,
            "sub" => DownloadPathSub,
            "dub" => DownloadPathDub,
            _ => string.Empty,
        };
    }

    /// <summary>Gets or sets the preferred language. Empty = use global.</summary>
    public string PreferredLanguage { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred provider. Empty = use global.</summary>
    public string PreferredProvider { get; set; } = string.Empty;

    /// <summary>Gets or sets the fallback provider. Empty = use global.</summary>
    public string FallbackProvider { get; set; } = string.Empty;

    /// <summary>Gets or sets whether only English Dub downloads are allowed for HiAnime.</summary>
    public bool OnlyEnglishDub { get; set; }

    /// <summary>Gets or sets whether only German languages (German Dub + German Sub) are allowed for AniWorld.</summary>
    public bool OnlyGermanLanguages { get; set; }
}
