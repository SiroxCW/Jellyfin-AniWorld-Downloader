using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Extractors;

/// <summary>
/// Extracts direct video URLs from VOE embeds.
/// Port of the Python VOE extractor from AniWorld-Downloader.
/// VOE uses a JS-based redirect (voe.sx → randomdomain.com) and encodes
/// the stream URL in a JSON script block with a multi-step decode chain.
/// </summary>
public class VoeExtractor : IStreamExtractor
{
    private static readonly Regex JsRedirectPattern = new(
        @"window\.location\.href\s*=\s*['""](?<url>https?://[^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex ScriptJsonPattern = new(
        @"<script\s+type=[""']application/json[""']>\s*(?<json>\[""[^<]+?\])\s*</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly string[] JunkParts = { "@$", "^^", "~@", "%?", "*~", "!!", "#&" };

    private readonly HttpClient _httpClient;
    private readonly ILogger<VoeExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VoeExtractor"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger instance.</param>
    public VoeExtractor(IHttpClientFactory httpClientFactory, ILogger<VoeExtractor> logger)
    {
        _httpClient = httpClientFactory.CreateClient("AniWorld");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "VOE";

    /// <inheritdoc />
    public async Task<string?> GetDirectLinkAsync(string embedUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Extracting VOE direct link from: {Url}", embedUrl);

            var html = await FetchWithJsRedirectAsync(embedUrl, cancellationToken).ConfigureAwait(false);
            var source = ExtractVoeSourceFromHtml(html);

            if (source != null)
            {
                _logger.LogDebug("VOE source extracted successfully");
            }
            else
            {
                _logger.LogWarning("Failed to extract VOE source from page");
            }

            return source;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract VOE direct link from {Url}", embedUrl);
            return null;
        }
    }

    private async Task<string> FetchWithJsRedirectAsync(string url, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // VOE uses a JS redirect: voe.sx page contains window.location.href = 'https://randomdomain.com/e/...'
        // We need to follow this JS redirect manually since HttpClient won't execute JS
        var hasJsonBlock = ScriptJsonPattern.IsMatch(html);
        if (!hasJsonBlock)
        {
            // Look for JS redirect to the actual VOE player page
            var jsRedirects = JsRedirectPattern.Matches(html);
            foreach (Match match in jsRedirects)
            {
                var redirectUrl = match.Groups["url"].Value;
                if (redirectUrl != url)
                {
                    _logger.LogDebug("Following VOE JS redirect: {RedirectUrl}", redirectUrl);
                    response = await _httpClient.GetAsync(redirectUrl, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    // Check if this page has the JSON block
                    if (ScriptJsonPattern.IsMatch(html))
                    {
                        return html;
                    }
                }
            }

            // Fallback: look for any URL that might be the actual player
            var urlPattern = new Regex(@"https?://[a-zA-Z0-9\-]+\.[a-zA-Z]+/e/[a-zA-Z0-9]+", RegexOptions.Compiled);
            var urlMatch = urlPattern.Match(html);
            if (urlMatch.Success && urlMatch.Value != url)
            {
                _logger.LogDebug("Following VOE fallback redirect: {RedirectUrl}", urlMatch.Value);
                response = await _httpClient.GetAsync(urlMatch.Value, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return html;
    }

    private string? ExtractVoeSourceFromHtml(string html)
    {
        var scriptBlocks = ScriptJsonPattern.Matches(html);
        if (scriptBlocks.Count == 0)
        {
            _logger.LogWarning("No JSON script blocks found in VOE page");
            return null;
        }

        foreach (Match match in scriptBlocks)
        {
            var rawJson = match.Groups["json"].Value.Trim();

            try
            {
                // The JSON is a string array: ["encoded_string"]
                // Parse it to extract the encoded string
                string encoded;
                using (var doc = JsonDocument.Parse(rawJson))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    {
                        encoded = doc.RootElement[0].GetString() ?? string.Empty;
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.String)
                    {
                        encoded = doc.RootElement.GetString() ?? string.Empty;
                    }
                    else
                    {
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(encoded))
                {
                    continue;
                }

                var decoded = DecodeVoeString(encoded);
                if (decoded != null)
                {
                    using var decodedDoc = JsonDocument.Parse(decoded);
                    if (decodedDoc.RootElement.TryGetProperty("source", out var sourceElem))
                    {
                        return sourceElem.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to decode one VOE script block, trying next...");
                continue;
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes a VOE encoded string.
    /// Steps: ROT13 -> remove junk -> base64 decode -> shift chars back by 3 -> reverse -> base64 decode.
    /// </summary>
    private static string? DecodeVoeString(string encoded)
    {
        try
        {
            // Step 1: ROT13
            var step1 = Rot13(encoded);

            // Step 2: Replace junk patterns with nothing
            var step2 = step1;
            foreach (var junk in JunkParts)
            {
                step2 = step2.Replace(junk, "_", StringComparison.Ordinal);
            }

            step2 = step2.Replace("_", string.Empty, StringComparison.Ordinal);

            // Step 3: Base64 decode
            var step3bytes = Convert.FromBase64String(step2);
            var step3 = Encoding.UTF8.GetString(step3bytes);

            // Step 4: Shift characters back by 3
            var step4 = new string(step3.Select(c => (char)(c - 3)).ToArray());

            // Step 5: Reverse and base64 decode
            var reversed = new string(step4.Reverse().ToArray());
            var step5bytes = Convert.FromBase64String(reversed);
            var step5 = Encoding.UTF8.GetString(step5bytes);

            return step5;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Rot13(string input)
    {
        var result = new char[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c >= 'A' && c <= 'Z')
            {
                result[i] = (char)(((c - 'A' + 13) % 26) + 'A');
            }
            else if (c >= 'a' && c <= 'z')
            {
                result[i] = (char)(((c - 'a' + 13) % 26) + 'a');
            }
            else
            {
                result[i] = c;
            }
        }

        return new string(result);
    }
}
