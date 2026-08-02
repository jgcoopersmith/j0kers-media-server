using System.Collections.Concurrent;
using System.Net;

namespace J0kersMediaServer.Hls;

/// <summary>
/// Who is currently watching over HLS.
///
/// RTSP has real sessions — SETUP opens one, TEARDOWN closes it — so the
/// dashboard can simply list them. HLS has nothing of the sort: a player
/// asks for a playlist, asks for some segments, and goes away, and each of
/// those is an unrelated HTTP request. A phone streaming a film for an hour
/// is, to the server, a few hundred strangers asking for files.
///
/// So a viewer is inferred: requests from the same client for the same
/// stream are one viewing, and it counts as live until the requests stop.
/// The window has to tolerate a player that has buffered a minute ahead and
/// gone quiet, which is why it is generous — an idle viewer lingering for a
/// short while is a better failure than a watching one vanishing.
/// </summary>
public sealed class HlsViewers
{
    /// <summary>
    /// How long after its last request a viewer still counts as watching.
    /// hls.js keeps ~30 s buffered and then fetches roughly one segment per
    /// segment-duration; native players on iOS buffer further ahead. 90 s
    /// covers the quiet stretch without keeping ghosts around for long.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

    /// <summary>Seen this recently and it is actively pulling data, not just buffered.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(20);

    private sealed class Entry
    {
        public required string Stream { get; init; }
        public required string Client { get; init; }
        public required string Player { get; init; }
        public string User { get; set; } = "";
        public DateTime StartedUtc { get; init; }
        public DateTime LastSeenUtc { get; set; }
        public long Bytes;
        public int Requests;
    }

    public sealed record Viewer(
        string Id, string Stream, string Client, string Player, string User,
        DateTime StartedUtc, DateTime LastSeenUtc, long Bytes, int Requests, string State);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a request against a viewing. <paramref name="bytes"/> is the
    /// response body size — 0 for a playlist, the segment size for media.
    /// </summary>
    public void Note(HttpListenerContext ctx, string stream, string? user, long bytes)
    {
        var client = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
        var player = DescribePlayer(ctx.Request.UserAgent);
        // one viewing = one client watching one stream with one player; two
        // tabs on the same phone are indistinguishable at this level and
        // deliberately count as one
        var id = Id(client, stream, player);

        var entry = _entries.GetOrAdd(id, _ => new Entry
        {
            Stream = stream,
            Client = client,
            Player = player,
            StartedUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
        });

        entry.LastSeenUtc = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(user)) entry.User = user;
        Interlocked.Add(ref entry.Bytes, bytes);
        Interlocked.Increment(ref entry.Requests);

        Prune();
    }

    /// <summary>Everyone still inside the window, newest viewing first.</summary>
    public IReadOnlyList<Viewer> Active
    {
        get
        {
            var now = DateTime.UtcNow;
            return _entries.Values
                .Where(e => now - e.LastSeenUtc <= Window)
                .OrderByDescending(e => e.LastSeenUtc)
                .Select(e => new Viewer(
                    Id(e.Client, e.Stream, e.Player),
                    e.Stream, e.Client, e.Player,
                    e.User.Length > 0 ? e.User : "share link",
                    e.StartedUtc, e.LastSeenUtc,
                    Interlocked.Read(ref e.Bytes), e.Requests,
                    now - e.LastSeenUtc <= Fresh ? "playing" : "buffered"))
                .ToArray();
        }
    }

    public int Count => Active.Count;

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - Window;
        foreach (var (id, e) in _entries)
            if (e.LastSeenUtc < cutoff) _entries.TryRemove(id, out _);
    }

    // cast rather than Math.Abs: Math.Abs(int.MinValue) throws, and this
    // runs on every media request
    private static string Id(string client, string stream, string player) =>
        $"hls-{(uint)System.HashCode.Combine(client, stream, player):x8}";

    /// <summary>A short, readable name for whatever is playing — for the dashboard's client column.</summary>
    private static string DescribePlayer(string? userAgent)
    {
        var ua = userAgent ?? "";
        if (ua.Length == 0) return "player";
        if (ua.Contains("VLC", StringComparison.OrdinalIgnoreCase)) return "VLC";
        if (ua.Contains("Lavf", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)) return "ffmpeg";
        if (ua.Contains("CrKey", StringComparison.OrdinalIgnoreCase)) return "Chromecast";
        if (ua.Contains("AppleCoreMedia", StringComparison.OrdinalIgnoreCase))
            return ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone"
                : ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPad"
                : "Apple player";
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android";
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return "iPhone";
        if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iPad";
        if (ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (ua.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (ua.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (ua.Contains("Safari", StringComparison.OrdinalIgnoreCase)) return "Safari";
        return "player";
    }
}
