using J0kersMediaServer.Config;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Services;

/// <summary>
/// Says, in one place at startup, everything this server is offering.
///
/// Each service already announced itself as it started, but only when it
/// started: a service that is switched off, or one that shares a port with
/// another and so never opens a listener of its own, said nothing at all.
/// DLNA was the worst of those. It is served from the control port whenever
/// the dashboard is plain HTTP, which is the usual case, so its own startup
/// path returns early and logs nothing - and DLNA is the one service here
/// that carries no credential of any kind. The log could not be read to find
/// out whether the library was being shared with the whole network.
///
/// Written as one table of every service rather than a line per subsystem,
/// so that "is it on?" is answered for all of them or for none of them, and
/// a service that is off is as visible as one that is on.
/// </summary>
public static class StartupSummary
{
    public static void Write(ServerConfig config, string boundHost, int dlnaPort,
                             string mediaRoot, bool ffmpegAvailable)
    {
        var scheme = UrlScheme.Prefix;
        var host = boundHost.Length == 0 ? config.Control.BindAddress : boundHost;
        var d = config.Discovery;

        Log.Info("services", "--- services offered ---");

        Line("dashboard + API", config.Control.Enabled,
             $"{scheme}{host}:{config.Control.Port}/");

        Line("media (HLS)", config.Hls.Enabled,
             $"{scheme}{host}:{config.Hls.Port}/  root: {mediaRoot}");

        Line("RTSP", config.Rtsp.Enabled,
             $"rtsp://{config.Rtsp.BindAddress}:{config.Rtsp.Port}  "
             + $"(auth {(config.Rtsp.RequireAuth ? "required" : "OFF")})");

        // The one with no sign-in, so it says so every time.
        Line("DLNA library", d.Dlna,
             $"http://{host}:{dlnaPort}/dlna/  NO SIGN-IN - any device on this network");

        Line("DLNA live TV", d.Dlna && d.DlnaLiveTv,
             "running channels offered to televisions as timeshift streams");

        Line("DLNA transcodes", d.Dlna && d.DlnaUseTranscode,
             "a converted copy is served in place of the original where one exists");

        Line("discovery", d.Enabled,
             $"mDNS {OnOff(d.Mdns)}, SSDP {OnOff(d.Ssdp)}, UDP probe {OnOff(d.UdpProbe)}"
             + $"  as {d.HostName}.local");

        Line("transcoding", ffmpegAvailable,
             ffmpegAvailable ? "ffmpeg ready" : "ffmpeg not found - conversions unavailable");

        Line("HTTPS", config.Https.Enabled,
             config.Https.Enabled ? "dashboard and media are encrypted"
                                  : "dashboard and media are served in the clear");

        Line("access log", config.Logging.AccessLog,
             "one line per request on every port");

        Log.Info("services", "------------------------");
    }

    private static void Line(string name, bool on, string detail) =>
        Log.Info("services", $"  {(on ? "on " : "off")}  {name,-16} {(on ? detail : "")}".TrimEnd());

    private static string OnOff(bool b) => b ? "on" : "off";
}
