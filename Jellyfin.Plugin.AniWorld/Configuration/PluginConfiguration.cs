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

    // ── Per-site configs ─────────────────────────────────────────

    /// <summary>
    /// Gets or sets the AniWorld (aniworld.to) downloader configuration.
    /// </summary>
    public SiteDownloaderConfig AniWorldConfig { get; set; } = new()
    {
        Enabled = true,
    };

    /// <summary>
    /// Gets or sets the s.to (SerienStream) downloader configuration.
    /// </summary>
    public SiteDownloaderConfig StoConfig { get; set; } = new()
    {
        Enabled = true,
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
    /// Resolves the effective download path for a given source.
    /// </summary>
    public string GetDownloadPath(string source)
    {
        var siteConfig = GetSiteConfig(source);
        return !string.IsNullOrEmpty(siteConfig.DownloadPath) ? siteConfig.DownloadPath : DownloadPath;
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
        return string.Equals(source, "sto", System.StringComparison.OrdinalIgnoreCase)
            ? StoConfig
            : AniWorldConfig;
    }
}

/// <summary>
/// Per-site downloader configuration.
/// </summary>
public class SiteDownloaderConfig
{
    /// <summary>Gets or sets whether this downloader is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the download path. Empty = use global DownloadPath.</summary>
    public string DownloadPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred language. Empty = use global.</summary>
    public string PreferredLanguage { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred provider. Empty = use global.</summary>
    public string PreferredProvider { get; set; } = string.Empty;

    /// <summary>Gets or sets the fallback provider. Empty = use global.</summary>
    public string FallbackProvider { get; set; } = string.Empty;
}
