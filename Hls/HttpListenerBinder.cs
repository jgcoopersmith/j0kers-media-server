using System.Net;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Hls;

/// <summary>
/// Starts an HttpListener on the configured address, papering over the
/// platform differences:
///
/// - Windows (http.sys): the all-interfaces prefix "http://+:port/" needs
///   an admin-granted URL ACL; when that fails with access-denied we fall
///   back to loopback so the server still comes up unprivileged.
///   (Grant the ACL with: netsh http add urlacl url=http://+:PORT/ user=Everyone)
/// - macOS/Linux (managed listener): no ACLs — "http://*:port/" binds all
///   interfaces directly and accepts any Host header.
///
/// Requests are matched by Host header, so loopback bindings register BOTH
/// "localhost" and "127.0.0.1" prefixes — otherwise a browser pointed at
/// one spelling gets "400 Invalid Hostname" from the other.
/// </summary>
public static class HttpListenerBinder
{
    private static readonly string[] LoopbackNames = { "localhost", "127.0.0.1" };

    /// <returns>A started listener and the host actually bound.</returns>
    public static (HttpListener listener, string boundHost) Start(string bindAddress, int port, string area)
    {
        if (bindAddress is "127.0.0.1" or "localhost" or "::1")
            return (StartLoopback(port), "localhost");

        var listener = new HttpListener();
        if (bindAddress == "0.0.0.0")
        {
            // '+' is http.sys syntax; the managed listener on Unix wants '*'
            var scheme = Services.UrlScheme.Name;
            listener.Prefixes.Add(OperatingSystem.IsWindows()
                ? $"{scheme}://+:{port}/"
                : $"{scheme}://*:{port}/");
        }
        else
        {
            listener.Prefixes.Add($"{Services.UrlScheme.Name}://{bindAddress}:{port}/");
        }

        try
        {
            listener.Start();
            return (listener, bindAddress);
        }
        catch (HttpListenerException ex) when (OperatingSystem.IsWindows() && ex.ErrorCode == 5) // ERROR_ACCESS_DENIED
        {
            Log.Warn(area,
                $"binding {Services.UrlScheme.Prefix}{bindAddress}:{port}/ denied (URL ACL); falling back to localhost. " +
                $"To listen on all interfaces run elevated once: netsh http add urlacl url={Services.UrlScheme.Prefix}+:{port}/ user=Everyone");
            return (StartLoopback(port), "localhost");
        }
    }

    private static HttpListener StartLoopback(int port)
    {
        // A failed Start() disposes the listener, so always build fresh here.
        var listener = new HttpListener();
        foreach (var name in LoopbackNames)
            listener.Prefixes.Add($"{Services.UrlScheme.Name}://{name}:{port}/");
        try
        {
            listener.Start();
            return listener;
        }
        catch (HttpListenerException ex) when (OperatingSystem.IsWindows() && ex.ErrorCode == 5)
        {
            // http.sys quirk: once an all-interfaces URL ACL is reserved for
            // the port, unprivileged host-specific registrations are denied.
            // Bind the wildcard (which the ACL permits) — callers that asked
            // for loopback enforce it per-request via IsLoopbackRequest.
            Log.Warn("http",
                $"loopback bind on port {port} denied because an all-interfaces URL ACL exists; " +
                "listening on the wildcard with loopback-only enforcement in the app");
            var wide = new HttpListener();
            wide.Prefixes.Add($"{Services.UrlScheme.Name}://+:{port}/");
            wide.Start();
            return wide;
        }
    }

    /// <summary>True when the configured bind is loopback-only.</summary>
    public static bool IsLoopbackBind(string bindAddress) =>
        bindAddress is "127.0.0.1" or "localhost" or "::1";

    /// <summary>Per-request loopback guard for listeners that may be bound wider than configured.</summary>
    public static bool IsLoopbackRequest(HttpListenerContext ctx) =>
        IPAddress.IsLoopback(ctx.Request.RemoteEndPoint.Address);
}
