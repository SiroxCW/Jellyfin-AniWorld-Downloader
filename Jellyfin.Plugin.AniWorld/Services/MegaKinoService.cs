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
/// Service for the MegaKino website (rotating domain, resolved via a public
/// tracker). Movies live under /films/, serials under /serials/. Serials list
/// every episode on one page (a se-select episode picker plus a per-episode
/// mr-select of hoster links); movies use a tabs-block of iframes. The site
/// is protected by a one-time JS token cookie (GET /index.php?yg=token).
/// Content is German-only.
/// </summary>
public class MegaKinoService : StreamingSiteService
{
    private const string DomainSource =
        "https://raw.githubusercontent.com/Yezun-hikari/new-domain-check/refs/heads/main/monitors/megakino/domain.txt";

    // ── Page patterns ────────────────────────────────────────────────
    private static readonly Regex TitlePattern = new(
        @"<meta\s+itemprop=[""']name[""']\s+content=[""'](?<title>[^""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PosterPattern = new(
        @"<img[^>]*itemprop=[""']image[""'][^>]*data-src=[""'](?<src>[^""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DescriptionPattern = new(
        @"class=[""']page__text[^""']*[""'][^>]*>(?<desc>.*?)</div>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Serial episode picker ────────────────────────────────────────
    private static readonly Regex EpisodePickerPattern = new(
        @"<select\b(?=[^>]*se-select)[^>]*>(?<opts>.*?)</select>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex EpisodeOptionPattern = new(
        @"<option\s+value=[""'](?<id>ep\d+)[""'][^>]*>\s*(?<label>[^<]*?)\s*</option>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProviderOptionPattern = new(
        @"<option\s+value=[""'](?<url>[^""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Movie player tabs ────────────────────────────────────────────
    private static readonly Regex ContentBlockPattern = new(
        @"<div\s+class=[""']tabs-block__content[^""']*[""'][^>]*>(?<block>.*?)</div>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IframeUrlPattern = new(
        @"<iframe[^>]*\b(?:src|data-src)=[""'](?<url>[^""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Cards (search / browse) ──────────────────────────────────────
    private static readonly Regex CardAnchorPattern = new(
        @"<a\b[^>]*href=[""'](?<href>/(?:films|serials)/[^""']+)[""'][^>]*>(?<card>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CardTitlePattern = new(
        @"<h3\s+class=[""']poster__title[^""']*[""']>(?<title>[^<]+)</h3>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CardAltPattern = new(
        @"<img[^>]*\balt=[""'](?<title>[^""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CardImagePattern = new(
        @"<img[^>]*\bdata-src=[""'](?<src>[^""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnusedPattern = new(@"\x00", RegexOptions.Compiled);

    private readonly SemaphoreSlim _domainLock = new(1, 1);
    private string? _domain;

    /// <summary>
    /// Initializes a new instance of the <see cref="MegaKinoService"/> class.
    /// </summary>
    public MegaKinoService(IHttpClientFactory httpClientFactory, ILogger<MegaKinoService> logger)
        : base(httpClientFactory.CreateClient("MegaKino"), logger)
    {
    }

    /// <inheritdoc />
    public override string SourceName => "megakino";

    /// <inheritdoc />
    protected override string BaseUrl => "https://{domain}";

    /// <inheritdoc />
    protected override string SearchUrl => string.Empty;

    /// <inheritdoc />
    protected override string SeriesPathPrefix => "/serials/";

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

    // ── Domain resolution ────────────────────────────────────────────

    /// <summary>
    /// Resolves the current MegaKino domain from the public tracker, caching it.
    /// </summary>
    private async Task<string> GetDomainAsync(CancellationToken cancellationToken)
    {
        var cached = _domain;
        if (cached != null)
        {
            return cached;
        }

        await _domainLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_domain != null)
            {
                return _domain;
            }

            var response = await HttpClient.GetAsync(DomainSource, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim().ToLowerInvariant();
            text = Regex.Replace(text, @"^https?://", string.Empty).Split('/', 1)[0];

            if (!Regex.IsMatch(text, @"^(?:[a-z0-9-]+\.)+[a-z]{2,}$"))
            {
                throw new InvalidOperationException($"MegaKino domain tracker returned an invalid domain: {text}");
            }

            _domain = text;
            Logger.LogInformation("Resolved MegaKino domain: {Domain}", _domain);
            return _domain;
        }
        finally
        {
            _domainLock.Release();
        }
    }

    // ── Fetching with token cookie ───────────────────────────────────

    /// <summary>
    /// Fetches a MegaKino page, priming the anti-bot token cookie first and
    /// retrying once when the token interstitial is served.
    /// </summary>
    private async Task<string> FetchMegaKinoAsync(string path, CancellationToken cancellationToken)
    {
        var domain = await GetDomainAsync(cancellationToken).ConfigureAwait(false);
        var baseUrl = $"https://{domain}";

        await EnsureTokenAsync(baseUrl, cancellationToken).ConfigureAwait(false);

        var page = await FetchPageAsync($"{baseUrl}{path}", cancellationToken).ConfigureAwait(false);
        if (page.Contains("yg=token", StringComparison.Ordinal) || page.Contains("location.replace", StringComparison.Ordinal))
        {
            Logger.LogDebug("MegaKino token interstitial on {Path}; retrying", path);
            await EnsureTokenAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            page = await FetchPageAsync($"{baseUrl}{path}", cancellationToken).ConfigureAwait(false);
        }

        return page;
    }

    /// <summary>
    /// Requests the one-time token so the cookie jar holds the session cookie.
    /// </summary>
    private async Task EnsureTokenAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            await HttpClient.GetAsync($"{baseUrl}/index.php?yg=token", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "MegaKino token request failed (continuing)");
        }
    }

    // ── Search ───────────────────────────────────────────────────────

    /// <summary>
    /// Searches MegaKino via its DataLife search endpoint.
    /// </summary>
    public override async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        Logger.LogDebug("megakino search: {Keyword}", keyword);

        var path = $"/index.php?do=search&subaction=search&search_start=0&full_search=0&result_from=1&story={Uri.EscapeDataString(keyword)}";
        var html = await FetchMegaKinoAsync(path, cancellationToken).ConfigureAwait(false);
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

    // ── Series (movie/serial) info ───────────────────────────────────

    /// <summary>
    /// Gets info for a MegaKino title. Movies expose a single Season 0; serials
    /// expose a single season whose URL is the serial page itself (all episodes
    /// are listed on that one page).
    /// </summary>
    public override async Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default)
    {
        var (html, isSerial) = await FetchTitleHtmlAsync(seriesUrl, cancellationToken).ConfigureAwait(false);

        var title = DecodeHtml(TitlePattern.Match(html).Groups["title"].Value.Trim());
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException($"Could not find title on {seriesUrl}");
        }

        var poster = PosterPattern.Match(html).Groups["src"].Value;
        if (!string.IsNullOrEmpty(poster) && !poster.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var domain = await GetDomainAsync(cancellationToken).ConfigureAwait(false);
            poster = $"https://{domain}{poster}";
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
                new() { Url = seriesUrl, Number = isSerial ? 1 : 0 },
            },
            HasMovies = false,
        };
    }

    // ── Episodes ─────────────────────────────────────────────────────

    /// <summary>
    /// Lists episodes: for a serial, the options of the se-select picker (each
    /// episode addressed by a #mkep=N marker on the serial URL); for a movie, a
    /// single movie entry.
    /// </summary>
    public override async Task<List<EpisodeRef>> GetEpisodesAsync(string seasonUrl, CancellationToken cancellationToken = default)
    {
        var (html, isSerial) = await FetchTitleHtmlAsync(seasonUrl, cancellationToken).ConfigureAwait(false);
        var cleanUrl = seasonUrl.Split('#', 2)[0];

        if (!isSerial)
        {
            return new List<EpisodeRef>
            {
                new() { Url = cleanUrl, Number = 1, IsMovie = true },
            };
        }

        var episodes = new List<EpisodeRef>();
        var picker = EpisodePickerPattern.Match(html);
        if (picker.Success)
        {
            var number = 0;
            foreach (Match option in EpisodeOptionPattern.Matches(picker.Groups["opts"].Value))
            {
                number++;
                episodes.Add(new EpisodeRef
                {
                    Url = $"{cleanUrl}#mkep={number}",
                    Number = number,
                    IsMovie = false,
                });
            }
        }

        return episodes;
    }

    // ── Episode details / providers ──────────────────────────────────

    /// <summary>
    /// Parses the providers for one episode. Serials: the mr-select block of
    /// the selected episode (#mkep=N). Movies: the tabs-block iframes. All
    /// content is German (language key "1").
    /// </summary>
    public override async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var cleanUrl = episodeUrl.Split('#', 2)[0];
        var html = await FetchTitlePageAsync(cleanUrl, cancellationToken).ConfigureAwait(false);

        var title = DecodeHtml(TitlePattern.Match(html).Groups["title"].Value.Trim());
        var details = new EpisodeDetails
        {
            Url = episodeUrl,
            TitleDe = title,
            TitleEn = title,
        };

        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var isSerial = EpisodePickerPattern.IsMatch(html);

        if (isSerial)
        {
            var mkep = Regex.Match(episodeUrl, @"#mkep=(\d+)");
            var index = mkep.Success ? int.Parse(mkep.Groups[1].Value) : 1;
            var episodeId = $"ep{index}";

            var block = Regex.Match(html, $@"<select\b(?=[^>]*id=[""']{episodeId}[""'])(?=[^>]*mr-select)[^>]*>(?<opts>.*?)</select>",
                RegexOptions.Singleline);
            if (block.Success)
            {
                foreach (Match option in ProviderOptionPattern.Matches(block.Groups["opts"].Value))
                {
                    var provider = MapProvider(option.Groups["url"].Value);
                    if (provider != null && !providers.ContainsKey(provider))
                    {
                        providers[provider] = option.Groups["url"].Value;
                    }
                }
            }
        }
        else
        {
            foreach (Match block in ContentBlockPattern.Matches(html))
            {
                var iframe = IframeUrlPattern.Match(block.Groups["block"].Value);
                if (!iframe.Success)
                {
                    continue;
                }

                var provider = MapProvider(iframe.Groups["url"].Value);
                if (provider != null && !providers.ContainsKey(provider))
                {
                    providers[provider] = iframe.Groups["url"].Value;
                }
            }
        }

        if (providers.Count > 0)
        {
            details.ProvidersByLanguage["1"] = providers;
        }

        return details;
    }

    // ── Popular / new ────────────────────────────────────────────────

    /// <summary>
    /// Gets the front page cards.
    /// </summary>
    public override async Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default)
    {
        var html = await FetchMegaKinoAsync("/", cancellationToken).ConfigureAwait(false);
        return ParseCards(html).Take(30).ToList();
    }

    /// <summary>
    /// MegaKino has no separate "new" listing; reuse the front page.
    /// </summary>
    public override async Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default)
    {
        return await GetPopularAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── Redirect resolution ──────────────────────────────────────────

    /// <summary>
    /// The stored provider value is already the provider embed URL.
    /// </summary>
    public override Task<string> ResolveRedirectAsync(string redirectUrl, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(redirectUrl);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the HTML for a title URL (stripping any #mkep marker).
    /// </summary>
    private async Task<string> FetchTitlePageAsync(string url, CancellationToken cancellationToken)
    {
        var clean = url.Split('#', 2)[0];
        if (Uri.TryCreate(clean, UriKind.Absolute, out var uri))
        {
            return await FetchMegaKinoAsync(uri.AbsolutePath, cancellationToken).ConfigureAwait(false);
        }

        return await FetchMegaKinoAsync(clean, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches the title page and reports whether it is a serial.
    /// </summary>
    private async Task<(string Html, bool IsSerial)> FetchTitleHtmlAsync(string url, CancellationToken cancellationToken)
    {
        var html = await FetchTitlePageAsync(url, cancellationToken).ConfigureAwait(false);
        return (html, EpisodePickerPattern.IsMatch(html));
    }

    /// <summary>
    /// Parses poster cards (title, URL, poster) from a page.
    /// </summary>
    private List<BrowseItem> ParseCards(string html)
    {
        var domain = _domain;
        var baseFor = domain != null ? $"https://{domain}" : string.Empty;

        var items = new List<BrowseItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in CardAnchorPattern.Matches(html))
        {
            var href = match.Groups["href"].Value;
            if (baseFor.Length == 0)
            {
                continue;
            }

            var url = $"{baseFor}{href}";
            if (!seen.Add(url))
            {
                continue;
            }

            var card = match.Groups["card"].Value;

            var titleMatch = CardTitlePattern.Match(card);
            if (!titleMatch.Success)
            {
                titleMatch = CardAltPattern.Match(card);
            }

            if (!titleMatch.Success)
            {
                continue;
            }

            var title = DecodeHtml(titleMatch.Groups["title"].Value.Trim());
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var imageMatch = CardImagePattern.Match(card);
            var cover = imageMatch.Success ? imageMatch.Groups["src"].Value : string.Empty;
            if (!string.IsNullOrEmpty(cover) && !cover.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                cover = $"{baseFor}{cover}";
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
    /// Maps a hoster embed URL to the canonical extractor name, or null when the
    /// plugin has no extractor for it.
    /// </summary>
    internal static string? MapProvider(string embedUrl)
    {
        if (!Uri.TryCreate(embedUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.EndsWith("voe.sx", StringComparison.OrdinalIgnoreCase) || host.Contains("voe"))
        {
            return "VOE";
        }

        if (host.EndsWith("gxplayer.xyz", StringComparison.OrdinalIgnoreCase))
        {
            return "MegaKino";
        }

        return null;
    }
}
