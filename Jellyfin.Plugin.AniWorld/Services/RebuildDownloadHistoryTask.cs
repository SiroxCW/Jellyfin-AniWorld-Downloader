using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniWorld.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Jellyfin scheduled task that rebuilds the download history database by scanning
/// all configured download paths for existing .mkv files. This fixes the "shows as
/// undownloaded" problem when the DB is out of sync with what's actually on disk.
///
/// Matching is based on the sanitized series title (= folder name on disk) + season + episode,
/// which is the same key used by the download and duplicate-detection logic.
/// No URL guessing is needed — the folder name IS the sanitized title.
///
/// The task:
/// 1. Reads all existing completed records (to preserve original episode URLs and metadata).
/// 2. Clears all completed records from the database.
/// 3. Scans every configured download path for .mkv files.
/// 4. Re-inserts records for files that still exist, preserving original metadata where possible.
/// 5. Creates new records for files found on disk without prior history.
///
/// Must be triggered manually from the Jellyfin Dashboard → Scheduled Tasks.
/// </summary>
public class RebuildDownloadHistoryTask : IScheduledTask
{
    private readonly ILogger<RebuildDownloadHistoryTask> _logger;
    private readonly DownloadHistoryService _historyService;

    private static readonly Regex SeasonFolderPattern = new(
        @"^Season\s+(\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpecialsFolderPattern = new(
        @"^Specials$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EpisodeFilePattern = new(
        @"S(\d{2,})E(\d{2,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <inheritdoc />
    public string Name => "Rebuild Download History";

    /// <inheritdoc />
    public string Key => "AniWorldRebuildDownloadHistory";

    /// <inheritdoc />
    public string Description =>
        "Scans all configured download paths for existing .mkv files and rebuilds the download " +
        "history database. Fixes checkmarks for files that exist on disk but are not tracked. " +
        "Must be triggered manually.";

    /// <inheritdoc />
    public string Category => "AniWorld Downloader";

    /// <summary>
    /// Initializes a new instance of the <see cref="RebuildDownloadHistoryTask"/> class.
    /// </summary>
    public RebuildDownloadHistoryTask(
        ILogger<RebuildDownloadHistoryTask> logger,
        DownloadHistoryService historyService)
    {
        _logger = logger;
        _historyService = historyService;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting download history rebuild...");
        progress.Report(0);

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogError("Plugin configuration is null, cannot rebuild history");
            return;
        }

        // Step 1: Read all existing completed records and index by output_path.
        // Multiple records can exist per path (one per language), so use a list.
        var existingRecords = _historyService.GetAllCompletedRecords();
        var recordsByPath = new Dictionary<string, List<DownloadHistoryRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in existingRecords)
        {
            if (!string.IsNullOrEmpty(record.OutputPath))
            {
                if (!recordsByPath.TryGetValue(record.OutputPath, out var list))
                {
                    list = new List<DownloadHistoryRecord>();
                    recordsByPath[record.OutputPath] = list;
                }

                list.Add(record);
            }
        }

        _logger.LogInformation("Found {Count} existing completed records in database", existingRecords.Count);
        progress.Report(5);

        // Step 2: Clear all completed records
        var deletedCount = _historyService.DeleteAllCompleted();
        _logger.LogInformation("Cleared {Count} completed records from database", deletedCount);
        progress.Report(10);

        // Step 3: Collect all (basePath, language, source) scan targets
        var scanTargets = CollectScanTargets(config);
        _logger.LogInformation("Collected {Count} download path(s) to scan", scanTargets.Count);

        if (scanTargets.Count == 0)
        {
            _logger.LogWarning("No download paths configured. Nothing to scan.");
            progress.Report(100);
            return;
        }

        // Step 4: Scan each path and rebuild records.
        // A file is processed once PER language target — if German Sub and English Sub both
        // point to the same folder, each file gets a record for each language (= both flags shown).
        // Dedup key is file+language to avoid true duplicates while allowing multi-language entries.
        int recordCount = 0;
        int totalFiles = 0;
        var processedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var countedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        double progressPerTarget = 85.0 / scanTargets.Count;
        int targetIndex = 0;

        foreach (var (basePath, language, source) in scanTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(basePath))
            {
                _logger.LogDebug("Download path does not exist, skipping: {Path}", basePath);
                targetIndex++;
                progress.Report(10 + (progressPerTarget * targetIndex));
                continue;
            }

            _logger.LogInformation("Scanning {Source} path for language '{Lang}': {Path}", source, language, basePath);

            foreach (var mkvFile in Directory.EnumerateFiles(basePath, "*.mkv", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Dedup by file+language — allows the same file to be recorded for multiple languages
                var dedupeKey = $"{mkvFile}|{language}";
                if (!processedKeys.Add(dedupeKey))
                {
                    continue;
                }

                if (countedFiles.Add(mkvFile))
                {
                    totalFiles++;
                }

                // Parse series/season/episode from path structure
                var parsed = ParseFileInfo(basePath, mkvFile);
                if (parsed == null)
                {
                    _logger.LogDebug("Could not parse episode info from: {Path}", mkvFile);
                    continue;
                }

                var (seriesName, season, episode) = parsed.Value;
                var fileInfo = new FileInfo(mkvFile);

                // Try to preserve episode_url from an old record with the same language
                var episodeUrl = string.Empty;
                if (recordsByPath.TryGetValue(mkvFile, out var oldRecords))
                {
                    var match = oldRecords.Find(r =>
                        string.Equals(r.Language, language, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        episodeUrl = match.EpisodeUrl;
                    }
                }

                // seriesName is the folder name which IS the sanitized title —
                // the same value that SanitizeFileName(originalTitle) produces.
                // Always use the CURRENT scan target's language, not the old record's.
                _historyService.InsertFileRecord(
                    episodeUrl: episodeUrl,
                    seriesTitle: seriesName,
                    season: season,
                    episode: episode,
                    language: language,
                    outputPath: mkvFile,
                    fileSizeBytes: fileInfo.Length,
                    source: source);

                recordCount++;
            }

            targetIndex++;
            progress.Report(10 + (progressPerTarget * targetIndex));
        }

        progress.Report(100);
        _logger.LogInformation(
            "Download history rebuild complete. " +
            "Scanned {TotalFiles} file(s), created {Records} record(s).",
            totalFiles, recordCount);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // No automatic triggers — manual only
        return Enumerable.Empty<TaskTriggerInfo>();
    }

    /// <summary>
    /// Collects all (basePath, language, source) tuples from the plugin configuration.
    /// </summary>
    private static List<(string BasePath, string Language, string Source)> CollectScanTargets(PluginConfiguration config)
    {
        var targets = new List<(string, string, string)>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Helper to add a target, avoiding duplicate base paths with different languages
        // (we keep all unique path+language combos so each file gets the right language)
        void AddTarget(string path, string language, string source)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var key = $"{path}|{language}|{source}";
            if (seenPaths.Add(key))
            {
                targets.Add((path, language, source));
            }
        }

        // AniWorld per-language paths
        foreach (var (langKey, path) in config.AniWorldConfig.DownloadPaths)
        {
            AddTarget(path, langKey, "aniworld");
        }

        // AniWorld general/legacy path
        if (!string.IsNullOrEmpty(config.AniWorldConfig.DownloadPath))
        {
            AddTarget(config.AniWorldConfig.DownloadPath, config.GetPreferredLanguage("aniworld"), "aniworld");
        }

        // STO per-language paths
        foreach (var (langKey, path) in config.StoConfig.DownloadPaths)
        {
            AddTarget(path, langKey, "sto");
        }

        // STO general/legacy path
        if (!string.IsNullOrEmpty(config.StoConfig.DownloadPath))
        {
            AddTarget(config.StoConfig.DownloadPath, config.GetPreferredLanguage("sto"), "sto");
        }

        // HiAnime per-language paths
        foreach (var (langKey, path) in config.HiAnimeConfig.DownloadPaths)
        {
            AddTarget(path, langKey, "hianime");
        }

        // Legacy global DownloadPath
        if (!string.IsNullOrEmpty(config.DownloadPath))
        {
            AddTarget(config.DownloadPath, config.PreferredLanguage, "aniworld");
        }

        return targets;
    }

    /// <summary>
    /// Parses series name, season, and episode from a file path relative to the base download path.
    /// Expected structure: {basePath}/{SeriesName}/Season XX/{file} - SXXEXX[...].mkv
    /// or: {basePath}/{SeriesName}/Specials/{file} - S00EXX[...].mkv
    /// </summary>
    private static (string SeriesName, int Season, int Episode)? ParseFileInfo(string basePath, string filePath)
    {
        var relativePath = Path.GetRelativePath(basePath, filePath);
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Need at least: SeriesName / Season XX / filename.mkv
        if (parts.Length < 3)
        {
            return null;
        }

        var seriesName = parts[0];
        var seasonFolderName = parts[1];
        var fileName = Path.GetFileNameWithoutExtension(parts[^1]);

        // Parse season from folder name
        int season;
        var seasonMatch = SeasonFolderPattern.Match(seasonFolderName);
        if (seasonMatch.Success)
        {
            season = int.Parse(seasonMatch.Groups[1].Value);
        }
        else if (SpecialsFolderPattern.IsMatch(seasonFolderName))
        {
            season = 0;
        }
        else
        {
            return null;
        }

        // Parse episode from filename
        var epMatch = EpisodeFilePattern.Match(fileName);
        if (!epMatch.Success)
        {
            return null;
        }

        var episode = int.Parse(epMatch.Groups[2].Value);
        return (seriesName, season, episode);
    }
}
