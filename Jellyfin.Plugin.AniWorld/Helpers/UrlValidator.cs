using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AniWorld.Helpers;

/// <summary>
/// Validates URLs to prevent SSRF attacks by ensuring requests only go to allowed streaming sites.
/// </summary>
public static class UrlValidator
{
    private static readonly string[] AllowedHosts =
    {
        "aniworld.to", "www.aniworld.to",
        "s.to", "www.s.to",
        "serienstream.to", "www.serienstream.to",
        "filmo.to", "www.filmo.to",
        "filmpalast.to", "www.filmpalast.to",
        "moflix-stream.xyz", "www.moflix-stream.xyz",
    };

    /// <summary>
    /// MegaKino rotates its domain (megakino.com, megakino21.com, ...). Match the
    /// rotating host family with a pattern instead of a fixed allow-list entry.
    /// </summary>
    private static readonly Regex MegaKinoHostPattern = new(
        @"^megakino\d*\.com$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Validates that a URL belongs to an allowed streaming site (aniworld.to, s.to or filmo.to).
    /// Prevents SSRF by rejecting URLs pointing to internal networks or other domains.
    /// </summary>
    public static bool IsValidUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Validate scheme (HTTPS required, unless custom base URL uses HTTP)
        var customBaseUrl = Plugin.Instance?.Configuration?.StoBaseUrl;
        var allowHttp = false;
        string? customHost = null;

        if (!string.IsNullOrWhiteSpace(customBaseUrl) && Uri.TryCreate(customBaseUrl, UriKind.Absolute, out var customUri))
        {
            customHost = customUri.Host.ToLowerInvariant();
            allowHttp = customUri.Scheme == Uri.UriSchemeHttp;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp))
        {
            return false;
        }

        // Validate hostname against allowlist
        var host = uri.Host.ToLowerInvariant();
        foreach (var allowed in AllowedHosts)
        {
            if (host == allowed)
            {
                return true;
            }
        }

        // Allow the rotating MegaKino domain family
        if (MegaKinoHostPattern.IsMatch(host))
        {
            return true;
        }

        // Also allow the custom s.to base URL host
        if (customHost != null && host == customHost)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates a URL and throws if invalid.
    /// </summary>
    public static void EnsureValidUrl(string url, string paramName = "url")
    {
        if (!IsValidUrl(url))
        {
            throw new ArgumentException(
                "Invalid URL. Only https://aniworld.to, https://s.to, https://filmo.to, https://filmpalast.to, " +
                "https://moflix-stream.xyz and MegaKino URLs are accepted.", paramName);
        }
    }

    /// <summary>
    /// Detects the source site from a URL.
    /// Returns "aniworld", "sto" or "filmo".
    /// </summary>
    public static string DetectSource(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "aniworld";
        }

        // filmo.to: fixed host, checked first
        if (url.Contains("filmo.to", StringComparison.OrdinalIgnoreCase))
        {
            return "filmo";
        }

        // filmpalast.to / moflix-stream.xyz: fixed hosts, checked before the
        // generic fallback so they are not mis-detected as aniworld
        if (url.Contains("filmpalast.to", StringComparison.OrdinalIgnoreCase))
        {
            return "filmpalast";
        }

        if (url.Contains("moflix-stream.xyz", StringComparison.OrdinalIgnoreCase))
        {
            return "moflix";
        }

        // Get custom s.to host for matching
        string? customStoHost = null;
        var customBaseUrl = Plugin.Instance?.Configuration?.StoBaseUrl;
        if (!string.IsNullOrWhiteSpace(customBaseUrl) && Uri.TryCreate(customBaseUrl, UriKind.Absolute, out var customUri))
        {
            customStoHost = customUri.Host.ToLowerInvariant();
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if (host == "s.to" || host == "www.s.to" || host == "serienstream.to" || host == "www.serienstream.to"
                || (customStoHost != null && host == customStoHost))
            {
                return "sto";
            }

            if (MegaKinoHostPattern.IsMatch(host))
            {
                return "megakino";
            }
        }

        // Also check raw string for cases without full URI parsing
        if (url.Contains("s.to/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("serienstream.to/", StringComparison.OrdinalIgnoreCase))
        {
            return "sto";
        }

        return "aniworld";
    }

    /// <summary>
    /// Legacy compatibility: validates aniworld.to URLs only.
    /// </summary>
    public static bool IsValidAniWorldUrl(string url) => IsValidUrl(url);

    /// <summary>
    /// Legacy compatibility.
    /// </summary>
    public static void EnsureValidAniWorldUrl(string url, string paramName = "url") => EnsureValidUrl(url, paramName);
}
