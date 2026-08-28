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
    /// Sends the request, following redirects ourselves so every hop is
    /// checked.
    ///
    /// The guard above vets the URL that was asked for; HttpClient follows a
    /// redirect on its own and never asks again. A playlist that points at a
    /// public address which answers 302 to 127.0.0.1:9090 or to the cloud
    /// metadata address would therefore be fetched and handed straight back -
    /// exactly the thing refusing private addresses is meant to prevent.
    /// Following the chain here means each destination faces the same test as
    /// the first, and a redirect somewhere private is refused rather than
    /// obeyed.
    /// </summary>
    private async Task<HttpResponseMessage> SendFollowingAsync(HttpRequestMessage request, CancellationToken ct)
    {
        const int maxHops = 5;
        var current = request;
        for (var hop = 0; ; hop++)
        {
            var response = await _http.SendAsync(current, HttpCompletionOption.ResponseContentRead, ct)
                                      .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var location = response.Headers.Location;
            if (status is not (301 or 302 or 303 or 307 or 308) || location is null) return response;
            if (hop >= maxHops)
            {
                Log.Warn("tv", $"too many redirects from {Trim(current.RequestUri?.ToString() ?? "")}");
                return response;
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (!Services.PrivateNetwork.MayFetch(next.ToString(), out var why))
            {
                Log.Warn("tv", $"refused to follow a redirect to {Trim(next.ToString())}: {why}");
                response.Dispose();
                return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("refused: that address is not fetchable from here"),
                };
            }

            var follow = new HttpRequestMessage(HttpMethod.Get, next);
            foreach (var h in current.Headers) follow.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (!ReferenceEquals(current, request)) current.Dispose();
            response.Dispose();
            current = follow;
        }
    }

    /// <summary>
    /// Fetches <paramref name="url"/>. A playlist comes back rewritten so its
    /// children point here; anything else is passed through untouched.
    /// </summary>
    /// <param name="channelTag">
    /// Which channel this playlist belongs to, carried down into every URL
    /// the rewrite produces. A player fetching a variant playlist or a
    /// relayed segment sends no hint of what it is watching — only the entry
    /// request names the channel — so without this the sessions table could
    /// say somebody is watching, but not what. It is a label only: nothing
    /// is authorized by it, and the upstream URL is still signature-checked.
    /// </param>
    public async Task<Result> FetchAsync(string url, string providerId, string proxyBase, bool relaySegments,
        CancellationToken ct, string? channelTag = null)
    {
        // Where this URL came from is a third party's playlist, and the
        // server is about to fetch it from inside the network and hand the
        // answer back. A playlist naming 127.0.0.1:9090 or 169.254.169.254
        // would make this a window onto everything the server can reach that
        // the caller cannot — so nothing private is fetched, whatever it says.
        if (!Services.PrivateNetwork.MayFetch(url, out var why))
        {
            Log.Warn("tv", $"refused to fetch {Trim(url)}: {why}");
            return new Result(403, "text/plain",
                Encoding.UTF8.GetBytes("refused: that address is not fetchable from here"));
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        // the stitcher only answers CORS for its own site; asking as that
        // origin keeps its edge from varying the response on us
        req.Headers.TryAddWithoutValidation("Origin", "http://pluto.tv");
        req.Headers.TryAddWithoutValidation("Referer", "http://pluto.tv/");

        using var resp = await SendFollowingAsync(req, ct);
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
        var rewritten = Rewrite(text, new Uri(url), providerId, proxyBase, relaySegments, channelTag);
        return new Result(200, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(rewritten));
    }

    /// <summary>
    /// Whether this response is a playlist, decided by what it contains
    /// rather than by what it is labelled.
    ///
    /// RFC 8216 §4 requires a playlist to begin with <c>#EXTM3U</c>, so the
    /// tag is the whole test — and it has to be, because the labels lie in a
    /// way that corrupts data. Pluto serves the AES-128 key for its ts_aes
    /// channels with <c>Content-Type: application/vnd.apple.mpegurl</c>.
    /// Trusting that sent a 16-byte binary key through the URL rewriter,
    /// which read those bytes as a relative URI and resolved them into an
    /// absolute one: 153 bytes of text where a key should be. ffmpeg then
    /// decrypts every segment with rubbish and gives up with "Invalid data
    /// found when processing input", which looks like a broken channel and
    /// is a broken proxy.
    ///
    /// Content type and extension are now only hints, used to decide how
    /// hard to look for the tag — never on their own.
    /// </summary>
    private static bool LooksLikePlaylist(string url, string contentType, byte[] body)
    {
        // A UTF-8 BOM before #EXTM3U is legal and does appear in the wild.
        var start = body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? 3 : 0;
        // leading whitespace is not legal, but tolerating it costs nothing
        while (start < body.Length && (body[start] == (byte)'\n' || body[start] == (byte)'\r'
               || body[start] == (byte)' ' || body[start] == (byte)'\t')) start++;

        return body.Length - start >= 7
               && Encoding.ASCII.GetString(body, start, 7) == "#EXTM3U";
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
    private string Rewrite(string playlist, Uri baseUri, string providerId, string proxyBase, bool relaySegments,
        string? channelTag)
    {
        var sb = new StringBuilder(playlist.Length + 512);

        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Length == 0) { sb.Append('\n'); continue; }

            if (line[0] == '#')
            {
                sb.Append(RewriteTagUris(line, baseUri, providerId, proxyBase, relaySegments, channelTag)).Append('\n');
                continue;
            }

            var abs = Resolve(baseUri, line);
            var viaProxy = relaySegments || IsPlaylist(abs);
            sb.Append(viaProxy ? Proxied(abs, providerId, proxyBase, relaySegments, channelTag) : abs).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Rewrites the URI="…" attribute carried by EXT-X-KEY (decryption key),
    /// EXT-X-MEDIA (alternate audio/subtitle playlists) and EXT-X-MAP
    /// (initialisation segment).
    /// </summary>
    private string RewriteTagUris(string line, Uri baseUri, string providerId, string proxyBase, bool relaySegments,
        string? channelTag)
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
        var replacement = viaProxy ? Proxied(abs, providerId, proxyBase, relaySegments, channelTag) : abs;

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
    private string Proxied(string absolute, string providerId, string proxyBase, bool relaySegments,
        string? channelTag)
    {
        var q = $"{proxyBase}?u={Uri.EscapeDataString(absolute)}" +
                $"&p={Uri.EscapeDataString(providerId)}" +
                $"&s={Uri.EscapeDataString(_links.SignUrl(absolute))}";
        if (relaySegments) q += "&relay=1";
        if (!string.IsNullOrEmpty(channelTag)) q += $"&c={Uri.EscapeDataString(channelTag)}";
        return q;
    }

    private static string Trim(string url) => url.Length <= 120 ? url : url[..120] + "…";
}
