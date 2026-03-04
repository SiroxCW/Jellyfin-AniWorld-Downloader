using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AniWorld.Helpers;

/// <summary>
/// Shared path and filename utilities used across the plugin.
/// Consolidated to avoid duplication between Controller and DownloadService.
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Regex to extract season and episode numbers from an aniworld.to episode URL.
    /// </summary>
    public static readonly Regex SeasonEpisodeFromUrl = new(
        @"/staffel-(?<season>\d+)/episode-(?<episode>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to extract movie number from an aniworld.to movie URL.
    /// </summary>
    public static readonly Regex MovieFromUrl = new(
        @"/filme/film-(?<num>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to extract the series slug from an aniworld.to URL.
    /// </summary>
    public static readonly Regex SeriesSlugFromUrl = new(
        @"/anime/stream/(?<slug>[^/]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Sanitizes a file/folder name by removing invalid and problematic characters.
    /// Strips characters that cause issues on Windows, SMB shares, and some media players:
    /// : ? ! * " &lt; &gt; | in addition to OS-level invalid chars.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var extraInvalid = new[] { ':', '?', '!', '*', '"', '<', '>', '|' };
        var sanitized = new string(name
            .Where(c => !invalid.Contains(c) && !extraInvalid.Contains(c))
            .ToArray());
        sanitized = Regex.Replace(sanitized, @"\s{2,}", " ");
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized.Trim();
    }

    /// <summary>
    /// Parses season and episode numbers from an aniworld.to URL.
    /// Returns (0, N) for movies and (0, 0) for unrecognised URLs.
    /// </summary>
    public static (int Season, int Episode) ParseSeasonEpisode(string url)
    {
        var seMatch = SeasonEpisodeFromUrl.Match(url);
        if (seMatch.Success)
        {
            return (int.Parse(seMatch.Groups["season"].Value), int.Parse(seMatch.Groups["episode"].Value));
        }

        var movieMatch = MovieFromUrl.Match(url);
        if (movieMatch.Success)
        {
            return (0, int.Parse(movieMatch.Groups["num"].Value));
        }

        return (0, 0);
    }

    /// <summary>
    /// Builds a Jellyfin-compatible output path from the episode URL.
    /// Format: basePath/SeriesName/Season XX/SeriesName - SXXEXX.mkv
    /// </summary>
    public static string BuildOutputPath(string basePath, string seriesTitle, string episodeUrl)
    {
        var safeName = SanitizeFileName(seriesTitle);

        var seMatch = SeasonEpisodeFromUrl.Match(episodeUrl);
        if (seMatch.Success)
        {
            var season = int.Parse(seMatch.Groups["season"].Value);
            var episode = int.Parse(seMatch.Groups["episode"].Value);
            var seasonFolder = $"Season {season:D2}";
            var fileName = $"{safeName} - S{season:D2}E{episode:D2}.mkv";

            return Path.Combine(basePath, safeName, seasonFolder, fileName);
        }

        var movieMatch = MovieFromUrl.Match(episodeUrl);
        if (movieMatch.Success)
        {
            var num = int.Parse(movieMatch.Groups["num"].Value);
            var fileName = $"{safeName} - S00E{num:D2}.mkv";

            return Path.Combine(basePath, safeName, "Specials", fileName);
        }

        // Fallback: use slug + timestamp
        var slugMatch = SeriesSlugFromUrl.Match(episodeUrl);
        var slug = slugMatch.Success ? slugMatch.Groups["slug"].Value : "unknown";
        return Path.Combine(basePath, safeName, $"{slug}_{DateTime.UtcNow:yyyyMMddHHmmss}.mkv");
    }

    /// <summary>
    /// Inserts the episode title into the filename.
    /// Transforms "SeriesName - S01E01.mkv" into "SeriesName - S01E01 - Episode Title.mkv".
    /// </summary>
    public static string InsertEpisodeTitleInPath(string outputPath, string episodeTitle)
    {
        if (string.IsNullOrWhiteSpace(episodeTitle) || episodeTitle == "Unknown")
        {
            return outputPath;
        }

        var dir = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(outputPath);
        var ext = Path.GetExtension(outputPath);

        var match = Regex.Match(fileName, @"^(.+ - S\d{2}E\d{2})$");
        if (match.Success)
        {
            var safeTitle = SanitizeFileName(episodeTitle);
            if (safeTitle.Length > 80)
            {
                safeTitle = safeTitle[..77] + "...";
            }

            var newName = $"{match.Groups[1].Value} - {safeTitle}{ext}";
            return Path.Combine(dir, newName);
        }

        return outputPath;
    }
}
