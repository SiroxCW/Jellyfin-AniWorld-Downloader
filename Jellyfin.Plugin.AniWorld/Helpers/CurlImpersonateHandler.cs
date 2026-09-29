using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Helpers;

/// <summary>
/// An <see cref="HttpMessageHandler"/> backed by curl-impersonate, so the TLS +
/// HTTP/2 fingerprint looks like a real Chrome browser. Needed for sites behind
/// Cloudflare that fingerprint non-browser clients (e.g. Moflix's JSON API).
///
/// The native library (libcurl-impersonate-chrome) is looked up next to the
/// plugin assembly. On platforms or installations where it is unavailable the
/// handler reports that it cannot be created and the caller falls back to the
/// regular .NET handler.
/// </summary>
public sealed class CurlImpersonateHandler : HttpMessageHandler
{
    // The name used in the DllImport attributes; the resolver below maps it to
    // the exact file we load, so the OS search path is not involved.
    private const string LibName = "libcurl-impersonate-chrome.so";

    // The browser profile to impersonate (the highest shipped by this lib build).
    private const string TargetProfile = "chrome116";

    // curl error
    private const int CURLE_OK = 0;

    // curl options (curl 8.1.x: value = type offset + number)
    private const int CURLOPT_URL = 10002;
    private const int CURLOPT_PROXY = 10004;
    private const int CURLOPT_NOPROGRESS = 43;
    private const int CURLOPT_SSL_VERIFYPEER = 64;
    private const int CURLOPT_SSL_VERIFYHOST = 81;
    private const int CURLOPT_POSTFIELDS = 10015;
    private const int CURLOPT_POSTFIELDSIZE = 60;
    private const int CURLOPT_ACCEPT_ENCODING = 10102;
    private const int CURLOPT_TIMEOUT_MS = 155;
    private const int CURLOPT_HTTPHEADER = 10023;
    private const int CURLOPT_COOKIE = 10022;
    private const int CURLOPT_WRITEDATA = 10001;
    private const int CURLOPT_WRITEFUNCTION = 20011;
    private const int CURLOPT_HEADERDATA = 10029;
    private const int CURLOPT_HEADERFUNCTION = 20079;

    // curl infos (value = type offset + number, per curl 8.1.x)
    private const int CURLINFO_RESPONSE_CODE = 2097154; // CURLINFO_LONG(0x200000)+2
    private const int CURLINFO_CONTENT_TYPE = 1048594;  // CURLINFO_STRING(0x100000)+18

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int curl_global_init(int flags);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr curl_easy_init();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void curl_easy_cleanup(IntPtr curl);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int curl_easy_impersonate(IntPtr curl, string target, int defaultHeaders);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "curl_easy_setopt")]
    private static extern int curl_easy_setopt_str(IntPtr curl, int opt, string value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "curl_easy_setopt")]
    private static extern int curl_easy_setopt_long(IntPtr curl, int opt, long value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "curl_easy_setopt")]
    private static extern int curl_easy_setopt_ptr(IntPtr curl, int opt, IntPtr value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int curl_easy_perform(IntPtr curl);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "curl_easy_getinfo")]
    private static extern int curl_easy_getinfo_long(IntPtr curl, int info, out long value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "curl_easy_getinfo")]
    private static extern int curl_easy_getinfo_str(IntPtr curl, int info, out IntPtr value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr curl_easy_strerror(int code);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr curl_slist_append(IntPtr list, string str);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void curl_slist_free_all(IntPtr list);

    private delegate ulong WriteCb(IntPtr content, ulong size, ulong nmemb, IntPtr userdata);
    private delegate ulong HeaderCb(IntPtr content, ulong size, ulong nmemb, IntPtr userdata);

    private static readonly object InitLock = new();
    private static bool _initialized;
    private static IntPtr _libraryHandle = IntPtr.Zero;

    private readonly string _proxyUrl;
    private readonly int _timeoutMs;
    private readonly WriteCb _writeDelegate = WriteCallback;
    private readonly HeaderCb _headerDelegate = HeaderCallback;

    // Shared across requests to this handler so the site session (cookies) is
    // kept between the home-page scrape and the API calls that need it.
    private readonly ConcurrentDictionary<string, string> _cookieJar = new(StringComparer.OrdinalIgnoreCase);

    // Per-request scratch, kept off the (shared) handler instance so concurrent
    // requests do not overwrite each other's state.
    private sealed class RequestContext
    {
        public required MemoryStream Body { get; init; }
        public required StringBuilder RawHeaders { get; init; }
        public byte[] CopyBuf { get; } = new byte[8192];
    }

    private CurlImpersonateHandler(string libraryPath, string? proxyUrl, int timeoutMs)
    {
        lock (InitLock)
        {
            if (!_initialized)
            {
                // Map the DllImport name to the exact file we load, so the loader
                // does not need to find it through the OS search path.
                NativeLibrary.SetDllImportResolver(
                    typeof(CurlImpersonateHandler).Assembly,
                    (name, assembly, searchPath) =>
                    {
                        if (string.Equals(name, LibName, StringComparison.Ordinal))
                        {
                            if (_libraryHandle != IntPtr.Zero)
                            {
                                return _libraryHandle;
                            }

                            throw new DllNotFoundException($"native library not loaded: {LibName}");
                        }

                        return IntPtr.Zero; // fall through to default resolution
                    });
                _libraryHandle = NativeLibrary.Load(libraryPath);
                curl_global_init(0); // CURL_GLOBAL_DEFAULT
                _initialized = true;
            }
        }

        _proxyUrl = proxyUrl ?? string.Empty;
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Tries to create a handler backed by the bundled curl-impersonate library.
    /// </summary>
    /// <param name="proxyUrl">Optional proxy to route through.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>A handler, or <c>null</c> if the platform is unsupported or the
    /// native library is missing or fails to load (caller should fall back).</returns>
    public static CurlImpersonateHandler? TryCreate(string? proxyUrl, ILogger? logger = null)
    {
        if (!IsSupportedPlatform())
        {
            return null;
        }

        var libraryPath = FindLibrary();
        if (libraryPath == null)
        {
            logger?.LogDebug("curl-impersonate native library not found; falling back to the regular HTTP handler");
            return null;
        }

        try
        {
            var handler = new CurlImpersonateHandler(libraryPath, proxyUrl, timeoutMs: 45_000);
            logger?.LogInformation("Using curl-impersonate ({Profile}) via {Lib} for browser-fingerprinted sites", TargetProfile, libraryPath);
            return handler;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load curl-impersonate library {Lib}; falling back to the regular HTTP handler", libraryPath);
            return null;
        }
    }

    private static bool IsSupportedPlatform()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        var arch = RuntimeInformation.OSArchitecture;
        return arch is Architecture.X64 or Architecture.Arm64;
    }

    private static string? FindLibrary()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (assemblyDir == null)
        {
            return null;
        }

        var searchDirs = new[]
        {
            Path.Combine(assemblyDir, "native"),
            assemblyDir,
        };
        var candidateNames = new[]
        {
            $"libcurl-impersonate-chrome-{arch}.so",
            LibName,
        };

        foreach (var dir in searchDirs)
        {
            foreach (var name in candidateNames)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return await Task.Run(() => SendSync(request), cancellationToken).ConfigureAwait(false);
    }

    private HttpResponseMessage SendSync(HttpRequestMessage request)
    {
        var ctx = new RequestContext
        {
            Body = new MemoryStream(),
            RawHeaders = new StringBuilder(),
        };

        var curl = curl_easy_init();
        if (curl == IntPtr.Zero)
        {
            throw new HttpRequestException("curl_easy_init failed");
        }

        var handle = GCHandle.Alloc(ctx, GCHandleType.Normal);
        var list = IntPtr.Zero; // extra-headers slist; freed in the finally after perform
        try
        {
            var userPtr = (IntPtr)handle;

            Check(curl_easy_impersonate(curl, TargetProfile, 1), "curl_easy_impersonate");

            var urlStr = request.RequestUri!.AbsoluteUri;
            Check(curl_easy_setopt_str(curl, CURLOPT_URL, urlStr), "setopt URL");
            Check(curl_easy_setopt_long(curl, CURLOPT_TIMEOUT_MS, _timeoutMs), "setopt TIMEOUT");
            Check(curl_easy_setopt_long(curl, CURLOPT_NOPROGRESS, 1), "setopt NOPROGRESS");
            Check(curl_easy_setopt_long(curl, CURLOPT_SSL_VERIFYPEER, 1), "setopt SSL_VERIFYPEER");
            Check(curl_easy_setopt_long(curl, CURLOPT_SSL_VERIFYHOST, 2), "setopt SSL_VERIFYHOST");
            Check(curl_easy_setopt_str(curl, CURLOPT_ACCEPT_ENCODING, "gzip, deflate, br"), "setopt ACCEPT_ENCODING");
            if (_proxyUrl.Length > 0)
            {
                Check(curl_easy_setopt_str(curl, CURLOPT_PROXY, _proxyUrl), "setopt PROXY");
            }
            Check(curl_easy_setopt_ptr(curl, CURLOPT_WRITEFUNCTION, Marshal.GetFunctionPointerForDelegate(_writeDelegate)), "setopt WRITEFUNCTION");
            Check(curl_easy_setopt_ptr(curl, CURLOPT_WRITEDATA, userPtr), "setopt WRITEDATA");
            Check(curl_easy_setopt_ptr(curl, CURLOPT_HEADERFUNCTION, Marshal.GetFunctionPointerForDelegate(_headerDelegate)), "setopt HEADERFUNCTION");
            Check(curl_easy_setopt_ptr(curl, CURLOPT_HEADERDATA, userPtr), "setopt HEADERDATA");

            // Extra headers. Browser headers (User-Agent, sec-ch-ua, ...) come from
            // the impersonation's built-in header list, so we only add what the
            // caller explicitly set (e.g. Accept, X-Requested-With, Referer).
            //
            // The slist must stay alive until AFTER curl_easy_perform: this lib
            // reads it during the transfer, so freeing it here (before perform)
            // is a use-after-free that segfaults the whole process. It is freed
            // in the outer finally below.
            {
                // The impersonation owns the browser-identity headers. In
                // particular it sends its own User-Agent (Chrome 116) that
                // matches the TLS fingerprint; if we also forward the
                // caller's User-Agent (the plugin's default Chrome 131), the
                // mismatch trips Cloudflare into a 403 challenge on the API
                // endpoints. So we drop the caller's User-Agent and let the
                // impersonation's win. Other headers (Accept, X-Requested-With,
                // Referer, X-XSRF-TOKEN) are request-specific and kept.
                foreach (var (name, values) in request.Headers)
                {
                    if (string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var v in values)
                    {
                        list = curl_slist_append(list, name + ": " + v);
                    }
                }
                if (request.Content != null)
                {
                    foreach (var (name, values) in request.Content.Headers)
                    {
                        if (string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        foreach (var v in values)
                        {
                            list = curl_slist_append(list, name + ": " + v);
                        }
                    }
                }
                if (list != IntPtr.Zero)
                {
                    Check(curl_easy_setopt_ptr(curl, CURLOPT_HTTPHEADER, list), "setopt HTTPHEADER");
                }

                // Cookies accumulated from earlier responses (session + XSRF).
                var cookie = string.Join("; ", CookiePairs());
                if (cookie.Length > 0)
                {
                    Check(curl_easy_setopt_str(curl, CURLOPT_COOKIE, cookie), "setopt COOKIE");
                }

                // Request body.
                if (request.Content != null &&
                    request.Method != HttpMethod.Get && request.Method != HttpMethod.Head &&
                    request.Method != HttpMethod.Delete && request.Method != HttpMethod.Options)
                {
                    var bodyBytes = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    // An empty POST still needs a non-null pointer with size 0.
                    var bodyForPin = bodyBytes.Length > 0 ? bodyBytes : new byte[1];
                    var bodyGc = GCHandle.Alloc(bodyForPin, GCHandleType.Pinned);
                    try
                    {
                        Check(curl_easy_setopt_ptr(curl, CURLOPT_POSTFIELDS, bodyGc.AddrOfPinnedObject()), "setopt POSTFIELDS");
                        Check(curl_easy_setopt_long(curl, CURLOPT_POSTFIELDSIZE, bodyBytes.Length), "setopt POSTFIELDSIZE");
                    }
                    finally
                    {
                        bodyGc.Free();
                    }
                }
            }

            int res = curl_easy_perform(curl);
            if (res != CURLE_OK)
            {
                var msg = Marshal.PtrToStringAnsi(curl_easy_strerror(res)) ?? "unknown curl error";
                throw new HttpRequestException($"curl error {res}: {msg}");
            }

            long status = 0;
            curl_easy_getinfo_long(curl, CURLINFO_RESPONSE_CODE, out status);
            string contentType = string.Empty;
            if (curl_easy_getinfo_str(curl, CURLINFO_CONTENT_TYPE, out var ctPtr) == CURLE_OK && ctPtr != IntPtr.Zero)
            {
                contentType = Marshal.PtrToStringUTF8(ctPtr) ?? string.Empty;
            }

            // Parse response headers and collect Set-Cookie values for the jar.
            var responseHeaders = new StringBuilder();
            foreach (var rawLine in ctx.RawHeaders.ToString().Split("\r\n"))
            {
                var line = rawLine.TrimEnd('\n');
                if (line.Length == 0 || line.StartsWith("HTTP/", StringComparison.Ordinal))
                {
                    continue;
                }

                var idx = line.IndexOf(':');
                if (idx <= 0)
                {
                    continue;
                }

                var name = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    StoreSetCookie(value);
                }
                else
                {
                    responseHeaders.AppendLine(line);
                }
            }

            var response = new HttpResponseMessage
            {
                StatusCode = (HttpStatusCode)status,
                RequestMessage = request,
                Content = new ByteArrayContent(ctx.Body.ToArray()),
            };
            foreach (var line in responseHeaders.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var l = line.TrimEnd('\r');
                var i = l.IndexOf(':');
                if (i <= 0)
                {
                    continue;
                }

                var n = l[..i].Trim();
                var v = l[(i + 1)..].Trim();
                if (n.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    response.Headers.TryAddWithoutValidation(n, v);
                }
                catch
                {
                    // ignore headers the .NET parser rejects
                }
            }
            if (contentType.Length > 0)
            {
                response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            return response;
        }
        finally
        {
            // Free the extra-headers slist only now, after curl_easy_perform has
            // finished reading it (freeing it earlier segfaults the process).
            if (list != IntPtr.Zero)
            {
                curl_slist_free_all(list);
            }

            curl_easy_cleanup(curl);
            handle.Free();
        }
    }

    private void StoreSetCookie(string value)
    {
        var part = value.Split(';')[0];
        var eq = part.IndexOf('=');
        if (eq > 0)
        {
            _cookieJar[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
    }

    private IEnumerable<string> CookiePairs()
    {
        foreach (var (k, v) in _cookieJar)
        {
            yield return k + "=" + v;
        }
    }

    private void Check(int rc, string what)
    {
        if (rc != CURLE_OK)
        {
            var msg = Marshal.PtrToStringAnsi(curl_easy_strerror(rc)) ?? "unknown curl error";
            throw new HttpRequestException($"{what} failed: {msg} (code {rc})");
        }
    }

    private static ulong WriteCallback(IntPtr content, ulong size, ulong nmemb, IntPtr userdata)
    {
        ulong total = size * nmemb;
        var ctx = (RequestContext)GCHandle.FromIntPtr(userdata).Target!;
        long offset = 0;
        long remaining = (long)total;
        while (remaining > 0)
        {
            int n = (int)Math.Min(remaining, ctx.CopyBuf.Length);
            Marshal.Copy(content + (int)offset, ctx.CopyBuf, 0, n);
            ctx.Body.Write(ctx.CopyBuf, 0, n);
            offset += n;
            remaining -= n;
        }

        return total;
    }

    private static ulong HeaderCallback(IntPtr content, ulong size, ulong nmemb, IntPtr userdata)
    {
        ulong total = size * nmemb;
        var ctx = (RequestContext)GCHandle.FromIntPtr(userdata).Target!;
        var chunk = new byte[(int)total];
        Marshal.Copy(content, chunk, 0, (int)total);
        ctx.RawHeaders.Append(Encoding.UTF8.GetString(chunk));
        return total;
    }
}
