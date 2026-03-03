using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniWorld.Extractors;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Manages downloads from aniworld.to using ffmpeg.
/// Supports retry with exponential backoff, provider fallback,
/// automatic Jellyfin library scanning after completion,
/// and persistent download history via SQLite.
/// </summary>
public class DownloadService
{
    private const int DefaultMaxRetries = 3;
    private const int BaseRetryDelayMs = 3000;

    private static readonly Regex SeasonEpisodeFromUrl = new(
        @"/staffel-(?<season>\d+)/episode-(?<episode>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MovieFromUrl = new(
        @"/filme/film-(?<num>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AniWorldService _aniWorldService;
    private readonly DownloadHistoryService _historyService;
    private readonly IEnumerable<IStreamExtractor> _extractors;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<DownloadService> _logger;
    private readonly ConcurrentDictionary<string, DownloadTask> _activeTasks = new();
    private readonly SemaphoreSlim _downloadSemaphore;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadService"/> class.
    /// </summary>
    public DownloadService(
        AniWorldService aniWorldService,
        DownloadHistoryService historyService,
        IEnumerable<IStreamExtractor> extractors,
        ILibraryMonitor libraryMonitor,
        ILogger<DownloadService> logger)
    {
        _aniWorldService = aniWorldService;
        _historyService = historyService;
        _extractors = extractors;
        _libraryMonitor = libraryMonitor;
        _logger = logger;

        var maxDownloads = Plugin.Instance?.Configuration.MaxConcurrentDownloads ?? 2;
        _downloadSemaphore = new SemaphoreSlim(maxDownloads, maxDownloads);

        // Mark any downloads that were in-progress when Jellyfin last shut down
        _historyService.MarkInterruptedDownloads();
    }

    /// <summary>
    /// Gets all active download tasks (in-memory, currently running).
    /// </summary>
    public List<DownloadTask> GetActiveDownloads()
    {
        return _activeTasks.Values.ToList();
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
    /// </summary>
    public bool IsAlreadyDownloaded(string episodeUrl, string language)
    {
        return _historyService.IsAlreadyDownloaded(episodeUrl, language);
    }

    /// <summary>
    /// Starts a download for an episode.
    /// </summary>
    public Task<string> StartDownloadAsync(
        string episodeUrl,
        string languageKey,
        string provider,
        string outputPath,
        string seriesTitle,
        CancellationToken cancellationToken = default)
    {
        var taskId = Guid.NewGuid().ToString("N")[..12];

        // Parse season/episode from URL for history tracking
        var (season, episode) = ParseSeasonEpisode(episodeUrl);

        var task = new DownloadTask
        {
            Id = taskId,
            EpisodeUrl = episodeUrl,
            Provider = provider,
            Language = languageKey,
            OutputPath = outputPath,
            SeriesTitle = seriesTitle,
            Season = season,
            Episode = episode,
            Status = DownloadStatus.Queued,
            StartedAt = DateTime.UtcNow,
            MaxRetries = Plugin.Instance?.Configuration.MaxRetries ?? DefaultMaxRetries,
        };

        _activeTasks[taskId] = task;

        // Persist initial state to SQLite
        _historyService.SaveDownload(task, seriesTitle, season, episode);

        // Run in background
        _ = Task.Run(async () => await ExecuteDownloadWithRetryAsync(task, cancellationToken).ConfigureAwait(false), cancellationToken);

        return Task.FromResult(taskId);
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

            // Clean up partial file regardless of size
            CleanupFileOnCancel(task.OutputPath);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes a completed/failed/cancelled download from the active list.
    /// </summary>
    public bool RemoveDownload(string taskId)
    {
        if (_activeTasks.TryRemove(taskId, out var task))
        {
            if (task.Status == DownloadStatus.Downloading)
            {
                task.CancellationSource?.Cancel();
            }

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
            task.Status = DownloadStatus.Queued;
            task.Error = null;
            task.RetryCount = 0;
            task.Progress = 0;

            _historyService.UpdateDownload(task);

            _ = Task.Run(async () => await ExecuteDownloadWithRetryAsync(task, CancellationToken.None).ConfigureAwait(false));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Wraps the download execution with retry logic and exponential backoff.
    /// </summary>
    private async Task ExecuteDownloadWithRetryAsync(DownloadTask task, CancellationToken externalToken)
    {
        task.CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = task.CancellationSource.Token;

        var maxRetries = task.MaxRetries;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (token.IsCancellationRequested)
            {
                task.Status = DownloadStatus.Cancelled;
                _historyService.UpdateDownload(task);
                CleanupFileOnCancel(task.OutputPath);
                return;
            }

            if (attempt > 0)
            {
                task.RetryCount = attempt;
                var delayMs = BaseRetryDelayMs * (int)Math.Pow(2, attempt - 1);
                task.Status = DownloadStatus.Retrying;
                task.Error = $"Retry {attempt}/{maxRetries} in {delayMs / 1000}s...";
                _historyService.UpdateDownload(task);
                _logger.LogInformation("Retry {Attempt}/{MaxRetries} for {Url} in {Delay}ms",
                    attempt, maxRetries, task.EpisodeUrl, delayMs);

                try
                {
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    task.Status = DownloadStatus.Cancelled;
                    _historyService.UpdateDownload(task);
                    CleanupFileOnCancel(task.OutputPath);
                    return;
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
                    return;
                }

                if (task.Status == DownloadStatus.Cancelled)
                {
                    _historyService.UpdateDownload(task);
                    CleanupFileOnCancel(task.OutputPath);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                task.Status = DownloadStatus.Cancelled;
                _historyService.UpdateDownload(task);
                CleanupFileOnCancel(task.OutputPath);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Download attempt {Attempt}/{MaxRetries} failed for {Url}",
                    attempt + 1, maxRetries + 1, task.EpisodeUrl);
                task.Error = ex.Message;

                if (attempt >= maxRetries)
                {
                    task.Status = DownloadStatus.Failed;
                    task.Error = $"Failed after {maxRetries + 1} attempts: {ex.Message}";
                    _historyService.UpdateDownload(task);
                    _logger.LogError(ex, "Download permanently failed for {Url} after {Attempts} attempts",
                        task.EpisodeUrl, maxRetries + 1);

                    CleanupPartialFile(task.OutputPath);
                    return;
                }
            }
        }
    }

    private async Task ExecuteDownloadAsync(DownloadTask task, CancellationToken token)
    {
        bool semaphoreAcquired = false;
        try
        {
            await _downloadSemaphore.WaitAsync(token).ConfigureAwait(false);
            semaphoreAcquired = true;
            task.Status = DownloadStatus.Resolving;
            _historyService.UpdateDownload(task);

            // 1. Get episode details
            var details = await _aniWorldService.GetEpisodeDetailsAsync(task.EpisodeUrl, token).ConfigureAwait(false);
            task.EpisodeTitle = details.TitleEn ?? details.TitleDe ?? "Unknown";

            // 2. Rename output path to include episode title if available
            var newPath = InsertEpisodeTitleInPath(task.OutputPath, task.EpisodeTitle);
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
            var embedUrl = await _aniWorldService.ResolveRedirectAsync(redirectUrl, token).ConfigureAwait(false);
            _logger.LogInformation("Resolved to embed URL: {EmbedUrl}", embedUrl);

            // 4. Extract direct stream URL
            var extractor = _extractors.FirstOrDefault(e =>
                e.ProviderName.Equals(task.Provider, StringComparison.OrdinalIgnoreCase));

            if (extractor == null)
            {
                throw new InvalidOperationException($"No extractor available for provider: {task.Provider}");
            }

            task.Status = DownloadStatus.Extracting;
            _historyService.UpdateDownload(task);
            var streamUrl = await extractor.GetDirectLinkAsync(embedUrl, token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(streamUrl))
            {
                throw new InvalidOperationException("Failed to extract stream URL from provider");
            }

            _logger.LogInformation("Stream URL: {StreamUrl}", streamUrl);

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
        finally
        {
            if (semaphoreAcquired)
            {
                _downloadSemaphore.Release();
            }
        }
    }

    /// <summary>
    /// Inserts the episode title into the filename.
    /// Transforms "SeriesName - S01E01.mkv" into "SeriesName - S01E01 - Episode Title.mkv".
    /// </summary>
    private static string InsertEpisodeTitleInPath(string outputPath, string episodeTitle)
    {
        if (string.IsNullOrWhiteSpace(episodeTitle) || episodeTitle == "Unknown")
        {
            return outputPath;
        }

        var dir = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(outputPath);
        var ext = Path.GetExtension(outputPath);

        // Match pattern like "SeriesName - S01E01" or "SeriesName - S00E01"
        var match = Regex.Match(fileName, @"^(.+ - S\d{2}E\d{2})$");
        if (match.Success)
        {
            var safeTitle = SanitizeFileName(episodeTitle);
            // Truncate very long titles to keep filenames reasonable
            if (safeTitle.Length > 80)
            {
                safeTitle = safeTitle[..77] + "...";
            }

            var newName = $"{match.Groups[1].Value} - {safeTitle}{ext}";
            return Path.Combine(dir, newName);
        }

        return outputPath;
    }

    /// <summary>
    /// Sanitizes a file/folder name by removing invalid and problematic characters.
    /// Strips characters that cause issues on Windows, SMB shares, and some media players:
    /// : ? ! * " &lt; &gt; | in addition to OS-level invalid chars.
    /// Apostrophes are preserved as simple '.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        // Additional chars that are problematic on Windows/SMB/media players
        var extraInvalid = new[] { ':', '?', '!', '*', '"', '<', '>', '|' };
        var sanitized = new string(name
            .Where(c => !invalid.Contains(c) && !extraInvalid.Contains(c))
            .ToArray());
        // Collapse multiple spaces that may result from removals
        sanitized = Regex.Replace(sanitized, @"\s{2,}", " ");
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized.Trim();
    }

    /// <summary>
    /// Parses season and episode numbers from an aniworld.to URL.
    /// </summary>
    private static (int season, int episode) ParseSeasonEpisode(string url)
    {
        var seMatch = SeasonEpisodeFromUrl.Match(url);
        if (seMatch.Success)
        {
            return (int.Parse(seMatch.Groups["season"].Value), int.Parse(seMatch.Groups["episode"].Value));
        }

        var movieMatch = MovieFromUrl.Match(url);
        if (movieMatch.Success)
        {
            return (0, int.Parse(movieMatch.Groups["num"].Value));
        }

        return (0, 0);
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
    /// Cleans up a partial/failed download file (only removes very small stubs).
    /// Also cleans up empty parent directories if the file was removed.
    /// </summary>
    private void CleanupPartialFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length < 1024)
                {
                    File.Delete(filePath);
                    _logger.LogDebug("Cleaned up partial file: {Path}", filePath);
                    CleanupEmptyParentDirectories(filePath);
                }
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
    /// Also removes empty parent directories (season folder, series folder)
    /// to prevent Jellyfin from displaying phantom entries.
    /// </summary>
    private void CleanupFileOnCancel(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return;
            }

            var size = new FileInfo(filePath).Length;
            File.Delete(filePath);
            _logger.LogInformation("Cleaned up cancelled download file: {Path} ({Size} bytes)", filePath, size);

            // Clean up empty parent directories (season folder → series folder)
            CleanupEmptyParentDirectories(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup cancelled file: {Path}", filePath);
        }
    }

    /// <summary>
    /// Removes empty parent directories up to (but not including) the configured download base path.
    /// Walks up from the file's directory: if Season folder is empty, delete it.
    /// Then if series folder is empty, delete it too.
    /// Stops at the configured download root to never delete the base path itself.
    /// </summary>
    private void CleanupEmptyParentDirectories(string filePath)
    {
        var basePath = Plugin.Instance?.Configuration.DownloadPath ?? string.Empty;
        if (string.IsNullOrEmpty(basePath))
        {
            return;
        }

        try
        {
            var normalizedBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar);
            var dir = Path.GetDirectoryName(filePath);

            // Walk up at most 2 levels (season folder → series folder)
            for (int i = 0; i < 2 && !string.IsNullOrEmpty(dir); i++)
            {
                var normalizedDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);

                // Never delete the download base path itself
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
                    // Directory not empty, stop walking up
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

    /// <summary>
    /// Checks if a directory is empty (no files or subdirectories).
    /// </summary>
    private static bool IsDirectoryEmpty(string path)
    {
        return !Directory.EnumerateFileSystemEntries(path).Any();
    }

    private async Task DownloadWithFfmpegAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        var ffmpegPath = FindFfmpeg();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            throw new InvalidOperationException("ffmpeg not found. Please ensure ffmpeg is installed.");
        }

        var args = $"-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 " +
                   $"-i \"{task.StreamUrl}\" -c copy -bsf:a aac_adtstoasc -y \"{task.OutputPath}\"";

        _logger.LogDebug("Running: {Ffmpeg} {Args}", ffmpegPath, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();

        var progressPattern = new Regex(@"time=(?<time>\d+:\d+:\d+\.\d+)", RegexOptions.Compiled);
        var durationPattern = new Regex(@"Duration:\s*(?<dur>\d+:\d+:\d+\.\d+)", RegexOptions.Compiled);
        var sizePattern = new Regex(@"size=\s*(?<size>\d+)kB", RegexOptions.Compiled);
        TimeSpan? totalDuration = null;

        _ = Task.Run(async () =>
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

    private static string? FindFfmpeg()
    {
        var paths = new[]
        {
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "ffmpeg",
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "ffmpeg",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });

            if (process != null)
            {
                var result = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (!string.IsNullOrEmpty(result) && File.Exists(result))
                {
                    return result;
                }
            }
        }
        catch
        {
            // Ignore
        }

        return null;
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
