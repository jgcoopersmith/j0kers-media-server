using System.Net;
using System.Text;
using System.Text.Json;
using J0kersMediaServer.Config;
using J0kersMediaServer.Logging;
using J0kersMediaServer.Rtsp;

namespace J0kersMediaServer.Control;

/// <summary>
/// HTTP/JSON control surface. RFC 5167 defines *requirements* for media
/// server control protocols rather than a wire format; this API satisfies
/// the core ones: session monitoring/auditing (REQ-MCP-08/09), resource
/// reporting, extensibility, and explicit session teardown by the
/// controlling application.
///
///   GET    /api/status          server identity, uptime, resource counts
///   GET    /api/config          effective (redacted) configuration
///   GET    /api/mounts          configured RTSP mounts
///   GET    /api/sessions        live RTSP sessions with RTP stats
///   DELETE /api/sessions/{id}   force-terminate a session
///   GET    /api/preview?mount=  live raw µ-law audio of a mount (dashboard player)
/// </summary>
public sealed class ControlApi : IDisposable
{
    private readonly ControlConfig _config;
    private readonly ServerConfig _serverConfig;
    private readonly RtspServer? _rtspServer;
    private readonly string _baseDirectory;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private HttpListener? _listener;
    private readonly CancellationTokenSource _cts = new();

    public ControlApi(ServerConfig serverConfig, RtspServer? rtspServer, string baseDirectory)
    {
        _config = serverConfig.Control;
        _serverConfig = serverConfig;
        _rtspServer = rtspServer;
        _baseDirectory = baseDirectory;
    }

    public void Start()
    {
        (var listener, var bound) = Hls.HttpListenerBinder.Start(_config.BindAddress, _config.Port, "control");
        _listener = listener;
        Log.Info("control", $"listening on http://{bound}:{_config.Port}/api/");
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync(); }
            catch (Exception) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Warn("control", $"accept failed: {ex.Message}"); continue; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private static readonly Lazy<byte[]> Dashboard = new(() =>
    {
        using var s = typeof(ControlApi).Assembly.GetManifestResourceStream("dashboard.html")!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });

    private void Handle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            // The dashboard page itself is static and served without auth;
            // every /api call it makes is still token-gated below.
            var rawPath = ctx.Request.Url?.AbsolutePath ?? "/";
            if (ctx.Request.HttpMethod == "GET" && rawPath is "/" or "/index.html")
            {
                res.StatusCode = 200;
                res.ContentType = "text/html; charset=utf-8";
                res.ContentLength64 = Dashboard.Value.Length;
                res.OutputStream.Write(Dashboard.Value);
                return;
            }

            if (_config.AuthToken.Length > 0)
            {
                var auth = ctx.Request.Headers["Authorization"];
                if (auth != $"Bearer {_config.AuthToken}")
                {
                    WriteJson(res, 401, new { error = "unauthorized" });
                    return;
                }
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;

            switch (method, path)
            {
                case ("GET", "/api/status"):
                    WriteJson(res, 200, new
                    {
                        server = _serverConfig.ServerName,
                        version = typeof(ControlApi).Assembly.GetName().Version?.ToString(3),
                        uptimeSeconds = (int)(DateTime.UtcNow - _startedUtc).TotalSeconds,
                        rtsp = new
                        {
                            enabled = _serverConfig.Rtsp.Enabled,
                            port = _serverConfig.Rtsp.Port,
                            sessions = _rtspServer?.Sessions.Count ?? 0,
                            maxSessions = _serverConfig.Rtsp.MaxSessions,
                        },
                        hls = new { enabled = _serverConfig.Hls.Enabled, port = _serverConfig.Hls.Port },
                    });
                    return;

                case ("GET", "/api/config"):
                    var redacted = JsonSerializer.Deserialize<JsonElement>(_serverConfig.ToJson());
                    WriteJson(res, 200, new { config = redacted, note = "control.authToken redacted" }, redactToken: true);
                    return;

                case ("GET", "/api/mounts"):
                    WriteJson(res, 200, new
                    {
                        mounts = _serverConfig.Mounts.Select(m => new
                        {
                            path = m.Path,
                            source = m.Source,
                            description = m.Description,
                            uri = $"rtsp://<host>:{_serverConfig.Rtsp.Port}{m.Path}",
                        }),
                        announcementService = _serverConfig.Services.AnnouncementEnabled
                            ? $"rtsp://<host>:{_serverConfig.Rtsp.Port}/annc?play=<clip>"
                            : null,
                    });
                    return;

                case ("GET", "/api/sessions"):
                    WriteJson(res, 200, new
                    {
                        sessions = (_rtspServer?.Sessions.All ?? Array.Empty<RtspSession>()).Select(s => new
                        {
                            id = s.Id,
                            mount = s.MountPath,
                            state = s.State.ToString().ToLowerInvariant(),
                            client = s.ClientAddress,
                            lastActivityUtc = s.LastActivity,
                            rtp = new { packetsSent = s.Sender.Stats.packets, octetsSent = s.Sender.Stats.octets },
                        }),
                    });
                    return;
            }

            if (method == "GET" && path == "/api/preview")
            {
                StreamPreview(ctx);
                return;
            }

            if (method == "DELETE" && path.StartsWith("/api/sessions/", StringComparison.Ordinal))
            {
                var id = path["/api/sessions/".Length..];
                var session = _rtspServer?.Sessions.Get(id);
                if (session is null)
                {
                    WriteJson(res, 404, new { error = "session not found" });
                    return;
                }
                _rtspServer!.Sessions.Remove(id);
                Log.Info("control", $"session {id} terminated via control API");
                WriteJson(res, 200, new { terminated = id });
                return;
            }

            WriteJson(res, 404, new { error = "not found" });
        }
        catch (Exception ex)
        {
            Log.Warn("control", $"request failed: {ex.Message}");
            try { WriteJson(res, 500, new { error = "internal error" }); } catch { }
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    /// <summary>
    /// Streams a mount's audio as raw G.711 µ-law (8 kHz mono) in real time,
    /// paced at 20 ms frames, until the client disconnects. The dashboard
    /// decodes it with Web Audio; browsers cannot consume RTSP directly.
    /// </summary>
    private void StreamPreview(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var mountPath = ctx.Request.QueryString["mount"];
        var mount = _serverConfig.Mounts.FirstOrDefault(m =>
            string.Equals(m.Path, mountPath, StringComparison.OrdinalIgnoreCase));
        if (mount is null)
        {
            WriteJson(res, 404, new { error = "unknown mount" });
            return;
        }

        Media.IMediaSource source;
        try
        {
            source = Media.MediaSourceFactory.Create(mount, _baseDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn("control", $"preview source for {mount.Path} failed: {ex.Message}");
            WriteJson(res, 500, new { error = "source unavailable" });
            return;
        }

        Log.Info("control", $"preview started: {mount.Path} for {ctx.Request.RemoteEndPoint}");
        res.StatusCode = 200;
        res.ContentType = "application/octet-stream";
        res.SendChunked = true;
        res.Headers["Cache-Control"] = "no-store";

        var frame = new byte[Media.MediaSourceFactory.FrameSamples];
        var nextTick = Environment.TickCount64;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                source.NextFrame(frame);
                res.OutputStream.Write(frame);
                nextTick += 20;
                var delay = nextTick - Environment.TickCount64;
                if (delay > 0) Thread.Sleep((int)delay);
            }
        }
        catch (Exception)
        {
            // client hung up — normal end of a preview
        }
        finally
        {
            Log.Info("control", $"preview ended: {mount.Path}");
        }
    }

    private void WriteJson(HttpListenerResponse res, int status, object body, bool redactToken = false)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true });
        if (redactToken && _config.AuthToken.Length > 0)
            json = json.Replace(_config.AuthToken, "***");
        var bytes = Encoding.UTF8.GetBytes(json);
        res.StatusCode = status;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
