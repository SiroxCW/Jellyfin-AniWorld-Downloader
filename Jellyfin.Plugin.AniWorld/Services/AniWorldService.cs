using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Service for interacting with the aniworld.to website.
/// </summary>
public class AniWorldService : StreamingSiteService
{
    private static readonly Regex AniWorldSeasonLinkPattern = new(
        @"<a[^>]*href=""(/anime/stream/[^""]+/staffel-\d+)""[^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex AniWorldEpisodeListPattern = new(
        @"<a[^>]*href=""(/anime/stream/[^""]+/staffel-\d+/episode-\d+)""[^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex AniWorldSearchFilterPattern = new(
        @"^/anime/stream/[a-zA-Z0-9\-]+/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AniWorldBrowseItemPattern = new(
        @"<a\s+href=""(?<url>/anime/stream/[^""]+)""[^>]*>.*?data-src=""(?<cover>[^""]+)"".*?<h3>(?<name>.+?)\s*(?:<span[^>]*>\s*</span>)?\s*</h3>\s*(?:<small>(?<genre>[^<]*)</small>)?",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="AniWorldService"/> class.
    /// </summary>
    public AniWorldService(IHttpClientFactory httpClientFactory, ILogger<AniWorldService> logger)
        : base(httpClientFactory.CreateClient("AniWorld"), logger)
    {
    }

    /// <inheritdoc />
    public override string SourceName => "aniworld";

    /// <inheritdoc />
    protected override string BaseUrl => "https://aniworld.to";

    /// <inheritdoc />
    protected override string SearchUrl => "https://aniworld.to/ajax/search";

    /// <inheritdoc />
    protected override string SeriesPathPrefix => "/anime/stream/";

    /// <inheritdoc />
    protected override string PopularPath => "/beliebte-animes";

    /// <inheritdoc />
    protected override string NewSectionHeading => "Neue Animes";

    /// <inheritdoc />
    protected override Regex SeasonLinkPattern => AniWorldSeasonLinkPattern;

    /// <inheritdoc />
    protected override Regex EpisodeListPattern => AniWorldEpisodeListPattern;

    /// <inheritdoc />
    protected override Regex SearchFilterPattern => AniWorldSearchFilterPattern;

    /// <inheritdoc />
    protected override Regex BrowseItemPattern => AniWorldBrowseItemPattern;
}
