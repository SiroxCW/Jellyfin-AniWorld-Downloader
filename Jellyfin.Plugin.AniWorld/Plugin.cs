using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AniWorld.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AniWorld;

/// <summary>
/// AniWorld Downloader plugin for Jellyfin.
/// Downloads anime from aniworld.to and series from s.to directly within Jellyfin's UI.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin GUID.
    /// </summary>
    public const string PluginGuid = "e93d1d02-df60-4545-ae3c-7bb87dff024c";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of <see cref="IApplicationPaths"/>.</param>
    /// <param name="xmlSerializer">Instance of <see cref="IXmlSerializer"/>.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "AniWorld Downloader";

    /// <inheritdoc />
    public override string Description => "Search and download anime from aniworld.to and hianime.to, and series from s.to directly within Jellyfin.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "AniWorldDownloader",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.aniworld.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "download",
                DisplayName = "AniWorld Downloader",
            },
            new PluginPageInfo
            {
                Name = "AniWorldDownloaderJS",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.aniworld.js",
            },
            new PluginPageInfo
            {
                Name = "AniWorldConfig",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.config.html",
                MenuSection = "server",
                MenuIcon = "download",
                DisplayName = "AniWorld Downloader",
            },
            new PluginPageInfo
            {
                Name = "AniWorldConfigJS",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.config.js",
            },
        };
    }
}
