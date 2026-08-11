namespace J0kersMediaServer.Services;

/// <summary>
/// Which scheme this server's own URLs use — every link it hands out, every
/// address it announces, and the prefixes its listeners bind.
///
/// It is one global because it is one fact about the process: the control
/// port and the media port move together (a dashboard on https loading
/// video from http is mixed content, which browsers block), and there is
/// nowhere sensible to thread a boolean that a discovery responder, a
/// playlist rewriter and a UPnP description document all need to agree on.
/// Set once at startup, before anything listens or announces.
/// </summary>
public static class UrlScheme
{
    public static bool Https { get; private set; }

    /// <summary>"http" or "https".</summary>
    public static string Name => Https ? "https" : "http";

    /// <summary>"http://" or "https://" — the form most call sites want.</summary>
    public static string Prefix => Https ? "https://" : "http://";

    public static void UseHttps(bool on) => Https = on;
}
