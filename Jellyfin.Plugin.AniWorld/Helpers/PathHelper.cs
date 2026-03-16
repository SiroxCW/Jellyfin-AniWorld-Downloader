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
        // Normalize unicode dashes to hyphens, ellipsis to dot
        var normalized = name
            .Replace('\u2013', '-')   // EN DASH → hyphen
            .Replace('\u2014', '-')   // EM DASH → hyphen
            .Replace('\u2026', '.');  // HORIZONTAL ELLIPSIS → period

        // Allowlist: keep only Unicode letters, digits, space, dot, underscore, hyphen
        var sanitized = new string(normalized
            .Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '_' || c == '-')
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
    /// HiAnime URLs always return Season 1. When <paramref name="episodeNumber"/> is provided
    /// it is used as the episode number; otherwise falls back to the ?ep=N URL parameter.
    /// </summary>
    public static (int Season, int Episode) ParseSeasonEpisode(string url, int? episodeNumber = null)
    {
        // HiAnime: /watch/{slug}?ep=N → Season 1
        if (url.Contains("hianime.to", StringComparison.OrdinalIgnoreCase))
        {
            if (episodeNumber.HasValue)
            {
                return (1, episodeNumber.Value);
            }

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
    /// When <paramref name="episodeNumber"/> is provided it is used for HiAnime episodes
    /// instead of the ?ep=N URL parameter (which is an internal ID, not the sequential number).
    /// </summary>
    public static string BuildOutputPath(string basePath, string seriesTitle, string episodeUrl, int? episodeNumber = null)
    {
        var safeName = SanitizeFileName(seriesTitle);

        // HiAnime: always Season 01
        if (episodeUrl.Contains("hianime.to", StringComparison.OrdinalIgnoreCase))
        {
            int epNum;
            if (episodeNumber.HasValue)
            {
                epNum = episodeNumber.Value;
            }
            else
            {
                var hiMatch = HiAnimeEpisodeFromUrl.Match(episodeUrl);
                epNum = hiMatch.Success ? int.Parse(hiMatch.Groups["episode"].Value) : 1;
            }

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
    /// Regex to extract episode numbers from filenames like "SeriesName - S01E05.mkv" or "SeriesName - S01E05 - Title.mkv".
    /// </summary>
    private static readonly Regex EpisodeFilePattern = new(
        @"- S\d{2}E(?<episode>\d{2,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Builds a Jellyfin-compatible output path using a custom folder and season number.
    /// Format: basePath/SanitizedFolder/Season XX/SanitizedFolder - SXXEXX.mkv
    /// Used when downloading HiAnime episodes into an existing series folder.
    /// </summary>
    public static string BuildOutputPathCustom(
        string basePath, string customFolder, int seasonNumber, int episodeNumber)
    {
        var safeName = SanitizeFileName(customFolder);
        var seasonFolder = $"Season {seasonNumber:D2}";
        var fileName = $"{safeName} - S{seasonNumber:D2}E{episodeNumber:D2}.mkv";
        return Path.Combine(basePath, safeName, seasonFolder, fileName);
    }

    /// <summary>
    /// Scans a season folder for existing episodes and returns the highest episode number found.
    /// Returns 0 if the folder doesn't exist or contains no matching files.
    /// </summary>
    public static int GetHighestEpisodeNumber(string basePath, string folderName, int seasonNumber)
    {
        var safeName = SanitizeFileName(folderName);
        var seasonFolder = $"Season {seasonNumber:D2}";
        var seasonPath = Path.Combine(basePath, safeName, seasonFolder);

        if (!Directory.Exists(seasonPath))
        {
            return 0;
        }

        var highest = 0;
        foreach (var file in Directory.EnumerateFiles(seasonPath, "*.mkv"))
        {
            var fileName = Path.GetFileName(file);
            var match = EpisodeFilePattern.Match(fileName);
            if (match.Success && int.TryParse(match.Groups["episode"].Value, out var epNum))
            {
                if (epNum > highest)
                {
                    highest = epNum;
                }
            }
        }

        return highest;
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
