using System.Net;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Hls;

/// <summary>
/// Starts an HttpListener on the configured address. On Windows, wildcard
/// prefixes ("http://+:port/") require an admin-granted URL ACL; when that
/// fails with access-denied, fall back to loopback so the server still
/// comes up unprivileged. (Grant the ACL with:
/// netsh http add urlacl url=http://+:PORT/ user=Everyone)
/// </summary>
public static class HttpListenerBinder
{
    /// <returns>A started listener and the host actually bound.</returns>
    public static (HttpListener listener, string boundHost) Start(string bindAddress, int port, string area)
    {
        var host = bindAddress == "0.0.0.0" ? "+" : bindAddress;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{host}:{port}/");
        try
        {
            listener.Start();
            return (listener, bindAddress);
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5 && host != "localhost") // ERROR_ACCESS_DENIED
        {
            Log.Warn(area,
                $"binding http://{host}:{port}/ denied (URL ACL); falling back to localhost. " +
                $"To listen on all interfaces run elevated once: netsh http add urlacl url=http://+:{port}/ user=Everyone");
            // A failed Start() disposes the listener; build a fresh one.
            var fallback = new HttpListener();
            fallback.Prefixes.Add($"http://localhost:{port}/");
            fallback.Prefixes.Add($"http://127.0.0.1:{port}/");
            fallback.Start();
            return (fallback, "localhost");
        }
    }
}
