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
        "serienstream.to", "www.serienstream.to",
        // NOTE: HiAnime (hianime.to) has been shut down (March 2026) — removed from allowlist
        // "hianime.to", "www.hianime.to",
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
                "Invalid URL. Only https://aniworld.to and https://s.to URLs are accepted.", paramName);
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
