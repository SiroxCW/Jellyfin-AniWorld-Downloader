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
/// Service for interacting with the aniworld.to website.
/// </summary>
public class AniWorldService
{
    private const string BaseUrl = "https://aniworld.to";
    private const string SearchUrl = "https://aniworld.to/ajax/search";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly Regex SeasonLinkPattern = new(
        @"<a[^>]*href=""(/anime/stream/[^""]+/staffel-\d+)""[^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex EpisodeListPattern = new(
        @"<a[^>]*href=""(/anime/stream/[^""]+/staffel-\d+/episode-\d+)""[^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex MovieListPattern = new(
        @"<a[^>]*href=""(/anime/stream/[^""]+/filme/film-\d+)""[^>]*>",
        RegexOptions.Compiled);

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

    private readonly HttpClient _httpClient;
    private readonly ILogger<AniWorldService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniWorldService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger instance.</param>
    public AniWorldService(IHttpClientFactory httpClientFactory, ILogger<AniWorldService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("AniWorld");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _logger = logger;
    }

    /// <summary>
    /// Searches for anime on aniworld.to.
    /// </summary>
    /// <param name="keyword">Search keyword.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of search results.</returns>
    public async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
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

        var animePattern = new Regex(@"^/anime/stream/[a-zA-Z0-9\-]+/?$", RegexOptions.IgnoreCase);
        return results
            .Where(r => !string.IsNullOrEmpty(r.Link) && animePattern.IsMatch(r.Link))
            .Select(r => new SearchResult
            {
                Title = StripHtml(r.Title ?? string.Empty),
                Url = $"{BaseUrl}{r.Link}",
                Description = StripHtml(r.Description ?? string.Empty),
            })
            .ToList();
    }

    /// <summary>
    /// Gets detailed information about an anime series.
    /// </summary>
    /// <param name="seriesUrl">The series URL on aniworld.to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Series info.</returns>
    public async Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default)
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

        // Check for movies
        var hasMovies = html.Contains("/filme/film-", StringComparison.OrdinalIgnoreCase);

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
    /// <param name="seasonUrl">The season URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of episodes.</returns>
    public async Task<List<EpisodeRef>> GetEpisodesAsync(string seasonUrl, CancellationToken cancellationToken = default)
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
    /// <param name="episodeUrl">The episode URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Episode details with provider links.</returns>
    public async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(episodeUrl, cancellationToken).ConfigureAwait(false);

        var germanTitle = GermanTitlePattern.Match(html);
        var englishTitle = EnglishTitlePattern.Match(html);

        var providers = new Dictionary<string, Dictionary<string, string>>();

        // Parse provider links from the episode page
        // Each <li> has data-lang-key, data-link-target, and <h4>ProviderName</h4>
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
    /// Resolves a redirect URL to the actual provider embed URL.
    /// </summary>
    /// <param name="redirectUrl">The aniworld.to redirect URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved provider URL.</returns>
    public async Task<string> ResolveRedirectAsync(string redirectUrl, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        // The redirect should land us on the provider's embed page
        return response.RequestMessage?.RequestUri?.ToString() ?? redirectUrl;
    }

    private async Task<string> FetchPageAsync(string url, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching page: {Url}", url);
        var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string StripHtml(string input)
    {
        var stripped = Regex.Replace(input, "<.*?>", string.Empty).Trim();
        return DecodeHtml(stripped);
    }

    /// <summary>
    /// Decodes HTML entities, handling double/triple-encoded content.
    /// Loops until the output stabilizes (no more entities to decode).
    /// </summary>
    private static string DecodeHtml(string input)
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
    /// Key: language key (1=German Dub, 2=English Sub, 3=German Sub).
    /// Value: dictionary of provider name to redirect URL.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ProvidersByLanguage { get; set; } = new();
}
