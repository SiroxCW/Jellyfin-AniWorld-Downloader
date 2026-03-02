using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Extractors;

/// <summary>
/// Extracts direct video URLs from Filemoon embeds (including Byse-based hosts).
/// Modern Filemoon clones use a REST API with AES-256-GCM encrypted playback data.
/// Legacy Filemoon hosts use Dean Edwards' packed JS.
/// </summary>
public class FilemoonExtractor : IStreamExtractor
{
    private static readonly Regex FileCodeFromUrl = new(
        @"/[de]/(?<code>[a-zA-Z0-9]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Pattern to find eval(function(p,a,c,k,e,d){...}) packed JS blocks (legacy Filemoon).
    /// </summary>
    private static readonly Regex PackedPattern = new(
        @"eval\(function\(p,a,c,k,e,d\)\{.*?\}\('(?<p>[^']+)',\s*(?<a>\d+),\s*(?<c>\d+),\s*'(?<k>[^']+)'\.split\('\|'\)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Direct sources pattern.
    /// </summary>
    private static readonly Regex SourcePattern = new(
        @"sources\s*:\s*\[\s*\{[^}]*file:\s*['""](?<url>[^'""]+)['""]",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// HLS URL pattern.
    /// </summary>
    private static readonly Regex HlsPattern = new(
        @"['""](?<url>https?://[^'""]+\.m3u8[^'""]*)['""]",
        RegexOptions.Compiled);

    /// <summary>
    /// File pattern in JWPlayer config.
    /// </summary>
    private static readonly Regex FilePattern = new(
        @"file\s*:\s*['""](?<url>https?://[^'""]+)['""]",
        RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<FilemoonExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilemoonExtractor"/> class.
    /// </summary>
    public FilemoonExtractor(IHttpClientFactory httpClientFactory, ILogger<FilemoonExtractor> logger)
    {
        _httpClient = httpClientFactory.CreateClient("AniWorld");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "Filemoon";

    /// <inheritdoc />
    public async Task<string?> GetDirectLinkAsync(string embedUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Extracting Filemoon direct link from: {Url}", embedUrl);

            // Strategy 1: Try modern Byse-style API (AES-256-GCM encrypted)
            var fileCode = ExtractFileCode(embedUrl);
            if (!string.IsNullOrEmpty(fileCode))
            {
                var apiUrl = await TryByseApiAsync(embedUrl, fileCode, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(apiUrl))
                {
                    return apiUrl;
                }
            }

            // Strategy 2: Try legacy HTML scraping (packed JS)
            var response = await _httpClient.GetAsync(embedUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return TryExtractFromHtml(html);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract Filemoon direct link from {Url}", embedUrl);
            return null;
        }
    }

    /// <summary>
    /// Tries the modern Byse-style REST API with AES-256-GCM decryption.
    /// </summary>
    private async Task<string?> TryByseApiAsync(string embedUrl, string fileCode, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = new Uri(embedUrl).GetLeftPart(UriPartial.Authority);
            var apiUrl = $"{baseUrl}/api/videos/{fileCode}";

            _logger.LogDebug("Trying Byse API: {ApiUrl}", apiUrl);

            var response = await _httpClient.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Byse API returned {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check if playback data exists with encryption
            if (!root.TryGetProperty("playback", out var playback))
            {
                _logger.LogDebug("No playback data in API response");
                return null;
            }

            // Decrypt the playback payload
            var decryptedSources = DecryptPlaybackData(playback);
            if (decryptedSources == null)
            {
                _logger.LogWarning("Failed to decrypt Byse playback data");
                return null;
            }

            // Extract the best quality stream URL
            var bestUrl = ExtractBestSourceUrl(decryptedSources);
            if (!string.IsNullOrEmpty(bestUrl))
            {
                _logger.LogInformation("Filemoon/Byse: extracted stream URL via API");
                return bestUrl;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Byse API approach failed, falling back to HTML scraping");
        }

        return null;
    }

    /// <summary>
    /// Decrypts the AES-256-GCM encrypted playback data from the Byse API.
    /// The key is formed by base64url-decoding and concatenating key_parts.
    /// </summary>
    private JsonDocument? DecryptPlaybackData(JsonElement playback)
    {
        try
        {
            // Get key_parts array
            if (!playback.TryGetProperty("key_parts", out var keyPartsArr) ||
                keyPartsArr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            // Concatenate decoded key parts to form the 256-bit AES key
            var keyBytes = new List<byte>();
            foreach (var part in keyPartsArr.EnumerateArray())
            {
                var partStr = part.GetString();
                if (string.IsNullOrEmpty(partStr)) return null;
                keyBytes.AddRange(Base64UrlDecode(partStr));
            }

            var key = keyBytes.ToArray();

            // Try primary payload first, then payload2
            var result = TryDecryptPayload(playback, key, "iv", "payload");
            if (result != null) return result;

            result = TryDecryptPayload(playback, key, "iv2", "payload2");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to decrypt playback data");
            return null;
        }
    }

    /// <summary>
    /// Attempts to decrypt a specific payload/iv pair.
    /// </summary>
    private JsonDocument? TryDecryptPayload(JsonElement playback, byte[] key, string ivProp, string payloadProp)
    {
        try
        {
            if (!playback.TryGetProperty(ivProp, out var ivElem) ||
                !playback.TryGetProperty(payloadProp, out var payloadElem))
            {
                return null;
            }

            var ivStr = ivElem.GetString();
            var payloadStr = payloadElem.GetString();
            if (string.IsNullOrEmpty(ivStr) || string.IsNullOrEmpty(payloadStr))
            {
                return null;
            }

            var iv = Base64UrlDecode(ivStr);
            var ciphertext = Base64UrlDecode(payloadStr);

            // AES-GCM: last 16 bytes are the auth tag
            const int tagSize = 16;
            if (ciphertext.Length <= tagSize)
            {
                return null;
            }

            var actualCiphertext = ciphertext[..^tagSize];
            var tag = ciphertext[^tagSize..];
            var plaintext = new byte[actualCiphertext.Length];

            using var aesGcm = new AesGcm(key, tagSize);
            aesGcm.Decrypt(iv, actualCiphertext, tag, plaintext);

            var jsonStr = Encoding.UTF8.GetString(plaintext);
            _logger.LogDebug("Decrypted playback JSON ({Length} chars)", jsonStr.Length);

            return JsonDocument.Parse(jsonStr);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Decrypt attempt failed for {IvProp}/{PayloadProp}", ivProp, payloadProp);
            return null;
        }
    }

    /// <summary>
    /// Extracts the best quality video URL from decrypted sources JSON.
    /// </summary>
    private string? ExtractBestSourceUrl(JsonDocument doc)
    {
        var root = doc.RootElement;

        // Try "sources" array
        if (root.TryGetProperty("sources", out var sources) &&
            sources.ValueKind == JsonValueKind.Array)
        {
            string? bestUrl = null;
            int bestHeight = 0;

            foreach (var source in sources.EnumerateArray())
            {
                var url = source.TryGetProperty("url", out var urlElem) ? urlElem.GetString() : null;
                var height = source.TryGetProperty("height", out var hElem) && hElem.TryGetInt32(out var h) ? h : 0;

                if (!string.IsNullOrEmpty(url) && height >= bestHeight)
                {
                    bestUrl = url;
                    bestHeight = height;
                }
            }

            if (!string.IsNullOrEmpty(bestUrl))
            {
                return bestUrl;
            }
        }

        // Try "source" property
        if (root.TryGetProperty("source", out var sourceElem) &&
            sourceElem.ValueKind == JsonValueKind.String)
        {
            return sourceElem.GetString();
        }

        // Try "file" property
        if (root.TryGetProperty("file", out var fileElem) &&
            fileElem.ValueKind == JsonValueKind.String)
        {
            return fileElem.GetString();
        }

        return null;
    }

    /// <summary>
    /// Tries to extract video URL from HTML (legacy Filemoon with packed JS).
    /// </summary>
    private string? TryExtractFromHtml(string html)
    {
        // Try packed JS
        var match = PackedPattern.Match(html);
        if (match.Success)
        {
            var packed = match.Groups["p"].Value;
            var radix = int.Parse(match.Groups["a"].Value);
            var count = int.Parse(match.Groups["c"].Value);
            var keywords = match.Groups["k"].Value.Split('|');

            var unpacked = UnpackJs(packed, radix, count, keywords);
            if (unpacked != null)
            {
                var url = ExtractUrlFromString(unpacked);
                if (url != null)
                {
                    _logger.LogInformation("Filemoon: extracted URL from packed JS");
                    return url;
                }
            }
        }

        // Try direct patterns
        var url2 = ExtractUrlFromString(html);
        if (url2 != null)
        {
            _logger.LogInformation("Filemoon: extracted URL from direct pattern in HTML");
        }

        return url2;
    }

    /// <summary>
    /// Extracts a video URL from text content.
    /// </summary>
    private string? ExtractUrlFromString(string text)
    {
        var hlsMatch = HlsPattern.Match(text);
        if (hlsMatch.Success) return hlsMatch.Groups["url"].Value;

        var srcMatch = SourcePattern.Match(text);
        if (srcMatch.Success) return srcMatch.Groups["url"].Value;

        var fileMatch = FilePattern.Match(text);
        if (fileMatch.Success)
        {
            var url = fileMatch.Groups["url"].Value;
            if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                url.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the file code from a Filemoon/Byse URL.
    /// e.g., https://bysezejataos.com/d/56q7gpy3qyo6 → 56q7gpy3qyo6
    /// </summary>
    private static string? ExtractFileCode(string url)
    {
        var match = FileCodeFromUrl.Match(url);
        return match.Success ? match.Groups["code"].Value : null;
    }

    /// <summary>
    /// Base64url decode (RFC 4648 §5).
    /// </summary>
    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }

    /// <summary>
    /// Unpacks Dean Edwards' packed JavaScript (legacy).
    /// </summary>
    private string? UnpackJs(string packed, int radix, int count, string[] keywords)
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
            _logger.LogDebug(ex, "Failed to unpack JS");
            return null;
        }
    }

    /// <summary>
    /// Converts a string from base-N to a decimal integer (up to base 62).
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
