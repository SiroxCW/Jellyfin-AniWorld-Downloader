using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Service for interacting with the filmo.to movie website.
/// filmo.to only hosts movies (no series). Each movie page lists provider
/// chips per language; picking a chip exchanges a one-shot playback token
/// (POST /n → GET /n/{token}) that yields the actual provider embed URL.
/// Tokens are single-use and change on every page load, so the exchange is
/// always performed with a freshly fetched page at download time.
/// </summary>
public class FilmoService : StreamingSiteService
{
    /// <summary>
    /// The playback-token exchange endpoint on filmo.to.
    /// </summary>
    private const string TokenEndpoint = "/n";

    // ── Card patterns (search / popular / new) ───────────────────────
    // Cards are anchors to /movies/{slug} containing a title element and a poster image.
    private static readonly Regex CardAnchorPattern = new(
        @"<a\b[^>]*href=[""'](?<url>(?:https?://(?:www\.)?filmo\.to)?/movies/[\w-]+)(?:\?[^""']*)?[""'][^>]*>(?<card>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CardTitlePattern = new(
        @"class=[""'][^""']*(?:popular-spotlight-card__title|movie-poster-grid-card__title|swiper-card-title)[^""']*[""'][^>]*>\s*(?<title>.*?)\s*</",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CardImagePattern = new(
        @"<img\b[^>]*\bsrc=[""'](?<src>[^""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Movie page patterns ──────────────────────────────────────────
    private static readonly Regex MovieTitlePattern = new(
        @"<h1\b[^>]*>\s*(?<title>.*?)\s*</h1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SynopsisPattern = new(
        @"<p\b[^>]*class=""[^""]*movie-detail-synopsis[^""]*""[^>]*>(?<synopsis>.*?)</p>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PosterPattern = new(
        @"<meta\s+property=""og:image""\s+content=""(?<src>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CsrfPattern = new(
        @"<meta\s+name=""csrf-token""\s+content=""(?<token>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LangMarkerPattern = new(
        @"<span[^>]*class=""[^""]*provider-row__lang[^""]*""[^>]*>\s*(?<lang>[^<]+?)\s*</span>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChipTagPattern = new(
        @"<div\b[^>]*\bdata-provider-chip\b[^>]*>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ChipTokenPattern = new(
        @"data-p=""(?<token>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex ChipLabelPattern = new(
        @"aria-label=""(?<label>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex GenreFieldPattern = new(
        @"<h3\b[^>]*>\s*Genres\s*</h3>.*?<dd\b[^>]*>(?<genres>.*?)</dd>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GenreLinkPattern = new(
        @"<a\b[^>]*>(?<genre>[^<]+)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Playback-token interstitial patterns ─────────────────────────
    private static readonly Regex InterstitialOpenTagPattern = new(
        @"<a\b[^>]*\bclass=""[^""]*\bopen\b[^""]*""[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HrefPattern = new(
        @"href=""(?<url>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmoService"/> class.
    /// </summary>
    public FilmoService(IHttpClientFactory httpClientFactory, ILogger<FilmoService> logger)
        : base(httpClientFactory.CreateClient("Filmo"), logger)
    {
    }

    /// <inheritdoc />
    public override string SourceName => "filmo";

    /// <inheritdoc />
    protected override string BaseUrl => "https://filmo.to";

    /// <inheritdoc />
    protected override string SearchUrl => $"{BaseUrl}/search";

    /// <inheritdoc />
    protected override string SeriesPathPrefix => "/movies/";

    /// <inheritdoc />
    protected override string PopularPath => "/popular";

    /// <inheritdoc />
    protected override string NewSectionHeading => "Neue Filme";

    /// <inheritdoc />
    protected override Regex SeasonLinkPattern => UnusedPattern;

    /// <inheritdoc />
    protected override Regex EpisodeListPattern => UnusedPattern;

    /// <inheritdoc />
    protected override Regex SearchFilterPattern => UnusedPattern;

    /// <inheritdoc />
    protected override Regex BrowseItemPattern => UnusedPattern;

    /// <summary>
    /// Placeholder pattern for inherited members that filmo.to does not use
    /// (all base implementations that consume these are overridden here).
    /// </summary>
    private static readonly Regex UnusedPattern = new(@"\x00", RegexOptions.Compiled);

    // ── Search ───────────────────────────────────────────────────────

    /// <summary>
    /// Searches filmo.to for movies via GET /search?q=keyword.
    /// </summary>
    public override async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        var url = $"{SearchUrl}?q={Uri.EscapeDataString(keyword)}";
        Logger.LogDebug("filmo search: {Url}", url);

        var html = await FetchPageAsync(url, cancellationToken).ConfigureAwait(false);
        var items = ParseCards(html);

        return items.Take(30)
            .Select(i => new SearchResult
            {
                Title = i.Title,
                Url = i.Url,
                Description = string.Empty,
                Source = SourceName,
            })
            .ToList();
    }

    // ── Series (movie) info ──────────────────────────────────────────

    /// <summary>
    /// Gets movie info. A filmo movie is exposed as a single "Season 0" so the
    /// existing series/season/episode UI flow works unchanged; the season URL
    /// is the movie URL itself.
    /// </summary>
    public override async Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(seriesUrl, cancellationToken).ConfigureAwait(false);

        var title = MovieTitlePattern.Match(html).Groups["title"].Value;
        var cleanTitle = DecodeHtml(StripHtml(title));
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            throw new InvalidOperationException($"Could not find movie title on {seriesUrl}");
        }

        var poster = PosterPattern.Match(html).Groups["src"].Value;
        if (!Uri.IsWellFormedUriString(poster, UriKind.Absolute))
        {
            poster = new Uri(new Uri(BaseUrl), poster).ToString();
        }

        var synopsis = SynopsisPattern.Match(html).Groups["synopsis"].Value;
        var description = DecodeHtml(StripHtml(synopsis));

        var genres = new List<string>();
        var genreMatch = GenreFieldPattern.Match(html);
        if (genreMatch.Success)
        {
            foreach (Match link in GenreLinkPattern.Matches(genreMatch.Groups["genres"].Value))
            {
                var genre = DecodeHtml(link.Groups["genre"].Value.Trim());
                if (!string.IsNullOrWhiteSpace(genre) && !genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                {
                    genres.Add(genre);
                }

                if (genres.Count >= 5)
                {
                    break;
                }
            }
        }

        return new SeriesInfo
        {
            Title = cleanTitle,
            Url = seriesUrl,
            CoverImageUrl = poster,
            Description = description,
            Genres = genres,
            Seasons = new List<SeasonRef>
            {
                new() { Url = seriesUrl, Number = 0 },
            },
            HasMovies = false,
        };
    }

    // ── Episodes ─────────────────────────────────────────────────────

    /// <summary>
    /// The "season" of a filmo movie is the movie itself: one movie entry.
    /// </summary>
    public override Task<List<EpisodeRef>> GetEpisodesAsync(string seasonUrl, CancellationToken cancellationToken = default)
    {
        var episodes = new List<EpisodeRef>
        {
            new() { Url = seasonUrl, Number = 1, IsMovie = true },
        };
        return Task.FromResult(episodes);
    }

    // ── Episode (movie) details / providers ──────────────────────────

    /// <summary>
    /// Parses the provider chips from the movie page.
    /// ProvidersByLanguage maps language key → provider name → composite
    /// "movieUrl|providerName"; the playback token is always re-fetched fresh
    /// during the redirect exchange because it changes on every page load.
    /// </summary>
    public override async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(episodeUrl, cancellationToken).ConfigureAwait(false);

        var titleMatch = MovieTitlePattern.Match(html);
        var title = titleMatch.Success ? DecodeHtml(StripHtml(titleMatch.Groups["title"].Value)) : string.Empty;

        var details = new EpisodeDetails
        {
            Url = episodeUrl,
            TitleDe = title,
            TitleEn = title,
        };

        // Walk the page in document order, pairing each provider chip with the
        // language row it appears in.
        var markers = new List<(int Index, string Lang)>();
        foreach (Match m in LangMarkerPattern.Matches(html))
        {
            var lang = MapLanguage(m.Groups["lang"].Value);
            if (lang != null)
            {
                markers.Add((m.Index, lang));
            }
        }

        if (markers.Count == 0)
        {
            return details;
        }

        var markerIndex = 0;
        foreach (Match chip in ChipTagPattern.Matches(html))
        {
            while (markerIndex < markers.Count - 1 && markers[markerIndex + 1].Index < chip.Index)
            {
                markerIndex++;
            }

            var langKey = markers[markerIndex].Lang;

            var tokenMatch = ChipTokenPattern.Match(chip.Value);
            var labelMatch = ChipLabelPattern.Match(chip.Value);
            if (!tokenMatch.Success)
            {
                continue;
            }

            var provider = MapProvider(labelMatch.Success ? labelMatch.Groups["label"].Value : null);
            if (provider == null)
            {
                Logger.LogDebug("Skipping unknown filmo provider '{Label}' on {Url}",
                    labelMatch.Success ? labelMatch.Groups["label"].Value : "(no label)", episodeUrl);
                continue;
            }

            if (!details.ProvidersByLanguage.TryGetValue(langKey, out var providers))
            {
                providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                details.ProvidersByLanguage[langKey] = providers;
            }

            // Composite value: the movie URL plus the provider name. ResolveRedirectAsync
            // re-fetches the page and uses a fresh token for this provider.
            if (!providers.ContainsKey(provider))
            {
                providers[provider] = $"{episodeUrl}|{provider}";
            }
        }

        return details;
    }

    // ── Popular / new ────────────────────────────────────────────────

    /// <summary>
    /// Gets the popular movies list from filmo.to/popular.
    /// </summary>
    public override async Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync($"{BaseUrl}{PopularPath}", cancellationToken).ConfigureAwait(false);
        return ParseCards(html).Take(30).ToList();
    }

    /// <summary>
    /// Gets the newest movies from filmo.to/movies.
    /// </summary>
    public override async Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync($"{BaseUrl}/movies", cancellationToken).ConfigureAwait(false);
        return ParseCards(html).Take(30).ToList();
    }

    // ── Redirect resolution (playback-token exchange) ────────────────

    /// <summary>
    /// Resolves a filmo composite value ("movieUrl|providerName") to the actual
    /// provider embed URL:
    /// 1. Re-fetch the movie page (fresh CSRF token and fresh provider token,
    ///    both of which change on every page load).
    /// 2. POST /n with the provider token → one-shot playback token.
    /// 3. GET /n/{playbackToken} → redirect or interstitial page pointing to
    ///    the provider embed.
    /// </summary>
    public override async Task<string> ResolveRedirectAsync(string redirectUrl, CancellationToken cancellationToken = default)
    {
        var separator = redirectUrl.LastIndexOf('|');
        if (separator < 0)
        {
            throw new InvalidOperationException($"Invalid filmo redirect value: {redirectUrl}");
        }

        var movieUrl = redirectUrl[..separator];
        var provider = redirectUrl[(separator + 1)..];

        // 1. Fresh movie page → CSRF token + fresh provider token
        var html = await FetchPageAsync(movieUrl, cancellationToken).ConfigureAwait(false);

        var csrfMatch = CsrfPattern.Match(html);
        if (!csrfMatch.Success)
        {
            throw new InvalidOperationException($"filmo.to page is missing its CSRF token: {movieUrl}");
        }

        var providerToken = await GetProviderTokenAsync(html, provider, movieUrl, cancellationToken).ConfigureAwait(false);

        // 2. Exchange provider token for a one-shot playback token
        var playbackToken = await ExchangePlaybackTokenAsync(
            csrfMatch.Groups["token"].Value, providerToken, movieUrl, cancellationToken).ConfigureAwait(false);

        // 3. Follow the one-shot playback token to the provider embed
        return await FollowPlaybackTokenAsync(playbackToken, movieUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the fresh data-p token of the given provider on an already-fetched movie page.
    /// </summary>
    private async Task<string> GetProviderTokenAsync(
        string html, string provider, string movieUrl, CancellationToken cancellationToken)
    {
        var markers = new List<(int Index, string Lang)>();
        foreach (Match m in LangMarkerPattern.Matches(html))
        {
            var lang = MapLanguage(m.Groups["lang"].Value);
            if (lang != null)
            {
                markers.Add((m.Index, lang));
            }
        }

        if (markers.Count > 0)
        {
            var markerIndex = 0;
            foreach (Match chip in ChipTagPattern.Matches(html))
            {
                while (markerIndex < markers.Count - 1 && markers[markerIndex + 1].Index < chip.Index)
                {
                    markerIndex++;
                }

                var labelMatch = ChipLabelPattern.Match(chip.Value);
                var chipProvider = MapProvider(labelMatch.Success ? labelMatch.Groups["label"].Value : null);
                if (!chipProvider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var tokenMatch = ChipTokenPattern.Match(chip.Value);
                if (tokenMatch.Success)
                {
                    return tokenMatch.Groups["token"].Value;
                }
            }
        }

        throw new InvalidOperationException(
            $"Provider {provider} is no longer available on {movieUrl} (page may have changed)");
    }

    /// <summary>
    /// POSTs the provider token to /n and returns the one-shot playback token.
    /// </summary>
    private async Task<string> ExchangePlaybackTokenAsync(
        string csrfToken, string providerToken, string movieUrl, CancellationToken cancellationToken)
    {
        var payload = new StringContent(
            JsonSerializer.Serialize(new { p = providerToken }),
            Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{TokenEndpoint}");
        request.Content = payload;
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("Referer", movieUrl);
        request.Headers.Accept.ParseAdd("application/json");

        var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("x", out var xElement) ||
            xElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("filmo.to did not return a playback token");
        }

        var playbackToken = xElement.GetString();
        if (string.IsNullOrWhiteSpace(playbackToken))
        {
            throw new InvalidOperationException("filmo.to returned an empty playback token");
        }

        return playbackToken;
    }

    /// <summary>
    /// GETs /n/{playbackToken} and extracts the provider embed URL from either
    /// a redirect or the "Video öffnen" interstitial page.
    /// </summary>
    private async Task<string> FollowPlaybackTokenAsync(
        string playbackToken, string movieUrl, CancellationToken cancellationToken)
    {
        var tokenUrl = $"{BaseUrl}{TokenEndpoint}/{Uri.EscapeDataString(playbackToken)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
        request.Headers.Add("Referer", movieUrl);

        var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // The shared HttpClient follows redirects. If filmo redirected to the
        // provider, the final request URI points at the provider embed.
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? tokenUrl;
        if (Uri.TryCreate(finalUrl, UriKind.Absolute, out var finalUri) &&
            finalUri.Host != "filmo.to" && finalUri.Host != "www.filmo.to")
        {
            ValidateEmbedUrl(finalUrl);
            return finalUrl;
        }

        // filmo serves an interstitial page with an "open" link to the provider
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var openMatch = InterstitialOpenTagPattern.Match(html);
        if (!openMatch.Success)
        {
            throw new InvalidOperationException("filmo.to did not redirect to a stream provider");
        }

        var hrefMatch = HrefPattern.Match(openMatch.Value);
        if (!hrefMatch.Success)
        {
            throw new InvalidOperationException("filmo.to interstitial is missing the provider link");
        }

        var embed = hrefMatch.Groups["url"].Value;
        if (!embed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            embed = new Uri(new Uri(tokenUrl), embed).ToString();
        }

        ValidateEmbedUrl(embed);
        return embed;
    }

    /// <summary>
    /// Ensures the resolved URL actually points at a provider host, not filmo itself.
    /// </summary>
    private static void ValidateEmbedUrl(string embedUrl)
    {
        if (!Uri.TryCreate(embedUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"filmo.to returned an invalid provider URL: {embedUrl}");
        }

        var host = uri.Host.ToLowerInvariant();
        if (host == "filmo.to" || host == "www.filmo.to")
        {
            throw new InvalidOperationException($"filmo.to did not redirect to a stream provider: {embedUrl}");
        }
    }

    // ── Shared card parsing ──────────────────────────────────────────

    /// <summary>
    /// Parses movie cards (title, URL, poster) from search/popular/new pages.
    /// </summary>
    private List<BrowseItem> ParseCards(string html)
    {
        var items = new List<BrowseItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in CardAnchorPattern.Matches(html))
        {
            var rawUrl = match.Groups["url"].Value;
            var url = rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? rawUrl
                : $"{BaseUrl}{rawUrl}";

            if (!seen.Add(url))
            {
                continue;
            }

            var card = match.Groups["card"].Value;

            var titleMatch = CardTitlePattern.Match(card);
            if (!titleMatch.Success)
            {
                continue;
            }

            var title = DecodeHtml(StripHtml(titleMatch.Groups["title"].Value));
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var imageMatch = CardImagePattern.Match(card);
            var cover = imageMatch.Success ? imageMatch.Groups["src"].Value : string.Empty;
            if (!string.IsNullOrEmpty(cover) && !cover.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                cover = $"{BaseUrl}{cover}";
            }

            items.Add(new BrowseItem
            {
                Title = title,
                Url = url,
                CoverImageUrl = cover,
                Genre = string.Empty,
                Source = SourceName,
            });
        }

        return items;
    }

    // ── Label mapping ────────────────────────────────────────────────

    /// <summary>
    /// Maps a filmo provider chip label to the canonical extractor name,
    /// or null when the plugin has no extractor for it.
    /// </summary>
    internal static string? MapProvider(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var text = label.Trim().ToLowerInvariant();
        if (text.Contains("voe"))
        {
            return "VOE";
        }

        if (text.Contains("filemoon") || text.Contains("byse"))
        {
            return "Filemoon";
        }

        if (text.Contains("vidmoly"))
        {
            return "Vidmoly";
        }

        if (text.Contains("vidoza"))
        {
            return "Vidoza";
        }

        return null;
    }

    /// <summary>
    /// Maps a filmo language label to the plugin language key
    /// ("1" = German Dub, "2" = English Dub), or null when unknown.
    /// </summary>
    internal static string? MapLanguage(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var text = label.Trim().ToLowerInvariant();
        if (text.Contains("deutsch") || text.Contains("german"))
        {
            return "1";
        }

        if (text.Contains("english"))
        {
            return "2";
        }

        return null;
    }
}
