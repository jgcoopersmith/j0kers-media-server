using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media.Providers;

/// <summary>
/// Pluto TV's free linear lineup (~400 channels, no account).
///
/// Three calls, in order:
///   1. <c>boot.pluto.tv/v4/start</c> — anonymous session. Returns a JWT,
///      the stitcher host to use, and the query string ("stitcherParams")
///      that every stream URL has to carry.
///   2. <c>service-channels…/v2/guide/channels</c> — the lineup, each entry
///      carrying the stitcher path for its master playlist.
///   3. the stitcher host + that path + params + jwt — a plain multi-bitrate
///      HLS master playlist.
///
/// The streams are ordinary HLS. Segments carry <c>EXT-X-KEY:METHOD=AES-128</c>
/// with the key served openly over HTTPS alongside them — that is RFC 8216
/// transport encryption, which ffmpeg and hls.js decrypt unaided, not DRM.
/// There is no licence server involved and nothing here circumvents one.
/// Ads are stitched into the stream by Pluto and are left exactly as sent.
///
/// The session expires (boot says how soon in <c>refreshInSec</c>), which is
/// why <see cref="ResolveAsync"/> re-mints the URL per play rather than
/// storing one.
/// </summary>
public sealed class PlutoTvProvider : IChannelProvider, IDisposable
{
    public string Id => "pluto";
    public string Name => "Pluto TV";
    public bool Enabled => true;

    private const string BootUrl = "https://boot.pluto.tv/v4/start";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly HttpClient _http;

    private sealed record Session(string Token, string StitcherHost, string Params, DateTime ExpiresUtc,
                                  string DeviceId);

    /// <summary>
    /// One session per channel, not one per server.
    ///
    /// Pluto's boot call hands back a <c>sid</c>, a <c>sessionID</c> and a
    /// <c>deviceId</c>, and every stream URL carries them. Sharing one set
    /// across several channels tells the stitcher that a single device is
    /// watching all of them at once, and it stitches for a session, not for
    /// a request: the ad breaks and the playback position are session state.
    /// Three players pulling on one session move that state under each
    /// other, which is why a second and third channel stutter while the
    /// first is fine, and why content restarts instead of running through.
    ///
    /// Keyed by channel, so each channel that is actually being watched
    /// presents as its own device. Nothing is minted for channels nobody
    /// opened, and the guide fetch has a key of its own — it is not a
    /// stream.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Session> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The device identity each key keeps across renewals. Pluto keys a
    /// session to the client id it was booted with, so re-minting one with a
    /// fresh id every few hours would look like a new device appearing
    /// mid-programme; holding it steady per channel is what makes each
    /// channel one consistent viewer rather than a stream of strangers.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _deviceIds =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>deviceId → session key, so a URL can find the session that minted it.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _byDevice =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The key used for calls that are not a stream — the guide and the categories.</summary>
    private const string GuideKey = "(guide)";
    private IReadOnlyList<ProviderChannel> _lineup = Array.Empty<ProviderChannel>();
    private Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lineupFetchedUtc = DateTime.MinValue;

    /// <summary>The lineup changes rarely; the session does not outlive the hour.</summary>
    private static readonly TimeSpan LineupTtl = TimeSpan.FromHours(6);

    public PlutoTvProvider(HttpClient http)
    {
        _http = http;
    }

    // ---- session ---------------------------------------------------------

    private async Task<Session> SessionAsync(string key, CancellationToken ct)
    {
        if (_sessions.TryGetValue(key, out var current) && DateTime.UtcNow < current.ExpiresUtc) return current;

        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(key, out current) && DateTime.UtcNow < current.ExpiresUtc) return current;

            // stable across renewals for this key — see _deviceIds
            var clientId = _deviceIds.GetOrAdd(key, _ => Guid.NewGuid().ToString());

            var q = new Dictionary<string, string>
            {
                ["appName"] = "web",
                ["appVersion"] = "8.0.0",
                ["deviceVersion"] = "120.0.0",
                ["deviceModel"] = "web",
                ["deviceMake"] = "chrome",
                ["deviceType"] = "web",
                ["clientID"] = clientId,
                ["clientModelNumber"] = "1.0.0",
                ["serverSideAds"] = "false",
            };
            var url = BootUrl + "?" + string.Join("&", q.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var boot = await resp.Content.ReadFromJsonAsync<BootResponse>(cancellationToken: ct)
                       ?? throw new InvalidOperationException("empty boot response");
            if (string.IsNullOrWhiteSpace(boot.SessionToken))
                throw new InvalidOperationException("boot returned no session token");

            var stitcher = boot.Servers?.Stitcher?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(stitcher))
                throw new InvalidOperationException("boot returned no stitcher host");

            // refreshInSec is how long Pluto says the session is good for;
            // renew early so a resolve never races the expiry.
            var life = boot.RefreshInSec > 60 ? boot.RefreshInSec : 3600;
            var session = new Session(boot.SessionToken, stitcher, boot.StitcherParams ?? "",
                DateTime.UtcNow.AddSeconds(life * 0.8), clientId);

            _sessions[key] = session;
            _byDevice[clientId] = key;
            Log.Info("pluto", $"session established for {key} (valid ~{(int)(life * 0.8 / 60)} min)");
            return session;
        }
        finally { gate.Release(); }
    }

    // ---- lineup ----------------------------------------------------------

    /// <summary>Separate from the session gate: this waits on the guide fetch, not the boot call.</summary>
    private readonly SemaphoreSlim _lineupGate = new(1, 1);

    public async Task<IReadOnlyList<ProviderChannel>> LineupAsync(CancellationToken ct = default)
    {
        if (Fresh()) return _lineup;

        // One fetch, however many callers. The guide is about a megabyte and
        // the dashboard can ask several times over as it starts, so without
        // this each of those pulls its own copy.
        await _lineupGate.WaitAsync(ct);
        try
        {
            if (Fresh()) return _lineup;
            return await FetchLineupAsync(ct);
        }
        finally { _lineupGate.Release(); }
    }

    private bool Fresh() => _lineup.Count > 0 && DateTime.UtcNow - _lineupFetchedUtc < LineupTtl;

    private async Task<IReadOnlyList<ProviderChannel>> FetchLineupAsync(CancellationToken ct)
    {
        var session = await SessionAsync(GuideKey, ct);
        var url = "https://service-channels.clusters.pluto.tv/v2/guide/channels" +
                  "?limit=1000&offset=0&sort=number%3Aasc";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session.Token);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var guide = await resp.Content.ReadFromJsonAsync<GuideResponse>(cancellationToken: ct);
        var raw = guide?.Data ?? new List<GuideChannel>();
        var categories = await CategoriesAsync(session, ct);

        var channels = new List<ProviderChannel>(raw.Count);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in raw)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name)) continue;
            // plutoOfficeOnly entries are internal test loops, not viewable
            if (c.PlutoOfficeOnly) continue;
            var path = c.Stitched?.Path;
            if (string.IsNullOrWhiteSpace(path)) continue;

            // the guide names a channel's category only by id
            var group = c.Category;
            if (string.IsNullOrWhiteSpace(group) && c.CategoryIDs is { Count: > 0 })
                categories.TryGetValue(c.CategoryIDs[0], out group);

            paths[c.Id] = path;
            channels.Add(new ProviderChannel(
                Id: c.Id,
                Name: c.Name,
                Group: group ?? "",
                LogoUrl: PickLogo(c.Images),
                Number: c.Number,
                Summary: c.Summary));
        }

        _lineup = channels;
        _paths = paths;
        _lineupFetchedUtc = DateTime.UtcNow;
        Log.Info("pluto", $"lineup: {channels.Count} channels");
        return _lineup;
    }

    /// <summary>
    /// Category id → display name, so the lineup can be grouped. A failure
    /// here costs the grouping and nothing else, so it is not fatal.
    /// </summary>
    private async Task<Dictionary<string, string>> CategoriesAsync(Session session, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://service-channels.clusters.pluto.tv/v2/guide/categories");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session.Token);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadFromJsonAsync<CategoryResponse>(cancellationToken: ct);
            foreach (var c in body?.Data ?? new List<Category>())
                if (!string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Name))
                    map[c.Id] = c.Name;
        }
        catch (Exception ex)
        {
            Log.Warn("pluto", $"could not load categories ({ex.Message}) — channels will be ungrouped");
        }
        return map;
    }

    /// <summary>Prefers a square/tile logo, falling back to whatever is offered.</summary>
    private static string? PickLogo(List<GuideImage>? images)
    {
        if (images is null || images.Count == 0) return null;
        return images.FirstOrDefault(i => i.Type == "colorLogoPNG")?.Url
               ?? images.FirstOrDefault(i => i.Type == "solidLogoPNG")?.Url
               ?? images[0].Url;
    }

    // ---- resolve ---------------------------------------------------------

    public async Task<string?> ResolveAsync(string channelId, CancellationToken ct = default)
    {
        if (_paths.Count == 0) await LineupAsync(ct);
        if (!_paths.TryGetValue(channelId, out var path)) return null;

        // this channel's own session — see _sessions
        var session = await SessionAsync(channelId, ct);

        // the guide gives "/stitch/hls/channel/{id}/master.m3u8" but the v4
        // stitcher host serves it under /v2
        var rel = path.StartsWith('/') ? path : "/" + path;
        if (!rel.StartsWith("/v2/", StringComparison.Ordinal)) rel = "/v2" + rel;

        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(session.Params)) parts.Add(session.Params);
        parts.Add("jwt=" + session.Token);

        var sep = rel.Contains('?') ? '&' : '?';
        return $"{session.StitcherHost}{rel}{sep}{string.Join("&", parts)}";
    }

    /// <summary>
    /// Swaps the <c>jwt</c> in a stitcher URL for the current session's.
    ///
    /// Only URLs on the stitcher host carry one; the segments and keys live
    /// on the CDN and authorize themselves, so they are returned untouched.
    /// </summary>
    public async Task<string> RefreshAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        if (!u.Query.Contains("jwt=", StringComparison.Ordinal)) return url;

        var query = u.Query.TrimStart('?').Split('&');

        // The URL names the device it was minted for, so it can be given back
        // that session's token and no one else's. Handing a channel the token
        // from another channel's session is precisely the mixing this class
        // now exists to avoid — and the URL knowing its own device is what
        // makes the lookup possible without threading state through the proxy.
        var deviceId = query.FirstOrDefault(kv => kv.StartsWith("deviceId=", StringComparison.Ordinal))
                            ?["deviceId=".Length..];
        var key = deviceId is not null && _byDevice.TryGetValue(deviceId, out var k) ? k : null;
        if (key is null) return url;   // not one of ours, or minted before a restart

        Session session;
        try { session = await SessionAsync(key, ct); }
        catch (Exception ex)
        {
            Log.Warn("pluto", $"could not refresh session for {key}: {ex.Message}");
            return url;
        }

        var rebuilt = string.Join("&", query.Select(kv =>
            kv.StartsWith("jwt=", StringComparison.Ordinal) ? "jwt=" + session.Token : kv));
        return u.GetLeftPart(UriPartial.Path) + "?" + rebuilt;
    }

    public void Dispose()
    {
        foreach (var gate in _gates.Values) gate.Dispose();
        _lineupGate.Dispose();
    }

    // ---- wire types ------------------------------------------------------

    private sealed class BootResponse
    {
        [JsonPropertyName("sessionToken")] public string? SessionToken { get; set; }
        [JsonPropertyName("stitcherParams")] public string? StitcherParams { get; set; }
        [JsonPropertyName("refreshInSec")] public int RefreshInSec { get; set; }
        [JsonPropertyName("servers")] public BootServers? Servers { get; set; }
    }

    private sealed class BootServers
    {
        [JsonPropertyName("stitcher")] public string? Stitcher { get; set; }
    }

    private sealed class GuideResponse
    {
        [JsonPropertyName("data")] public List<GuideChannel>? Data { get; set; }
    }

    private sealed class GuideChannel
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("categoryIDs")] public List<string>? CategoryIDs { get; set; }
        [JsonPropertyName("plutoOfficeOnly")] public bool PlutoOfficeOnly { get; set; }
        [JsonPropertyName("stitched")] public GuideStitched? Stitched { get; set; }
        [JsonPropertyName("images")] public List<GuideImage>? Images { get; set; }
    }

    private sealed class GuideStitched
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
    }

    private sealed class GuideImage
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class CategoryResponse
    {
        [JsonPropertyName("data")] public List<Category>? Data { get; set; }
    }

    private sealed class Category
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
