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
    /// Paths that are the dashboard talking to itself rather than a person
    /// asking for something. Exact matches only - /api/logs/file is somebody
    /// opening a rotated log and is kept.
    /// </summary>
    private static readonly HashSet<string> Heartbeat = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/status",
        "/api/log",
    };

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
            if (Heartbeat.Contains(path)) return;

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
