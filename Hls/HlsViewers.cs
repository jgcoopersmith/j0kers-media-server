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
        public required string Protocol { get; init; }
        /// <summary>The file on disk, when the viewing is of one directly (DLNA).</summary>
        public string? File { get; init; }
        public string User { get; set; } = "";
        public DateTime StartedUtc { get; init; }
        public DateTime LastSeenUtc { get; set; }
        public long Bytes;
        public int Requests;
    }

    public sealed record Viewer(
        string Id, string Stream, string Client, string Player, string User,
        DateTime StartedUtc, DateTime LastSeenUtc, long Bytes, int Requests, string State,
        string Protocol, string? File);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised the moment a new viewing begins — the first request of a
    /// client/stream/player combination. This is the only place that knows a
    /// session *started*, as opposed to that one exists: everything else here
    /// is a running count.
    /// </summary>
    public event Action<Viewer>? ViewingStarted;

    /// <summary>
    /// Records a request against a viewing. <paramref name="bytes"/> is the
    /// response body size — 0 for a playlist, the segment size for media.
    /// </summary>
    /// <param name="create">
    /// Whether this request may begin a viewing. False for playlist fetches:
    /// a playlist is read by anything that merely looks at a stream — the
    /// dashboard listing it, a link being checked, a player deciding whether
    /// it can play it — and counting those made the sessions table claim
    /// someone was watching a stream nobody had opened. Media is the proof of
    /// watching, so only a segment starts one; a playlist keeps a viewing
    /// that already exists alive, which matters for a paused-but-buffering
    /// player still polling for the next segment.
    /// </param>
    /// <param name="protocol">
    /// How this viewing reaches the media — "hls" for the streaming path,
    /// "dlna" for a television pulling the file whole. It separates the two
    /// in the sessions table, and it keeps their ids from colliding.
    /// </param>
    /// <param name="file">
    /// The file being watched, when the protocol serves one directly. HLS
    /// infers it later from the stream directory; DLNA already knows it, and
    /// without it the history entry would not be replayable.
    /// </param>
    /// <returns>
    /// The viewing's id, for <see cref="Progress"/>. DLNA sends a whole film
    /// as one HTTP response, so the bytes arrive over the life of the
    /// response rather than at the end of it.
    /// </returns>
    public string Note(HttpListenerContext ctx, string stream, string? user, long bytes, bool create = true,
                       string protocol = "hls", string? file = null)
    {
        var client = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
        var player = DescribePlayer(ctx.Request.UserAgent);
        // one viewing = one client watching one stream with one player; two
        // tabs on the same phone are indistinguishable at this level and
        // deliberately count as one
        var id = Id(client, stream, player, protocol);

        var started = false;
        Entry? entry;
        if (create)
        {
            if (!_entries.TryGetValue(id, out entry))
            {
                // The value overload, not the factory one: a factory can run
                // on several threads for the same key and only one result is
                // kept, so a flag set inside it says "started" on every thread
                // that raced — and the viewing gets announced more than once.
                // Comparing against what the dictionary actually kept is the
                // only answer that is true exactly once.
                var candidate = new Entry
                {
                    Stream = stream,
                    Client = client,
                    Player = player,
                    Protocol = protocol,
                    File = file,
                    StartedUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                };
                entry = _entries.GetOrAdd(id, candidate);
                started = ReferenceEquals(entry, candidate);
            }
        }
        else if (!_entries.TryGetValue(id, out entry))
        {
            return id;   // looking at a stream is not watching it
        }

        entry.LastSeenUtc = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(user)) entry.User = user;
        Interlocked.Add(ref entry.Bytes, bytes);
        Interlocked.Increment(ref entry.Requests);

        // after the entry is complete, and never inside GetOrAdd's factory —
        // that can run more than once under contention
        if (started)
        {
            try { ViewingStarted?.Invoke(Describe(entry, DateTime.UtcNow)); }
            catch { /* a subscriber must never break the media path */ }
        }

        MaybePrune();
        return id;
    }

    /// <summary>
    /// Adds bytes to a viewing already under way, and keeps it alive.
    ///
    /// For HLS every segment is its own request, so <see cref="Note"/> counts
    /// the traffic as it goes. DLNA is one response for the whole film: with
    /// nothing reported until it ends, a television watching for two hours
    /// would show as a session that has sent nothing, then vanish. This is
    /// how the transfer reports itself while it is still running. It does not
    /// count as another request — it is the same one, still going.
    /// </summary>
    /// <returns>
    /// False when the viewing is no longer known, which happens if the
    /// television paused for longer than the window and the sweep took it:
    /// the response is still open, so the caller should start a fresh viewing
    /// rather than let the rest of the film go uncounted.
    /// </returns>
    public bool Progress(string id, long bytes)
    {
        if (!_entries.TryGetValue(id, out var entry)) return false;
        if (bytes <= 0) return true;
        entry.LastSeenUtc = DateTime.UtcNow;
        Interlocked.Add(ref entry.Bytes, bytes);
        return true;
    }

    private long _lastPruneTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// Sweeps at most every 30 seconds. Sweeping on every request made the
    /// cost of serving one segment proportional to the number of viewers,
    /// which is the wrong shape for something on the media path — and the
    /// only visible effect of a late sweep is a viewer lingering a few
    /// seconds past the window.
    /// </summary>
    private void MaybePrune()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now - last < TimeSpan.TicksPerSecond * 30) return;
        if (Interlocked.CompareExchange(ref _lastPruneTicks, now, last) != last) return;
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
                .Select(e => Describe(e, now))
                .ToArray();
        }
    }

    private static Viewer Describe(Entry e, DateTime now) => new(
        Id(e.Client, e.Stream, e.Player, e.Protocol),
        e.Stream, e.Client, e.Player,
        // DLNA has no account to name — the protocol carries no credential
        // at all — so saying "share link" there would be a fiction
        e.User.Length > 0 ? e.User : e.Protocol == "dlna" ? "no sign-in (DLNA)" : "share link",
        e.StartedUtc, e.LastSeenUtc,
        Interlocked.Read(ref e.Bytes), e.Requests,
        now - e.LastSeenUtc <= Fresh ? "playing" : "buffered",
        e.Protocol, e.File);

    /// How many are watching. Deliberately not Active.Count: that filters,
    /// sorts and projects the whole table into new objects, and this is asked
    /// on every dashboard poll and by the idle-shutdown check.
    public int Count
    {
        get
        {
            var now = DateTime.UtcNow;
            var n = 0;
            foreach (var e in _entries.Values)
                if (now - e.LastSeenUtc <= Window) n++;
            return n;
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - Window;
        foreach (var (id, e) in _entries)
            if (e.LastSeenUtc < cutoff) _entries.TryRemove(id, out _);
    }

    // cast rather than Math.Abs: Math.Abs(int.MinValue) throws, and this
    // runs on every media request
    private static string Id(string client, string stream, string player, string protocol) =>
        $"{protocol}-{(uint)System.HashCode.Combine(client, stream, player, protocol):x8}";

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
        // televisions, which arrive over DLNA and name themselves in their
        // own way rather than pretending to be a browser
        if (ua.Contains("SEC_HHP", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("Samsung", StringComparison.OrdinalIgnoreCase)) return "Samsung TV";
        if (ua.Contains("webOS", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("LG Browser", StringComparison.OrdinalIgnoreCase)) return "LG TV";
        if (ua.Contains("BRAVIA", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("Sony", StringComparison.OrdinalIgnoreCase)) return "Sony TV";
        if (ua.Contains("Roku", StringComparison.OrdinalIgnoreCase)) return "Roku";
        if (ua.Contains("Xbox", StringComparison.OrdinalIgnoreCase)) return "Xbox";
        if (ua.Contains("PLAYSTATION", StringComparison.OrdinalIgnoreCase)) return "PlayStation";
        if (ua.Contains("Kodi", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("XBMC", StringComparison.OrdinalIgnoreCase)) return "Kodi";
        if (ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (ua.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (ua.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (ua.Contains("Safari", StringComparison.OrdinalIgnoreCase)) return "Safari";
        // a UPnP client that named nothing recognisable is still a TV-ish box
        if (ua.Contains("DLNADOC", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("UPnP", StringComparison.OrdinalIgnoreCase)) return "DLNA device";
        return "player";
    }
}
