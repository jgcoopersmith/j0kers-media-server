using System.Net;

namespace J0kersMediaServer.Logging;

/// <summary>
/// One line per request served, across every door into this server: the
/// dashboard and its API, the HLS media port, and DLNA.
///
/// The event logs say what the server decided - a channel started, a
/// conversion finished, an account signed in. They never said what was
/// *asked for*, so there was no way to answer "who played that file", "what
/// did that television actually fetch", or "was anything served while I was
/// out". This is that record.
///
/// Deliberately excluded, and only this: the dashboard's own repeating polls
/// (/api/status and /api/log). The page asks for those every two seconds
/// whether anyone is touching it or not, so they are a heartbeat rather than
/// anything a person did - forty thousand lines a day that would bury the
/// requests that mean something. Everything else is logged, including the
/// page-close beacon, because closing the page is an action.
///
/// Query strings are never logged. Signed media links carry their signature
/// there, and a log that reproduces them is a log that hands out playable
/// links to anyone who reads it - including the dashboard's own log panel.
/// The path alone already names the stream, the file or the endpoint.
/// </summary>
public static class AccessLog
{
    /// <summary>
    /// Off switches the whole thing off. On by default: a server that serves
    /// media on a home network should be able to say what it served.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// GET paths that are the dashboard's own polling rather than a person
    /// asking for something.
    ///
    /// The method is half of the rule, and leaving it out would have been a
    /// silent disaster. Five of these paths carry actions on the identical
    /// path - POST /api/channels adds a channel, DELETE /api/mounts removes a
    /// mount, DELETE /api/history forgets what was watched - and those are
    /// precisely the events this log exists to record. Matching on the path
    /// alone would have stopped recording nine real actions in exchange for
    /// quietening six timers.
    ///
    /// Exact paths only: /api/log/file is somebody opening a rotated log by
    /// hand and is kept, as is /api/sessions/{id} for terminating a session.
    /// </summary>
    private static readonly HashSet<string> HeartbeatGets = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/status",
        "/api/log",
        // the live link an open dashboard holds: one line per page-open, and
        // it would be written when the page closes rather than when it opened
        "/api/server/session",
        // The rest of what an open page asks for on a timer, whether or not
        // anybody is touching it: sessions every 2s, history every 4s,
        // channels every 10s, and mounts, playlists and favourites every 15s.
        // That is 63 requests a minute per open window, and measured on this
        // install it was 88% of the log - 6,738 lines against 930 that
        // recorded something happening. The six were always the same kind of
        // traffic as /api/status above; they were simply never added.
        "/api/sessions",
        "/api/history",
        "/api/channels",
        "/api/mounts",
        "/api/playlists",
        "/api/favorites",
    };

    /// <summary>
    /// Whether this request is the dashboard's own heartbeat rather than
    /// something a person did. Separate from Served so the rule can be tested
    /// without standing up an HttpListener - and it is worth testing, because
    /// the method half of it is easy to drop and the damage would be silent.
    /// </summary>
    internal static bool IsHeartbeat(string method, string path) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        && HeartbeatGets.Contains(path);

    /// <summary>
    /// Records one served request. Call it once, from a finally, so a request
    /// that threw is logged with the status it actually returned.
    /// </summary>
    /// <param name="service">which door: hls, control, dlna.</param>
    /// <param name="ctx">the request, for its method, path and client.</param>
    /// <param name="user">the account behind it, when it has one.</param>
    public static void Served(string service, HttpListenerContext ctx, string? user = null)
    {
        if (!Enabled) return;
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (IsHeartbeat(ctx.Request.HttpMethod, path)) return;

            var client = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "-";
            var who = string.IsNullOrWhiteSpace(user) ? "-" : user;
            var status = ctx.Response.StatusCode;
            var bytes = ctx.Response.ContentLength64;
            var size = bytes > 0 ? " " + Size(bytes) : "";

            Log.Info("access",
                $"{service} {client} {who} {ctx.Request.HttpMethod} {path} {status}{size}");
        }
        catch
        {
            // A request that has already been closed can throw on any of the
            // properties above. Losing one line of the record is not a reason
            // to fail the request that produced it.
        }
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}
