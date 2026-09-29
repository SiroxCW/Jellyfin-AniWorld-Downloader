using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Extractors;

/// <summary>
/// Extracts the direct HLS playlist from the XFileSharing player used by
/// moflix-stream.click. The embed page contains a Dean Edwards packed-JS block
/// whose <c>links</c> object holds hls4/hls3/hls2 playlists; the first one that
/// actually returns an HLS playlist is returned. Port of the Python
/// <c>get_direct_link_from_moflixclick</c> extractor.
/// </summary>
public class MoflixClickExtractor : IStreamExtractor
{
    /// <summary>
    /// Packed-JS player block. The <c>p</c> payload may contain escaped quotes.
    /// </summary>
    private static readonly Regex PackedPlayerPattern = new(
        @"eval\(function\(p,a,c,k,e,d\).*?\}\('(?<p>(?:\\.|[^'\\])*)',\s*(?<radix>\d+),\s*\d+,\s*'(?<keywords>(?:\\.|[^'\\])*)'\.split\('\|'\)\)\)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LinksPattern = new(
        @"var\s+links\s*=\s*(\{[^}]+\})",
        RegexOptions.Compiled);

    private static readonly string[] HlsKeys = { "hls4", "hls3", "hls2" };

    private readonly HttpClient _httpClient;
    private readonly ILogger<MoflixClickExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MoflixClickExtractor"/> class.
    /// </summary>
    public MoflixClickExtractor(IHttpClientFactory httpClientFactory, ILogger<MoflixClickExtractor> logger)
    {
        _httpClient = httpClientFactory.CreateClient("AniWorld");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "MoflixClick";

    /// <inheritdoc />
    public async Task<string?> GetDirectLinkAsync(string embedUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Uri.TryCreate(embedUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.Equals("moflix-stream.click", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("MoflixClick: not a moflix-stream.click URL: {Url}", embedUrl);
                return null;
            }

            _logger.LogDebug("Extracting MoflixClick direct link from: {Url}", embedUrl);

            var response = await _httpClient.GetAsync(embedUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var match = PackedPlayerPattern.Match(html);
            if (!match.Success)
            {
                _logger.LogWarning("MoflixClick: player data (packed JS) not found");
                return null;
            }

            var p = match.Groups["p"].Value.Replace("\\'", "'", StringComparison.Ordinal);
            var radix = int.Parse(match.Groups["radix"].Value);
            var keywords = match.Groups["keywords"].Value.Split('|');

            var unpacked = UnpackJs(p, radix, keywords);
            if (unpacked == null)
            {
                _logger.LogWarning("MoflixClick: failed to unpack packed JS");
                return null;
            }

            var linksMatch = LinksPattern.Match(unpacked);
            if (!linksMatch.Success)
            {
                _logger.LogWarning("MoflixClick: 'links' object not found");
                return null;
            }

            JsonElement links;
            using (var doc = JsonDocument.Parse(linksMatch.Groups[1].Value))
            {
                links = doc.RootElement.Clone();
            }

            foreach (var key in HlsKeys)
            {
                if (!links.TryGetProperty(key, out var urlElem) || urlElem.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var url = urlElem.GetString();
                if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) ||
                    u.Scheme != Uri.UriSchemeHttps)
                {
                    continue;
                }

                var isHls = await IsHlsPlaylistAsync(url, cancellationToken).ConfigureAwait(false);
                if (isHls)
                {
                    _logger.LogInformation("MoflixClick: reachable playlist via {Key}", key);
                    return url;
                }
            }

            _logger.LogWarning("MoflixClick: no reachable HLS playlist");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract MoflixClick direct link from {Url}", embedUrl);
            return null;
        }
    }

    /// <summary>
    /// GETs the playlist with a Referer and checks that the body is an HLS playlist.
    /// </summary>
    private async Task<bool> IsHlsPlaylistAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://moflix-stream.click/");
            request.Headers.AcceptEncoding.ParseAdd("identity");

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return body.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MoflixClick: playlist check failed for {Url}", url);
            return false;
        }
    }

    /// <summary>
    /// Unpacks Dean Edwards' packed JavaScript (same algorithm as the legacy Filemoon path).
    /// </summary>
    private string? UnpackJs(string packed, int radix, string[] keywords)
    {
        try
        {
            return Regex.Replace(packed, @"\b(\w+)\b", m =>
            {
                var token = m.Groups[1].Value;
                var index = DecodeBaseN(token, radix);
                if (index >= 0 && index < keywords.Length && !string.IsNullOrEmpty(keywords[index]))
                {
                    return keywords[index];
                }

                return token;
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MoflixClick: failed to unpack JS");
            return null;
        }
    }

    /// <summary>
    /// Converts a string from base-N (up to base 62) to a decimal integer.
    /// </summary>
    private static int DecodeBaseN(string token, int radix)
    {
        if (radix <= 10)
        {
            return int.TryParse(token, out var val) ? val : -1;
        }

        int result = 0;
        foreach (var c in token)
        {
            int digit;
            if (c >= '0' && c <= '9') digit = c - '0';
            else if (c >= 'a' && c <= 'z') digit = c - 'a' + 10;
            else if (c >= 'A' && c <= 'Z') digit = c - 'A' + 36;
            else return -1;

            if (digit >= radix) return -1;
            result = result * radix + digit;
        }

        return result;
    }
}
