using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Extractors;

/// <summary>
/// Extracts the direct HLS URL from the gxplayer.xyz player used by MegaKino
/// (watch.gxplayer.xyz/watch?v=...). The player page embeds a JSON object with
/// "uid", "md5" and "id"; the master playlist is then derived from those parts.
/// Port of the Python <c>get_direct_link_from_megakino</c> extractor.
/// </summary>
public class MegaKinoExtractor : IStreamExtractor
{
    private static readonly Regex UidPattern = new(
        @"""uid""\s*:\s*""(?<uid>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex Md5Pattern = new(
        @"""md5""\s*:\s*""(?<md5>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex IdPattern = new(
        @"""id""\s*:\s*""(?<id>[^""]+)""",
        RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<MegaKinoExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MegaKinoExtractor"/> class.
    /// </summary>
    public MegaKinoExtractor(IHttpClientFactory httpClientFactory, ILogger<MegaKinoExtractor> logger)
    {
        _httpClient = httpClientFactory.CreateClient("AniWorld");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "MegaKino";

    /// <inheritdoc />
    public async Task<string?> GetDirectLinkAsync(string embedUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Extracting MegaKino (gxplayer) direct link from: {Url}", embedUrl);

            var response = await _httpClient.GetAsync(embedUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var uid = UidPattern.Match(html);
            var md5 = Md5Pattern.Match(html);
            var id = IdPattern.Match(html);

            if (!uid.Success || !md5.Success || !id.Success)
            {
                _logger.LogWarning(
                    "MegaKino: missing uid/md5/id in player page (uid={Uid}, md5={Md5}, id={Id})",
                    uid.Success, md5.Success, id.Success);
                return null;
            }

            var streamLink =
                $"https://watch.gxplayer.xyz/m3u8/{uid.Groups["uid"].Value}/{md5.Groups["md5"].Value}/master.txt?s=1&id={id.Groups["id"].Value}&cache=1";

            _logger.LogDebug("MegaKino: built playlist URL {Url}", streamLink);
            return streamLink;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract MegaKino (gxplayer) direct link from {Url}", embedUrl);
            return null;
        }
    }
}
