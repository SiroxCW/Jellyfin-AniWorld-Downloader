using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Thin wrapper around the FlareSolverr v1 HTTP API.
/// Posts <c>{cmd: "request.get", url, maxTimeout}</c> and returns the solved
/// HTML, the user-agent that was used, and the cookies the browser collected.
/// </summary>
public class FlareSolverrClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FlareSolverrClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FlareSolverrClient"/> class.
    /// </summary>
    public FlareSolverrClient(IHttpClientFactory httpClientFactory, ILogger<FlareSolverrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether FlareSolverr is configured.
    /// </summary>
    public bool IsConfigured
    {
        get
        {
            var url = Plugin.Instance?.Configuration?.FlareSolverrUrl;
            return !string.IsNullOrWhiteSpace(url);
        }
    }

    /// <summary>
    /// Sends a GET request through FlareSolverr.
    /// Returns null if FlareSolverr is not configured or the request failed.
    /// </summary>
    public async Task<FlareSolverrResult?> SolveGetAsync(
        string url,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var endpoint = Plugin.Instance?.Configuration?.FlareSolverrUrl;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var timeoutSec = Plugin.Instance?.Configuration?.FlareSolverrTimeoutSeconds ?? 60;
        var maxTimeoutMs = Math.Max(15, timeoutSec) * 1000;

        var payload = new Dictionary<string, object?>
        {
            ["cmd"] = "request.get",
            ["url"] = url,
            ["maxTimeout"] = maxTimeoutMs,
        };

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            payload["userAgent"] = userAgent;
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient("FlareSolverr");
            _logger.LogInformation("Calling FlareSolverr at {Endpoint} for {Url}", endpoint, url);

            using var response = await httpClient.PostAsJsonAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("FlareSolverr returned HTTP {Status}: {Body}", (int)response.StatusCode, Truncate(body, 500));
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "(no message)";
                _logger.LogWarning("FlareSolverr non-ok status '{Status}': {Message}", status, msg);
                return null;
            }

            if (!root.TryGetProperty("solution", out var solution))
            {
                _logger.LogWarning("FlareSolverr response missing 'solution' field");
                return null;
            }

            var responseHtml = solution.TryGetProperty("response", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            var solvedUa = solution.TryGetProperty("userAgent", out var ua) ? ua.GetString() ?? string.Empty : string.Empty;
            var finalUrl = solution.TryGetProperty("url", out var fu) ? fu.GetString() ?? url : url;
            var solvedStatus = solution.TryGetProperty("status", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt32() : 0;

            var cookies = new List<FlareSolverrCookie>();
            if (solution.TryGetProperty("cookies", out var cookiesElem) && cookiesElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cookiesElem.EnumerateArray())
                {
                    var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var value = c.TryGetProperty("value", out var v) ? v.GetString() : null;
                    var domain = c.TryGetProperty("domain", out var d) ? d.GetString() : null;
                    var path = c.TryGetProperty("path", out var p) ? p.GetString() : "/";
                    var secure = c.TryGetProperty("secure", out var sc) && sc.ValueKind == JsonValueKind.True;
                    var httpOnly = c.TryGetProperty("httpOnly", out var ho) && ho.ValueKind == JsonValueKind.True;

                    if (string.IsNullOrEmpty(name) || value == null || string.IsNullOrEmpty(domain))
                    {
                        continue;
                    }

                    cookies.Add(new FlareSolverrCookie
                    {
                        Name = name,
                        Value = value,
                        Domain = domain,
                        Path = string.IsNullOrEmpty(path) ? "/" : path,
                        Secure = secure,
                        HttpOnly = httpOnly,
                    });
                }
            }

            _logger.LogInformation(
                "FlareSolverr solved {Url} (status {Status}, {CookieCount} cookies, UA={Ua})",
                finalUrl, solvedStatus, cookies.Count, Truncate(solvedUa, 60));

            return new FlareSolverrResult
            {
                Html = responseHtml,
                UserAgent = solvedUa,
                FinalUrl = finalUrl,
                Status = solvedStatus,
                Cookies = cookies,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FlareSolverr request failed for {Url}", url);
            return null;
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
        {
            return s ?? string.Empty;
        }

        return s[..max] + "…";
    }
}

/// <summary>
/// Result of a successful FlareSolverr call.
/// </summary>
public class FlareSolverrResult
{
    /// <summary>Gets the HTML returned after the challenge was solved.</summary>
    public string Html { get; init; } = string.Empty;

    /// <summary>Gets the User-Agent string FlareSolverr used (must be reused on follow-up requests).</summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>Gets the final URL after redirects.</summary>
    public string FinalUrl { get; init; } = string.Empty;

    /// <summary>Gets the HTTP status code reported by FlareSolverr.</summary>
    public int Status { get; init; }

    /// <summary>Gets the cookies collected by the browser.</summary>
    public List<FlareSolverrCookie> Cookies { get; init; } = new();
}

/// <summary>
/// A cookie returned by FlareSolverr.
/// </summary>
public class FlareSolverrCookie
{
    /// <summary>Gets the cookie name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the cookie value.</summary>
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    /// <summary>Gets the cookie domain.</summary>
    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    /// <summary>Gets the cookie path.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = "/";

    /// <summary>Gets a value indicating whether the cookie is secure.</summary>
    [JsonPropertyName("secure")]
    public bool Secure { get; init; }

    /// <summary>Gets a value indicating whether the cookie is HttpOnly.</summary>
    [JsonPropertyName("httpOnly")]
    public bool HttpOnly { get; init; }
}
