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
/// </summary>
public sealed class ControlApi : IDisposable
{
    private readonly ControlConfig _config;
    private readonly ServerConfig _serverConfig;
    private readonly RtspServer? _rtspServer;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private HttpListener? _listener;
    private readonly CancellationTokenSource _cts = new();

    public ControlApi(ServerConfig serverConfig, RtspServer? rtspServer)
    {
        _config = serverConfig.Control;
        _serverConfig = serverConfig;
        _rtspServer = rtspServer;
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

    private void Handle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
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
