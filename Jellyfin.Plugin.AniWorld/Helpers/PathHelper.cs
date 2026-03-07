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
    /// Regex to extract season and episode numbers from an episode URL.
    /// </summary>
    public static readonly Regex SeasonEpisodeFromUrl = new(
        @"/staffel-(?<season>\d+)/episode-(?<episode>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to extract movie number from a movie URL.
    /// </summary>
    public static readonly Regex MovieFromUrl = new(
        @"/filme/film-(?<num>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to extract the series slug from a URL.
    /// Supports /anime/stream/{slug} (aniworld), /serie/{slug} (s.to), and /watch/{slug} (hianime).
    /// </summary>
    public static readonly Regex SeriesSlugFromUrl = new(
        @"/(?:anime/stream|serie|watch)/(?<slug>[^/?\#]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to extract episode number from a HiAnime URL (?ep=N).
    /// </summary>
    public static readonly Regex HiAnimeEpisodeFromUrl = new(
        @"[?&]ep=(?<episode>\d+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Sanitizes a file/folder name by removing invalid and problematic characters.
    /// Strips characters that cause issues on Windows, SMB shares, and some media players:
    /// : ? ! * " &lt; &gt; | [ ] in addition to OS-level invalid chars.
    /// Normalizes unicode quotes and dashes to their ASCII equivalents.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        // Normalize unicode typography to ASCII equivalents
        var normalized = name
            .Replace('\u2018', '\'')  // LEFT SINGLE QUOTATION MARK → apostrophe
            .Replace('\u2019', '\'')  // RIGHT SINGLE QUOTATION MARK → apostrophe
            .Replace('\u201A', '\'')  // SINGLE LOW-9 QUOTATION MARK → apostrophe
            .Replace("\u201C", string.Empty)  // LEFT DOUBLE QUOTATION MARK → remove
            .Replace("\u201D", string.Empty)  // RIGHT DOUBLE QUOTATION MARK → remove
            .Replace('\u2013', '-')   // EN DASH → hyphen
            .Replace('\u2014', '-')   // EM DASH → hyphen
            .Replace('\u2026', '.');  // HORIZONTAL ELLIPSIS → period

        var invalid = Path.GetInvalidFileNameChars();
        var extraInvalid = new[] { ':', '?', '!', '*', '"', '<', '>', '|', '[', ']' };
        var sanitized = new string(normalized
            .Where(c => !invalid.Contains(c) && !extraInvalid.Contains(c))
            .ToArray());

        // Collapse multiple spaces/dashes, trim trailing punctuation
        sanitized = Regex.Replace(sanitized, @"\s{2,}", " ");
        sanitized = Regex.Replace(sanitized, @"-{2,}", "-");
        sanitized = sanitized.Trim().TrimEnd('.', '-', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }

    /// <summary>
    /// Parses season and episode numbers from an episode URL.
    /// Returns (0, N) for movies and (0, 0) for unrecognised URLs.
    /// HiAnime URLs always return Season 1 with episode from ?ep=N.
    /// </summary>
    public static (int Season, int Episode) ParseSeasonEpisode(string url)
    {
        // HiAnime: /watch/{slug}?ep=N → Season 1, Episode N
        if (url.Contains("hianime.to", StringComparison.OrdinalIgnoreCase))
        {
            var hiMatch = HiAnimeEpisodeFromUrl.Match(url);
            if (hiMatch.Success)
            {
                return (1, int.Parse(hiMatch.Groups["episode"].Value));
            }

            return (1, 0);
        }

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

        // HiAnime: always Season 01, episode from ?ep=N
        if (episodeUrl.Contains("hianime.to", StringComparison.OrdinalIgnoreCase))
        {
            var hiMatch = HiAnimeEpisodeFromUrl.Match(episodeUrl);
            var epNum = hiMatch.Success ? int.Parse(hiMatch.Groups["episode"].Value) : 1;
            var fileName = $"{safeName} - S01E{epNum:D2}.mkv";
            return Path.Combine(basePath, safeName, "Season 01", fileName);
        }

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
                // Truncate at word boundary, trim trailing punctuation
                safeTitle = safeTitle[..77].TrimEnd(' ', '-', ',', '.', ';');
                safeTitle += "...";
            }

            var newName = $"{match.Groups[1].Value} - {safeTitle}{ext}";
            return Path.Combine(dir, newName);
        }

        return outputPath;
    }
}
