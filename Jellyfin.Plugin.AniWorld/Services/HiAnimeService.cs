using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Standalone service for hianime.to. Not based on StreamingSiteService because
/// HiAnime uses a completely different site structure, API, and extraction pipeline.
/// </summary>
public class HiAnimeService
{
    private const string BaseUrl = "https://hianime.to";
    private const string AjaxUrl = $"{BaseUrl}/ajax/v2";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:122.0) Gecko/20100101 Firefox/122.0";

    private static readonly Regex ShowIdFromSlug = new(@"-(\d+)$", RegexOptions.Compiled);
    private static readonly Regex EpParamFromUrl = new(@"[?&]ep=(?<episode>\d+)", RegexOptions.Compiled);
    private static readonly Regex SlugFromUrl = new(@"/watch/(?<slug>[^?#]+)", RegexOptions.Compiled);
    private static readonly Regex EmbedSourceId = new(@"/([^/?]+)\?", RegexOptions.Compiled);
    private static readonly Regex EmbedBaseUrl = new(@"^(https?://[^/]+(?:/[^/]+){3})", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<HiAnimeService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HiAnimeService"/> class.
    /// </summary>
    public HiAnimeService(IHttpClientFactory httpClientFactory, ILogger<HiAnimeService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("HiAnime");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _httpClient.DefaultRequestHeaders.Referrer = new Uri(BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _logger = logger;
    }

    /// <summary>Gets the source identifier.</summary>
    public string SourceName => "hianime";

    // ── Search ──────────────────────────────────────────────────────

    /// <summary>
    /// Searches hianime.to for anime.
    /// </summary>
    public async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        var query = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(query))
        {
            return new List<SearchResult>();
        }

        var url = $"{BaseUrl}/search?keyword={Uri.EscapeDataString(query)}";
        var html = await FetchPageAsync(url, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(html))
        {
            return new List<SearchResult>();
        }

        var results = new List<SearchResult>();

        // Split by .flw-item boundaries (mirrors POC's soup.select(".flw-item"))
        var blocks = html.Split(new[] { "class=\"flw-item\"" }, StringSplitOptions.None);

        var titlePattern = new Regex(
            @"<a[^>]*href=""(?<href>[^""]+)""[^>]*class=""dynamic-name""[^>]*>(?<title>[^<]+)",
            RegexOptions.Singleline);

        var typePattern = new Regex(
            @"<span[^>]*class=""fdi-item""[^>]*>(?<type>[^<]+)",
            RegexOptions.Singleline);

        // Skip first segment (before the first flw-item)
        for (int i = 1; i < blocks.Length; i++)
        {
            var block = blocks[i];
            var titleMatch = titlePattern.Match(block);
            if (!titleMatch.Success)
            {
                continue;
            }

            var title = WebUtility.HtmlDecode(titleMatch.Groups["title"].Value.Trim());
            var href = titleMatch.Groups["href"].Value;
            var animeId = href.Split('/').LastOrDefault()?.Split('?').FirstOrDefault();

            if (string.IsNullOrEmpty(animeId) || string.IsNullOrEmpty(title))
            {
                continue;
            }

            var typeMatch = typePattern.Match(block);
            var animeType = typeMatch.Success ? typeMatch.Groups["type"].Value.Trim() : "TV";

            results.Add(new SearchResult
            {
                Title = title,
                Url = $"{BaseUrl}/watch/{animeId}",
                Description = animeType,
                Source = "hianime",
            });
        }

        return results;
    }

    // ── Series Info ─────────────────────────────────────────────────

    /// <summary>
    /// Gets series information from a hianime URL.
    /// </summary>
    public async Task<SeriesInfo> GetSeriesInfoAsync(string animeUrl, CancellationToken cancellationToken = default)
    {
        // Extract slug from /watch/{slug} URL
        var slugMatch = SlugFromUrl.Match(animeUrl);
        var slug = slugMatch.Success ? slugMatch.Groups["slug"].Value : string.Empty;

        // Fetch the anime page
        var html = await FetchPageAsync(animeUrl, cancellationToken).ConfigureAwait(false);

        // Parse title
        var titlePattern = new Regex(@"<h2[^>]*class=""film-name[^""]*""[^>]*>.*?dynamic-name[^>]*>(?<title>[^<]+)", RegexOptions.Singleline);
        var titleMatch = titlePattern.Match(html);
        var title = titleMatch.Success ? WebUtility.HtmlDecode(titleMatch.Groups["title"].Value.Trim()) : slug;

        // Parse cover image (src may appear before or after class)
        var coverPattern = new Regex(@"<img[^>]*class=""[^""]*film-poster-img[^""]*""[^>]*>", RegexOptions.Singleline);
        var coverMatch = coverPattern.Match(html);
        var coverUrl = string.Empty;
        if (coverMatch.Success)
        {
            var srcMatch = Regex.Match(coverMatch.Value, @"(?:src|data-src)=""(?<src>[^""]+)""");
            if (srcMatch.Success)
            {
                coverUrl = srcMatch.Groups["src"].Value;
            }
        }

        // Parse description
        var descPattern = new Regex(@"<div[^>]*class=""film-description[^""]*""[^>]*>.*?<div[^>]*class=""text""[^>]*>(?<desc>.*?)</div>", RegexOptions.Singleline);
        var descMatch = descPattern.Match(html);
        var description = descMatch.Success
            ? WebUtility.HtmlDecode(Regex.Replace(descMatch.Groups["desc"].Value.Trim(), "<[^>]+>", string.Empty).Trim())
            : string.Empty;

        // Parse genres
        var genres = new List<string>();
        var genrePattern = new Regex(@"<a[^>]*href=""/genre/[^""]+""[^>]*>(?<genre>[^<]+)</a>", RegexOptions.Compiled);
        foreach (Match gm in genrePattern.Matches(html))
        {
            var g = WebUtility.HtmlDecode(gm.Groups["genre"].Value.Trim());
            if (!string.IsNullOrEmpty(g) && !genres.Contains(g))
            {
                genres.Add(g);
            }
        }

        return new SeriesInfo
        {
            Title = title,
            Url = animeUrl,
            CoverImageUrl = coverUrl,
            Description = description,
            Genres = genres,
            Seasons = new List<SeasonRef> { new() { Url = animeUrl, Number = 1 } },
            HasMovies = false,
        };
    }

    // ── Episodes ────────────────────────────────────────────────────

    /// <summary>
    /// Gets episodes for a hianime anime.
    /// </summary>
    public async Task<List<EpisodeRef>> GetEpisodesAsync(string animeUrl, CancellationToken cancellationToken = default)
    {
        var slugMatch = SlugFromUrl.Match(animeUrl);
        if (!slugMatch.Success)
        {
            return new List<EpisodeRef>();
        }

        var slug = slugMatch.Groups["slug"].Value;
        var showIdMatch = ShowIdFromSlug.Match(slug);
        if (!showIdMatch.Success)
        {
            return new List<EpisodeRef>();
        }

        var showId = showIdMatch.Groups[1].Value;
        var json = await FetchJsonAsync($"{AjaxUrl}/episode/list/{showId}", cancellationToken).ConfigureAwait(false);
        if (json == null || !json.Value.TryGetProperty("html", out var htmlProp))
        {
            return new List<EpisodeRef>();
        }

        var html = htmlProp.GetString() ?? string.Empty;
        var episodes = new List<EpisodeRef>();

        // Match any <a> tag containing ep-item class (attribute order varies)
        var epTagPattern = new Regex(
            @"<a[^>]*class=""[^""]*ep-item[^""]*""[^>]*>",
            RegexOptions.Singleline);

        var hrefAttr = new Regex(@"href=""(?<href>[^""]+)""");
        var titleAttr = new Regex(@"title=""(?<title>[^""]*?)""");

        int epNum = 0;
        foreach (Match ep in epTagPattern.Matches(html))
        {
            epNum++;
            var tag = ep.Value;

            var hrefMatch = hrefAttr.Match(tag);
            if (!hrefMatch.Success)
            {
                continue;
            }

            var href = hrefMatch.Groups["href"].Value;

            episodes.Add(new EpisodeRef
            {
                Url = $"{BaseUrl}{href}",
                Number = epNum,
                IsMovie = false,
            });
        }

        return episodes;
    }

    // ── Episode Details ─────────────────────────────────────────────

    /// <summary>
    /// Gets episode details for a hianime episode URL.
    /// Returns provider info structured for the existing UI pattern.
    /// </summary>
    public async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var slugMatch = SlugFromUrl.Match(episodeUrl);
        var slug = slugMatch.Success ? slugMatch.Groups["slug"].Value : string.Empty;
        var epMatch = EpParamFromUrl.Match(episodeUrl);
        var epNum = epMatch.Success ? int.Parse(epMatch.Groups["episode"].Value) : 0;

        string? title = null;

        // Try to get episode title from the episode list
        if (!string.IsNullOrEmpty(slug))
        {
            try
            {
                var showIdMatch = ShowIdFromSlug.Match(slug);
                if (showIdMatch.Success)
                {
                    var showId = showIdMatch.Groups[1].Value;
                    var json = await FetchJsonAsync($"{AjaxUrl}/episode/list/{showId}", cancellationToken).ConfigureAwait(false);
                    if (json != null && json.Value.TryGetProperty("html", out var htmlProp))
                    {
                        var html = htmlProp.GetString() ?? string.Empty;
                        // Find the matching episode tag by ep number in href
                        var epTagPat = new Regex(
                            @"<a[^>]*class=""[^""]*ep-item[^""]*""[^>]*>",
                            RegexOptions.Singleline);
                        foreach (Match etm in epTagPat.Matches(html))
                        {
                            var tag = etm.Value;
                            if (!Regex.IsMatch(tag, @"href=""[^""]*[?&]ep=" + epNum + @""""))
                            {
                                continue;
                            }

                            var tm = Regex.Match(tag, @"title=""(?<title>[^""]*?)""");
                            if (tm.Success)
                            {
                                title = WebUtility.HtmlDecode(tm.Groups["title"].Value.Trim());
                            }

                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch episode title for {Url}", episodeUrl);
            }
        }

        // Return providers structured for UI: "sub" and "dub" language groups, each with "Auto"
        var providers = new Dictionary<string, Dictionary<string, string>>
        {
            ["sub"] = new Dictionary<string, string> { ["Auto"] = episodeUrl },
            ["dub"] = new Dictionary<string, string> { ["Auto"] = episodeUrl },
        };

        return new EpisodeDetails
        {
            Url = episodeUrl,
            TitleEn = title,
            TitleDe = null,
            ProvidersByLanguage = providers,
        };
    }

    // ── Stream Extraction (MegaCloud Pipeline) ─────────────────────

    /// <summary>
    /// Full MegaCloud extraction pipeline. Tries HD-2 first, then HD-3.
    /// Returns null if all servers fail.
    /// </summary>
    public async Task<HiAnimeStreamResult?> GetStreamAsync(string episodeUrl, string language, CancellationToken cancellationToken)
    {
        var epMatch = EpParamFromUrl.Match(episodeUrl);
        if (!epMatch.Success)
        {
            _logger.LogWarning("Cannot extract ep number from URL: {Url}", episodeUrl);
            return null;
        }

        var epNum = epMatch.Groups["episode"].Value;

        // Step 1: Get server list
        var serversJson = await FetchJsonAsync($"{AjaxUrl}/episode/servers?episodeId={epNum}", cancellationToken).ConfigureAwait(false);
        if (serversJson == null || !serversJson.Value.TryGetProperty("html", out var serversHtml))
        {
            _logger.LogWarning("Failed to fetch servers for episode {EpNum}", epNum);
            return null;
        }

        var html = serversHtml.GetString() ?? string.Empty;
        var servers = ParseServers(html, language);

        if (servers.Count == 0)
        {
            _logger.LogWarning("No servers found for episode {EpNum} with language {Lang}", epNum, language);
            return null;
        }

        // Step 2: Try each server (HD-2 first, then HD-3)
        foreach (var server in servers)
        {
            _logger.LogDebug("Trying server {Name} (id={Id}) for ep {EpNum}", server.Name, server.DataId, epNum);

            try
            {
                var result = await ExtractStreamFromServerAsync(server, cancellationToken).ConfigureAwait(false);
                if (result != null)
                {
                    _logger.LogInformation("Successfully extracted stream from server {Name} for ep {EpNum}", server.Name, epNum);
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Server {Name} failed for ep {EpNum}", server.Name, epNum);
            }
        }

        _logger.LogWarning("All servers failed for episode {EpNum}", epNum);
        return null;
    }

    /// <summary>
    /// Gets popular anime (empty for MVP).
    /// </summary>
    public Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<BrowseItem>());
    }

    /// <summary>
    /// Gets new releases (empty for MVP).
    /// </summary>
    public Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<BrowseItem>());
    }

    // ── Private: Server Parsing ─────────────────────────────────────

    private List<HiAnimeServer> ParseServers(string html, string language)
    {
        var servers = new List<HiAnimeServer>();
        var lang = language.ToLowerInvariant();

        // Determine CSS class for the language section
        var cssClass = lang == "dub" ? ".servers-dub" : ".servers-sub";

        // Extract the server section for the requested language
        var sectionPattern = new Regex(
            Regex.Escape(cssClass.Replace(".", @"class=""[^""]*")) +
            @".*?(?=servers-(?:sub|dub)|$)",
            RegexOptions.Singleline);

        // Simpler approach: parse all server-item elements and check parent section
        var serverPattern = new Regex(
            @"<div[^>]*class=""[^""]*server-item[^""]*""[^>]*data-id=""(?<id>\d+)""[^>]*>.*?<a[^>]*>(?<name>[^<]+)",
            RegexOptions.Singleline);

        // Parse sub and dub sections separately
        var subSection = ExtractSection(html, "servers-sub");
        var dubSection = ExtractSection(html, "servers-dub");

        var targetSection = lang == "dub" ? dubSection : subSection;

        foreach (Match m in serverPattern.Matches(targetSection))
        {
            var name = m.Groups["name"].Value.Trim().ToLowerInvariant();
            var dataId = m.Groups["id"].Value;

            // Skip HD-1: its CDN always blocks via Cloudflare
            if (name == "hd-1")
            {
                continue;
            }

            servers.Add(new HiAnimeServer
            {
                DataId = int.Parse(dataId),
                Name = name,
                Type = lang,
            });
        }

        // Sort: HD-2 first, then others
        servers.Sort((a, b) =>
        {
            if (a.Name == "hd-2" && b.Name != "hd-2") return -1;
            if (b.Name == "hd-2" && a.Name != "hd-2") return 1;
            return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        return servers;
    }

    private static string ExtractSection(string html, string sectionClass)
    {
        var idx = html.IndexOf(sectionClass, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;

        // Find the containing div
        var start = html.LastIndexOf('<', idx);
        if (start < 0) start = idx;

        // Find the next major section or end
        var nextSection = html.IndexOf("servers-", idx + sectionClass.Length, StringComparison.OrdinalIgnoreCase);
        var end = nextSection > 0 ? nextSection : html.Length;

        return html[start..end];
    }

    // ── Private: Stream Extraction ──────────────────────────────────

    private async Task<HiAnimeStreamResult?> ExtractStreamFromServerAsync(HiAnimeServer server, CancellationToken cancellationToken)
    {
        // Step 1: Get MegaCloud embed link
        var sourcesJson = await FetchJsonAsync(
            $"{BaseUrl}/ajax/v2/episode/sources?id={server.DataId}",
            cancellationToken).ConfigureAwait(false);

        if (sourcesJson == null || !sourcesJson.Value.TryGetProperty("link", out var linkProp))
        {
            _logger.LogDebug("No embed link from server {Id}", server.DataId);
            return null;
        }

        var embedLink = linkProp.GetString();
        if (string.IsNullOrEmpty(embedLink))
        {
            return null;
        }

        // Step 2: Parse source_id and base_url from embed link
        var idMatch = EmbedSourceId.Match(embedLink);
        var baseMatch = EmbedBaseUrl.Match(embedLink);
        var sourceId = idMatch.Success ? idMatch.Groups[1].Value : null;
        var baseUrl = baseMatch.Success ? baseMatch.Groups[1].Value : null;

        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(baseUrl))
        {
            _logger.LogDebug("Could not parse embed link: {Link}", embedLink);
            return null;
        }

        // Step 3: Extract token from embed page (with retries for rate limiting)
        string? token = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            token = await ExtractTokenAsync($"{baseUrl}/{sourceId}?k=1&autoPlay=0&oa=0&asi=1", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
            {
                break;
            }

            if (attempt < 2)
            {
                _logger.LogDebug("Token extraction attempt {Attempt} failed, retrying in 500ms...", attempt + 1);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("Failed to extract token from MegaCloud after 3 attempts");
            return null;
        }

        // Step 4: Get stream sources using the token
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/getSources?id={sourceId}&_k={token}");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri($"{baseUrl}/{sourceId}");

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("getSources returned {Code}", response.StatusCode);
            return null;
        }

        var sourcesBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(sourcesBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("sources", out var sourcesEl))
        {
            return null;
        }

        // Check if sources is encrypted (string instead of array)
        if (sourcesEl.ValueKind == JsonValueKind.String)
        {
            _logger.LogWarning("MegaCloud sources are encrypted (AES). Decryption not implemented.");
            return null;
        }

        if (sourcesEl.ValueKind != JsonValueKind.Array || sourcesEl.GetArrayLength() == 0)
        {
            return null;
        }

        var firstSource = sourcesEl[0];
        if (!firstSource.TryGetProperty("file", out var fileProp))
        {
            return null;
        }

        var hlsUrl = fileProp.GetString();
        if (string.IsNullOrEmpty(hlsUrl))
        {
            return null;
        }

        // Extract subtitle URL
        string? subtitleUrl = null;
        if (root.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
        {
            subtitleUrl = GetSubtitleUrl(tracks);
        }

        return new HiAnimeStreamResult
        {
            Url = hlsUrl,
            SubtitleUrl = subtitleUrl,
            Headers = new Dictionary<string, string> { ["Referer"] = "https://megacloud.tv" },
        };
    }

    // ── Private: Token Extraction ───────────────────────────────────

    private async Task<string?> ExtractTokenAsync(string embedPageUrl, CancellationToken cancellationToken)
    {
        string html;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, embedPageUrl);
            request.Headers.Referrer = new Uri($"{BaseUrl}/");
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch embed page: {Url}", embedPageUrl);
            return null;
        }

        if (string.IsNullOrEmpty(html) || html.Length < 1200)
        {
            // Rate-limited "File not found" pages are ~1050 bytes
            _logger.LogDebug("Embed page too small ({Length} bytes), likely rate-limited", html?.Length ?? 0);
            return null;
        }

        // Location 1: <meta name="_gg_fb" content="TOKEN">
        var meta = Regex.Match(html, @"<meta\s+name=""_gg_fb""\s+content=""(?<token>[^""]+)""", RegexOptions.IgnoreCase);
        if (meta.Success && !string.IsNullOrEmpty(meta.Groups["token"].Value))
        {
            return meta.Groups["token"].Value;
        }

        // Location 2: [data-dpi] attribute
        var dataDpi = Regex.Match(html, @"data-dpi=""(?<token>[^""]+)""", RegexOptions.IgnoreCase);
        if (dataDpi.Success && !string.IsNullOrEmpty(dataDpi.Groups["token"].Value))
        {
            return dataDpi.Groups["token"].Value;
        }

        // Location 3: <script nonce="TOKEN"> where TOKEN length >= 10
        var nonceMatches = Regex.Matches(html, @"<script[^>]*\bnonce=""(?<nonce>[^""]+)""", RegexOptions.IgnoreCase);
        foreach (Match nm in nonceMatches)
        {
            var nonce = nm.Groups["nonce"].Value;
            if (!string.IsNullOrEmpty(nonce) && nonce.Length >= 10)
            {
                return nonce;
            }
        }

        // Location 4: JS variable patterns
        var jsPatterns = new[]
        {
            @"window\.\w+\s*=\s*[""'](?<token>[a-zA-Z0-9_-]{10,})[""']",
            @"data-k\s*=\s*[""'](?<token>[a-zA-Z0-9_-]{10,})[""']",
        };

        foreach (var pattern in jsPatterns)
        {
            var jsMatch = Regex.Match(html, pattern);
            if (jsMatch.Success)
            {
                return jsMatch.Groups["token"].Value;
            }
        }

        _logger.LogDebug("Could not find token in embed page ({Length} bytes)", html.Length);
        return null;
    }

    // ── Private: Subtitle Extraction ────────────────────────────────

    private static string? GetSubtitleUrl(JsonElement tracks)
    {
        string? firstSub = null;

        foreach (var track in tracks.EnumerateArray())
        {
            if (!track.TryGetProperty("kind", out var kind))
            {
                continue;
            }

            var kindStr = kind.GetString()?.ToLowerInvariant();
            if (kindStr != "captions" && kindStr != "subtitles")
            {
                continue;
            }

            var label = track.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? string.Empty : string.Empty;
            var file = track.TryGetProperty("file", out var fileProp) ? fileProp.GetString() : null;

            if (string.IsNullOrEmpty(file))
            {
                continue;
            }

            if (label.Contains("english", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }

            firstSub ??= file;
        }

        return firstSub;
    }

    // ── Private: HTTP Helpers ────────────────────────────────────────

    private async Task<string> FetchPageAsync(string url, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching page: {Url}", url);
        var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement?> FetchJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Fetching JSON: {Url}", url);
            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch JSON from {Url}", url);
            return null;
        }
    }

    // ── Internal Types ──────────────────────────────────────────────

    private class HiAnimeServer
    {
        public int DataId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "sub";
    }
}

/// <summary>
/// Result from HiAnime stream extraction.
/// </summary>
public class HiAnimeStreamResult
{
    /// <summary>Gets or sets the HLS .m3u8 URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional subtitle track URL.</summary>
    public string? SubtitleUrl { get; set; }

    /// <summary>Gets or sets the required HTTP headers for downloading (Referer).</summary>
    public Dictionary<string, string> Headers { get; set; } = new() { ["Referer"] = "https://megacloud.tv" };
}
