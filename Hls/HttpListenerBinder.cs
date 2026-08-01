using System.Net;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Hls;

/// <summary>
/// Starts an HttpListener on the configured address. On Windows, wildcard
/// prefixes ("http://+:port/") require an admin-granted URL ACL; when that
/// fails with access-denied, fall back to loopback so the server still
/// comes up unprivileged. (Grant the ACL with:
/// netsh http add urlacl url=http://+:PORT/ user=Everyone)
///
/// http.sys matches requests by Host header, so a loopback binding must
/// register BOTH "localhost" and "127.0.0.1" prefixes — otherwise a browser
/// pointed at one spelling gets "400 Invalid Hostname" from the other.
/// </summary>
public static class HttpListenerBinder
{
    private static readonly string[] LoopbackNames = { "localhost", "127.0.0.1" };

    /// <returns>A started listener and the host actually bound.</returns>
    public static (HttpListener listener, string boundHost) Start(string bindAddress, int port, string area)
    {
        if (bindAddress is "127.0.0.1" or "localhost" or "::1")
            return (StartLoopback(port), "localhost");

        var host = bindAddress == "0.0.0.0" ? "+" : bindAddress;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{host}:{port}/");
        try
        {
            listener.Start();
            return (listener, bindAddress);
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5) // ERROR_ACCESS_DENIED
        {
            Log.Warn(area,
                $"binding http://{host}:{port}/ denied (URL ACL); falling back to localhost. " +
                $"To listen on all interfaces run elevated once: netsh http add urlacl url=http://+:{port}/ user=Everyone");
            return (StartLoopback(port), "localhost");
        }
    }

    private static HttpListener StartLoopback(int port)
    {
        // A failed Start() disposes the listener, so always build fresh here.
        var listener = new HttpListener();
        foreach (var name in LoopbackNames)
            listener.Prefixes.Add($"http://{name}:{port}/");
        listener.Start();
        return listener;
    }
}
