using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniWorld.Helpers;
using Jellyfin.Plugin.AniWorld.Services;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AniWorld.Api;

/// <summary>
/// REST API controller for AniWorld Downloader plugin.
/// </summary>
[ApiController]
[Route("AniWorld")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class AniWorldController : ControllerBase
{
    private readonly AniWorldService _aniWorldService;
    private readonly DownloadService _downloadService;
    private readonly DownloadHistoryService _historyService;
    private readonly IServerConfigurationManager _configManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniWorldController"/> class.
    /// </summary>
    public AniWorldController(
        AniWorldService aniWorldService,
        DownloadService downloadService,
        DownloadHistoryService historyService,
        IServerConfigurationManager configManager)
    {
        _aniWorldService = aniWorldService;
        _downloadService = downloadService;
        _historyService = historyService;
        _configManager = configManager;
    }

    // ── Search & Browse ─────────────────────────────────────────────

    /// <summary>
    /// Search for anime on aniworld.to.
    /// </summary>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<SearchResult>>> Search(
        [Required] string query,
        CancellationToken cancellationToken)
    {
        var results = await _aniWorldService.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        return Ok(results);
    }

    /// <summary>
    /// Get series information.
    /// </summary>
    [HttpGet("Series")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SeriesInfo>> GetSeries(
        [Required] string url,
        CancellationToken cancellationToken)
    {
        if (!UrlValidator.IsValidAniWorldUrl(url))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to URLs are accepted.");
        }

        var info = await _aniWorldService.GetSeriesInfoAsync(url, cancellationToken).ConfigureAwait(false);
        return Ok(info);
    }

    /// <summary>
    /// Get episodes for a season.
    /// </summary>
    [HttpGet("Episodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<EpisodeRef>>> GetEpisodes(
        [Required] string url,
        CancellationToken cancellationToken)
    {
        if (!UrlValidator.IsValidAniWorldUrl(url))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to URLs are accepted.");
        }

        var episodes = await _aniWorldService.GetEpisodesAsync(url, cancellationToken).ConfigureAwait(false);
        return Ok(episodes);
    }

    /// <summary>
    /// Get episode details (provider links).
    /// </summary>
    [HttpGet("Episode")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EpisodeDetails>> GetEpisodeDetails(
        [Required] string url,
        CancellationToken cancellationToken)
    {
        if (!UrlValidator.IsValidAniWorldUrl(url))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to URLs are accepted.");
        }

        var details = await _aniWorldService.GetEpisodeDetailsAsync(url, cancellationToken).ConfigureAwait(false);
        return Ok(details);
    }

    // ── Downloads ───────────────────────────────────────────────────

    /// <summary>
    /// Start downloading an episode. Automatically constructs the proper file path
    /// following Jellyfin naming conventions: SeriesName/Season XX/SeriesName - SXXEXX - EpisodeTitle.mkv
    /// </summary>
    [HttpPost("Download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DownloadTask>> StartDownload(
        [FromBody] DownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.EpisodeUrl))
        {
            return BadRequest("Episode URL is required");
        }

        if (!UrlValidator.IsValidAniWorldUrl(request.EpisodeUrl))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to URLs are accepted.");
        }

        var config = Plugin.Instance?.Configuration;
        var basePath = config?.DownloadPath ?? string.Empty;

        if (string.IsNullOrEmpty(basePath))
        {
            return BadRequest("No download path configured. Please set a download path in the plugin settings.");
        }

        var language = request.LanguageKey ?? config?.PreferredLanguage ?? "1";
        var provider = request.Provider ?? config?.PreferredProvider ?? "VOE";
        var seriesTitle = request.SeriesTitle ?? "Unknown Anime";

        // Check if already downloaded (duplicate detection)
        if (!request.Force && _downloadService.IsAlreadyDownloaded(request.EpisodeUrl, language))
        {
            return BadRequest("This episode has already been downloaded with this language. Set 'Force' to true to re-download.");
        }

        var outputPath = PathHelper.BuildOutputPath(basePath, seriesTitle, request.EpisodeUrl);

        var taskId = await _downloadService.StartDownloadAsync(
            request.EpisodeUrl,
            language,
            provider,
            outputPath,
            seriesTitle,
            cancellationToken).ConfigureAwait(false);

        var task = _downloadService.GetDownload(taskId);
        return Ok(task);
    }

    /// <summary>
    /// Start downloading all episodes in a season (batch download).
    /// </summary>
    [HttpPost("DownloadSeason")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<DownloadTask>>> DownloadSeason(
        [FromBody] BatchDownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.SeasonUrl))
        {
            return BadRequest("Season URL is required");
        }

        if (!UrlValidator.IsValidAniWorldUrl(request.SeasonUrl))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to URLs are accepted.");
        }

        var config = Plugin.Instance?.Configuration;
        var basePath = config?.DownloadPath ?? string.Empty;

        if (string.IsNullOrEmpty(basePath))
        {
            return BadRequest("No download path configured. Please set a download path in the plugin settings.");
        }

        var language = request.LanguageKey ?? config?.PreferredLanguage ?? "1";
        var provider = request.Provider ?? config?.PreferredProvider ?? "VOE";
        var seriesTitle = request.SeriesTitle ?? "Unknown Anime";

        var episodes = await _aniWorldService.GetEpisodesAsync(request.SeasonUrl, cancellationToken).ConfigureAwait(false);

        if (episodes.Count == 0)
        {
            return BadRequest("No episodes found for this season.");
        }

        var tasks = new List<DownloadTask>();

        foreach (var ep in episodes)
        {
            var outputPath = PathHelper.BuildOutputPath(basePath, seriesTitle, ep.Url);

            // Skip if file already exists on disk
            if (System.IO.File.Exists(outputPath))
            {
                continue;
            }

            // Skip if already downloaded in history (unless forced)
            if (_downloadService.IsAlreadyDownloaded(ep.Url, language))
            {
                continue;
            }

            var taskId = await _downloadService.StartDownloadAsync(
                ep.Url,
                language,
                provider,
                outputPath,
                seriesTitle,
                cancellationToken).ConfigureAwait(false);

            var task = _downloadService.GetDownload(taskId);
            if (task != null)
            {
                tasks.Add(task);
            }
        }

        return Ok(tasks);
    }

    /// <summary>
    /// Start downloading all episodes across all seasons of a series (full series batch download).
    /// </summary>
    [HttpPost("DownloadAll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<object>> DownloadAllSeasons(
        [FromBody] FullSeriesDownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.SeriesUrl))
        {
            return BadRequest("Series URL is required");
        }

        if (!UrlValidator.IsValidAniWorldUrl(request.SeriesUrl))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to URLs are accepted.");
        }

        var config = Plugin.Instance?.Configuration;
        var basePath = config?.DownloadPath ?? string.Empty;

        if (string.IsNullOrEmpty(basePath))
        {
            return BadRequest("No download path configured. Please set a download path in the plugin settings.");
        }

        var language = request.LanguageKey ?? config?.PreferredLanguage ?? "1";
        var provider = request.Provider ?? config?.PreferredProvider ?? "VOE";

        // Get series info to enumerate all seasons
        var seriesInfo = await _aniWorldService.GetSeriesInfoAsync(request.SeriesUrl, cancellationToken).ConfigureAwait(false);
        var seriesTitle = request.SeriesTitle ?? seriesInfo.Title ?? "Unknown Anime";

        if (seriesInfo.Seasons == null || seriesInfo.Seasons.Count == 0)
        {
            return BadRequest("No seasons found for this series.");
        }

        var allTasks = new List<DownloadTask>();
        var skippedCount = 0;

        foreach (var season in seriesInfo.Seasons)
        {
            var episodes = await _aniWorldService.GetEpisodesAsync(season.Url, cancellationToken).ConfigureAwait(false);

            foreach (var ep in episodes)
            {
                var outputPath = PathHelper.BuildOutputPath(basePath, seriesTitle, ep.Url);

                if (System.IO.File.Exists(outputPath) || _downloadService.IsAlreadyDownloaded(ep.Url, language))
                {
                    skippedCount++;
                    continue;
                }

                var taskId = await _downloadService.StartDownloadAsync(
                    ep.Url,
                    language,
                    provider,
                    outputPath,
                    seriesTitle,
                    cancellationToken).ConfigureAwait(false);

                var task = _downloadService.GetDownload(taskId);
                if (task != null)
                {
                    allTasks.Add(task);
                }
            }
        }

        // Also handle movies if they exist
        if (seriesInfo.HasMovies)
        {
            var movieUrl = request.SeriesUrl.TrimEnd('/') + "/filme";
            var movies = await _aniWorldService.GetEpisodesAsync(movieUrl, cancellationToken).ConfigureAwait(false);

            foreach (var ep in movies)
            {
                var outputPath = PathHelper.BuildOutputPath(basePath, seriesTitle, ep.Url);

                if (System.IO.File.Exists(outputPath) || _downloadService.IsAlreadyDownloaded(ep.Url, language))
                {
                    skippedCount++;
                    continue;
                }

                var taskId = await _downloadService.StartDownloadAsync(
                    ep.Url,
                    language,
                    provider,
                    outputPath,
                    seriesTitle,
                    cancellationToken).ConfigureAwait(false);

                var task = _downloadService.GetDownload(taskId);
                if (task != null)
                {
                    allTasks.Add(task);
                }
            }
        }

        return Ok(new
        {
            queued = allTasks.Count,
            skipped = skippedCount,
            seasons = seriesInfo.Seasons.Count,
            tasks = allTasks
        });
    }

    /// <summary>
    /// Get all active/recent downloads (in-memory).
    /// </summary>
    [HttpGet("Downloads")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<DownloadTask>> GetDownloads()
    {
        return Ok(_downloadService.GetActiveDownloads());
    }

    /// <summary>
    /// Get a specific download task.
    /// </summary>
    [HttpGet("Downloads/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DownloadTask> GetDownload(string id)
    {
        var task = _downloadService.GetDownload(id);
        if (task == null)
        {
            return NotFound();
        }

        return Ok(task);
    }

    /// <summary>
    /// Cancel a download.
    /// </summary>
    [HttpDelete("Downloads/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult CancelDownload(string id)
    {
        if (_downloadService.CancelDownload(id))
        {
            return Ok(new { success = true });
        }

        return NotFound();
    }

    /// <summary>
    /// Clear completed/failed downloads from the active list.
    /// </summary>
    [HttpPost("Downloads/Clear")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ClearCompleted()
    {
        var cleared = _downloadService.ClearCompleted();
        return Ok(new { cleared });
    }

    /// <summary>
    /// Retry a failed download.
    /// </summary>
    [HttpPost("Downloads/{id}/Retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RetryDownload(string id)
    {
        if (_downloadService.RetryDownload(id))
        {
            return Ok(new { success = true });
        }

        return NotFound(new { error = "Download not found or not in failed state" });
    }

    // ── History & Stats ─────────────────────────────────────────────

    /// <summary>
    /// Get persistent download history from the database.
    /// Survives Jellyfin restarts unlike the active downloads list.
    /// </summary>
    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<DownloadHistoryRecord>> GetHistory(
        int limit = 50,
        int offset = 0,
        string? status = null,
        string? series = null)
    {
        var records = _historyService.GetHistory(limit, offset, status, series);
        return Ok(records);
    }

    /// <summary>
    /// Get download statistics (total downloads, bytes, series count, etc).
    /// </summary>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DownloadStats> GetStats()
    {
        var stats = _historyService.GetStats();
        return Ok(stats);
    }

    /// <summary>
    /// Get the list of unique series that have been downloaded.
    /// </summary>
    [HttpGet("Series/Downloaded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<string>> GetDownloadedSeries()
    {
        var series = _historyService.GetDownloadedSeries();
        return Ok(series);
    }

    /// <summary>
    /// Check if an episode has already been downloaded.
    /// </summary>
    [HttpGet("IsDownloaded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> CheckIsDownloaded([Required] string url, string? language = null)
    {
        if (!UrlValidator.IsValidAniWorldUrl(url))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to URLs are accepted.");
        }

        var lang = language ?? Plugin.Instance?.Configuration.PreferredLanguage ?? "1";
        var downloaded = _downloadService.IsAlreadyDownloaded(url, lang);
        return Ok(new { downloaded, url, language = lang });
    }

    /// <summary>
    /// Delete a specific history record.
    /// </summary>
    [HttpDelete("History/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteHistoryRecord(string id)
    {
        if (_historyService.DeleteRecord(id))
        {
            return Ok(new { success = true });
        }

        return NotFound();
    }

    /// <summary>
    /// Clean up old history records.
    /// </summary>
    [HttpPost("History/Cleanup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult CleanupHistory(int days = 90)
    {
        var removed = _historyService.CleanupOld(days);
        return Ok(new { removed });
    }
}

/// <summary>
/// Download request model.
/// </summary>
public class DownloadRequest
{
    /// <summary>Gets or sets the episode URL.</summary>
    public string EpisodeUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the language key.</summary>
    public string? LanguageKey { get; set; }

    /// <summary>Gets or sets the provider.</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets the series title for file naming.</summary>
    public string? SeriesTitle { get; set; }

    /// <summary>Gets or sets whether to force re-download even if already downloaded.</summary>
    public bool Force { get; set; }
}

/// <summary>
/// Batch download request for an entire season.
/// </summary>
public class BatchDownloadRequest
{
    /// <summary>Gets or sets the season URL.</summary>
    public string SeasonUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the language key.</summary>
    public string? LanguageKey { get; set; }

    /// <summary>Gets or sets the provider.</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets the series title for file naming.</summary>
    public string? SeriesTitle { get; set; }
}

/// <summary>
/// Full series download request — downloads all seasons.
/// </summary>
public class FullSeriesDownloadRequest
{
    /// <summary>Gets or sets the series URL.</summary>
    public string SeriesUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the language key.</summary>
    public string? LanguageKey { get; set; }

    /// <summary>Gets or sets the provider.</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets the series title for file naming.</summary>
    public string? SeriesTitle { get; set; }
}
