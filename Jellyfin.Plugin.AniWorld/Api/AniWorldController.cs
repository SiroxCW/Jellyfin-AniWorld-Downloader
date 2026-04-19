using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniWorld.Helpers;
using Jellyfin.Plugin.AniWorld.Services;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
    private readonly StoService _stoService;
    private readonly HiAnimeService _hiAnimeService;
    private readonly DownloadService _downloadService;
    private readonly DownloadHistoryService _historyService;
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<AniWorldController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniWorldController"/> class.
    /// </summary>
    public AniWorldController(
        AniWorldService aniWorldService,
        StoService stoService,
        HiAnimeService hiAnimeService,
        DownloadService downloadService,
        DownloadHistoryService historyService,
        IServerConfigurationManager configManager,
        ILogger<AniWorldController> logger)
    {
        _aniWorldService = aniWorldService;
        _stoService = stoService;
        _hiAnimeService = hiAnimeService;
        _downloadService = downloadService;
        _historyService = historyService;
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the correct streaming service for a source.
    /// </summary>
    private StreamingSiteService GetService(string? source)
    {
        return string.Equals(source, "sto", StringComparison.OrdinalIgnoreCase)
            ? _stoService
            : _aniWorldService;
    }

    /// <summary>
    /// Resolves the source from an explicit parameter or URL auto-detection.
    /// </summary>
    private static string ResolveSource(string? explicitSource, string? url = null)
    {
        if (!string.IsNullOrEmpty(explicitSource))
        {
            return explicitSource;
        }

        if (!string.IsNullOrEmpty(url))
        {
            return UrlValidator.DetectSource(url);
        }

        return "aniworld";
    }

    // ── Non-admin access endpoints ────────────────────────────────────

    /// <summary>
    /// Serves the injection script for non-admin sidebar access.
    /// </summary>
    [HttpGet("InjectionScript")]
    [AllowAnonymous]
    public ActionResult GetInjectionScript()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.AniWorld.Web.injection.js");
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "application/javascript");
    }

    /// <summary>
    /// Serves the main plugin page HTML for non-admin rendering.
    /// </summary>
    [HttpGet("Page")]
    [AllowAnonymous]
    public ActionResult GetPage()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.AniWorld.Web.aniworld.html");
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "text/html");
    }

    /// <summary>
    /// Serves the main plugin page JavaScript for non-admin rendering.
    /// </summary>
    [HttpGet("PageScript")]
    [AllowAnonymous]
    public ActionResult GetPageScript()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.AniWorld.Web.aniworld.js");
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "application/javascript");
    }

    /// <summary>
    /// Returns which sources are enabled in the configuration.
    /// </summary>
    [HttpGet("EnabledSources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetEnabledSources()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            aniworld = config?.AniWorldConfig.Enabled ?? true,
            sto = config?.StoConfig.Enabled ?? false,
            // NOTE: HiAnime (hianime.to) has been shut down — always disabled
            hianime = false,
            hiAnimeOnlyDub = config?.HiAnimeConfig.OnlyEnglishDub ?? false,
            aniWorldOnlyGerman = config?.AniWorldConfig.OnlyGermanLanguages ?? false,
            maintenanceMode = config?.MaintenanceMode ?? false,
            maintenanceMessage = config?.MaintenanceMessage ?? string.Empty
        });
    }

    // ── Search & Browse ─────────────────────────────────────────────

    /// <summary>
    /// Search for series. Use source=all to query both sites, source=aniworld or source=sto for one site.
    /// </summary>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<SearchResult>>> Search(
        [Required] string query,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;

        if (string.Equals(source, "all", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(source))
        {
            var results = new List<SearchResult>();

            if (config?.AniWorldConfig.Enabled != false)
            {
                try
                {
                    var awResults = await _aniWorldService.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                    results.AddRange(awResults);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AniWorld search failed for query: {Query}", query);
                }
            }

            if (config?.StoConfig.Enabled == true)
            {
                try
                {
                    var stoResults = await _stoService.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                    results.AddRange(stoResults);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "s.to search failed for query: {Query}", query);
                }
            }

            if (config?.HiAnimeConfig.Enabled != false)
            {
                try
                {
                    var hiResults = await _hiAnimeService.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                    results.AddRange(hiResults);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "HiAnime search failed for query: {Query}", query);
                }
            }

            return Ok(results);
        }

        if (string.Equals(source, "hianime", StringComparison.OrdinalIgnoreCase))
        {
            var hiResults = await _hiAnimeService.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            return Ok(hiResults);
        }

        var service = GetService(source);
        var singleResults = await service.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        return Ok(singleResults);
    }

    /// <summary>
    /// Get series information.
    /// </summary>
    [HttpGet("Series")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SeriesInfo>> GetSeries(
        [Required] string url,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        if (!UrlValidator.IsValidUrl(url))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to and https://s.to URLs are accepted.");
        }

        var resolvedSource = ResolveSource(source, url);
        if (string.Equals(resolvedSource, "hianime", StringComparison.OrdinalIgnoreCase))
        {
            var info = await _hiAnimeService.GetSeriesInfoAsync(url, cancellationToken).ConfigureAwait(false);
            return Ok(info);
        }

        var service = GetService(resolvedSource);
        var result = await service.GetSeriesInfoAsync(url, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Get episodes for a season.
    /// </summary>
    [HttpGet("Episodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<EpisodeRef>>> GetEpisodes(
        [Required] string url,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        if (!UrlValidator.IsValidUrl(url))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to and https://s.to URLs are accepted.");
        }

        var resolvedSource = ResolveSource(source, url);
        if (string.Equals(resolvedSource, "hianime", StringComparison.OrdinalIgnoreCase))
        {
            var episodes = await _hiAnimeService.GetEpisodesAsync(url, cancellationToken).ConfigureAwait(false);
            return Ok(episodes);
        }

        var service = GetService(resolvedSource);
        var result = await service.GetEpisodesAsync(url, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Get episode details (provider links).
    /// </summary>
    [HttpGet("Episode")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EpisodeDetails>> GetEpisodeDetails(
        [Required] string url,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        if (!UrlValidator.IsValidUrl(url))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to and https://s.to URLs are accepted.");
        }

        var resolvedSource = ResolveSource(source, url);
        if (string.Equals(resolvedSource, "hianime", StringComparison.OrdinalIgnoreCase))
        {
            var details = await _hiAnimeService.GetEpisodeDetailsAsync(url, cancellationToken).ConfigureAwait(false);
            return Ok(details);
        }

        var service = GetService(resolvedSource);
        var result = await service.GetEpisodeDetailsAsync(url, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Get popular series. Use source parameter to select site.
    /// </summary>
    [HttpGet("Popular")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<BrowseItem>>> GetPopular(
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedSource = ResolveSource(source);
        if (string.Equals(resolvedSource, "hianime", StringComparison.OrdinalIgnoreCase))
        {
            var items = await _hiAnimeService.GetPopularAsync(cancellationToken).ConfigureAwait(false);
            return Ok(items);
        }

        var service = GetService(resolvedSource);
        var result = await service.GetPopularAsync(cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Get newly added series. Use source parameter to select site.
    /// </summary>
    [HttpGet("New")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<BrowseItem>>> GetNewReleases(
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedSource = ResolveSource(source);
        if (string.Equals(resolvedSource, "hianime", StringComparison.OrdinalIgnoreCase))
        {
            var items = await _hiAnimeService.GetNewReleasesAsync(cancellationToken).ConfigureAwait(false);
            return Ok(items);
        }

        var service = GetService(resolvedSource);
        var result = await service.GetNewReleasesAsync(cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    // ── Custom Target (HiAnime) ──────────────────────────────────────

    /// <summary>
    /// Lists existing series folders in the configured download path.
    /// </summary>
    [HttpGet("ExistingFolders")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<string>> GetExistingFolders(string? source = null)
    {
        var resolvedSource = string.IsNullOrEmpty(source) ? "hianime" : source;
        var config = Plugin.Instance?.Configuration;
        var allPaths = config?.GetAllDownloadPaths(resolvedSource) ?? new List<string>();

        var folderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var basePath in allPaths)
        {
            if (!Directory.Exists(basePath))
            {
                continue;
            }

            foreach (var dir in Directory.GetDirectories(basePath))
            {
                var name = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(name))
                {
                    folderSet.Add(name);
                }
            }
        }

        var folders = folderSet.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return Ok(folders);
    }

    /// <summary>
    /// Returns info about a specific season folder (whether it exists and highest episode number).
    /// </summary>
    [HttpGet("SeasonInfo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetSeasonInfo(
        [Required] string folder,
        [Required] int season,
        string? source = null)
    {
        if (folder.Contains('/') || folder.Contains('\\'))
        {
            return BadRequest("Folder name must not contain path separators.");
        }

        if (season < 0)
        {
            return BadRequest("Season number must be >= 0.");
        }

        var resolvedSource = string.IsNullOrEmpty(source) ? "hianime" : source;
        var config = Plugin.Instance?.Configuration;
        var allPaths = config?.GetAllDownloadPaths(resolvedSource) ?? new List<string>();

        var highest = 0;
        foreach (var basePath in allPaths)
        {
            var h = PathHelper.GetHighestEpisodeNumber(basePath, folder, season);
            if (h > highest)
            {
                highest = h;
            }
        }

        return Ok(new { exists = highest > 0, highestEpisode = highest });
    }

    // ── Downloads ───────────────────────────────────────────────────

    /// <summary>
    /// Start downloading an episode.
    /// </summary>
    [HttpPost("Download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DownloadTask>> StartDownload(
        [FromBody] DownloadRequest request,
        CancellationToken cancellationToken)
    {
        var maintenanceConfig = Plugin.Instance?.Configuration;
        if (maintenanceConfig?.MaintenanceMode == true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                maintenanceConfig.MaintenanceMessage);
        }

        if (string.IsNullOrEmpty(request.EpisodeUrl))
        {
            return BadRequest("Episode URL is required");
        }

        if (!UrlValidator.IsValidUrl(request.EpisodeUrl))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to and https://s.to URLs are accepted.");
        }

        var source = ResolveSource(request.Source, request.EpisodeUrl);
        var config = Plugin.Instance?.Configuration;
        var isHiAnime = string.Equals(source, "hianime", StringComparison.OrdinalIgnoreCase);
        var language = request.LanguageKey ?? config?.GetPreferredLanguage(source) ?? (isHiAnime ? "sub" : "1");

        // Enforce language restrictions — reject blocked languages
        if (isHiAnime && config?.HiAnimeConfig.OnlyEnglishDub == true && language != "dub")
        {
            return BadRequest("Only English Dub downloads are allowed for HiAnime. This language is blocked by the admin.");
        }

        if (!isHiAnime && string.Equals(source, "aniworld", StringComparison.OrdinalIgnoreCase)
            && config?.AniWorldConfig.OnlyGermanLanguages == true && language == "2")
        {
            return BadRequest("English Sub downloads are blocked for AniWorld. Only German Dub and German Sub are allowed.");
        }

        var isMovieRequest = PathHelper.MovieFromUrl.IsMatch(request.EpisodeUrl);
        var basePath = config?.GetDownloadPath(source, language, isMovieRequest) ?? string.Empty;

        if (string.IsNullOrEmpty(basePath))
        {
            return BadRequest("No download path configured. Please set a download path in the plugin settings.");
        }

        var provider = request.Provider ?? (isHiAnime ? "Auto" : config?.GetPreferredProvider(source) ?? "VOE");
        var seriesTitle = request.SeriesTitle ?? "Unknown";

        // Validate custom target parameters
        var useCustomTarget = isHiAnime && !string.IsNullOrEmpty(request.CustomFolder) && request.CustomSeason.HasValue;
        if (useCustomTarget)
        {
            if (request.CustomFolder!.Contains('/') || request.CustomFolder.Contains('\\'))
            {
                return BadRequest("Custom folder name must not contain path separators.");
            }

            if (request.CustomSeason!.Value < 0)
            {
                return BadRequest("Custom season number must be >= 0.");
            }

            if (request.EpisodeOffset.HasValue && request.EpisodeOffset.Value < 0)
            {
                return BadRequest("Episode offset must be >= 0.");
            }
        }

        string outputPath;
        int? effectiveEpisodeNumber = request.EpisodeNumber;
        int? customSeason = null;

        if (useCustomTarget)
        {
            var offset = request.EpisodeOffset ?? 0;
            var epNum = (request.EpisodeNumber ?? 1) + offset;
            effectiveEpisodeNumber = epNum;
            customSeason = request.CustomSeason!.Value;
            outputPath = PathHelper.BuildOutputPathCustom(basePath, request.CustomFolder!, customSeason.Value, epNum);
            seriesTitle = request.CustomFolder!;
        }
        else
        {
            outputPath = PathHelper.BuildOutputPath(basePath, seriesTitle, request.EpisodeUrl, request.EpisodeNumber);
        }

        // Check if already downloaded (duplicate detection)
        if (!request.Force)
        {
            var (checkSeason, checkEpisode) = useCustomTarget
                ? (request.CustomSeason!.Value, (request.EpisodeNumber ?? 1) + (request.EpisodeOffset ?? 0))
                : PathHelper.ParseSeasonEpisode(request.EpisodeUrl, request.EpisodeNumber);

            if (_downloadService.IsAlreadyDownloaded(seriesTitle, checkSeason, checkEpisode, language))
            {
                return BadRequest("This episode has already been downloaded in this language.");
            }
        }

        var username = User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity?.Name;

        var taskId = await _downloadService.StartDownloadAsync(
            request.EpisodeUrl,
            language,
            provider,
            outputPath,
            seriesTitle,
            source,
            cancellationToken,
            effectiveEpisodeNumber,
            customSeason,
            username,
            request.Priority).ConfigureAwait(false);

        if (taskId == null)
        {
            return BadRequest("This episode is already queued or downloading.");
        }

        var task = _downloadService.GetDownload(taskId);
        return Ok(task);
    }

    /// <summary>
    /// Start downloading all episodes in a season (batch download).
    /// </summary>
    [HttpPost("DownloadSeason")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<List<DownloadTask>>> DownloadSeason(
        [FromBody] BatchDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var maintenanceConfig = Plugin.Instance?.Configuration;
        if (maintenanceConfig?.MaintenanceMode == true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                maintenanceConfig.MaintenanceMessage);
        }

        if (string.IsNullOrEmpty(request.SeasonUrl))
        {
            return BadRequest("Season URL is required");
        }

        if (!UrlValidator.IsValidUrl(request.SeasonUrl))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to and https://s.to URLs are accepted.");
        }

        var source = ResolveSource(request.Source, request.SeasonUrl);
        var config = Plugin.Instance?.Configuration;
        var isHiAnime = string.Equals(source, "hianime", StringComparison.OrdinalIgnoreCase);
        var language = request.LanguageKey ?? config?.GetPreferredLanguage(source) ?? (isHiAnime ? "sub" : "1");

        // Enforce language restrictions — reject blocked languages
        if (isHiAnime && config?.HiAnimeConfig.OnlyEnglishDub == true && language != "dub")
        {
            return BadRequest("Only English Dub downloads are allowed for HiAnime. This language is blocked by the admin.");
        }

        if (!isHiAnime && string.Equals(source, "aniworld", StringComparison.OrdinalIgnoreCase)
            && config?.AniWorldConfig.OnlyGermanLanguages == true && language == "2")
        {
            return BadRequest("English Sub downloads are blocked for AniWorld. Only German Dub and German Sub are allowed.");
        }

        var basePath = config?.GetDownloadPath(source, language) ?? string.Empty;
        var movieBasePath = config?.GetDownloadPath(source, language, true) ?? string.Empty;

        var provider = request.Provider ?? (isHiAnime ? "Auto" : config?.GetPreferredProvider(source) ?? "VOE");
        var seriesTitle = request.SeriesTitle ?? "Unknown";

        // Validate custom target parameters
        var useCustomTarget = isHiAnime && !string.IsNullOrEmpty(request.CustomFolder) && request.CustomSeason.HasValue;
        if (useCustomTarget)
        {
            if (request.CustomFolder!.Contains('/') || request.CustomFolder.Contains('\\'))
            {
                return BadRequest("Custom folder name must not contain path separators.");
            }

            if (request.CustomSeason!.Value < 0)
            {
                return BadRequest("Custom season number must be >= 0.");
            }

            if (request.EpisodeOffset.HasValue && request.EpisodeOffset.Value < 0)
            {
                return BadRequest("Episode offset must be >= 0.");
            }

            seriesTitle = request.CustomFolder!;
        }

        List<EpisodeRef> episodes;
        if (isHiAnime)
        {
            episodes = await _hiAnimeService.GetEpisodesAsync(request.SeasonUrl, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var service = GetService(source);
            episodes = await service.GetEpisodesAsync(request.SeasonUrl, cancellationToken).ConfigureAwait(false);
        }

        if (episodes.Count == 0)
        {
            return BadRequest("No episodes found for this season.");
        }

        var hasRegularEpisodes = episodes.Any(e => !e.IsMovie);
        var hasMovieEpisodes = episodes.Any(e => e.IsMovie);

        if ((useCustomTarget || hasRegularEpisodes) && string.IsNullOrEmpty(basePath))
        {
            return BadRequest("No download path configured. Please set a download path in the plugin settings.");
        }

        if (hasMovieEpisodes && string.IsNullOrEmpty(movieBasePath))
        {
            return BadRequest("No movie download path configured. Please set a path in the plugin settings.");
        }

        var tasks = new List<DownloadTask>();
        var offset = useCustomTarget ? (request.EpisodeOffset ?? 0) : 0;
        var username = User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity?.Name;

        foreach (var ep in episodes)
        {
            string outputPath;
            int? effectiveEpNum = isHiAnime ? ep.Number : null;
            int? customSeason = null;

            if (useCustomTarget)
            {
                var epNum = ep.Number + offset;
                effectiveEpNum = epNum;
                customSeason = request.CustomSeason!.Value;
                outputPath = PathHelper.BuildOutputPathCustom(basePath, request.CustomFolder!, customSeason.Value, epNum);
            }
            else
            {
                var effectiveBasePath = ep.IsMovie
                    ? movieBasePath
                    : basePath;

                if (string.IsNullOrEmpty(effectiveBasePath))
                {
                    continue;
                }

                outputPath = PathHelper.BuildOutputPath(effectiveBasePath, seriesTitle, ep.Url, effectiveEpNum);
            }

            var (checkSeason, checkEpisode) = useCustomTarget
                ? (request.CustomSeason!.Value, ep.Number + offset)
                : PathHelper.ParseSeasonEpisode(ep.Url, effectiveEpNum);
            var checkTitle = useCustomTarget ? request.CustomFolder! : seriesTitle;

            if (_downloadService.IsAlreadyDownloaded(checkTitle, checkSeason, checkEpisode, language))
            {
                continue;
            }

            var taskId = await _downloadService.StartDownloadAsync(
                ep.Url,
                language,
                provider,
                outputPath,
                seriesTitle,
                source,
                cancellationToken,
                effectiveEpNum,
                customSeason,
                username,
                request.Priority).ConfigureAwait(false);

            if (taskId == null) continue;

            var task = _downloadService.GetDownload(taskId);
            if (task != null)
            {
                tasks.Add(task);
            }
        }

        return Ok(tasks);
    }

    /// <summary>
    /// Start downloading all episodes across all seasons of a series.
    /// </summary>
    [HttpPost("DownloadAll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<object>> DownloadAllSeasons(
        [FromBody] FullSeriesDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var maintenanceConfig = Plugin.Instance?.Configuration;
        if (maintenanceConfig?.MaintenanceMode == true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                maintenanceConfig.MaintenanceMessage);
        }

        if (string.IsNullOrEmpty(request.SeriesUrl))
        {
            return BadRequest("Series URL is required");
        }

        if (!UrlValidator.IsValidUrl(request.SeriesUrl))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to and https://s.to URLs are accepted.");
        }

        var source = ResolveSource(request.Source, request.SeriesUrl);
        var config = Plugin.Instance?.Configuration;
        var isHiAnime = string.Equals(source, "hianime", StringComparison.OrdinalIgnoreCase);
        var language = request.LanguageKey ?? config?.GetPreferredLanguage(source) ?? (isHiAnime ? "sub" : "1");

        // Enforce language restrictions — reject blocked languages
        if (isHiAnime && config?.HiAnimeConfig.OnlyEnglishDub == true && language != "dub")
        {
            return BadRequest("Only English Dub downloads are allowed for HiAnime. This language is blocked by the admin.");
        }

        if (!isHiAnime && string.Equals(source, "aniworld", StringComparison.OrdinalIgnoreCase)
            && config?.AniWorldConfig.OnlyGermanLanguages == true && language == "2")
        {
            return BadRequest("English Sub downloads are blocked for AniWorld. Only German Dub and German Sub are allowed.");
        }

        var basePath = config?.GetDownloadPath(source, language) ?? string.Empty;
        var movieBasePath = config?.GetDownloadPath(source, language, true) ?? string.Empty;

        if (string.IsNullOrEmpty(basePath))
        {
            return BadRequest("No download path configured. Please set a download path in the plugin settings.");
        }

        var provider = request.Provider ?? (isHiAnime ? "Auto" : config?.GetPreferredProvider(source) ?? "VOE");

        // Validate custom target parameters
        var useCustomTarget = isHiAnime && !string.IsNullOrEmpty(request.CustomFolder) && request.CustomSeason.HasValue;
        if (useCustomTarget)
        {
            if (request.CustomFolder!.Contains('/') || request.CustomFolder.Contains('\\'))
            {
                return BadRequest("Custom folder name must not contain path separators.");
            }

            if (request.CustomSeason!.Value < 0)
            {
                return BadRequest("Custom season number must be >= 0.");
            }

            if (request.EpisodeOffset.HasValue && request.EpisodeOffset.Value < 0)
            {
                return BadRequest("Episode offset must be >= 0.");
            }
        }

        SeriesInfo seriesInfo;
        if (isHiAnime)
        {
            seriesInfo = await _hiAnimeService.GetSeriesInfoAsync(request.SeriesUrl, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var service = GetService(source);
            seriesInfo = await service.GetSeriesInfoAsync(request.SeriesUrl, cancellationToken).ConfigureAwait(false);
        }
        var seriesTitle = useCustomTarget ? request.CustomFolder! : (request.SeriesTitle ?? seriesInfo.Title ?? "Unknown");

        if (seriesInfo.Seasons == null || seriesInfo.Seasons.Count == 0)
        {
            return BadRequest("No seasons found for this series.");
        }

        var allTasks = new List<DownloadTask>();
        var skippedCount = 0;
        var offset = useCustomTarget ? (request.EpisodeOffset ?? 0) : 0;
        var username = User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity?.Name;

        foreach (var season in seriesInfo.Seasons)
        {
            List<EpisodeRef> episodes;
            if (isHiAnime)
            {
                episodes = await _hiAnimeService.GetEpisodesAsync(season.Url, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var svc = GetService(source);
                episodes = await svc.GetEpisodesAsync(season.Url, cancellationToken).ConfigureAwait(false);
            }

            foreach (var ep in episodes)
            {
                string outputPath;
                int? effectiveEpNum = isHiAnime ? ep.Number : null;
                int? customSeason = null;

                if (useCustomTarget)
                {
                    var epNum = ep.Number + offset;
                    effectiveEpNum = epNum;
                    customSeason = request.CustomSeason!.Value;
                    outputPath = PathHelper.BuildOutputPathCustom(basePath, request.CustomFolder!, customSeason.Value, epNum);
                }
                else
                {
                    var effectiveBasePath = ep.IsMovie
                        ? movieBasePath
                        : basePath;

                    outputPath = PathHelper.BuildOutputPath(effectiveBasePath, seriesTitle, ep.Url, effectiveEpNum);
                }

                var (checkSeason, checkEpisode) = useCustomTarget
                    ? (request.CustomSeason!.Value, ep.Number + offset)
                    : PathHelper.ParseSeasonEpisode(ep.Url, effectiveEpNum);
                var checkTitle = useCustomTarget ? request.CustomFolder! : seriesTitle;

                if (_downloadService.IsAlreadyDownloaded(checkTitle, checkSeason, checkEpisode, language))
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
                    source,
                    cancellationToken,
                    effectiveEpNum,
                    customSeason,
                    username,
                    request.Priority).ConfigureAwait(false);

                if (taskId == null) { skippedCount++; continue; }

                var task = _downloadService.GetDownload(taskId);
                if (task != null)
                {
                    allTasks.Add(task);
                }
            }
        }

        // Also handle movies if they exist (not applicable for hianime)
        if (seriesInfo.HasMovies && !isHiAnime)
        {
            var movieUrl = request.SeriesUrl.TrimEnd('/') + "/filme";
            var svc = GetService(source);
            var movies = await svc.GetEpisodesAsync(movieUrl, cancellationToken).ConfigureAwait(false);

            foreach (var ep in movies)
            {
                var outputPath = PathHelper.BuildOutputPath(movieBasePath, seriesTitle, ep.Url);

                var (movieSeason, movieEpisode) = PathHelper.ParseSeasonEpisode(ep.Url);

                if (_downloadService.IsAlreadyDownloaded(seriesTitle, movieSeason, movieEpisode, language))
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
                    source,
                    cancellationToken,
                    username: username,
                    priority: request.Priority).ConfigureAwait(false);

                if (taskId == null) { skippedCount++; continue; }

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
    /// Get download statistics.
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
    public ActionResult<object> CheckIsDownloaded([Required] string url, [Required] string title, string? language = null)
    {
        if (!UrlValidator.IsValidUrl(url))
        {
            return BadRequest("Invalid URL. Only https://aniworld.to and https://s.to URLs are accepted.");
        }

        var (season, episode) = PathHelper.ParseSeasonEpisode(url);
        var completedLanguages = _historyService.GetCompletedLanguages(title, season, episode);
        return Ok(new { downloaded = completedLanguages.Count > 0, languages = completedLanguages, url });
    }

    /// <summary>
    /// Serves a language flag SVG by language key.
    /// Accepts an optional source parameter to differentiate flag for language key "2":
    /// aniworld: japanese-english.svg, sto: english.svg.
    /// </summary>
    [HttpGet("Flag/{lang}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [AllowAnonymous]
    public ActionResult GetFlag(string lang, string? source = null)
    {
        var isSto = string.Equals(source, "sto", StringComparison.OrdinalIgnoreCase);
        var isHiAnime = string.Equals(source, "hianime", StringComparison.OrdinalIgnoreCase);

        string? resourceName;
        if (isHiAnime)
        {
            resourceName = lang switch
            {
                "sub" => "Jellyfin.Plugin.AniWorld.Web.japanese-english.svg",
                "dub" => "Jellyfin.Plugin.AniWorld.Web.english.svg",
                _ => null
            };
        }
        else
        {
            resourceName = lang switch
            {
                "1" => "Jellyfin.Plugin.AniWorld.Web.german.svg",
                "2" => isSto
                    ? "Jellyfin.Plugin.AniWorld.Web.english.svg"
                    : "Jellyfin.Plugin.AniWorld.Web.japanese-english.svg",
                "3" => "Jellyfin.Plugin.AniWorld.Web.japanese-german.svg",
                _ => null
            };
        }

        if (resourceName == null)
        {
            return NotFound();
        }

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "image/svg+xml");
    }

    /// <summary>
    /// Serves the site logo SVG for a source.
    /// </summary>
    [HttpGet("SiteLogo/{source}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [AllowAnonymous]
    public ActionResult GetSiteLogo(string source)
    {
        var resourceName = source.ToLowerInvariant() switch
        {
            "aniworld" => "Jellyfin.Plugin.AniWorld.Web.aniworld.svg",
            "sto" => "Jellyfin.Plugin.AniWorld.Web.sto.svg",
            "hianime" => "Jellyfin.Plugin.AniWorld.Web.hianime.svg",
            _ => null
        };

        if (resourceName == null)
        {
            return NotFound();
        }

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "image/svg+xml");
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

    /// <summary>Gets or sets the source site ("aniworld" or "sto").</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the sequential episode number (used for HiAnime where URL contains an internal ID, not the episode number).</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the custom folder name for downloading into an existing series (HiAnime only).</summary>
    public string? CustomFolder { get; set; }

    /// <summary>Gets or sets the custom season number (HiAnime only).</summary>
    public int? CustomSeason { get; set; }

    /// <summary>Gets or sets the episode offset (highest existing episode number to offset from, HiAnime only).</summary>
    public int? EpisodeOffset { get; set; }

    /// <summary>Gets or sets whether this is a priority download (added to front of queue).</summary>
    public bool Priority { get; set; }
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

    /// <summary>Gets or sets the source site ("aniworld" or "sto").</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the custom folder name for downloading into an existing series (HiAnime only).</summary>
    public string? CustomFolder { get; set; }

    /// <summary>Gets or sets the custom season number (HiAnime only).</summary>
    public int? CustomSeason { get; set; }

    /// <summary>Gets or sets the episode offset (highest existing episode number to offset from, HiAnime only).</summary>
    public int? EpisodeOffset { get; set; }

    /// <summary>Gets or sets whether this is a priority download (added to front of queue).</summary>
    public bool Priority { get; set; }
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

    /// <summary>Gets or sets the source site ("aniworld" or "sto").</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the custom folder name for downloading into an existing series (HiAnime only).</summary>
    public string? CustomFolder { get; set; }

    /// <summary>Gets or sets the custom season number (HiAnime only).</summary>
    public int? CustomSeason { get; set; }

    /// <summary>Gets or sets the episode offset (highest existing episode number to offset from, HiAnime only).</summary>
    public int? EpisodeOffset { get; set; }

    /// <summary>Gets or sets whether this is a priority download (added to front of queue).</summary>
    public bool Priority { get; set; }
}
