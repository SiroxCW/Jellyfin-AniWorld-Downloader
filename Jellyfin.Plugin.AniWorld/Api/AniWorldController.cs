using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
    private static readonly Regex SeasonEpisodeFromUrl = new(
        @"/staffel-(?<season>\d+)/episode-(?<episode>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MovieFromUrl = new(
        @"/filme/film-(?<num>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SeriesSlugFromUrl = new(
        @"/anime/stream/(?<slug>[^/]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AniWorldService _aniWorldService;
    private readonly DownloadService _downloadService;
    private readonly IServerConfigurationManager _configManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniWorldController"/> class.
    /// </summary>
    public AniWorldController(
        AniWorldService aniWorldService,
        DownloadService downloadService,
        IServerConfigurationManager configManager)
    {
        _aniWorldService = aniWorldService;
        _downloadService = downloadService;
        _configManager = configManager;
    }

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
    public async Task<ActionResult<SeriesInfo>> GetSeries(
        [Required] string url,
        CancellationToken cancellationToken)
    {
        var info = await _aniWorldService.GetSeriesInfoAsync(url, cancellationToken).ConfigureAwait(false);
        return Ok(info);
    }

    /// <summary>
    /// Get episodes for a season.
    /// </summary>
    [HttpGet("Episodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<EpisodeRef>>> GetEpisodes(
        [Required] string url,
        CancellationToken cancellationToken)
    {
        var episodes = await _aniWorldService.GetEpisodesAsync(url, cancellationToken).ConfigureAwait(false);
        return Ok(episodes);
    }

    /// <summary>
    /// Get episode details (provider links).
    /// </summary>
    [HttpGet("Episode")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<EpisodeDetails>> GetEpisodeDetails(
        [Required] string url,
        CancellationToken cancellationToken)
    {
        var details = await _aniWorldService.GetEpisodeDetailsAsync(url, cancellationToken).ConfigureAwait(false);
        return Ok(details);
    }

    /// <summary>
    /// Start downloading an episode. Automatically constructs the proper file path
    /// following Jellyfin naming conventions: SeriesName/Season XX/SeriesName - SXXEXX.mkv
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

        var config = Plugin.Instance?.Configuration;
        var basePath = config?.DownloadPath ?? string.Empty;

        if (string.IsNullOrEmpty(basePath))
        {
            return BadRequest("No download path configured. Please set a download path in the plugin settings.");
        }

        var language = request.LanguageKey ?? config?.PreferredLanguage ?? "1";
        var provider = request.Provider ?? config?.PreferredProvider ?? "VOE";
        var seriesTitle = request.SeriesTitle ?? "Unknown Anime";

        // Build proper output path: basePath/SeriesName/Season XX/SeriesName - SXXEXX.mkv
        var outputPath = BuildOutputPath(basePath, seriesTitle, request.EpisodeUrl);

        var taskId = await _downloadService.StartDownloadAsync(
            request.EpisodeUrl,
            language,
            provider,
            outputPath,
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

        var config = Plugin.Instance?.Configuration;
        var basePath = config?.DownloadPath ?? string.Empty;

        if (string.IsNullOrEmpty(basePath))
        {
            return BadRequest("No download path configured. Please set a download path in the plugin settings.");
        }

        var language = request.LanguageKey ?? config?.PreferredLanguage ?? "1";
        var provider = request.Provider ?? config?.PreferredProvider ?? "VOE";
        var seriesTitle = request.SeriesTitle ?? "Unknown Anime";

        // Get all episodes for this season
        var episodes = await _aniWorldService.GetEpisodesAsync(request.SeasonUrl, cancellationToken).ConfigureAwait(false);

        if (episodes.Count == 0)
        {
            return BadRequest("No episodes found for this season.");
        }

        var tasks = new List<DownloadTask>();

        foreach (var ep in episodes)
        {
            var outputPath = BuildOutputPath(basePath, seriesTitle, ep.Url);

            // Skip if file already exists
            if (System.IO.File.Exists(outputPath))
            {
                continue;
            }

            var taskId = await _downloadService.StartDownloadAsync(
                ep.Url,
                language,
                provider,
                outputPath,
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
    /// Get all active/recent downloads.
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
    /// Clear completed/failed downloads from the list.
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

    /// <summary>
    /// Builds a Jellyfin-compatible output path from the episode URL.
    /// Format: basePath/SeriesName/Season XX/SeriesName - SXXEXX.mkv
    /// For movies: basePath/SeriesName/Specials/SeriesName - S00EXX.mkv
    /// </summary>
    private static string BuildOutputPath(string basePath, string seriesTitle, string episodeUrl)
    {
        var safeName = SanitizeFileName(seriesTitle);

        var seMatch = SeasonEpisodeFromUrl.Match(episodeUrl);
        if (seMatch.Success)
        {
            var season = int.Parse(seMatch.Groups["season"].Value);
            var episode = int.Parse(seMatch.Groups["episode"].Value);
            var seasonFolder = $"Season {season:D2}";
            var fileName = $"{safeName} - S{season:D2}E{episode:D2}.mkv";

            return Path.Combine(basePath, safeName, seasonFolder, fileName);
        }

        var movieMatch = MovieFromUrl.Match(episodeUrl);
        if (movieMatch.Success)
        {
            var num = int.Parse(movieMatch.Groups["num"].Value);
            var fileName = $"{safeName} - S00E{num:D2}.mkv";

            return Path.Combine(basePath, safeName, "Specials", fileName);
        }

        // Fallback: use slug + timestamp
        var slugMatch = SeriesSlugFromUrl.Match(episodeUrl);
        var slug = slugMatch.Success ? slugMatch.Groups["slug"].Value : "unknown";
        return Path.Combine(basePath, safeName, $"{slug}_{DateTime.UtcNow:yyyyMMddHHmmss}.mkv");
    }

    /// <summary>
    /// Sanitizes a file/folder name by removing invalid characters.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized.Trim();
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

    /// <summary>Gets or sets the output path (deprecated, use config instead).</summary>
    public string? OutputPath { get; set; }
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
