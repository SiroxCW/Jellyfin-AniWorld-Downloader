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
    /// Regex to extract the movie slug from a filmo.to movie URL.
    /// Supports /movies/{slug} (filmo.to).
    /// </summary>
    public static readonly Regex FilmoMovieFromUrl = new(
        @"filmo\.to/movies/[\w-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to match a filmpalast.to movie URL (/stream/{slug}).
    /// </summary>
    public static readonly Regex FilmpalastMovieFromUrl = new(
        @"filmpalast\.to/stream/[\w-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to match a MegaKino movie URL (/films/{slug}) on its rotating domain.
    /// </summary>
    public static readonly Regex MegaKinoMovieFromUrl = new(
        @"megakino\d*\.com/films/[\w-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to extract the episode number from a MegaKino serial URL fragment
    /// (serial page + #mkep={N}); MegaKino serials are always season 1.
    /// </summary>
    public static readonly Regex MegaKinoEpisodeFromUrl = new(
        @"#mkep=(?<episode>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to extract season and episode numbers from a moflix-stream.xyz
    /// series URL (/titles/{id}/season/{s}/episodes/{e}).
    /// </summary>
    public static readonly Regex MoflixEpisodeFromUrl = new(
        @"moflix-stream\.xyz/titles/\d+/season/(?<season>\d+)/episodes/(?<episode>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to match a moflix-stream.xyz movie URL (/titles/{id} with no
    /// trailing /season).
    /// </summary>
    public static readonly Regex MoflixMovieFromUrl = new(
        @"moflix-stream\.xyz/titles/\d+(?!\d|/season)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Matches the placeholder name of an aniworld/s.to film whose title is not known yet
    /// ("{Series} - Film {NN}").
    /// </summary>
    private static readonly Regex FilmPlaceholderName = new(
        @"^(?<series>.+) - Film (?<num>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to extract the series slug from a URL.
    /// Supports /anime/stream/{slug} (aniworld), /serie/{slug} (s.to) and /movies/{slug} (filmo.to).
    /// </summary>
    public static readonly Regex SeriesSlugFromUrl = new(
        @"/(?:anime/stream|serie|movies)/(?<slug>[^/?\#]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns true when the URL points to a movie (aniworld/s.to movie page or a filmo.to movie page).
    /// </summary>
    public static bool IsMovieUrl(string url)
    {
        return MovieFromUrl.IsMatch(url) || IsMovieSiteUrl(url);
    }

    /// <summary>
    /// Returns true when the URL is a standalone movie on filmo.to, filmpalast.to,
    /// megakino or moflix. Excludes aniworld/s.to films, which belong to a series.
    /// </summary>
    public static bool IsMovieSiteUrl(string url)
    {
        return FilmoMovieFromUrl.IsMatch(url)
            || FilmpalastMovieFromUrl.IsMatch(url)
            || MegaKinoMovieFromUrl.IsMatch(url)
            || MoflixMovieFromUrl.IsMatch(url);
    }

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
    /// </summary>
    public static (int Season, int Episode) ParseSeasonEpisode(string url)
    {
        var seMatch = SeasonEpisodeFromUrl.Match(url);
        if (seMatch.Success)
        {
            return (int.Parse(seMatch.Groups["season"].Value), int.Parse(seMatch.Groups["episode"].Value));
        }

        var moflixMatch = MoflixEpisodeFromUrl.Match(url);
        if (moflixMatch.Success)
        {
            return (int.Parse(moflixMatch.Groups["season"].Value), int.Parse(moflixMatch.Groups["episode"].Value));
        }

        var megaKinoMatch = MegaKinoEpisodeFromUrl.Match(url);
        if (megaKinoMatch.Success)
        {
            return (1, int.Parse(megaKinoMatch.Groups["episode"].Value));
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
    /// Episodes: basePath/SeriesName/Season XX/SeriesName - SXXEXX.mkv
    /// Movies: basePath/Title/Title.mkv
    /// aniworld/s.to films stay in the series folder (Specials) unless a dedicated movie
    /// path is configured. In that case they get a placeholder movie folder here, which
    /// <see cref="ApplyFilmTitle"/> renames once the film title is known.
    /// </summary>
    public static string BuildOutputPath(string basePath, string seriesTitle, string episodeUrl, bool hasDedicatedMoviePath = false)
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

            if (hasDedicatedMoviePath)
            {
                return BuildMoviePath(basePath, FilmPlaceholder(safeName, num), ".mkv");
            }

            var fileName = $"{safeName} - S00E{num:D2}.mkv";

            return Path.Combine(basePath, safeName, "Specials", fileName);
        }

        // moflix series episodes: /titles/{id}/season/{s}/episodes/{e}
        var moflixMatch = MoflixEpisodeFromUrl.Match(episodeUrl);
        if (moflixMatch.Success)
        {
            var season = int.Parse(moflixMatch.Groups["season"].Value);
            var episode = int.Parse(moflixMatch.Groups["episode"].Value);
            var seasonFolder = $"Season {season:D2}";
            var fileName = $"{safeName} - S{season:D2}E{episode:D2}.mkv";

            return Path.Combine(basePath, safeName, seasonFolder, fileName);
        }

        // MegaKino serial episodes: {serial url}#mkep={n} (always season 1)
        var megaKinoMatch = MegaKinoEpisodeFromUrl.Match(episodeUrl);
        if (megaKinoMatch.Success)
        {
            var episode = int.Parse(megaKinoMatch.Groups["episode"].Value);
            var fileName = $"{safeName} - S01E{episode:D2}.mkv";

            return Path.Combine(basePath, safeName, "Season 01", fileName);
        }

        // Movies on filmo.to / filmpalast.to / megakino / moflix
        if (IsMovieSiteUrl(episodeUrl))
        {
            return BuildMoviePath(basePath, safeName, ".mkv");
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
            var safeTitle = SanitizeAndTruncateTitle(episodeTitle);

            var newName = $"{match.Groups[1].Value} - {safeTitle}{ext}";
            return Path.Combine(dir, newName);
        }

        return outputPath;
    }

    /// <summary>
    /// Renames the placeholder movie folder and file of an aniworld/s.to film to its real title:
    /// "basePath/Series - Film 01/Series - Film 01.mkv" becomes "basePath/Film Title/Film Title.mkv",
    /// or "basePath/Series - Film Title/Series - Film Title.mkv" with <paramref name="prefixSeries"/>.
    /// Returns the path unchanged for anything else, so it is safe to call again on a retry.
    /// </summary>
    public static string ApplyFilmTitle(string outputPath, string seriesTitle, string? filmTitle, string episodeUrl, bool prefixSeries = false)
    {
        var movieMatch = MovieFromUrl.Match(episodeUrl);
        if (!movieMatch.Success || string.IsNullOrWhiteSpace(filmTitle) || filmTitle == "Unknown")
        {
            return outputPath;
        }

        var safeSeries = SanitizeFileName(seriesTitle);
        var placeholder = FilmPlaceholder(safeSeries, int.Parse(movieMatch.Groups["num"].Value));
        var movieDir = Path.GetDirectoryName(outputPath);
        var basePath = Path.GetDirectoryName(movieDir);

        if (basePath == null
            || Path.GetFileName(movieDir) != placeholder
            || Path.GetFileNameWithoutExtension(outputPath) != placeholder)
        {
            return outputPath;
        }

        // aniworld ends titles with a tag ("Title [Movie]", "Title [OVA]"), which would hurt Jellyfin's
        // matching. A folder name must not end in a dot (Windows/SMB strip it), so drop the truncation "..."
        var titleWithoutTag = Regex.Replace(filmTitle, @"\s*\[[^\]]*\]\s*$", string.Empty);
        var safeTitle = SanitizeAndTruncateTitle(titleWithoutTag).TrimEnd('.', ' ');
        if (safeTitle.Length == 0 || safeTitle == "Unknown")
        {
            return outputPath;
        }

        var name = prefixSeries ? $"{safeSeries} - {safeTitle}" : safeTitle;
        return BuildMoviePath(basePath, name, Path.GetExtension(outputPath));
    }

    /// <summary>
    /// Returns a variant of a "basePath/Title/Title.mkv" path with the given text added to both
    /// the folder and the file name. Used when the plain path already belongs to something else.
    /// </summary>
    public static string AppendToMovieName(string outputPath, string suffix)
    {
        var movieDir = Path.GetDirectoryName(outputPath);
        var basePath = Path.GetDirectoryName(movieDir);
        var name = Path.GetFileNameWithoutExtension(outputPath);

        return basePath == null || Path.GetFileName(movieDir) != name
            ? outputPath
            : BuildMoviePath(basePath, $"{name} - {suffix}", Path.GetExtension(outputPath));
    }

    /// <summary>
    /// Parses a movie-layout file from its path parts relative to the download path:
    /// "Series - Film 01/Series - Film 01.mkv" gives (Series, 1) and "Title/Title.mkv" gives (Title, 0).
    /// The folder and file name must match, so any other file directly in a folder is rejected.
    /// </summary>
    public static bool TryParseMovieFile(string[] parts, out string seriesName, out int episode)
    {
        seriesName = string.Empty;
        episode = 0;

        if (parts.Length != 2
            || !string.Equals(parts[0], Path.GetFileNameWithoutExtension(parts[1]), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var placeholder = FilmPlaceholderName.Match(parts[0]);
        if (placeholder.Success)
        {
            seriesName = placeholder.Groups["series"].Value;
            episode = int.Parse(placeholder.Groups["num"].Value);
            return true;
        }

        seriesName = parts[0];
        return true;
    }

    private static string FilmPlaceholder(string safeSeriesName, int number)
    {
        return $"{safeSeriesName} - Film {number:D2}";
    }

    private static string BuildMoviePath(string basePath, string name, string extension)
    {
        return Path.Combine(basePath, name, name + extension);
    }

    private static string SanitizeAndTruncateTitle(string title)
    {
        var safeTitle = SanitizeFileName(title);
        if (safeTitle.Length > 80)
        {
            // Truncate at word boundary, trim trailing punctuation
            safeTitle = safeTitle[..77].TrimEnd(' ', '-', ',', '.', ';');
            safeTitle += "...";
        }

        return safeTitle;
    }
}
