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
/// Service for the moflix-stream.xyz website. The site is a JS app backed by a
/// JSON API (api/v1) that requires a session cookie plus a CSRF token scraped
/// from the HTML page. Movies and series both live under /titles/{id}; series
/// add /season/{s} and /episodes/{e}. Only hosters the plugin can extract
/// (MoflixClick, Filemoon/Byse, VOE, Vidmoly, Vidoza) are exposed.
/// </summary>
public class MoflixService : StreamingSiteService
{
    private const string SiteBase = "https://moflix-stream.xyz";

    private static readonly Regex CsrfPattern = new(
        @"""csrf_token""\s*:\s*""(?<token>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex TitleIdPattern = new(
        @"/titles/(?<id>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex SeasonPattern = new(
        @"/season/(?<s>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex EpisodePattern = new(
        @"/episodes/(?<e>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex UnusedPattern = new(@"\x00", RegexOptions.Compiled);

    /// <summary>
    /// The site does not strictly enforce the CSRF token on read endpoints, so
    /// a missing token only means we retry scraping it. Scrape at most once per
    /// interval to keep the token fresh.
    /// </summary>
    private static readonly TimeSpan CsrfRefreshInterval = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _csrfLock = new(1, 1);
    private string? _csrfToken;
    private DateTime _csrfFetchedAtUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="MoflixService"/> class.
    /// </summary>
    public MoflixService(IHttpClientFactory httpClientFactory, ILogger<MoflixService> logger)
        : base(httpClientFactory.CreateClient("Moflix"), logger)
    {
    }

    /// <inheritdoc />
    public override string SourceName => "moflix";

    /// <inheritdoc />
    protected override string BaseUrl => SiteBase;

    /// <inheritdoc />
    protected override string SearchUrl => $"{SiteBase}/api/v1/search";

    /// <inheritdoc />
    protected override string SeriesPathPrefix => "/titles/";

    /// <inheritdoc />
    protected override string PopularPath => string.Empty;

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

    // ── CSRF + API helpers ───────────────────────────────────────────

    /// <summary>
    /// Scrapes the CSRF token from the site's HTML and caches it. The token is
    /// best-effort (the API tolerates its absence), so this never throws.
    /// </summary>
    private async Task<string> EnsureCsrfAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_csrfToken) &&
            DateTime.UtcNow - _csrfFetchedAtUtc < CsrfRefreshInterval)
        {
            return _csrfToken;
        }

        await _csrfLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_csrfToken) &&
                DateTime.UtcNow - _csrfFetchedAtUtc < CsrfRefreshInterval)
            {
                return _csrfToken;
            }

            try
            {
                var html = await FetchPageAsync(SiteBase, cancellationToken).ConfigureAwait(false);
                var match = CsrfPattern.Match(html);
                if (match.Success)
                {
                    _csrfToken = match.Groups["token"].Value;
                    _csrfFetchedAtUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not scrape the moflix CSRF token; continuing without it");
            }

            return _csrfToken ?? string.Empty;
        }
        finally
        {
            _csrfLock.Release();
        }
    }

    /// <summary>
    /// Performs a JSON API GET with the CSRF token and XHR headers.
    /// </summary>
    private async Task<JsonElement> ApiGetAsync(string path, CancellationToken cancellationToken)
    {
        var csrf = await EnsureCsrfAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{SiteBase}{path}");
        if (!string.IsNullOrEmpty(csrf))
        {
            request.Headers.Add("X-XSRF-TOKEN", csrf);
        }

        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("Referer", SiteBase + "/");
        request.Headers.Accept.ParseAdd("application/json");

        var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    // ── Search ───────────────────────────────────────────────────────

    /// <summary>
    /// Searches moflix via /api/v1/search/{keyword}, keeping title results.
    /// </summary>
    public override async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        Logger.LogDebug("moflix search: {Keyword}", keyword);

        var root = await ApiGetAsync($"/api/v1/search/{Uri.EscapeDataString(keyword)}", cancellationToken).ConfigureAwait(false);
        var results = new List<SearchResult>();

        if (root.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var modelType = item.TryGetProperty("model_type", out var mt) ? mt.GetString() : null;
                if (!string.Equals(modelType, "title", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!item.TryGetProperty("id", out var idEl) ||
                    (idEl.ValueKind != JsonValueKind.Number && idEl.ValueKind != JsonValueKind.String))
                {
                    continue;
                }

                var id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : int.Parse(idEl.GetString()!);
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var poster = item.TryGetProperty("poster", out var p) ? p.GetString() ?? string.Empty : string.Empty;

                results.Add(new SearchResult
                {
                    Title = name,
                    Url = $"{SiteBase}/titles/{id}",
                    Description = string.Empty,
                    Source = SourceName,
                });

                if (results.Count >= 30)
                {
                    break;
                }
            }
        }

        return results;
    }

    // ── Series (movie/series) info ───────────────────────────────────

    /// <summary>
    /// Gets info for a moflix title via /api/v1/titles/{id}. Movies expose a
    /// single Season 0; series expose one season per entry in the seasons list.
    /// </summary>
    public override async Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default)
    {
        var root = await ApiGetAsync(TitlePath(seriesUrl), cancellationToken).ConfigureAwait(false);
        var title = root.GetProperty("title");

        var name = title.TryGetProperty("name", out var nm) ? nm.GetString() ?? "Unknown" : "Unknown";
        var isSeries = title.TryGetProperty("is_series", out var isSer) && isSer.ValueKind == JsonValueKind.True;

        var poster = title.TryGetProperty("poster", out var p) ? p.GetString() ?? string.Empty : string.Empty;
        if (!string.IsNullOrEmpty(poster) && !Uri.IsWellFormedUriString(poster, UriKind.Absolute))
        {
            poster = new Uri(new Uri(SiteBase), poster).ToString();
        }

        var description = title.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;

        var genres = new List<string>();
        if (title.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in g.EnumerateArray())
            {
                string? genre;
                if (item.ValueKind == JsonValueKind.String)
                {
                    genre = item.GetString();
                }
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var nameEl))
                {
                    // The API returns genres as objects like {"id":1,"name":"Drama"}.
                    genre = nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString() : nameEl.ToString();
                }
                else
                {
                    genre = item.ToString();
                }

                if (!string.IsNullOrWhiteSpace(genre) && !genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                {
                    genres.Add(genre!);
                }

                if (genres.Count >= 5)
                {
                    break;
                }
            }
        }

        var seasons = new List<SeasonRef>();
        if (isSeries && root.TryGetProperty("seasons", out var seasonsEl) &&
            seasonsEl.ValueKind == JsonValueKind.Object &&
            seasonsEl.TryGetProperty("data", out var seasonArr) && seasonArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in seasonArr.EnumerateArray())
            {
                if (!s.TryGetProperty("number", out var numEl) || numEl.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var number = numEl.GetInt32();
                seasons.Add(new SeasonRef
                {
                    Url = $"{SiteBase}/titles/{ExtractTitleId(seriesUrl)}/season/{number}",
                    Number = number,
                });
            }
        }

        if (seasons.Count == 0)
        {
            seasons.Add(new SeasonRef { Url = seriesUrl, Number = 0 });
        }

        return new SeriesInfo
        {
            Title = name,
            Url = seriesUrl,
            CoverImageUrl = poster,
            Description = description,
            Genres = genres,
            Seasons = seasons.OrderBy(s => s.Number).ToList(),
            HasMovies = false,
        };
    }

    // ── Episodes ─────────────────────────────────────────────────────

    /// <summary>
    /// Lists episodes. A movie (no /season/) is a single movie entry; a season
    /// uses /api/v1/titles/{id}/seasons/{s} for its episode list.
    /// </summary>
    public override async Task<List<EpisodeRef>> GetEpisodesAsync(string seasonUrl, CancellationToken cancellationToken = default)
    {
        var titleId = ExtractTitleId(seasonUrl);

        var seasonMatch = SeasonPattern.Match(seasonUrl);
        if (!seasonMatch.Success)
        {
            return new List<EpisodeRef>
            {
                new() { Url = seasonUrl, Number = 1, IsMovie = true },
            };
        }

        var seasonNumber = int.Parse(seasonMatch.Groups["s"].Value);
        var root = await ApiGetAsync($"/api/v1/titles/{titleId}/seasons/{seasonNumber}", cancellationToken).ConfigureAwait(false);

        var episodes = new List<EpisodeRef>();
        if (root.TryGetProperty("episodes", out var epsEl) &&
            epsEl.ValueKind == JsonValueKind.Object &&
            epsEl.TryGetProperty("data", out var epArr) && epArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in epArr.EnumerateArray())
            {
                if (!e.TryGetProperty("episode_number", out var enEl) || enEl.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var epNumber = enEl.GetInt32();
                episodes.Add(new EpisodeRef
                {
                    Url = $"{SiteBase}/titles/{titleId}/season/{seasonNumber}/episodes/{epNumber}",
                    Number = epNumber,
                    IsMovie = false,
                });
            }
        }

        return episodes;
    }

    // ── Episode details / providers ──────────────────────────────────

    /// <summary>
    /// Parses the provider links for one episode. Movies read title.videos;
    /// series read /api/v1/titles/{id}/seasons/{s}/episodes/{e}. Videos are
    /// grouped by their language (de→"1", en→"2").
    /// </summary>
    public override async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var titleId = ExtractTitleId(episodeUrl);
        var seasonMatch = SeasonPattern.Match(episodeUrl);
        var episodeMatch = EpisodePattern.Match(episodeUrl);

        JsonElement videoOwner;
        string? title;

        if (seasonMatch.Success && episodeMatch.Success)
        {
            var s = int.Parse(seasonMatch.Groups["s"].Value);
            var e = int.Parse(episodeMatch.Groups["e"].Value);
            var root = await ApiGetAsync($"/api/v1/titles/{titleId}/seasons/{s}/episodes/{e}", cancellationToken).ConfigureAwait(false);
            videoOwner = root.GetProperty("episode");
            title = videoOwner.TryGetProperty("name", out var n) ? n.GetString() : null;
        }
        else
        {
            var root = await ApiGetAsync($"/api/v1/titles/{titleId}", cancellationToken).ConfigureAwait(false);
            videoOwner = root.GetProperty("title");
            title = videoOwner.TryGetProperty("name", out var n) ? n.GetString() : null;
        }

        var details = new EpisodeDetails
        {
            Url = episodeUrl,
            TitleDe = title,
            TitleEn = title,
        };

        if (!videoOwner.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array)
        {
            return details;
        }

        foreach (var video in videos.EnumerateArray())
        {
            if (video.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var src = video.TryGetProperty("src", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(src) || !Uri.TryCreate(src, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            var provider = MapProvider(src);
            if (provider == null)
            {
                continue;
            }

            var lang = video.TryGetProperty("language", out var l) ? l.GetString() : null;
            var langKey = MapLanguage(lang) ?? "1";

            if (!details.ProvidersByLanguage.TryGetValue(langKey, out var providers))
            {
                providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                details.ProvidersByLanguage[langKey] = providers;
            }

            if (!providers.ContainsKey(provider))
            {
                providers[provider] = src;
            }
        }

        return details;
    }

    // ── Popular / new ────────────────────────────────────────────────

    /// <summary>
    /// Gets the most recently added titles.
    /// </summary>
    public override async Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default)
    {
        var root = await ApiGetAsync("/api/v1/titles?perPage=30&orderBy=popularity&orderDir=desc", cancellationToken).ConfigureAwait(false);
        return ParseTitleItems(root);
    }

    /// <summary>
    /// Gets the newest titles.
    /// </summary>
    public override async Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default)
    {
        var root = await ApiGetAsync("/api/v1/titles?perPage=30&orderBy=createdAt&orderDir=desc", cancellationToken).ConfigureAwait(false);
        return ParseTitleItems(root);
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
    /// Builds the /api/v1/titles/{id} path for a title URL.
    /// </summary>
    private static string TitlePath(string titleUrl)
    {
        return $"/api/v1/titles/{ExtractTitleId(titleUrl)}";
    }

    /// <summary>
    /// Extracts the numeric title id from a moflix URL.
    /// </summary>
    private static string ExtractTitleId(string url)
    {
        var match = TitleIdPattern.Match(url);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Could not find a moflix title id in {url}");
        }

        return match.Groups["id"].Value;
    }

    /// <summary>
    /// Parses the pagination.data list of a /api/v1/titles response into browse items.
    /// </summary>
    private List<BrowseItem> ParseTitleItems(JsonElement root)
    {
        var items = new List<BrowseItem>();

        if (root.TryGetProperty("pagination", out var pag) &&
            pag.ValueKind == JsonValueKind.Object &&
            pag.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in data.EnumerateArray())
            {
                if (!t.TryGetProperty("id", out var idEl))
                {
                    continue;
                }

                var id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : int.Parse(idEl.GetString()!);
                var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var poster = t.TryGetProperty("poster", out var p) ? p.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrEmpty(poster) && !Uri.IsWellFormedUriString(poster, UriKind.Absolute))
                {
                    poster = new Uri(new Uri(SiteBase), poster).ToString();
                }

                items.Add(new BrowseItem
                {
                    Title = name,
                    Url = $"{SiteBase}/titles/{id}",
                    CoverImageUrl = poster,
                    Genre = string.Empty,
                    Source = SourceName,
                });
            }
        }

        return items;
    }

    /// <summary>
    /// Maps a provider embed URL to the canonical extractor name, or null when
    /// the plugin has no extractor for it.
    /// </summary>
    internal static string? MapProvider(string embedUrl)
    {
        if (!Uri.TryCreate(embedUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();

        if (host.EndsWith("moflix-stream.click", StringComparison.OrdinalIgnoreCase))
        {
            return "MoflixClick";
        }

        if (host.Contains("voe"))
        {
            return "VOE";
        }

        if (host.Contains("filemoon") || host.Contains("byse") || host == "moflix-stream.link")
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

    /// <summary>
    /// Maps a moflix video language to the plugin language key
    /// ("1" = German Dub, "2" = English), defaulting to "1".
    /// </summary>
    internal static string? MapLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var text = language.Trim().ToLowerInvariant();
        if (text.Contains("en") || text.Contains("english"))
        {
            return "2";
        }

        if (text.Contains("de") || text.Contains("german") || text.Contains("deutsch"))
        {
            return "1";
        }

        return null;
    }
}
