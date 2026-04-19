using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Abstract base class for streaming site services (aniworld.to / s.to).
/// Both sites share the same template engine but differ in URL paths, content type, and languages.
/// </summary>
public abstract class StreamingSiteService
{
    private static readonly Regex TitlePattern = new(
        @"<h1[^>]*><span>(?<title>[^<]+)</span>",
        RegexOptions.Compiled);

    private static readonly Regex CoverImagePattern = new(
        @"<div[^>]*class=""seriesCoverBox""[^>]*>.*?data-src=""(?<src>[^""]+)""",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DescriptionPattern = new(
        @"<p[^>]*class=""seri_des""[^>]*data-full-description=""(?<desc>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex GermanTitlePattern = new(
        @"<span[^>]*class=""episodeGermanTitle""[^>]*>(?<title>[^<]*)",
        RegexOptions.Compiled);

    private static readonly Regex EnglishTitlePattern = new(
        @"<small[^>]*class=""episodeEnglishTitle""[^>]*>(?<title>[^<]*)",
        RegexOptions.Compiled);

    private static readonly Regex GenrePattern = new(
        @"<a[^>]*href=""/genre/[^""]+""[^>]*class=""genreButton[^""]*""[^>]*>(?<genre>[^<]+)</a>",
        RegexOptions.Compiled);

    private static readonly Regex MovieListPattern = new(
        @"<a[^>]*href=""(/(?:anime/stream|serie)/[^""]+/filme/film-\d+)""[^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex MovieSectionLinkPattern = new(
        @"href=""/(?:anime/stream|serie)/[^""]+/filme(?:[""?#/]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;

    /// <summary>Gets the HTTP client for derived classes.</summary>
    protected HttpClient HttpClient => _httpClient;

    /// <summary>
    /// Logger instance for derived classes.
    /// </summary>
    protected readonly ILogger Logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingSiteService"/> class.
    /// </summary>
    protected StreamingSiteService(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        Logger = logger;
    }

    // ── Abstract site-specific members ──────────────────────────

    /// <summary>Gets the source identifier ("aniworld" or "sto").</summary>
    public abstract string SourceName { get; }

    /// <summary>Gets the base URL (e.g. "https://aniworld.to").</summary>
    protected abstract string BaseUrl { get; }

    /// <summary>Gets the search URL.</summary>
    protected abstract string SearchUrl { get; }

    /// <summary>Gets the series path prefix (e.g. "/anime/stream/" or "/serie/").</summary>
    protected abstract string SeriesPathPrefix { get; }

    /// <summary>Gets the popular page path (e.g. "/beliebte-animes").</summary>
    protected abstract string PopularPath { get; }

    /// <summary>Gets the heading text for new releases section (e.g. "Neue Animes").</summary>
    protected abstract string NewSectionHeading { get; }

    /// <summary>Gets the user agent string.</summary>
    protected virtual string UserAgent => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    // ── Shared regex patterns ───────────────────────────────────

    /// <summary>Gets the season link pattern. Overridable for different path prefixes.</summary>
    protected abstract Regex SeasonLinkPattern { get; }

    /// <summary>Gets the episode list pattern. Overridable for different path prefixes.</summary>
    protected abstract Regex EpisodeListPattern { get; }

    /// <summary>Gets the search result URL filter pattern.</summary>
    protected abstract Regex SearchFilterPattern { get; }

    /// <summary>Gets the browse item pattern for this site.</summary>
    protected abstract Regex BrowseItemPattern { get; }

    // ── Public API ──────────────────────────────────────────────

    /// <summary>
    /// Searches for series on the site.
    /// </summary>
    public virtual async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("keyword", keyword)
        });

        var response = await _httpClient.PostAsync(SearchUrl, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var results = JsonSerializer.Deserialize<List<SearchResultRaw>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (results == null)
        {
            return new List<SearchResult>();
        }

        return results
            .Where(r => !string.IsNullOrEmpty(r.Link) && SearchFilterPattern.IsMatch(r.Link))
            .Select(r => new SearchResult
            {
                Title = StripHtml(r.Title ?? string.Empty),
                Url = $"{BaseUrl}{r.Link}",
                Description = StripHtml(r.Description ?? string.Empty),
                Source = SourceName,
            })
            .ToList();
    }

    /// <summary>
    /// Gets detailed information about a series.
    /// </summary>
    public virtual async Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(seriesUrl, cancellationToken).ConfigureAwait(false);

        var titleMatch = TitlePattern.Match(html);
        var coverMatch = CoverImagePattern.Match(html);
        var descMatch = DescriptionPattern.Match(html);

        var genres = GenrePattern.Matches(html)
            .Select(m => DecodeHtml(m.Groups["genre"].Value.Trim()))
            .Distinct()
            .ToList();

        // Extract seasons
        var seasons = SeasonLinkPattern.Matches(html)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Select(path =>
            {
                var numMatch = Regex.Match(path, @"staffel-(\d+)");
                return new SeasonRef
                {
                    Url = $"{BaseUrl}{path}",
                    Number = numMatch.Success ? int.Parse(numMatch.Groups[1].Value) : 0,
                };
            })
            .OrderBy(s => s.Number)
            .ToList();

        // Check for movies. Some pages only expose the /filme section link on the series page
        // and render concrete /filme/film-* entries on the movie subpage.
        var hasMovies = html.Contains("/filme/film-", StringComparison.OrdinalIgnoreCase)
            || MovieSectionLinkPattern.IsMatch(html);

        var coverUrl = coverMatch.Success ? coverMatch.Groups["src"].Value : string.Empty;
        if (!string.IsNullOrEmpty(coverUrl) && !coverUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            coverUrl = $"{BaseUrl}{coverUrl}";
        }

        return new SeriesInfo
        {
            Title = titleMatch.Success ? DecodeHtml(titleMatch.Groups["title"].Value.Trim()) : "Unknown",
            Url = seriesUrl,
            CoverImageUrl = coverUrl,
            Description = descMatch.Success ? DecodeHtml(descMatch.Groups["desc"].Value.Trim()) : string.Empty,
            Genres = genres,
            Seasons = seasons,
            HasMovies = hasMovies,
        };
    }

    /// <summary>
    /// Gets episodes for a given season.
    /// </summary>
    public virtual async Task<List<EpisodeRef>> GetEpisodesAsync(string seasonUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(seasonUrl, cancellationToken).ConfigureAwait(false);

        var isMovies = seasonUrl.Contains("/filme", StringComparison.OrdinalIgnoreCase);
        var pattern = isMovies ? MovieListPattern : EpisodeListPattern;

        var episodes = pattern.Matches(html)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Select(path =>
            {
                var numMatch = Regex.Match(path, @"(?:episode|film)-(\d+)");
                return new EpisodeRef
                {
                    Url = $"{BaseUrl}{path}",
                    Number = numMatch.Success ? int.Parse(numMatch.Groups[1].Value) : 0,
                    IsMovie = isMovies,
                };
            })
            .OrderBy(e => e.Number)
            .ToList();

        return episodes;
    }

    /// <summary>
    /// Gets provider links for an episode.
    /// </summary>
    public virtual async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(episodeUrl, cancellationToken).ConfigureAwait(false);

        var germanTitle = GermanTitlePattern.Match(html);
        var englishTitle = EnglishTitlePattern.Match(html);

        var providers = new Dictionary<string, Dictionary<string, string>>();

        var liPattern = new Regex(
            @"<li[^>]*data-lang-key=""(?<langKey>\d+)""[^>]*data-link-target=""(?<redirect>[^""]+)""[^>]*>.*?<h4>(?<provider>[^<]+)</h4>",
            RegexOptions.Singleline);

        foreach (Match match in liPattern.Matches(html))
        {
            var langKey = match.Groups["langKey"].Value;
            var redirect = match.Groups["redirect"].Value;
            var provider = match.Groups["provider"].Value.Trim();

            if (!providers.ContainsKey(langKey))
            {
                providers[langKey] = new Dictionary<string, string>();
            }

            var redirectUrl = redirect.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? redirect
                : $"{BaseUrl}{redirect}";

            providers[langKey][provider] = redirectUrl;
        }

        return new EpisodeDetails
        {
            Url = episodeUrl,
            TitleDe = germanTitle.Success ? DecodeHtml(germanTitle.Groups["title"].Value.Trim()) : null,
            TitleEn = englishTitle.Success ? DecodeHtml(englishTitle.Groups["title"].Value.Trim()) : null,
            ProvidersByLanguage = providers,
        };
    }

    /// <summary>
    /// Gets the popular series list.
    /// </summary>
    public virtual async Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync($"{BaseUrl}{PopularPath}", cancellationToken).ConfigureAwait(false);
        return ParseBrowseItems(html);
    }

    /// <summary>
    /// Gets newly added series from the homepage.
    /// </summary>
    public virtual async Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(BaseUrl, cancellationToken).ConfigureAwait(false);

        var heading = $"<h2>{NewSectionHeading}</h2>";
        var newSectionIdx = html.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (newSectionIdx < 0)
        {
            // Fallback: try without h2 tags
            newSectionIdx = html.IndexOf($"{NewSectionHeading}</h2>", StringComparison.OrdinalIgnoreCase);
        }

        if (newSectionIdx < 0)
        {
            Logger.LogWarning("Could not find '{Heading}' heading on homepage", NewSectionHeading);
            return new List<BrowseItem>();
        }

        var sectionHtml = html[newSectionIdx..];
        var nextSection = sectionHtml.IndexOf("<div class=\"homeContentPromotionBox", 10, StringComparison.OrdinalIgnoreCase);
        if (nextSection < 0)
        {
            nextSection = sectionHtml.IndexOf("<footer", StringComparison.OrdinalIgnoreCase);
        }

        if (nextSection > 0)
        {
            sectionHtml = sectionHtml[..nextSection];
        }

        return ParseBrowseItems(sectionHtml);
    }

    /// <summary>
    /// Resolves a redirect URL to the actual provider embed URL.
    /// </summary>
    public async Task<string> ResolveRedirectAsync(string redirectUrl, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return response.RequestMessage?.RequestUri?.ToString() ?? redirectUrl;
    }

    // ── Protected helpers ───────────────────────────────────────

    /// <summary>
    /// Parses browse items (popular/new) from HTML.
    /// </summary>
    protected List<BrowseItem> ParseBrowseItems(string html)
    {
        var items = new List<BrowseItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in BrowseItemPattern.Matches(html))
        {
            var url = $"{BaseUrl}{match.Groups["url"].Value}";

            if (!seen.Add(url))
            {
                continue;
            }

            var coverPath = match.Groups["cover"].Value;
            var coverUrl = coverPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? coverPath
                : $"{BaseUrl}{coverPath}";

            var rawTitle = match.Groups["name"].Value.Trim();
            var cleanTitle = Regex.Replace(rawTitle, "<[^>]+>", string.Empty).Trim();

            items.Add(new BrowseItem
            {
                Title = DecodeHtml(cleanTitle),
                Url = url,
                CoverImageUrl = coverUrl,
                Genre = match.Groups["genre"].Success ? DecodeHtml(match.Groups["genre"].Value.Trim()) : string.Empty,
                Source = SourceName,
            });
        }

        return items;
    }

    /// <summary>
    /// Fetches a page from the site.
    /// </summary>
    protected async Task<string> FetchPageAsync(string url, CancellationToken cancellationToken)
    {
        Logger.LogDebug("Fetching page: {Url}", url);
        var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Strips HTML tags and decodes entities.
    /// </summary>
    protected static string StripHtml(string input)
    {
        var stripped = Regex.Replace(input, "<.*?>", string.Empty).Trim();
        return DecodeHtml(stripped);
    }

    /// <summary>
    /// Decodes HTML entities, handling double/triple-encoded content.
    /// </summary>
    protected static string DecodeHtml(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var decoded = input;
        for (int i = 0; i < 5; i++)
        {
            var next = System.Net.WebUtility.HtmlDecode(decoded);
            if (next == decoded)
            {
                break;
            }

            decoded = next;
        }

        return decoded;
    }
}

// ── DTO classes ─────────────────────────────────────────────────

/// <summary>
/// Raw search result from the API.
/// </summary>
public class SearchResultRaw
{
    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the link.</summary>
    public string? Link { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// Search result.
/// </summary>
public class SearchResult
{
    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the source site ("aniworld" or "sto").</summary>
    public string Source { get; set; } = "aniworld";
}

/// <summary>
/// Series information.
/// </summary>
public class SeriesInfo
{
    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the cover image URL.</summary>
    public string CoverImageUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the genres.</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>Gets or sets the seasons.</summary>
    public List<SeasonRef> Seasons { get; set; } = new();

    /// <summary>Gets or sets whether the series has movies.</summary>
    public bool HasMovies { get; set; }
}

/// <summary>
/// Season reference.
/// </summary>
public class SeasonRef
{
    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the season number.</summary>
    public int Number { get; set; }
}

/// <summary>
/// Episode reference.
/// </summary>
public class EpisodeRef
{
    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode number.</summary>
    public int Number { get; set; }

    /// <summary>Gets or sets whether this is a movie.</summary>
    public bool IsMovie { get; set; }
}

/// <summary>
/// A series item from the browse (popular/new) lists.
/// </summary>
public class BrowseItem
{
    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the cover image URL.</summary>
    public string CoverImageUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the genre label.</summary>
    public string Genre { get; set; } = string.Empty;

    /// <summary>Gets or sets the source site ("aniworld" or "sto").</summary>
    public string Source { get; set; } = "aniworld";
}

/// <summary>
/// Episode details with provider links.
/// </summary>
public class EpisodeDetails
{
    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the German title.</summary>
    public string? TitleDe { get; set; }

    /// <summary>Gets or sets the English title.</summary>
    public string? TitleEn { get; set; }

    /// <summary>
    /// Gets or sets the providers grouped by language key.
    /// Key: language key (1=German Dub, 2=English Sub/Dub, 3=German Sub).
    /// Value: dictionary of provider name to redirect URL.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ProvidersByLanguage { get; set; } = new();
}
