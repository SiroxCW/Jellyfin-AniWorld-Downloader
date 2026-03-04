using System;

namespace Jellyfin.Plugin.AniWorld.Helpers;

/// <summary>
/// Validates URLs to prevent SSRF attacks by ensuring requests only go to aniworld.to.
/// </summary>
public static class UrlValidator
{
    private static readonly string[] AllowedHosts = { "aniworld.to", "www.aniworld.to" };

    /// <summary>
    /// Validates that a URL belongs to aniworld.to.
    /// Prevents SSRF by rejecting URLs pointing to internal networks or other domains.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <returns>True if the URL is a valid aniworld.to URL.</returns>
    public static bool IsValidAniWorldUrl(string url)
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
    /// Validates a URL and throws if invalid, providing a clear error message.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <param name="paramName">The parameter name for the error message.</param>
    /// <exception cref="ArgumentException">Thrown when the URL is not a valid aniworld.to URL.</exception>
    public static void EnsureValidAniWorldUrl(string url, string paramName = "url")
    {
        if (!IsValidAniWorldUrl(url))
        {
            throw new ArgumentException(
                $"Invalid URL. Only https://aniworld.to URLs are accepted.", paramName);
        }
    }
}
