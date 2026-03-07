using System;

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
        "hianime.to", "www.hianime.to",
    };

    /// <summary>
    /// Validates that a URL belongs to an allowed streaming site (aniworld.to or s.to).
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

        // Only allow HTTPS
        if (uri.Scheme != Uri.UriSchemeHttps)
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
                "Invalid URL. Only https://aniworld.to, https://s.to, and https://hianime.to URLs are accepted.", paramName);
        }
    }

    /// <summary>
    /// Detects the source site from a URL.
    /// Returns "aniworld", "sto", or "hianime".
    /// </summary>
    public static string DetectSource(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "aniworld";
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if (host == "s.to" || host == "www.s.to")
            {
                return "sto";
            }

            if (host == "hianime.to" || host == "www.hianime.to")
            {
                return "hianime";
            }
        }

        // Also check raw string for cases without full URI parsing
        if (url.Contains("hianime.to/", StringComparison.OrdinalIgnoreCase))
        {
            return "hianime";
        }

        if (url.Contains("s.to/", StringComparison.OrdinalIgnoreCase))
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
