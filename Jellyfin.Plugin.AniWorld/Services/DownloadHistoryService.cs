using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Persists download history to SQLite so downloads survive Jellyfin restarts.
/// Stores completed downloads, tracks duplicates, and provides stats.
/// </summary>
public class DownloadHistoryService : IDisposable
{
    private readonly ILogger<DownloadHistoryService> _logger;
    private readonly SqliteConnection _db;
    private readonly object _dbLock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadHistoryService"/> class.
    /// </summary>
    public DownloadHistoryService(ILogger<DownloadHistoryService> logger)
    {
        _logger = logger;

        var pluginDataDir = Plugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "jellyfin", "plugins", "AniWorldDownloader");

        Directory.CreateDirectory(pluginDataDir);
        var dbPath = Path.Combine(pluginDataDir, "downloads.db");

        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();

        InitializeSchema();
        _logger.LogInformation("Download history database initialized at {Path}", dbPath);
    }

    private void InitializeSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS download_history (
                id              TEXT PRIMARY KEY,
                episode_url     TEXT NOT NULL,
                series_title    TEXT NOT NULL DEFAULT '',
                episode_title   TEXT DEFAULT '',
                season          INTEGER DEFAULT 0,
                episode         INTEGER DEFAULT 0,
                provider        TEXT NOT NULL DEFAULT '',
                language        TEXT NOT NULL DEFAULT '',
                output_path     TEXT NOT NULL DEFAULT '',
                status          TEXT NOT NULL DEFAULT 'Queued',
                progress        INTEGER DEFAULT 0,
                file_size_bytes INTEGER DEFAULT 0,
                error           TEXT,
                retry_count     INTEGER DEFAULT 0,
                max_retries     INTEGER DEFAULT 3,
                started_at      TEXT NOT NULL,
                completed_at    TEXT,
                created_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE INDEX IF NOT EXISTS idx_dh_episode_url ON download_history(episode_url);
            CREATE INDEX IF NOT EXISTS idx_dh_status ON download_history(status);
            CREATE INDEX IF NOT EXISTS idx_dh_series ON download_history(series_title);
            CREATE INDEX IF NOT EXISTS idx_dh_started ON download_history(started_at);
        ";
        cmd.ExecuteNonQuery();

        // Migration: add source column if it doesn't exist
        MigrateAddSourceColumn();
    }

    private void MigrateAddSourceColumn()
    {
        try
        {
            using var checkCmd = _db.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('download_history') WHERE name='source'";
            var exists = Convert.ToInt64(checkCmd.ExecuteScalar()) > 0;

            if (!exists)
            {
                using var alterCmd = _db.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE download_history ADD COLUMN source TEXT NOT NULL DEFAULT 'aniworld'";
                alterCmd.ExecuteNonQuery();
                _logger.LogInformation("Migrated download_history: added source column");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to migrate source column (may already exist)");
        }
    }

    /// <summary>
    /// Saves a new download record.
    /// </summary>
    public void SaveDownload(DownloadTask task, string seriesTitle, int season, int episode)
    {
        lock (_dbLock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO download_history
                        (id, episode_url, series_title, episode_title, season, episode,
                         provider, language, output_path, status, progress,
                         file_size_bytes, error, retry_count, max_retries, started_at, completed_at, source)
                    VALUES
                        (@id, @url, @series, @epTitle, @season, @episode,
                         @provider, @language, @path, @status, @progress,
                         @size, @error, @retry, @maxRetry, @started, @completed, @source)
                ";
                cmd.Parameters.AddWithValue("@id", task.Id);
                cmd.Parameters.AddWithValue("@url", task.EpisodeUrl);
                cmd.Parameters.AddWithValue("@series", seriesTitle);
                cmd.Parameters.AddWithValue("@epTitle", task.EpisodeTitle ?? string.Empty);
                cmd.Parameters.AddWithValue("@season", season);
                cmd.Parameters.AddWithValue("@episode", episode);
                cmd.Parameters.AddWithValue("@provider", task.Provider);
                cmd.Parameters.AddWithValue("@language", task.Language);
                cmd.Parameters.AddWithValue("@path", task.OutputPath);
                cmd.Parameters.AddWithValue("@status", task.Status.ToString());
                cmd.Parameters.AddWithValue("@progress", task.Progress);
                cmd.Parameters.AddWithValue("@size", task.FileSizeBytes);
                cmd.Parameters.AddWithValue("@error", (object?)task.Error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@retry", task.RetryCount);
                cmd.Parameters.AddWithValue("@maxRetry", task.MaxRetries);
                cmd.Parameters.AddWithValue("@started", task.StartedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@completed", task.CompletedAt.HasValue
                    ? (object)task.CompletedAt.Value.ToString("o")
                    : DBNull.Value);
                cmd.Parameters.AddWithValue("@source", task.Source ?? "aniworld");
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save download {Id} to history", task.Id);
            }
        }
    }

    /// <summary>
    /// Updates an existing download record's status/progress.
    /// </summary>
    public void UpdateDownload(DownloadTask task)
    {
        lock (_dbLock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    UPDATE download_history SET
                        episode_title = @epTitle,
                        provider = @provider,
                        output_path = @path,
                        status = @status,
                        progress = @progress,
                        file_size_bytes = @size,
                        error = @error,
                        retry_count = @retry,
                        completed_at = @completed,
                        source = @source
                    WHERE id = @id
                ";
                cmd.Parameters.AddWithValue("@id", task.Id);
                cmd.Parameters.AddWithValue("@epTitle", task.EpisodeTitle ?? string.Empty);
                cmd.Parameters.AddWithValue("@provider", task.Provider);
                cmd.Parameters.AddWithValue("@path", task.OutputPath);
                cmd.Parameters.AddWithValue("@status", task.Status.ToString());
                cmd.Parameters.AddWithValue("@progress", task.Progress);
                cmd.Parameters.AddWithValue("@size", task.FileSizeBytes);
                cmd.Parameters.AddWithValue("@error", (object?)task.Error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@retry", task.RetryCount);
                cmd.Parameters.AddWithValue("@completed", task.CompletedAt.HasValue
                    ? (object)task.CompletedAt.Value.ToString("o")
                    : DBNull.Value);
                cmd.Parameters.AddWithValue("@source", task.Source ?? "aniworld");
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update download {Id} in history", task.Id);
            }
        }
    }

    /// <summary>
    /// Checks if an episode has already been completed for a specific language.
    /// </summary>
    public bool IsAlreadyDownloaded(string episodeUrl, string language)
    {
        lock (_dbLock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(1) FROM download_history
                    WHERE episode_url = @url AND language = @lang AND status = 'Completed'
                ";
                cmd.Parameters.AddWithValue("@url", episodeUrl);
                cmd.Parameters.AddWithValue("@lang", language);

                var result = cmd.ExecuteScalar();
                return result is long count && count > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check download history for {Url}", episodeUrl);
                return false;
            }
        }
    }

    /// <summary>
    /// Returns the language of the most recent completed download for this episode.
    /// </summary>
    public string? GetCompletedLanguage(string episodeUrl)
    {
        lock (_dbLock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    SELECT language FROM download_history
                    WHERE episode_url = @url AND status = 'Completed'
                    ORDER BY completed_at DESC
                    LIMIT 1
                ";
                cmd.Parameters.AddWithValue("@url", episodeUrl);

                var result = cmd.ExecuteScalar();
                return result as string;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check download history for {Url}", episodeUrl);
                return null;
            }
        }
    }

    /// <summary>
    /// Returns all distinct languages for which this episode has been successfully downloaded.
    /// </summary>
    public List<string> GetCompletedLanguages(string episodeUrl)
    {
        lock (_dbLock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    SELECT DISTINCT language FROM download_history
                    WHERE episode_url = @url AND status = 'Completed'
                ";
                cmd.Parameters.AddWithValue("@url", episodeUrl);

                var languages = new List<string>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var lang = reader.GetString(0);
                    if (!string.IsNullOrEmpty(lang))
                    {
                        languages.Add(lang);
                    }
                }

                return languages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check download history for {Url}", episodeUrl);
                return new List<string>();
            }
        }
    }

    /// <summary>
    /// Gets the download history, most recent first.
    /// </summary>
    public List<DownloadHistoryRecord> GetHistory(int limit = 50, int offset = 0, string? statusFilter = null, string? seriesFilter = null)
    {
        var records = new List<DownloadHistoryRecord>();
        lock (_dbLock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                var where = "WHERE 1=1";
                if (!string.IsNullOrEmpty(statusFilter))
                {
                    where += " AND status = @status";
                    cmd.Parameters.AddWithValue("@status", statusFilter);
                }

                if (!string.IsNullOrEmpty(seriesFilter))
                {
                    where += " AND series_title LIKE @series";
                    cmd.Parameters.AddWithValue("@series", $"%{seriesFilter}%");
                }

                cmd.CommandText = $@"
                    SELECT id, episode_url, series_title, episode_title, season, episode,
                           provider, language, output_path, status, progress,
                           file_size_bytes, error, retry_count, started_at, completed_at, source
                    FROM download_history
                    {where}
                    ORDER BY started_at DESC
                    LIMIT @limit OFFSET @offset
                ";
                cmd.Parameters.AddWithValue("@limit", limit);
                cmd.Parameters.AddWithValue("@offset", offset);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(new DownloadHistoryRecord
                    {
                        Id = reader.GetString(0),
                        EpisodeUrl = reader.GetString(1),
                        SeriesTitle = reader.GetString(2),
                        EpisodeTitle = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Season = reader.GetInt32(4),
                        Episode = reader.GetInt32(5),
                        Provider = reader.GetString(6),
                        Language = reader.GetString(7),
                        OutputPath = reader.GetString(8),
                        Status = reader.GetString(9),
                        Progress = reader.GetInt32(10),
                        FileSizeBytes = reader.GetInt64(11),
                        Error = reader.IsDBNull(12) ? null : reader.GetString(12),
                        RetryCount = reader.GetInt32(13),
                        StartedAt = reader.GetString(14),
                        CompletedAt = reader.IsDBNull(15) ? null : reader.GetString(15),
                        Source = reader.IsDBNull(16) ? "aniworld" : reader.GetString(16),
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get download history");
            }
        }

        return records;
    }

    /// <summary>
    /// Gets download statistics.
    /// </summary>
    public DownloadStats GetStats()
    {
        var stats = new DownloadStats();
        lock (_dbLock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    SELECT
                        COUNT(*) as total,
                        COALESCE(SUM(CASE WHEN status = 'Completed' THEN 1 ELSE 0 END), 0) as completed,
                        COALESCE(SUM(CASE WHEN status = 'Failed' THEN 1 ELSE 0 END), 0) as failed,
                        COALESCE(SUM(CASE WHEN status = 'Cancelled' THEN 1 ELSE 0 END), 0) as cancelled,
                        COALESCE(SUM(CASE WHEN status = 'Completed' THEN file_size_bytes ELSE 0 END), 0) as total_bytes,
                        COUNT(DISTINCT series_title) as series_count
                    FROM download_history
                ";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    stats.TotalDownloads = reader.GetInt32(0);
                    stats.Completed = reader.GetInt32(1);
                    stats.Failed = reader.GetInt32(2);
                    stats.Cancelled = reader.GetInt32(3);
                    stats.TotalBytes = reader.GetInt64(4);
                    stats.UniqueSeriesCount = reader.GetInt32(5);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get download stats");
            }
        }

        return stats;
    }

    /// <summary>
    /// Gets unique series that have been downloaded.
    /// </summary>
    public List<string> GetDownloadedSeries()
    {
        var series = new List<string>();
        lock (_dbLock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    SELECT DISTINCT series_title FROM download_history
                    WHERE series_title != '' AND status = 'Completed'
                    ORDER BY series_title
                ";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    series.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get downloaded series list");
            }
        }

        return series;
    }

    /// <summary>
    /// Marks any incomplete downloads from a previous session as failed (interrupted by restart).
    /// </summary>
    public int MarkInterruptedDownloads()
    {
        lock (_dbLock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    UPDATE download_history
                    SET status = 'Failed', error = 'Interrupted by server restart'
                    WHERE status IN ('Queued', 'Resolving', 'Extracting', 'Downloading', 'Retrying')
                ";
                var count = cmd.ExecuteNonQuery();
                if (count > 0)
                {
                    _logger.LogWarning("Marked {Count} interrupted download(s) as failed", count);
                }

                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to mark interrupted downloads");
                return 0;
            }
        }
    }

    /// <summary>
    /// Deletes a specific download history record.
    /// </summary>
    public bool DeleteRecord(string id)
    {
        lock (_dbLock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "DELETE FROM download_history WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);
                return cmd.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete history record {Id}", id);
                return false;
            }
        }
    }

    /// <summary>
    /// Clears all history records older than the specified number of days.
    /// </summary>
    public int CleanupOld(int daysOld = 90)
    {
        lock (_dbLock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"
                    DELETE FROM download_history
                    WHERE started_at < datetime('now', @days || ' days')
                      AND status IN ('Completed', 'Failed', 'Cancelled')
                ";
                cmd.Parameters.AddWithValue("@days", $"-{daysOld}");
                return cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup old history");
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _db?.Close();
            _db?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// A record from the download history database.
/// </summary>
public class DownloadHistoryRecord
{
    /// <summary>Gets or sets the download ID.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode URL.</summary>
    public string EpisodeUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the series title.</summary>
    public string SeriesTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode title.</summary>
    public string? EpisodeTitle { get; set; }

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

    /// <summary>Gets or sets the status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the progress (0-100).</summary>
    public int Progress { get; set; }

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the retry count.</summary>
    public int RetryCount { get; set; }

    /// <summary>Gets or sets the started timestamp.</summary>
    public string StartedAt { get; set; } = string.Empty;

    /// <summary>Gets or sets the completed timestamp.</summary>
    public string? CompletedAt { get; set; }

    /// <summary>Gets or sets the source site ("aniworld" or "sto").</summary>
    public string Source { get; set; } = "aniworld";
}

/// <summary>
/// Download statistics.
/// </summary>
public class DownloadStats
{
    /// <summary>Gets or sets the total number of downloads.</summary>
    public int TotalDownloads { get; set; }

    /// <summary>Gets or sets the completed count.</summary>
    public int Completed { get; set; }

    /// <summary>Gets or sets the failed count.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets the cancelled count.</summary>
    public int Cancelled { get; set; }

    /// <summary>Gets or sets the total bytes downloaded.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the unique series count.</summary>
    public int UniqueSeriesCount { get; set; }
}
