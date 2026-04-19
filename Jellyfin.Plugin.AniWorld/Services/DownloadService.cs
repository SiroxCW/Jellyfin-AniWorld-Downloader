using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniWorld.Extractors;
using Jellyfin.Plugin.AniWorld.Helpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Manages downloads from streaming sites using ffmpeg.
/// Supports retry with exponential backoff, provider fallback,
/// automatic Jellyfin library scanning after completion,
/// and persistent download history via SQLite.
/// </summary>
public class DownloadService
{
    private const int DefaultMaxRetries = 3;
    private const int BaseRetryDelayMs = 3000;

    private readonly AniWorldService _aniWorldService;
    private readonly StoService _stoService;
    private readonly HiAnimeService _hiAnimeService;
    private readonly DownloadHistoryService _historyService;
    private readonly IEnumerable<IStreamExtractor> _extractors;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<DownloadService> _logger;
    private readonly ConcurrentDictionary<string, DownloadTask> _activeTasks = new();
    private readonly Channel<DownloadTask> _downloadQueue = Channel.CreateUnbounded<DownloadTask>(
        new UnboundedChannelOptions { SingleReader = false });
    private readonly Channel<DownloadTask> _priorityQueue = Channel.CreateUnbounded<DownloadTask>(
        new UnboundedChannelOptions { SingleReader = false });
    private readonly object _queueLock = new();
    private long _sequenceCounter;
    private long _prioritySequenceCounter = long.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadService"/> class.
    /// </summary>
    public DownloadService(
        AniWorldService aniWorldService,
        StoService stoService,
        HiAnimeService hiAnimeService,
        DownloadHistoryService historyService,
        IEnumerable<IStreamExtractor> extractors,
        IMediaEncoder mediaEncoder,
        ILibraryMonitor libraryMonitor,
        ILogger<DownloadService> logger)
    {
        _aniWorldService = aniWorldService;
        _stoService = stoService;
        _hiAnimeService = hiAnimeService;
        _historyService = historyService;
        _extractors = extractors;
        _mediaEncoder = mediaEncoder;
        _libraryMonitor = libraryMonitor;
        _logger = logger;

        // Mark any downloads that were in-progress when Jellyfin last shut down
        _historyService.MarkInterruptedDownloads();

        // Start FIFO worker tasks for concurrent downloads
        var maxDownloads = Plugin.Instance?.Configuration.MaxConcurrentDownloads ?? 2;
        for (int i = 0; i < maxDownloads; i++)
        {
            _ = Task.Run(() => ProcessDownloadQueueAsync());
        }
    }

    /// <summary>
    /// Worker loop that processes downloads from the priority and normal queues.
    /// Priority queue items are always picked up before normal queue items.
    /// </summary>
    private async Task ProcessDownloadQueueAsync()
    {
        while (true)
        {
            DownloadTask? task = null;

            // Always check priority queue first
            if (_priorityQueue.Reader.TryRead(out task) || _downloadQueue.Reader.TryRead(out task))
            {
                // Got a task
            }
            else
            {
                // Wait for either channel to have data
                var priorityWait = _priorityQueue.Reader.WaitToReadAsync().AsTask();
                var normalWait = _downloadQueue.Reader.WaitToReadAsync().AsTask();

                await Task.WhenAny(priorityWait, normalWait).ConfigureAwait(false);

                // Try priority first again
                if (!_priorityQueue.Reader.TryRead(out task) && !_downloadQueue.Reader.TryRead(out task))
                {
                    continue;
                }
            }

            try
            {
                await ExecuteDownloadWithRetryAsync(task, task.CancellationSource?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing download {TaskId}", task.Id);
            }
        }
    }

    /// <summary>
    /// Gets the correct streaming service for a source.
    /// </summary>
    private StreamingSiteService GetService(string source)
    {
        return string.Equals(source, "sto", StringComparison.OrdinalIgnoreCase)
            ? _stoService
            : _aniWorldService;
    }

    /// <summary>
    /// Gets all active download tasks (in-memory, currently running).
    /// </summary>
    public List<DownloadTask> GetActiveDownloads()
    {
        return _activeTasks.Values.OrderBy(t => t.SequenceNumber).ToList();
    }

    /// <summary>
    /// Gets a specific download task by ID.
    /// </summary>
    public DownloadTask? GetDownload(string taskId)
    {
        _activeTasks.TryGetValue(taskId, out var task);
        return task;
    }

    /// <summary>
    /// Checks whether an episode has already been successfully downloaded.
    /// Matches by sanitized series title + season + episode + language.
    /// </summary>
    public bool IsAlreadyDownloaded(string seriesTitle, int season, int episode, string language)
    {
        return _historyService.IsAlreadyDownloaded(seriesTitle, season, episode, language);
    }

    /// <summary>
    /// Starts a download for an episode.
    /// </summary>
    public async Task<string?> StartDownloadAsync(
        string episodeUrl,
        string languageKey,
        string provider,
        string outputPath,
        string seriesTitle,
        string source = "aniworld",
        CancellationToken cancellationToken = default,
        int? episodeNumber = null,
        int? customSeason = null,
        string? username = null,
        bool priority = false)
    {
        DownloadTask task;

        // Lock the check-and-insert to prevent duplicate queuing from concurrent requests
        lock (_queueLock)
        {
            var existing = _activeTasks.Values.FirstOrDefault(t =>
                t.EpisodeUrl == episodeUrl &&
                t.Status is DownloadStatus.Queued or DownloadStatus.Resolving or DownloadStatus.Extracting
                    or DownloadStatus.Downloading or DownloadStatus.Retrying);
            if (existing != null)
            {
                return null;
            }

            var taskId = Guid.NewGuid().ToString("N")[..12];
            var (season, episode) = PathHelper.ParseSeasonEpisode(episodeUrl, episodeNumber);

            // Override season when a custom season is provided (HiAnime "download to existing title")
            if (customSeason.HasValue)
            {
                season = customSeason.Value;
            }

            task = new DownloadTask
            {
                Id = taskId,
                EpisodeUrl = episodeUrl,
                Provider = provider,
                Language = languageKey,
                OutputPath = outputPath,
                SeriesTitle = seriesTitle,
                Season = season,
                Episode = episode,
                Source = source,
                Status = DownloadStatus.Queued,
                StartedAt = DateTime.UtcNow,
                SequenceNumber = priority
                    ? Interlocked.Increment(ref _prioritySequenceCounter)
                    : Interlocked.Increment(ref _sequenceCounter),
                MaxRetries = Plugin.Instance?.Configuration.MaxRetries ?? DefaultMaxRetries,
                Username = username,
            };

            task.CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeTasks[taskId] = task;
        }

        // Persist initial state to SQLite
        _historyService.SaveDownload(task, task.SeriesTitle, task.Season, task.Episode);

        // Enqueue: priority downloads go to the front of the line
        if (priority)
        {
            await _priorityQueue.Writer.WriteAsync(task, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _downloadQueue.Writer.WriteAsync(task, cancellationToken).ConfigureAwait(false);
        }

        return task.Id;
    }

    /// <summary>
    /// Cancels a download and cleans up any partial file on disk.
    /// </summary>
    public bool CancelDownload(string taskId)
    {
        if (_activeTasks.TryGetValue(taskId, out var task))
        {
            task.CancellationSource?.Cancel();
            task.Status = DownloadStatus.Cancelled;
            _historyService.UpdateDownload(task);

            CleanupFileOnCancel(task.OutputPath, task.Source, task.Language, task.EpisodeUrl);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Clears all completed, failed, and cancelled downloads from the active list.
    /// </summary>
    public int ClearCompleted()
    {
        var toRemove = _activeTasks.Values
            .Where(t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
            .Select(t => t.Id)
            .ToList();

        foreach (var id in toRemove)
        {
            _activeTasks.TryRemove(id, out _);
        }

        return toRemove.Count;
    }

    /// <summary>
    /// Retries a failed download.
    /// </summary>
    public bool RetryDownload(string taskId)
    {
        if (_activeTasks.TryGetValue(taskId, out var task) &&
            task.Status is DownloadStatus.Failed)
        {
            // Check if another task for the same episode is already active
            var duplicate = _activeTasks.Values.FirstOrDefault(t =>
                t.Id != taskId &&
                t.EpisodeUrl == task.EpisodeUrl &&
                t.Status is DownloadStatus.Queued or DownloadStatus.Resolving or DownloadStatus.Extracting
                    or DownloadStatus.Downloading or DownloadStatus.Retrying);
            if (duplicate != null)
            {
                return false;
            }

            task.Status = DownloadStatus.Queued;
            task.Error = null;
            task.RetryCount = 0;
            task.Progress = 0;
            task.SequenceNumber = Interlocked.Increment(ref _sequenceCounter);
            task.CancellationSource?.Dispose();
            task.CancellationSource = new CancellationTokenSource();

            _historyService.UpdateDownload(task);

            _downloadQueue.Writer.TryWrite(task);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Wraps the download execution with retry logic, exponential backoff, and provider fallback.
    /// </summary>
    private async Task ExecuteDownloadWithRetryAsync(DownloadTask task, CancellationToken externalToken)
    {
        // Use the existing CancellationSource if set, otherwise create one
        task.CancellationSource ??= CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = task.CancellationSource.Token;

        var originalProvider = task.Provider;

        // Try primary provider first, then fallback if configured
        if (!await TryDownloadWithRetriesAsync(task, token).ConfigureAwait(false))
        {
            var config = Plugin.Instance?.Configuration;
            var fallbackProvider = config?.GetFallbackProvider(task.Source) ?? string.Empty;

            if (!string.IsNullOrEmpty(fallbackProvider) &&
                !fallbackProvider.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                !fallbackProvider.Equals(originalProvider, StringComparison.OrdinalIgnoreCase) &&
                task.Status == DownloadStatus.Failed)
            {
                _logger.LogInformation(
                    "Primary provider {Primary} failed for {Url}. Trying fallback provider {Fallback}",
                    originalProvider, task.EpisodeUrl, fallbackProvider);

                task.Provider = fallbackProvider;
                task.Status = DownloadStatus.Queued;
                task.RetryCount = 0;
                task.Progress = 0;
                task.Error = $"Falling back to {fallbackProvider}...";
                _historyService.UpdateDownload(task);

                CleanupFileOnCancel(task.OutputPath, task.Source, task.Language, task.EpisodeUrl);

                if (await TryDownloadWithRetriesAsync(task, token).ConfigureAwait(false))
                {
                    return;
                }

                task.Error = $"Failed with {originalProvider} and fallback {fallbackProvider}: {task.Error}";
                _historyService.UpdateDownload(task);
            }

            if (task.Status == DownloadStatus.Failed)
            {
                CleanupPartialFile(task.OutputPath, task.Source, task.Language, task.EpisodeUrl);
            }
        }
    }

    /// <summary>
    /// Attempts to download with the current task.Provider, retrying up to MaxRetries times.
    /// Returns true if download completed successfully, false if all retries exhausted.
    /// </summary>
    private async Task<bool> TryDownloadWithRetriesAsync(DownloadTask task, CancellationToken token)
    {
        var maxRetries = task.MaxRetries;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (token.IsCancellationRequested)
            {
                task.Status = DownloadStatus.Cancelled;
                _historyService.UpdateDownload(task);
                CleanupFileOnCancel(task.OutputPath, task.Source, task.Language, task.EpisodeUrl);
                return true;
            }

            if (attempt > 0)
            {
                task.RetryCount = attempt;
                var delayMs = BaseRetryDelayMs * (int)Math.Pow(2, attempt - 1);
                task.Status = DownloadStatus.Retrying;
                task.Error = $"Retry {attempt}/{maxRetries} ({task.Provider}) in {delayMs / 1000}s...";
                _historyService.UpdateDownload(task);
                _logger.LogInformation("Retry {Attempt}/{MaxRetries} for {Url} with {Provider} in {Delay}ms",
                    attempt, maxRetries, task.EpisodeUrl, task.Provider, delayMs);

                try
                {
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    task.Status = DownloadStatus.Cancelled;
                    _historyService.UpdateDownload(task);
                    CleanupFileOnCancel(task.OutputPath, task.Source, task.Language, task.EpisodeUrl);
                    return true;
                }

                task.Error = null;
                task.Progress = 0;
            }

            try
            {
                await ExecuteDownloadAsync(task, token).ConfigureAwait(false);

                if (task.Status == DownloadStatus.Completed)
                {
                    _historyService.UpdateDownload(task);
                    TriggerLibraryScan(task.OutputPath);
                    return true;
                }

                if (task.Status == DownloadStatus.Cancelled)
                {
                    _historyService.UpdateDownload(task);
                    CleanupFileOnCancel(task.OutputPath, task.Source, task.Language, task.EpisodeUrl);
                    return true;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // User-initiated cancellation — stop immediately
                task.Status = DownloadStatus.Cancelled;
                _historyService.UpdateDownload(task);
                CleanupFileOnCancel(task.OutputPath, task.Source, task.Language, task.EpisodeUrl);
                return true;
            }
            catch (OperationCanceledException ex)
            {
                // HttpClient timeout — treat as a retryable failure
                _logger.LogWarning("Download attempt {Attempt}/{MaxRetries} timed out for {Url} with {Provider}",
                    attempt + 1, maxRetries + 1, task.EpisodeUrl, task.Provider);
                task.Error = "Request timed out";

                if (attempt >= maxRetries)
                {
                    task.Status = DownloadStatus.Failed;
                    task.Error = $"Failed after {maxRetries + 1} attempts with {task.Provider}: Request timed out";
                    _historyService.UpdateDownload(task);
                    _logger.LogError(ex, "Download failed for {Url} after {Attempts} attempts with {Provider} (timeout)",
                        task.EpisodeUrl, maxRetries + 1, task.Provider);
                    return false;
                }

                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Download attempt {Attempt}/{MaxRetries} failed for {Url} with {Provider}",
                    attempt + 1, maxRetries + 1, task.EpisodeUrl, task.Provider);
                task.Error = ex.Message;

                if (attempt >= maxRetries)
                {
                    task.Status = DownloadStatus.Failed;
                    task.Error = $"Failed after {maxRetries + 1} attempts with {task.Provider}: {ex.Message}";
                    _historyService.UpdateDownload(task);
                    _logger.LogError(ex, "Download failed for {Url} after {Attempts} attempts with {Provider}",
                        task.EpisodeUrl, maxRetries + 1, task.Provider);
                    return false;
                }
            }
        }

        return false;
    }

    private async Task ExecuteDownloadAsync(DownloadTask task, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (string.Equals(task.Source, "hianime", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteHiAnimeDownloadAsync(task, token).ConfigureAwait(false);
            return;
        }

        task.Status = DownloadStatus.Resolving;
        _historyService.UpdateDownload(task);

        // Route to the correct service based on source
        var service = GetService(task.Source);

        // 1. Get episode details
        var details = await service.GetEpisodeDetailsAsync(task.EpisodeUrl, token).ConfigureAwait(false);
        task.EpisodeTitle = details.TitleEn ?? details.TitleDe ?? "Unknown";

        // 2. Rename output path to include episode title if available
        var newPath = PathHelper.InsertEpisodeTitleInPath(task.OutputPath, task.EpisodeTitle);
        if (newPath != task.OutputPath)
        {
            task.OutputPath = newPath;
            _logger.LogDebug("Updated output path with episode title: {Path}", newPath);
        }

        if (!details.ProvidersByLanguage.TryGetValue(task.Language, out var providers) ||
            !providers.TryGetValue(task.Provider, out var redirectUrl))
        {
            var fallbackResult = TryFindFallbackProvider(details, task.Language, task.Provider);
            if (fallbackResult == null)
            {
                throw new InvalidOperationException(
                    $"Provider {task.Provider} not available for language key {task.Language}, and no fallback found");
            }

            redirectUrl = fallbackResult.Value.url;
            task.Provider = fallbackResult.Value.provider;
            _logger.LogInformation("Falling back to provider {Provider} for {Url}", task.Provider, task.EpisodeUrl);
        }

        // 3. Resolve redirect to provider embed URL
        var embedUrl = await service.ResolveRedirectAsync(redirectUrl, token).ConfigureAwait(false);
        _logger.LogInformation("Resolved to embed URL: {EmbedUrl}", embedUrl);

        // 4. Extract direct stream URL
        var extractor = _extractors.FirstOrDefault(e =>
            e.ProviderName.Equals(task.Provider, StringComparison.OrdinalIgnoreCase));

        if (extractor == null)
        {
            throw new InvalidOperationException($"No extractor available for provider: {task.Provider}");
        }

        token.ThrowIfCancellationRequested();

        task.Status = DownloadStatus.Extracting;
        _historyService.UpdateDownload(task);
        var streamUrl = await extractor.GetDirectLinkAsync(embedUrl, token).ConfigureAwait(false);

        if (string.IsNullOrEmpty(streamUrl))
        {
            throw new InvalidOperationException("Failed to extract stream URL from provider");
        }

        // Validate the extracted stream URL is a real HTTP(S) URL
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var streamUri) ||
            (streamUri.Scheme != Uri.UriSchemeHttp && streamUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Extracted stream URL is not a valid HTTP(S) URL");
        }

        _logger.LogInformation("Stream URL: {StreamUrl}", streamUrl);

        token.ThrowIfCancellationRequested();

        // 5. Download with ffmpeg
        task.Status = DownloadStatus.Downloading;
        task.StreamUrl = streamUrl;
        _historyService.UpdateDownload(task);

        var dir = Path.GetDirectoryName(task.OutputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await DownloadWithFfmpegAsync(task, token).ConfigureAwait(false);

        VerifyDownloadedFile(task, token);
    }

    /// <summary>
    /// Executes the HiAnime-specific download pipeline: MegaCloud extraction with HD-2/HD-3 fallback.
    /// </summary>
    private async Task ExecuteHiAnimeDownloadAsync(DownloadTask task, CancellationToken token)
    {
        task.Status = DownloadStatus.Resolving;
        _historyService.UpdateDownload(task);

        // Get episode title
        var details = await _hiAnimeService.GetEpisodeDetailsAsync(task.EpisodeUrl, token).ConfigureAwait(false);
        task.EpisodeTitle = details.TitleEn ?? "Unknown";

        var newPath = PathHelper.InsertEpisodeTitleInPath(task.OutputPath, task.EpisodeTitle);
        if (newPath != task.OutputPath)
        {
            task.OutputPath = newPath;
            _logger.LogDebug("Updated output path with episode title: {Path}", newPath);
        }

        token.ThrowIfCancellationRequested();

        task.Status = DownloadStatus.Extracting;
        _historyService.UpdateDownload(task);

        // Full MegaCloud extraction with HD-2 -> HD-3 fallback
        var streamResult = await _hiAnimeService.GetStreamAsync(task.EpisodeUrl, task.Language, token).ConfigureAwait(false);
        if (streamResult == null)
        {
            throw new InvalidOperationException("Failed to extract stream from HiAnime (all servers failed)");
        }

        task.StreamUrl = streamResult.Url;
        task.Status = DownloadStatus.Downloading;
        _historyService.UpdateDownload(task);

        var dir = Path.GetDirectoryName(task.OutputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await DownloadWithFfmpegAsync(task, token, streamResult.Headers).ConfigureAwait(false);

        VerifyDownloadedFile(task, token);
    }

    /// <summary>
    /// Verifies the downloaded file exists and has content, marks task as completed.
    /// </summary>
    private void VerifyDownloadedFile(DownloadTask task, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            task.Status = DownloadStatus.Cancelled;
            return;
        }

        // Verify the file exists and has content
        var fileInfo = new FileInfo(task.OutputPath);
        if (!fileInfo.Exists || fileInfo.Length < 1024)
        {
            throw new InvalidOperationException(
                $"Downloaded file is missing or too small ({fileInfo.Length} bytes)");
        }

        task.Status = DownloadStatus.Completed;
        task.CompletedAt = DateTime.UtcNow;
        task.Progress = 100;
        task.FileSizeBytes = fileInfo.Length;
        _logger.LogInformation("Download completed: {Path} ({Size} bytes)", task.OutputPath, fileInfo.Length);
    }

    /// <summary>
    /// Tries to find a fallback provider when the preferred one is unavailable.
    /// </summary>
    private (string provider, string url)? TryFindFallbackProvider(
        EpisodeDetails details,
        string language,
        string excludeProvider)
    {
        if (!details.ProvidersByLanguage.TryGetValue(language, out var providers))
        {
            return null;
        }

        var providerPriority = new[] { "VOE", "Filemoon", "Vidmoly", "Vidoza" };
        var extractorNames = _extractors.Select(e => e.ProviderName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var prov in providerPriority)
        {
            if (prov.Equals(excludeProvider, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (providers.TryGetValue(prov, out var url) &&
                extractorNames.Contains(prov))
            {
                return (prov, url);
            }
        }

        foreach (var (name, url) in providers)
        {
            if (!name.Equals(excludeProvider, StringComparison.OrdinalIgnoreCase) &&
                extractorNames.Contains(name))
            {
                return (name, url);
            }
        }

        return null;
    }

    /// <summary>
    /// Triggers a Jellyfin library scan for the directory containing the downloaded file.
    /// </summary>
    private void TriggerLibraryScan(string filePath)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.AutoScanLibrary != true)
        {
            _logger.LogDebug("Auto library scan disabled, skipping");
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                _libraryMonitor.ReportFileSystemChanged(directory);
                _logger.LogInformation("Triggered library scan for: {Directory}", directory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to trigger library scan for {Path}", filePath);
        }
    }

    /// <summary>
    /// Cleans up a failed download file and its empty parent directories.
    /// </summary>
    private void CleanupPartialFile(string filePath, string source, string? language = null, string? episodeUrl = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                var size = new FileInfo(filePath).Length;
                File.Delete(filePath);
                _logger.LogInformation("Cleaned up failed download file: {Path} ({Size} bytes)", filePath, size);
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                CleanupEmptyParentDirectories(filePath, source, language, episodeUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to cleanup partial file: {Path}", filePath);
        }
    }

    /// <summary>
    /// Cleans up a file on cancellation — removes regardless of size since
    /// a cancelled download is always incomplete/unwanted.
    /// </summary>
    private void CleanupFileOnCancel(string filePath, string source, string? language = null, string? episodeUrl = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                var size = new FileInfo(filePath).Length;
                File.Delete(filePath);
                _logger.LogInformation("Cleaned up cancelled download file: {Path} ({Size} bytes)", filePath, size);
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                CleanupEmptyParentDirectories(filePath, source, language, episodeUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup cancelled file: {Path}", filePath);
        }
    }

    /// <summary>
    /// Removes empty parent directories up to (but not including) the configured download base path.
    /// </summary>
    private void CleanupEmptyParentDirectories(string filePath, string source, string? language = null, string? episodeUrl = null)
    {
        var isMovie = !string.IsNullOrEmpty(episodeUrl) && PathHelper.MovieFromUrl.IsMatch(episodeUrl);
        var basePath = Plugin.Instance?.Configuration.GetDownloadPath(source, language, isMovie) ?? string.Empty;
        if (string.IsNullOrEmpty(basePath))
        {
            return;
        }

        try
        {
            var normalizedBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar);
            var dir = Path.GetDirectoryName(filePath);

            for (int i = 0; i < 2 && !string.IsNullOrEmpty(dir); i++)
            {
                var normalizedDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);

                if (normalizedDir.Equals(normalizedBase, StringComparison.Ordinal) ||
                    !normalizedDir.StartsWith(normalizedBase, StringComparison.Ordinal))
                {
                    break;
                }

                if (Directory.Exists(dir) && IsDirectoryEmpty(dir))
                {
                    Directory.Delete(dir);
                    _logger.LogInformation("Removed empty directory: {Dir}", dir);
                }
                else
                {
                    break;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to cleanup empty parent directories for: {Path}", filePath);
        }
    }

    private static bool IsDirectoryEmpty(string path)
    {
        return !Directory.EnumerateFileSystemEntries(path).Any();
    }

    /// <summary>
    /// Downloads a stream using ffmpeg. Uses ArgumentList to avoid shell injection
    /// and argument quoting issues with URLs or file paths containing special characters.
    /// </summary>
    private async Task DownloadWithFfmpegAsync(
        DownloadTask task,
        CancellationToken cancellationToken,
        Dictionary<string, string>? extraHeaders = null)
    {
        var ffmpegPath = FindFfmpeg();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            throw new InvalidOperationException("ffmpeg not found. Please ensure ffmpeg is installed.");
        }

        // Use ProcessStartInfo.ArgumentList for safe argument passing (no shell quoting issues).
        // This prevents argument injection via crafted stream URLs or file paths.
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-reconnect");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-reconnect_streamed");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-reconnect_delay_max");
        startInfo.ArgumentList.Add("5");

        // Add extra headers (e.g., Referer for HiAnime/MegaCloud)
        if (extraHeaders != null && extraHeaders.Count > 0)
        {
            var headerStr = string.Join("\r\n", extraHeaders.Select(h => $"{h.Key}: {h.Value}")) + "\r\n";
            startInfo.ArgumentList.Add("-headers");
            startInfo.ArgumentList.Add(headerStr);
        }

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(task.StreamUrl!);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-bsf:a");
        startInfo.ArgumentList.Add("aac_adtstoasc");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add(task.OutputPath);

        // Pass proxy to ffmpeg via the http_proxy environment variable
        var proxyUrl = Plugin.Instance?.Configuration?.ProxyUrl;
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            startInfo.Environment["http_proxy"] = proxyUrl;
            startInfo.Environment["HTTP_PROXY"] = proxyUrl;
        }

        _logger.LogDebug("Running ffmpeg for: {Url} -> {Path}", task.StreamUrl, task.OutputPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var progressPattern = new Regex(@"time=(?<time>\d+:\d+:\d+\.\d+)", RegexOptions.Compiled);
        var durationPattern = new Regex(@"Duration:\s*(?<dur>\d+:\d+:\d+\.\d+)", RegexOptions.Compiled);
        var sizePattern = new Regex(@"size=\s*(?<size>\d+)kB", RegexOptions.Compiled);
        TimeSpan? totalDuration = null;

        var stderrTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null) continue;

                if (totalDuration == null)
                {
                    var durMatch = durationPattern.Match(line);
                    if (durMatch.Success && TimeSpan.TryParse(durMatch.Groups["dur"].Value, out var dur))
                    {
                        totalDuration = dur;
                    }
                }

                var timeMatch = progressPattern.Match(line);
                if (timeMatch.Success && TimeSpan.TryParse(timeMatch.Groups["time"].Value, out var currentTime))
                {
                    if (totalDuration.HasValue && totalDuration.Value.TotalSeconds > 0)
                    {
                        task.Progress = Math.Min(99, (int)(currentTime.TotalSeconds / totalDuration.Value.TotalSeconds * 100));
                    }
                }

                var sizeMatch = sizePattern.Match(line);
                if (sizeMatch.Success && long.TryParse(sizeMatch.Groups["size"].Value, out var sizeKb))
                {
                    task.FileSizeBytes = sizeKb * 1024;
                }
            }
        }, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}");
        }
    }

    private string? FindFfmpeg()
    {
        // Use Jellyfin's own ffmpeg path — works on all platforms.
        // EncoderPath may be a full path or just "ffmpeg" (bare name on PATH).
        // Jellyfin already validated it at startup, so trust it if non-empty.
        var encoderPath = _mediaEncoder.EncoderPath;
        return string.IsNullOrEmpty(encoderPath) ? null : encoderPath;
    }
}

/// <summary>
/// Represents an active download task.
/// </summary>
public class DownloadTask
{
    /// <summary>Gets or sets the task ID.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode URL.</summary>
    public string EpisodeUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode title.</summary>
    public string? EpisodeTitle { get; set; }

    /// <summary>Gets or sets the series title.</summary>
    public string SeriesTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the season number.</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    public int Episode { get; set; }

    /// <summary>Gets or sets the provider name.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets or sets the language key.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets the output file path.</summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the stream URL.</summary>
    public string? StreamUrl { get; set; }

    /// <summary>Gets or sets the download status.</summary>
    public DownloadStatus Status { get; set; }

    /// <summary>Gets or sets the progress (0-100).</summary>
    public int Progress { get; set; }

    /// <summary>Gets or sets error message if failed.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the started timestamp.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets the completed timestamp.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the retry count.</summary>
    public int RetryCount { get; set; }

    /// <summary>Gets or sets the max retries allowed.</summary>
    public int MaxRetries { get; set; }

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>Gets or sets the source site ("aniworld" or "sto").</summary>
    public string Source { get; set; } = "aniworld";

    /// <summary>Gets or sets the username of the user who queued the download.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the insertion order for stable sorting.</summary>
    [JsonIgnore]
    public long SequenceNumber { get; set; }

    /// <summary>Gets or sets the cancellation token source.</summary>
    [JsonIgnore]
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>
/// Download status enum.
/// </summary>
public enum DownloadStatus
{
    /// <summary>Queued for download.</summary>
    Queued,

    /// <summary>Resolving provider links.</summary>
    Resolving,

    /// <summary>Extracting stream URL.</summary>
    Extracting,

    /// <summary>Downloading with ffmpeg.</summary>
    Downloading,

    /// <summary>Completed successfully.</summary>
    Completed,

    /// <summary>Download failed.</summary>
    Failed,

    /// <summary>Download cancelled.</summary>
    Cancelled,

    /// <summary>Waiting to retry after failure.</summary>
    Retrying,
}
