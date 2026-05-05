using System;

namespace Jellyfin.Plugin.AniWorld.Helpers;

/// <summary>
/// Detects DDoS-Guard / Cloudflare interstitial challenge pages by looking for
/// well-known markers in the response HTML.
/// </summary>
public static class ChallengeDetector
{
    private static readonly string[] Markers =
    {
        // DDoS-Guard
        "ddos-guard.net",
        "DDoS-Guard",
        "__ddg1_",
        "__ddg2_",
        "/.well-known/ddos-guard",

        // Cloudflare interstitials
        "cf-mitigated",
        "Just a moment...",
        "challenge-platform",
        "/cdn-cgi/challenge-platform",
        "cf_chl_opt",
        "Checking your browser before accessing",
    };

    /// <summary>
    /// Returns true when the response body looks like an interstitial challenge
    /// page rather than the actual content.
    /// </summary>
    public static bool IsChallenge(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return false;
        }

        foreach (var marker in Markers)
        {
            if (html.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
