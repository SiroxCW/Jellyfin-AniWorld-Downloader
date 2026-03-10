using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Startup task placeholder for AniWorld Downloader.
/// The actual File Transformation registration is handled by the Plugin constructor
/// via a delayed background task.
/// </summary>
public class StartupService : IScheduledTask
{
    private readonly ILogger<StartupService> _logger;

    /// <inheritdoc />
    public string Name => "AniWorld Downloader Startup";

    /// <inheritdoc />
    public string Key => "AniWorldDownloaderStartup";

    /// <inheritdoc />
    public string Description => "AniWorld Downloader startup task.";

    /// <inheritdoc />
    public string Category => "AniWorld Downloader";

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupService"/> class.
    /// </summary>
    public StartupService(ILogger<StartupService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("AniWorld Downloader startup task executed.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = "StartupTrigger"
        };
    }
}
