using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Coordinates challenge solving for site fetchers. Owns a per-host lock so
/// only one solve runs at a time per host, injects FlareSolverr's cookies into
/// the shared <see cref="CookieContainer"/>, and remembers the User-Agent that
/// was used so callers can pin follow-up requests to the same UA.
/// </summary>
public class CaptchaSessionService
{
    private readonly FlareSolverrClient _flareSolverr;
    private readonly ILogger<CaptchaSessionService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _userAgentByHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSolvedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="CaptchaSessionService"/> class.
    /// </summary>
    public CaptchaSessionService(FlareSolverrClient flareSolverr, ILogger<CaptchaSessionService> logger)
    {
        _flareSolverr = flareSolverr;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether FlareSolverr is configured.
    /// </summary>
    public bool IsConfigured => _flareSolverr.IsConfigured;

    /// <summary>
    /// Gets the User-Agent that successfully solved the latest challenge for
    /// the host of <paramref name="url"/>, or <c>null</c> if none.
    /// </summary>
    public string? GetUserAgentFor(string url)
    {
        var host = TryGetHost(url);
        if (host == null)
        {
            return null;
        }

        return _userAgentByHost.TryGetValue(host, out var ua) ? ua : null;
    }

    /// <summary>
    /// Solves a challenge for <paramref name="url"/> via FlareSolverr.
    /// Cookies are injected into the shared cookie container so subsequent
    /// native requests pass DDoS-Guard without re-solving.
    /// Returns the solved HTML, or <c>null</c> if the solve failed or
    /// FlareSolverr is not configured.
    /// </summary>
    public async Task<string?> SolveAsync(string url, CancellationToken cancellationToken)
    {
        if (!_flareSolverr.IsConfigured)
        {
            return null;
        }

        var host = TryGetHost(url);
        if (host == null)
        {
            return null;
        }

        // Per-host gate: when many parallel downloads hit a fresh challenge
        // simultaneously we don't want N concurrent FlareSolverr calls. The
        // first one solves and primes the cookie jar; the rest wait briefly
        // and then retry with cookies already in place.
        var gate = _hostLocks.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // If another caller solved for this host within the last few
            // seconds, skip this round-trip — its cookies are already in our
            // CookieContainer and the caller can just retry.
            if (_lastSolvedAt.TryGetValue(host, out var last)
                && (DateTimeOffset.UtcNow - last) < TimeSpan.FromSeconds(5))
            {
                _logger.LogDebug("Skipping FlareSolverr call for {Host} (recently solved {Age}s ago)", host, (DateTimeOffset.UtcNow - last).TotalSeconds);
                return string.Empty;
            }

            var existingUa = _userAgentByHost.TryGetValue(host, out var ua) ? ua : null;
            var result = await _flareSolverr.SolveGetAsync(url, existingUa, cancellationToken).ConfigureAwait(false);
            if (result == null)
            {
                return null;
            }

            InjectCookies(result);

            if (!string.IsNullOrWhiteSpace(result.UserAgent))
            {
                _userAgentByHost[host] = result.UserAgent;
            }

            _lastSolvedAt[host] = DateTimeOffset.UtcNow;
            return result.Html;
        }
        finally
        {
            gate.Release();
        }
    }

    private void InjectCookies(FlareSolverrResult result)
    {
        var jar = PluginServiceRegistrator.Cookies;
        var injected = 0;

        foreach (var c in result.Cookies)
        {
            try
            {
                var domain = c.Domain.TrimStart('.');
                if (string.IsNullOrEmpty(domain))
                {
                    continue;
                }

                var scheme = c.Secure ? "https" : "http";
                var uri = new Uri($"{scheme}://{domain}{(string.IsNullOrEmpty(c.Path) ? "/" : c.Path)}");

                var cookie = new Cookie(c.Name, c.Value, c.Path, domain)
                {
                    Secure = c.Secure,
                    HttpOnly = c.HttpOnly,
                };

                jar.Add(uri, cookie);
                injected++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to inject cookie {Name} for domain {Domain}", c.Name, c.Domain);
            }
        }

        _logger.LogDebug("Injected {Count}/{Total} cookies from FlareSolverr", injected, result.Cookies.Count);
    }

    private static string? TryGetHost(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return null;
    }
}
