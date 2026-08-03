using System.Text;
using J0kersMediaServer.Auth;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media.Providers;

/// <summary>
/// Fetches a provider's HLS on the client's behalf and rewrites the
/// playlists so playback keeps working from here.
///
/// Two things make a raw provider URL unusable directly from a page:
///
///   * <b>The session expires.</b> Pluto's stitcher rejects a playlist
///     request whose JWT has lapsed (401), and a live player refetches the
///     media playlist every few seconds — so a URL captured once dies
///     mid-programme. Going through here means the token is re-minted on
///     every playlist fetch and playback simply continues.
///   * <b>CORS.</b> The stitcher answers
///     <c>Access-Control-Allow-Origin: http://pluto.tv</c>, so a browser on
///     this dashboard's origin cannot read those playlists at all.
///
/// Only the playlists go through here — a few KB of text every few seconds.
/// The segments carry their own authorization in the URL and are served
/// <c>Access-Control-Allow-Origin: *</c>, so they are rewritten to absolute
/// URLs and fetched by the player straight from the CDN. The video never
/// touches this process, which is what makes browsing a 400-channel lineup
/// cost nothing until something is actually pinned.
///
/// Sources whose segment hosts are not CORS-open can be relayed in full with
/// <c>relaySegments</c>; that trades the bandwidth for universal playback.
/// </summary>
public sealed class HlsProxy
{
    private readonly HttpClient _http;
    private readonly MediaLink _links;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public HlsProxy(HttpClient http, MediaLink links)
    {
        _http = http;
        _links = links;
    }

    /// <summary>What the proxy fetched, ready to write to a response.</summary>
    public sealed record Result(int Status, string ContentType, byte[] Body);

    /// <summary>
    /// Fetches <paramref name="url"/>. A playlist comes back rewritten so its
    /// children point here; anything else is passed through untouched.
    /// </summary>
    public async Task<Result> FetchAsync(string url, string providerId, string proxyBase, bool relaySegments,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        // the stitcher only answers CORS for its own site; asking as that
        // origin keeps its edge from varying the response on us
        req.Headers.TryAddWithoutValidation("Origin", "http://pluto.tv");
        req.Headers.TryAddWithoutValidation("Referer", "http://pluto.tv/");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn("tv", $"upstream {(int)resp.StatusCode} for {Trim(url)}");
            return new Result((int)resp.StatusCode, contentType, bytes);
        }

        if (!LooksLikePlaylist(url, contentType, bytes))
            return new Result(200, contentType, bytes);

        var text = Encoding.UTF8.GetString(bytes);
        var rewritten = Rewrite(text, new Uri(url), providerId, proxyBase, relaySegments);
        return new Result(200, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(rewritten));
    }

    private static bool LooksLikePlaylist(string url, string contentType, byte[] body)
    {
        if (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)) return true;
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return true;
        // some edges serve playlists as text/plain
        return body.Length > 7 && Encoding.ASCII.GetString(body, 0, 7) == "#EXTM3U";
    }

    // ---- rewriting -------------------------------------------------------

    /// <summary>
    /// Rewrites every URI in a playlist. Playlists and decryption keys are
    /// routed back through the proxy — the first because its host is
    /// CORS-locked and its token expires, the second because it is a few
    /// bytes and its host's CORS policy is not ours to assume. Media
    /// segments are made absolute and left to the player unless the caller
    /// asked for a full relay.
    /// </summary>
    private string Rewrite(string playlist, Uri baseUri, string providerId, string proxyBase, bool relaySegments)
    {
        var sb = new StringBuilder(playlist.Length + 512);

        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Length == 0) { sb.Append('\n'); continue; }

            if (line[0] == '#')
            {
                sb.Append(RewriteTagUris(line, baseUri, providerId, proxyBase, relaySegments)).Append('\n');
                continue;
            }

            var abs = Resolve(baseUri, line);
            var viaProxy = relaySegments || IsPlaylist(abs);
            sb.Append(viaProxy ? Proxied(abs, providerId, proxyBase, relaySegments) : abs).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Rewrites the URI="…" attribute carried by EXT-X-KEY (decryption key),
    /// EXT-X-MEDIA (alternate audio/subtitle playlists) and EXT-X-MAP
    /// (initialisation segment).
    /// </summary>
    private string RewriteTagUris(string line, Uri baseUri, string providerId, string proxyBase, bool relaySegments)
    {
        const string marker = "URI=\"";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return line;

        var open = start + marker.Length;
        var close = line.IndexOf('"', open);
        if (close < 0) return line;

        var original = line[open..close];
        if (original.Length == 0) return line;

        var abs = Resolve(baseUri, original);
        var isKey = line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("#EXT-X-SESSION-KEY", StringComparison.OrdinalIgnoreCase);
        var viaProxy = relaySegments || isKey || IsPlaylist(abs);
        var replacement = viaProxy ? Proxied(abs, providerId, proxyBase, relaySegments) : abs;

        return string.Concat(line.AsSpan(0, open), replacement, line.AsSpan(close));
    }

    private static bool IsPlaylist(string url) =>
        (Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url)
        .EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Makes a playlist reference absolute, carrying down any query
    /// parameters of the parent it doesn't set for itself.
    ///
    /// Pluto's master lists its variants as
    /// <c>1042180/playlist.m3u8?sid=…&amp;deviceType=…</c> — the session
    /// parameters but no <c>jwt</c>, because a player fetching it from the
    /// real site still has the one from the master's own URL in scope.
    /// Fetched on its own the same URL is a 401, so the parent's parameters
    /// have to come down with it. Restricted to the same host, so nothing
    /// leaks a token to a third party, and never overwriting a parameter the
    /// child states itself.
    /// </summary>
    private static string Resolve(Uri baseUri, string reference)
    {
        if (!Uri.TryCreate(baseUri, reference, out var abs)) return reference;
        if (!abs.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)) return abs.ToString();

        var parent = ParseQuery(baseUri.Query);
        if (parent.Count == 0) return abs.ToString();

        var own = ParseQuery(abs.Query);
        var missing = parent.Where(kv => !own.ContainsKey(kv.Key)).ToList();
        if (missing.Count == 0) return abs.ToString();

        var merged = string.Join("&", own.Select(kv => $"{kv.Key}={kv.Value}")
            .Concat(missing.Select(kv => $"{kv.Key}={kv.Value}")));
        return abs.GetLeftPart(UriPartial.Path) + "?" + merged;
    }

    /// <summary>
    /// Query string to pairs, values left exactly as written. Re-encoding
    /// them would corrupt a signed token whose escaping the upstream expects
    /// back byte for byte.
    /// </summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            if (key.Length > 0) map[key] = eq < 0 ? "" : pair[(eq + 1)..];
        }
        return map;
    }

    /// <summary>
    /// A signed link back to this proxy for one upstream URL. The provider
    /// travels with it so the token inside can be renewed on the way out.
    /// </summary>
    private string Proxied(string absolute, string providerId, string proxyBase, bool relaySegments)
    {
        var q = $"{proxyBase}?u={Uri.EscapeDataString(absolute)}" +
                $"&p={Uri.EscapeDataString(providerId)}" +
                $"&s={Uri.EscapeDataString(_links.SignUrl(absolute))}";
        return relaySegments ? q + "&relay=1" : q;
    }

    private static string Trim(string url) => url.Length <= 120 ? url : url[..120] + "…";
}
