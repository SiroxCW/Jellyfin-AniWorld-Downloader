using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AniWorld.Configuration;

/// <summary>
/// Plugin configuration for AniWorld Downloader.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the download path for anime files.
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
    /// Gets or sets the fallback provider. After all retries with the primary provider fail,
    /// the download will be attempted again using this provider.
    /// Set to "None" or empty to disable fallback.
    /// If fallback equals the primary provider, fallback is skipped.
    /// </summary>
    public string FallbackProvider { get; set; } = string.Empty;

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
}
