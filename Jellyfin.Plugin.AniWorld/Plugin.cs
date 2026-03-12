using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniWorld.Configuration;
using Jellyfin.Plugin.AniWorld.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Newtonsoft.Json.Linq;

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

    private const string PluginDisplayName = "AniWorld Downloader";

    private const int FileTransformationMaxRetries = 30;
    private const int FileTransformationRetryDelayMs = 1000;

    private readonly IApplicationPaths _applicationPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of <see cref="IApplicationPaths"/>.</param>
    /// <param name="xmlSerializer">Instance of <see cref="IXmlSerializer"/>.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationPaths = applicationPaths;

        if (Configuration.EnableNonAdminAccess)
        {
            // Defer File Transformation registration — it's not initialized
            // when our constructor runs. Use a delayed task that retries
            // until File Transformation is ready, then falls back to direct
            // index.html injection.
            _ = Task.Run(async () =>
            {
                // Wait for File Transformation to initialize
                for (int i = 0; i < FileTransformationMaxRetries; i++)
                {
                    await Task.Delay(FileTransformationRetryDelayMs).ConfigureAwait(false);
                    try
                    {
                        if (TryRegisterFileTransformation())
                        {
                            return;
                        }
                    }
                    catch
                    {
                        // Not ready yet, retry
                    }
                }

                // Fallback: directly modify index.html
                InjectScript();
            });
        }
        else
        {
            CleanupInjection();
        }
    }

    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => PluginDisplayName;

    /// <inheritdoc />
    public override string Description => "Search and download anime from aniworld.to and hianime.to, and series from s.to directly within Jellyfin.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    private string IndexHtmlPath => Path.Combine(_applicationPaths.WebPath, "index.html");

    /// <summary>
    /// Attempts to register with the File Transformation plugin.
    /// Returns true if registration succeeded, false if not available yet.
    /// </summary>
    private bool TryRegisterFileTransformation()
    {
        Assembly? fileTransformationAssembly =
            AssemblyLoadContext.All.SelectMany(x => x.Assemblies).FirstOrDefault(x =>
                x.FullName?.Contains(".FileTransformation") ?? false);

        if (fileTransformationAssembly == null)
        {
            return false;
        }

        Type? pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        if (pluginInterfaceType == null)
        {
            return false;
        }

        var payload = new JObject
        {
            { "id", PluginGuid },
            { "fileNamePattern", "index.html" },
            { "callbackAssembly", GetType().Assembly.FullName },
            { "callbackClass", typeof(TransformationPatches).FullName },
            { "callbackMethod", nameof(TransformationPatches.IndexHtml) }
        };

        pluginInterfaceType.GetMethod("RegisterTransformation")?.Invoke(null, new object?[] { payload });
        return true;
    }

    /// <summary>
    /// Injects the script tag into index.html directly.
    /// </summary>
    public void InjectScript()
    {
        UpdateIndexHtml(true);
    }

    /// <summary>
    /// Removes any injected script from index.html.
    /// </summary>
    public void CleanupInjection()
    {
        UpdateIndexHtml(false);
    }

    private void UpdateIndexHtml(bool inject)
    {
        try
        {
            var indexPath = IndexHtmlPath;
            if (!File.Exists(indexPath))
            {
                return;
            }

            var content = File.ReadAllText(indexPath);
            var scriptTag = $"<script plugin=\"{PluginDisplayName}\" src=\"../AniWorld/InjectionScript\" defer></script>";
            var regex = new Regex($"<script[^>]*plugin=[\"']{Regex.Escape(PluginDisplayName)}[\"'][^>]*>\\s*</script>\\n?");

            // Remove existing script tag first
            content = regex.Replace(content, string.Empty);

            if (inject)
            {
                if (content.Contains("</body>"))
                {
                    content = content.Replace("</body>", $"{scriptTag}\n</body>");
                }
                else
                {
                    return;
                }
            }

            File.WriteAllText(indexPath, content);
        }
        catch
        {
            // Best effort — don't crash plugin init
        }
    }

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        CleanupInjection();
        base.OnUninstalling();
    }

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
