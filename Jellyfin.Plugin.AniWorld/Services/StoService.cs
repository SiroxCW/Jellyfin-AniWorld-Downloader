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
/// Service for interacting with the s.to (SerienStream) website.
/// s.to uses a completely different HTML template than aniworld.to, so most extraction
/// methods are overridden here.
/// </summary>
public class StoService : StreamingSiteService
{
    // ── Season/episode link patterns (relative paths) ────────────────
    private static readonly Regex StoSeasonLinkPattern = new(
        @"href=""(?:https?://(?:serienstream|s)\.to)?(/serie/[^""]+/staffel-\d+)/?""",
        RegexOptions.Compiled);

    private static readonly Regex StoEpisodeListPattern = new(
        @"href=""(?:https?://(?:serienstream|s)\.to)?(/serie/[^""]+/staffel-\d+/episode-\d+)""",
        RegexOptions.Compiled);

    private static readonly Regex StoSearchFilterPattern = new(
        @"^/serie/[a-zA-Z0-9\-]+/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StoBrowseItemPattern = new(
        @"<a\s+href=""(?<url>/serie/[^""]+)""[^>]*>.*?data-src=""(?<cover>[^""]+)"".*?<h3>(?<name>.+?)\s*(?:<span[^>]*>\s*</span>)?\s*</h3>\s*(?:<small>(?<genre>[^<]*)</small>)?",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // ── s.to-specific extraction patterns ────────────────────────────
    private static readonly Regex StoTitlePattern = new(
        @"<h1[^>]*class=""[^""]*h2[^""]*""[^>]*>\s*(?<title>.+?)\s*</h1>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex StoDescriptionPattern = new(
        @"<span[^>]*class=""description-text""[^>]*>\s*(?<desc>.*?)\s*</span>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex StoPosterPattern = new(
        @"<img[^>]+(?:data-)?src=""(?<src>(?:https?://(?:serienstream|s)\.to)?/media/images/channel/desktop/[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex StoGenrePattern = new(
        @"<li[^>]*class=""series-group""[^>]*>\s*<strong[^>]*>Genre:</strong>(?<genres>.*?)</li>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex StoGenreLinkPattern = new(
        @"<a[^>]*class=""link-light""[^>]*>(?<genre>[^<]+)</a>",
        RegexOptions.Compiled);

    // s.to episode provider pattern: data-play-url, data-provider-name, data-language-label
    private static readonly Regex StoProviderPattern = new(
        @"data-play-url=""(?<url>[^""]+)"".*?data-provider-name=""(?<provider>[^""]+)"".*?data-language-label=""(?<lang>[^""]+)""",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // s.to episode title: <h2 class="h4 mb-1">S01E01: German Title (English Title) </h2>
    private static readonly Regex StoEpTitleDePattern = new(
        @"S\d{2}E\d{2}:\s(?<title>.*?)(?:\s\(|</h2>)",
        RegexOptions.Compiled);

    private static readonly Regex StoEpTitleEnPattern = new(
        @"S\d{2}E\d{2}:\s.*\((?<title>.*?)\)",
        RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="StoService"/> class.
    /// </summary>
    public StoService(IHttpClientFactory httpClientFactory, ILogger<StoService> logger)
        : base(httpClientFactory.CreateClient("STO"), logger)
    {
    }

    /// <inheritdoc />
    public override string SourceName => "sto";

    /// <inheritdoc />
    protected override string BaseUrl => "https://s.to";

    /// <inheritdoc />
    protected override string SearchUrl => "https://s.to/api/search/suggest";

    /// <inheritdoc />
    protected override string SeriesPathPrefix => "/serie/";

    /// <inheritdoc />
    protected override string PopularPath => "/beliebte-serien";

    /// <inheritdoc />
    protected override string NewSectionHeading => "Neue Serien";

    /// <inheritdoc />
    protected override Regex SeasonLinkPattern => StoSeasonLinkPattern;

    /// <inheritdoc />
    protected override Regex EpisodeListPattern => StoEpisodeListPattern;

    /// <inheritdoc />
    protected override Regex SearchFilterPattern => StoSearchFilterPattern;

    /// <inheritdoc />
    protected override Regex BrowseItemPattern => StoBrowseItemPattern;

    // ── Search (completely different API) ────────────────────────────

    /// <summary>
    /// s.to uses GET /api/search/suggest?term=keyword instead of POST /ajax/search.
    /// Response format: {"shows": [{"name": "...", "url": "/serie/..."}]}
    /// </summary>
    public override async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        var url = $"{SearchUrl}?term={Uri.EscapeDataString(keyword)}";
        Logger.LogDebug("s.to search: {Url}", url);

        var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Logger.LogDebug("s.to search response length: {Length}", json.Length);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var results = new List<SearchResult>();

        if (root.TryGetProperty("shows", out var shows) && shows.ValueKind == JsonValueKind.Array)
        {
            foreach (var show in shows.EnumerateArray())
            {
                var name = show.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var link = show.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;

                link = NormalizeStoLink(link);

                if (string.IsNullOrEmpty(link) || !SearchFilterPattern.IsMatch(link))
                {
                    continue;
                }

                results.Add(new SearchResult
                {
                    Title = StripHtml(name),
                    Url = $"{BaseUrl}{link}",
                    Description = string.Empty,
                    Source = SourceName,
                });
            }
        }

        return results;
    }

    // ── Series info (different HTML template) ────────────────────────

    /// <summary>
    /// s.to uses different HTML elements for title, description, cover, genres, and seasons.
    /// </summary>
    public override async Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(seriesUrl, cancellationToken).ConfigureAwait(false);

        // Title: <h1 class="h2 mb-1 fw-bold">Title</h1>
        var titleMatch = StoTitlePattern.Match(html);
        var title = titleMatch.Success
            ? DecodeHtml(Regex.Replace(titleMatch.Groups["title"].Value.Trim(), "<[^>]+>", string.Empty))
            : "Unknown";

        // Description: <span class="description-text">...</span>
        var descMatch = StoDescriptionPattern.Match(html);
        var description = descMatch.Success
            ? DecodeHtml(descMatch.Groups["desc"].Value.Trim())
            : string.Empty;

        // Cover: <img src="/media/images/channel/desktop/slug...">
        var coverUrl = string.Empty;
        var posterMatch = StoPosterPattern.Match(html);
        if (posterMatch.Success)
        {
            coverUrl = posterMatch.Groups["src"].Value;
            if (!coverUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                coverUrl = $"{BaseUrl}{coverUrl}";
            }
        }

        // Genres: <li class="series-group"><strong>Genre:</strong> <a class="link-light">Horror</a>
        var genres = new List<string>();
        var genreSectionMatch = StoGenrePattern.Match(html);
        if (genreSectionMatch.Success)
        {
            genres = StoGenreLinkPattern.Matches(genreSectionMatch.Groups["genres"].Value)
                .Select(m => DecodeHtml(m.Groups["genre"].Value.Trim()))
                .Distinct()
                .ToList();
        }

        // Seasons: href="/serie/slug/staffel-N" (handles both absolute and relative URLs)
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

        return new SeriesInfo
        {
            Title = title,
            Url = seriesUrl,
            CoverImageUrl = coverUrl,
            Description = description,
            Genres = genres,
            Seasons = seasons,
            HasMovies = false, // s.to doesn't have movie collections like aniworld
        };
    }

    // ── Episode details (different provider HTML attributes) ──────────

    /// <summary>
    /// s.to uses data-play-url, data-provider-name, data-language-label
    /// instead of aniworld's data-lang-key, data-link-target, h4.
    /// Language labels "Deutsch" -> key "1", "Englisch" -> key "2".
    /// </summary>
    public override async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(episodeUrl, cancellationToken).ConfigureAwait(false);

        // Title: <h2 class="h4 mb-1">S01E01: German Title (English Title)</h2>
        var titleDeMatch = StoEpTitleDePattern.Match(html);
        var titleEnMatch = StoEpTitleEnPattern.Match(html);

        var titleDe = titleDeMatch.Success ? DecodeHtml(titleDeMatch.Groups["title"].Value.Trim()) : null;
        var titleEn = titleEnMatch.Success ? DecodeHtml(titleEnMatch.Groups["title"].Value.Trim()) : null;

        // Providers: data-play-url="..." data-provider-name="..." data-language-label="..."
        var providers = new Dictionary<string, Dictionary<string, string>>();

        foreach (Match match in StoProviderPattern.Matches(html))
        {
            var playUrl = match.Groups["url"].Value;
            var provider = match.Groups["provider"].Value.Trim();
            var langLabel = match.Groups["lang"].Value.Trim();

            // Map language label to key: Deutsch -> "1", Englisch -> "2"
            string langKey;
            if (string.Equals(langLabel, "Deutsch", StringComparison.OrdinalIgnoreCase))
            {
                langKey = "1";
            }
            else if (string.Equals(langLabel, "Englisch", StringComparison.OrdinalIgnoreCase))
            {
                langKey = "2";
            }
            else
            {
                continue;
            }

            if (!providers.ContainsKey(langKey))
            {
                providers[langKey] = new Dictionary<string, string>();
            }

            var redirectUrl = playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? playUrl
                : $"https://serienstream.to{playUrl}";

            providers[langKey][provider] = redirectUrl;
        }

        return new EpisodeDetails
        {
            Url = episodeUrl,
            TitleDe = titleDe,
            TitleEn = titleEn,
            ProvidersByLanguage = providers,
        };
    }

    // ── Browse (different page structure) ────────────────────────────

    /// <summary>
    /// s.to popular page uses mb-5 sections; "Meistgesehen" is the popular section.
    /// </summary>
    public override async Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync($"{BaseUrl}{PopularPath}", cancellationToken).ConfigureAwait(false);
        return ExtractStoSection(html, new[] { @"Meistgesehen", @"Beliebt" }, 1);
    }

    /// <summary>
    /// s.to new releases section uses "Neue Staffeln" heading.
    /// </summary>
    public override async Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync($"{BaseUrl}{PopularPath}", cancellationToken).ConfigureAwait(false);
        return ExtractStoSection(html, new[] { @"Neue\s+Staffeln", @"Neue\s+Serien" }, 0);
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static string NormalizeStoLink(string link)
    {
        if (string.IsNullOrEmpty(link))
        {
            return link;
        }

        if (link.StartsWith("/serie/stream/", StringComparison.OrdinalIgnoreCase))
        {
            var slug = link["/serie/stream/".Length..].Trim('/').Split('/')[0];
            return string.IsNullOrEmpty(slug) ? link : $"/serie/{slug}";
        }

        if (link.StartsWith("/serie/", StringComparison.OrdinalIgnoreCase))
        {
            var slug = link["/serie/".Length..].Trim('/').Split('/')[0];
            return string.IsNullOrEmpty(slug) ? link : $"/serie/{slug}";
        }

        return link;
    }

    /// <summary>
    /// Extracts series cards from s.to HTML sections.
    /// Strategy 1: Find section by heading text hints.
    /// Strategy 2: Fall back to mb-5 div index.
    /// </summary>
    private List<BrowseItem> ExtractStoSection(string fullHtml, string[] headingHints, int fallbackIndex)
    {
        string? sectionHtml = null;

        foreach (var hint in headingHints)
        {
            var h2Pattern = new Regex(@"<h2[^>]*>[^<]*" + hint, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var match = h2Pattern.Match(fullHtml);
            if (match.Success)
            {
                var rest = fullHtml[match.Index..];
                var nextSection = Regex.Match(rest[50..], @"<div[^>]*class=""[^""]*\bmb-5\b");
                sectionHtml = nextSection.Success ? rest[..(nextSection.Index + 50)] : rest;
                break;
            }
        }

        if (sectionHtml == null)
        {
            var mb5Starts = Regex.Matches(fullHtml, @"<div[^>]*class=""[^""]*\bmb-5\b")
                .Select(m => m.Index)
                .ToList();

            if (fallbackIndex < mb5Starts.Count)
            {
                var start = mb5Starts[fallbackIndex];
                var end = fallbackIndex + 1 < mb5Starts.Count
                    ? mb5Starts[fallbackIndex + 1]
                    : fullHtml.Length;
                sectionHtml = fullHtml[start..end];
            }
        }

        if (string.IsNullOrEmpty(sectionHtml))
        {
            Logger.LogWarning("Could not find s.to browse section");
            return new List<BrowseItem>();
        }

        return ExtractStoCards(sectionHtml);
    }

    /// <summary>
    /// Extracts series cards from s.to HTML using anchor links with img alt for title.
    /// </summary>
    private List<BrowseItem> ExtractStoCards(string sectionHtml)
    {
        var results = new List<BrowseItem>();
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var linkPattern = new Regex(
            @"<a\s[^>]*href=""(/serie/[^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.Singleline);

        foreach (Match m in linkPattern.Matches(sectionHtml))
        {
            var path = m.Groups[1].Value;
            var inner = m.Groups[2].Value;

            var parts = path.Trim('/').Split('/');
            if (parts.Length < 2)
            {
                continue;
            }

            var seriesSlug = parts[1];
            if (!seenSlugs.Add(seriesSlug))
            {
                continue;
            }

            var seriesUrl = $"{BaseUrl}/serie/{seriesSlug}";

            var title = string.Empty;
            var altMatch = Regex.Match(inner, @"<img[^>]+\balt=""([^""]+)""");
            if (altMatch.Success)
            {
                title = DecodeHtml(altMatch.Groups[1].Value.Trim());
            }
            else
            {
                var titleAttr = Regex.Match(inner, @"\btitle=""([^""]+)""");
                if (titleAttr.Success)
                {
                    title = DecodeHtml(titleAttr.Groups[1].Value.Trim());
                }
            }

            var posterUrl = string.Empty;
            var dataSrcMatch = Regex.Match(inner, @"<img[^>]+\bdata-src=""(/[^""]+)""");
            if (dataSrcMatch.Success)
            {
                posterUrl = $"{BaseUrl}{dataSrcMatch.Groups[1].Value.Trim()}";
            }
            else
            {
                var srcMatch = Regex.Match(inner, @"<img[^>]+\bsrc=""((?:https?://|/)[^""]+)""");
                if (srcMatch.Success)
                {
                    var raw = srcMatch.Groups[1].Value.Trim();
                    posterUrl = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? raw : $"{BaseUrl}{raw}";
                }
            }

            if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(posterUrl))
            {
                results.Add(new BrowseItem
                {
                    Title = title,
                    Url = seriesUrl,
                    CoverImageUrl = posterUrl,
                    Genre = string.Empty,
                    Source = SourceName,
                });
            }
        }

        return results;
    }
}
