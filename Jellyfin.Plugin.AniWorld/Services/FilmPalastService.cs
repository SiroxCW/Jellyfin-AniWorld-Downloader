using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Service for the filmpalast.to movie website.
/// Each /stream/{slug} page is a single movie listing provider links in
/// <c>currentStreamLinks</c> blocks. Only providers that the plugin can
/// extract (VOE, Filemoon/Byse, Vidmoly, Vidoza) are exposed.
/// </summary>
public class FilmPalastService : StreamingSiteService
{
    /// <summary>
    /// Card on search / browse pages: an anchor to /stream/{slug} with a title
    /// and a poster image.
    /// </summary>
    private static readonly Regex CardPattern = new(
        @"<a\b[^>]*href=([""])(?:https?:)?//(?:www\.)?filmpalast\.(?:to|de)/stream/(?<slug>[^/?#]+)\1[^>]*title=([""])(?<title>[^""]*)\2[^>]*>(?<card>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CardImagePattern = new(
        @"<img\b[^>]*\bsrc=([""])(?<src>[^""]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Movie title: <c>&lt;span class="value-title" title="Name" /&gt;</c>.
    /// </summary>
    private static readonly Regex MovieTitlePattern = new(
        @"class=[""]value-title[""][^>]*title=[""](?<title>[^""]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PageTitlePattern = new(
        @"<title>\s*Film\s+(?<title>.+?)\s+Stream",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DescriptionPattern = new(
        @"<meta\s+name=[""]description[""]\s+content=[""](?<desc>[^""]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PosterPattern = new(
        @"<img\b[^>]*\bsrc=([""])(?<src>/files/movies/[^""]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// One provider block: the host name and the active player URL
    /// (either a data-player-url or a plain href after comments are stripped).
    /// </summary>
    private static readonly Regex BlockNamePattern = new(
        @"<p\s+class=[""]hostName[""]>\s*(?<name>[^<]+?)\s*</p>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DataPlayerUrlPattern = new(
        @"data-player-url=([""])(?<url>[^""]+)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HrefPattern = new(
        @"href=([""])(?<url>https?://[^""]+)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Placeholder pattern for inherited members this site does not use.
    /// </summary>
    private static readonly Regex UnusedPattern = new(@"\x00", RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmPalastService"/> class.
    /// </summary>
    public FilmPalastService(IHttpClientFactory httpClientFactory, ILogger<FilmPalastService> logger)
        : base(httpClientFactory.CreateClient("FilmPalast"), logger)
    {
    }

    /// <inheritdoc />
    public override string SourceName => "filmpalast";

    /// <inheritdoc />
    protected override string BaseUrl => "https://filmpalast.to";

    /// <inheritdoc />
    protected override string SearchUrl => $"{BaseUrl}/";

    /// <inheritdoc />
    protected override string SeriesPathPrefix => "/stream/";

    /// <inheritdoc />
    protected override string PopularPath => "/";

    /// <inheritdoc />
    protected override string NewSectionHeading => string.Empty;

    /// <inheritdoc />
    protected override Regex SeasonLinkPattern => UnusedPattern;

    /// <inheritdoc />
    protected override Regex EpisodeListPattern => UnusedPattern;

    /// <inheritdoc />
    protected override Regex SearchFilterPattern => UnusedPattern;

    /// <inheritdoc />
    protected override Regex BrowseItemPattern => UnusedPattern;

    // ── Search ───────────────────────────────────────────────────────

    /// <summary>
    /// Searches filmpalast.to via GET /?s=keyword and parses the result cards.
    /// </summary>
    public override async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        var url = $"{SearchUrl}?s={Uri.EscapeDataString(keyword)}";
        Logger.LogDebug("filmpalast search: {Url}", url);

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

    // ── Movie info ───────────────────────────────────────────────────

    /// <summary>
    /// Gets movie info. A filmpalast movie is exposed as a single "Season 0"
    /// so the existing series/season/episode UI flow works unchanged.
    /// </summary>
    public override async Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(seriesUrl, cancellationToken).ConfigureAwait(false);

        var title = ExtractTitle(html, seriesUrl);
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException($"Could not find movie title on {seriesUrl}");
        }

        var poster = PosterPattern.Match(html).Groups["src"].Value;
        if (!string.IsNullOrEmpty(poster) && !poster.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            poster = $"{BaseUrl}{poster}";
        }

        var description = DecodeHtml(StripHtml(DescriptionPattern.Match(html).Groups["desc"].Value));

        return new SeriesInfo
        {
            Title = title,
            Url = seriesUrl,
            CoverImageUrl = poster,
            Description = description,
            Genres = new List<string>(),
            Seasons = new List<SeasonRef>
            {
                new() { Url = seriesUrl, Number = 0 },
            },
            HasMovies = false,
        };
    }

    // ── Episodes ─────────────────────────────────────────────────────

    /// <summary>
    /// The "season" of a filmpalast movie is the movie itself: one movie entry.
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
    /// Parses the currentStreamLinks blocks from the movie page into providers.
    /// The page is single-language; the language key is derived from the
    /// slug/title ("english" marker → "2", otherwise German "1").
    /// </summary>
    public override async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(episodeUrl, cancellationToken).ConfigureAwait(false);

        var title = ExtractTitle(html, episodeUrl);

        var details = new EpisodeDetails
        {
            Url = episodeUrl,
            TitleDe = title,
            TitleEn = title,
        };

        var langKey = episodeUrl.Contains("english", StringComparison.OrdinalIgnoreCase) ||
                       (title != null && title.Contains("english", StringComparison.OrdinalIgnoreCase))
            ? "2"
            : "1";

        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Strip HTML comments so only the active (uncommented) player link per
        // block is considered.
        var noComments = Regex.Replace(html, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

        var blocks = noComments.Split(new[] { "<ul class=\"currentStreamLinks\">" }, StringSplitOptions.None);
        foreach (var rawBlock in blocks.Skip(1))
        {
            var endIdx = rawBlock.IndexOf("</ul>", StringComparison.Ordinal);
            var block = endIdx >= 0 ? rawBlock[..endIdx] : rawBlock;

            var nameMatch = BlockNamePattern.Match(block);
            var name = nameMatch.Success ? nameMatch.Groups["name"].Value.Trim() : string.Empty;

            var urlMatch = DataPlayerUrlPattern.Match(block);
            if (!urlMatch.Success)
            {
                urlMatch = HrefPattern.Match(block);
            }

            if (!urlMatch.Success)
            {
                continue;
            }

            var embedUrl = urlMatch.Groups["url"].Value;
            var provider = MapProvider(embedUrl, name);
            if (provider == null)
            {
                Logger.LogDebug("Skipping filmpalast host '{Name}' ({Url}) — no extractor", name, embedUrl);
                continue;
            }

            if (!providers.ContainsKey(provider))
            {
                providers[provider] = embedUrl;
            }
        }

        if (providers.Count > 0)
        {
            details.ProvidersByLanguage[langKey] = providers;
        }

        return details;
    }

    // ── Popular / new ────────────────────────────────────────────────

    /// <summary>
    /// Gets the front page cards from filmpalast.to.
    /// </summary>
    public override async Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(BaseUrl, cancellationToken).ConfigureAwait(false);
        return ParseCards(html).Take(30).ToList();
    }

    /// <summary>
    /// filmpalast.to has no separate "new" listing; reuse the front page.
    /// </summary>
    public override async Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default)
    {
        return await GetPopularAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── Redirect resolution ──────────────────────────────────────────

    /// <summary>
    /// The stored provider value is already the provider embed URL, so there is
    /// nothing to resolve.
    /// </summary>
    public override Task<string> ResolveRedirectAsync(string redirectUrl, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(redirectUrl);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the clean movie title, preferring the value-title span and
    /// falling back to the <c>&lt;title&gt;</c> tag.
    /// </summary>
    private static string ExtractTitle(string html, string url)
    {
        var valueMatch = MovieTitlePattern.Match(html);
        if (valueMatch.Success && !string.IsNullOrWhiteSpace(valueMatch.Groups["title"].Value))
        {
            return valueMatch.Groups["title"].Value.Trim();
        }

        var pageMatch = PageTitlePattern.Match(html);
        if (pageMatch.Success)
        {
            return pageMatch.Groups["title"].Value.Trim();
        }

        return string.Empty;
    }

    /// <summary>
    /// Parses movie cards (title, URL, poster) from a page.
    /// </summary>
    private List<BrowseItem> ParseCards(string html)
    {
        var items = new List<BrowseItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in CardPattern.Matches(html))
        {
            var slug = match.Groups["slug"].Value;
            var url = $"{BaseUrl}/stream/{slug}";

            if (!seen.Add(url))
            {
                continue;
            }

            var title = DecodeHtml(match.Groups["title"].Value.Trim());
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var imageMatch = CardImagePattern.Match(match.Groups["card"].Value);
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

    /// <summary>
    /// Maps a provider embed URL (with its label as a hint) to the canonical
    /// extractor name, or null when the plugin has no extractor for it.
    /// </summary>
    internal static string? MapProvider(string embedUrl, string? label)
    {
        if (!Uri.TryCreate(embedUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();

        if (host.Contains("voe"))
        {
            return "VOE";
        }

        if (host.Contains("filemoon") || host.Contains("byse") ||
            host == "moflix-stream.link")
        {
            return "Filemoon";
        }

        if (host.Contains("vidmoly"))
        {
            return "Vidmoly";
        }

        if (host.Contains("vidoza"))
        {
            return "Vidoza";
        }

        return null;
    }
}
