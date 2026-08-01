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
///   GET    /api/preview?mount=  live WAV audio of a mount (dashboard player)
///   GET    /api/browse[?path=]  drive / folder / file listing (dashboard picker)
/// </summary>
public sealed class ControlApi : IDisposable
{
    private readonly ControlConfig _config;
    private readonly ServerConfig _serverConfig;
    private readonly Services.ServiceController _services;
    private readonly string _baseDirectory;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private HttpListener? _listener;
    private readonly CancellationTokenSource _cts = new();

    private readonly Media.FfmpegManager? _ffmpeg;

    private RtspServer? RtspServer => _services.Rtsp;
    private readonly Media.PlaylistStore _playlists;
    private readonly Media.LibraryStore _library;
    private readonly Media.FavoritesStore _favorites;

    public ControlApi(ServerConfig serverConfig, Services.ServiceController services, string baseDirectory,
        Media.FfmpegManager? ffmpeg = null)
    {
        _config = serverConfig.Control;
        _serverConfig = serverConfig;
        _services = services;
        _baseDirectory = baseDirectory;
        _ffmpeg = ffmpeg;
        _playlists = new Media.PlaylistStore(baseDirectory);
        _library = new Media.LibraryStore(baseDirectory);
        _favorites = new Media.FavoritesStore(baseDirectory);
    }

    /// <summary>The host the listener actually bound (may differ from config after the Windows ACL fallback).</summary>
    public string BoundHost { get; private set; } = "localhost";

    public void Start()
    {
        (var listener, var bound) = Hls.HttpListenerBinder.Start(_config.BindAddress, _config.Port, "control");
        _listener = listener;
        BoundHost = bound;
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
                // ?token= is accepted as an alternative to the Authorization
                // header because <audio> elements cannot set request headers.
                var auth = ctx.Request.Headers["Authorization"];
                var queryToken = ctx.Request.QueryString["token"];
                if (auth != $"Bearer {_config.AuthToken}" && queryToken != _config.AuthToken)
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
                        running = _services.Running,
                        uptimeSeconds = (int)(DateTime.UtcNow - _startedUtc).TotalSeconds,
                        rtsp = new
                        {
                            enabled = _serverConfig.Rtsp.Enabled,
                            port = _serverConfig.Rtsp.Port,
                            sessions = RtspServer?.Sessions.Count ?? 0,
                            maxSessions = _serverConfig.Rtsp.MaxSessions,
                        },
                        hls = new { enabled = _serverConfig.Hls.Enabled, port = _serverConfig.Hls.Port },
                        ffmpeg = new
                        {
                            available = _ffmpeg?.Available ?? false,
                            version = _ffmpeg?.VersionLine ?? "not configured",
                        },
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
                            dynamic = _serverConfig.IsDynamicMount(m.Path),
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
                        sessions = (RtspServer?.Sessions.All ?? Array.Empty<RtspSession>()).Select(s => new
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

            if (method == "GET" && path == "/api/browse")
            {
                Browse(ctx);
                return;
            }

            // ---- service power + settings ----
            if (method == "POST" && path == "/api/server/start")
            {
                try { _services.StartServices(); }
                catch (Exception ex) { WriteJson(res, 500, new { error = ex.Message }); return; }
                Log.Info("control", "services started via dashboard");
                WriteJson(res, 200, new { running = _services.Running });
                return;
            }

            if (method == "POST" && path == "/api/server/stop")
            {
                _services.StopServices();
                Log.Info("control", "services stopped via dashboard");
                WriteJson(res, 200, new { running = _services.Running });
                return;
            }

            if (method == "GET" && path == "/api/settings")
            {
                WriteJson(res, 200, new
                {
                    serverName = _serverConfig.ServerName,
                    bindAddress = _serverConfig.Rtsp.BindAddress,
                    rtspPort = _serverConfig.Rtsp.Port,
                    hlsPort = _serverConfig.Hls.Port,
                    controlPort = _serverConfig.Control.Port,
                });
                return;
            }

            if (method == "POST" && path == "/api/settings")
            {
                SaveSettings(ctx);
                return;
            }

            // ---- media engine (ffmpeg) ----
            if (method == "POST" && path == "/api/play")
            {
                PlayFile(ctx);
                return;
            }

            if (method == "GET" && path == "/api/play")
            {
                var stream = ctx.Request.QueryString["stream"] ?? "";
                WriteJson(res, 200, new { stream, ready = _ffmpeg?.IsVodReady(stream) ?? false });
                return;
            }

            if (method == "GET" && path == "/api/channels")
            {
                WriteJson(res, 200, new
                {
                    ffmpegAvailable = _ffmpeg?.Available ?? false,
                    channels = (_ffmpeg?.Channels ?? new List<(Media.FfmpegManager.ChannelDef, string, string)>())
                        .Select(c => new { name = c.Item1.Name, url = c.Item1.Url, stream = c.Item2, status = c.Item3 }),
                });
                return;
            }

            if (method == "POST" && path == "/api/channels")
            {
                AddChannel(ctx);
                return;
            }

            if (method == "DELETE" && path == "/api/channels")
            {
                var name = ctx.Request.QueryString["name"] ?? "";
                if (_ffmpeg?.RemoveChannel(name) == true)
                {
                    Log.Info("control", $"channel removed: {name}");
                    WriteJson(res, 200, new { removed = name });
                }
                else WriteJson(res, 404, new { error = "unknown channel" });
                return;
            }

            if (method == "POST" && path == "/api/channels/restart")
            {
                var name = ctx.Request.QueryString["name"] ?? "";
                if (_ffmpeg?.RestartChannel(name) == true) WriteJson(res, 200, new { restarted = name });
                else WriteJson(res, 404, new { error = "unknown channel" });
                return;
            }

            // ---- pinned media (quick buttons) ----
            if (method == "GET" && path == "/api/favorites")
            {
                WriteJson(res, 200, new
                {
                    favorites = _favorites.All.Select(f => new { name = f.Name, path = f.Path }),
                });
                return;
            }

            if (method == "POST" && path == "/api/favorites")
            {
                AddFavorite(ctx);
                return;
            }

            if (method == "DELETE" && path == "/api/favorites")
            {
                var favPath = ctx.Request.QueryString["path"] ?? "";
                if (_favorites.Remove(favPath))
                {
                    Log.Info("control", $"favorite removed: {favPath}");
                    WriteJson(res, 200, new { removed = favPath });
                }
                else WriteJson(res, 404, new { error = "unknown favorite" });
                return;
            }

            // ---- library root folders ----
            if (method == "GET" && path == "/api/library")
            {
                WriteJson(res, 200, new { folders = _library.All });
                return;
            }

            if (method == "POST" && path == "/api/library")
            {
                AddLibraryFolder(ctx);
                return;
            }

            if (method == "DELETE" && path == "/api/library")
            {
                var folder = ctx.Request.QueryString["folder"] ?? "";
                if (_library.Remove(folder))
                {
                    Log.Info("control", $"library folder removed: {folder}");
                    WriteJson(res, 200, new { removed = folder });
                }
                else WriteJson(res, 404, new { error = "unknown library folder" });
                return;
            }

            if (method == "GET" && path == "/api/thumb")
            {
                ServeThumbnail(ctx);
                return;
            }

            // ---- remembered playlists (media library folders) ----
            if (method == "GET" && path == "/api/playlists")
            {
                WriteJson(res, 200, new
                {
                    playlists = _playlists.All.Select(p => new { name = p.Name, folder = p.Folder }),
                });
                return;
            }

            if (method == "POST" && path == "/api/playlists")
            {
                SavePlaylist(ctx);
                return;
            }

            if (method == "DELETE" && path == "/api/playlists")
            {
                var plName = ctx.Request.QueryString["name"] ?? "";
                if (_playlists.Remove(plName))
                {
                    Log.Info("control", $"playlist removed: {plName}");
                    WriteJson(res, 200, new { removed = plName });
                }
                else WriteJson(res, 404, new { error = "unknown playlist" });
                return;
            }

            if (method == "GET" && path == "/api/image")
            {
                ServeImage(ctx);
                return;
            }

            if (method == "POST" && path == "/api/mounts")
            {
                AddMount(ctx);
                return;
            }

            if (method == "DELETE" && path == "/api/mounts")
            {
                var mountPath = ctx.Request.QueryString["path"] ?? "";
                if (_serverConfig.RemoveMount(mountPath))
                {
                    Log.Info("control", $"mount removed via dashboard: {mountPath}");
                    WriteJson(res, 200, new { removed = mountPath });
                }
                else
                {
                    WriteJson(res, 404, new { error = "unknown mount" });
                }
                return;
            }

            if (method == "DELETE" && path == "/api/hls")
            {
                RemoveHlsStream(ctx);
                return;
            }

            if (method == "DELETE" && path.StartsWith("/api/sessions/", StringComparison.Ordinal))
            {
                var id = path["/api/sessions/".Length..];
                var session = RtspServer?.Sessions.Get(id);
                if (session is null)
                {
                    WriteJson(res, 404, new { error = "session not found" });
                    return;
                }
                RtspServer!.Sessions.Remove(id);
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
    /// Streams a mount's audio as a live WAV file (16-bit PCM, 8 kHz mono)
    /// with an open-ended length, paced in real time until the client
    /// disconnects. A plain &lt;audio&gt; element can play this natively —
    /// browsers cannot consume RTSP directly.
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
        res.ContentType = "audio/wav";
        // Identity-encoded delivery with an absurdly large advertised length:
        // browsers treat it as a huge WAV download and start playing right
        // away, whereas chunked live WAV stalls in the buffering heuristics.
        // (HttpListener demands either SendChunked or a Content-Length; with
        // neither it closes the response as Content-Length: 0.)
        res.SendChunked = false;
        res.KeepAlive = false;
        res.ContentLength64 = 0x7FFFFFF0; // ~2 GiB ≈ 37 hours of 8 kHz PCM16
        res.Headers["Cache-Control"] = "no-store";

        var ulaw = new byte[Media.MediaSourceFactory.FrameSamples];
        var pcm = new byte[ulaw.Length * 2];
        try
        {
            res.OutputStream.Write(BuildWavHeader(res.ContentLength64));

            void WriteFrame()
            {
                source.NextFrame(ulaw);
                for (var i = 0; i < ulaw.Length; i++)
                {
                    var s = Media.G711.UlawToLinear(ulaw[i]);
                    pcm[i * 2] = (byte)(s & 0xFF);
                    pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
                res.OutputStream.Write(pcm);
            }

            // 1 s preroll burst so the browser's decoder reaches "can play"
            // immediately instead of trickle-buffering at the live rate.
            for (var i = 0; i < 50; i++) WriteFrame();

            var nextTick = Environment.TickCount64;
            while (!_cts.IsCancellationRequested)
            {
                WriteFrame();
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

    /// <summary>Canonical 44-byte WAV header sized to match the advertised body length.</summary>
    private static byte[] BuildWavHeader(long totalLength)
    {
        const int sampleRate = Media.MediaSourceFactory.SampleRate;
        const short channels = 1, bitsPerSample = 16;
        const int byteRate = sampleRate * channels * bitsPerSample / 8;

        using var ms = new MemoryStream(44);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write((uint)(totalLength - 8));
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                          // fmt chunk size
        w.Write((short)1);                    // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)(channels * bitsPerSample / 8)); // block align
        w.Write(bitsPerSample);
        w.Write("data"u8);
        w.Write((uint)(totalLength - 44));
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// POST /api/settings � save hostname/port settings to the settings.json
    /// sidecar and apply them by restarting the streaming services. A control
    /// port change is saved but only takes effect on the next full restart
    /// (this API is serving the current request on the old port).
    /// </summary>
    private void SaveSettings(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        ServerConfig.SettingsOverrides? s;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            s = JsonSerializer.Deserialize<ServerConfig.SettingsOverrides>(reader.ReadToEnd(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            WriteJson(res, 400, new { error = "bad JSON: " + ex.Message });
            return;
        }
        if (s is null) { WriteJson(res, 400, new { error = "empty body" }); return; }

        foreach (var (port, name) in new[] { (s.RtspPort, "rtspPort"), (s.HlsPort, "hlsPort"), (s.ControlPort, "controlPort") })
        {
            if (port is int p and (< 1 or > 65535))
            {
                WriteJson(res, 400, new { error = $"{name} must be 1�65535" });
                return;
            }
        }
        if (!string.IsNullOrWhiteSpace(s.BindAddress) &&
            !System.Net.IPAddress.TryParse(s.BindAddress, out _) &&
            !s.BindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(res, 400, new { error = "bindAddress must be an IP address (0.0.0.0 = all interfaces) or 'localhost'" });
            return;
        }

        var ports = new[] { s.RtspPort ?? _serverConfig.Rtsp.Port, s.HlsPort ?? _serverConfig.Hls.Port, s.ControlPort ?? _serverConfig.Control.Port };
        if (ports.Distinct().Count() != 3)
        {
            WriteJson(res, 400, new { error = "rtsp, hls, and control ports must all be different" });
            return;
        }

        var controlPortChanged = s.ControlPort is int ncp && ncp != _serverConfig.Control.Port;

        _serverConfig.UpdateSettings(s);

        try
        {
            if (_services.Running) _services.RestartServices();
            else _services.StartServices();
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { error = "saved, but restart failed: " + ex.Message });
            return;
        }

        Log.Info("control", $"settings saved: bind={_serverConfig.Rtsp.BindAddress} rtsp={_serverConfig.Rtsp.Port} hls={_serverConfig.Hls.Port} control={_serverConfig.Control.Port}");
        WriteJson(res, 200, new
        {
            saved = true,
            servicesRestarted = true,
            controlPortChanged,
            note = controlPortChanged ? "control port applies after the server process restarts" : null,
        });
    }

    /// <summary>
    /// DELETE /api/hls?stream=name — delete an HLS stream directory (its
    /// playlist and segments) from the media root. Streams backing a live
    /// channel are refused; remove the channel instead.
    /// </summary>
    private void RemoveHlsStream(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var name = ctx.Request.QueryString["stream"] ?? "";
        if (name.Length == 0 || name.Contains("..") || name.Contains('/') || name.Contains('\\'))
        {
            WriteJson(res, 400, new { error = "invalid stream name" });
            return;
        }

        if (_ffmpeg?.Channels.Any(c => c.stream.Equals(name, StringComparison.OrdinalIgnoreCase)) == true)
        {
            WriteJson(res, 400, new { error = "that stream is a live channel — remove the channel instead" });
            return;
        }

        var mediaRoot = Path.GetFullPath(Path.IsPathRooted(_serverConfig.Hls.MediaRoot)
            ? _serverConfig.Hls.MediaRoot
            : Path.Combine(_baseDirectory, _serverConfig.Hls.MediaRoot));
        var dir = Path.GetFullPath(Path.Combine(mediaRoot, name));
        if (!dir.StartsWith(mediaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(dir))
        {
            WriteJson(res, 404, new { error = "unknown stream" });
            return;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
            Log.Info("control", $"HLS stream removed via dashboard: {name}");
            WriteJson(res, 200, new { removed = name });
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { error = "could not delete: " + ex.Message });
        }
    }

    private sealed record FavoriteRequest(string? name, string? path);

    /// <summary>POST /api/favorites {path, name?} — pin a media file as a quick button.</summary>
    private void AddFavorite(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var req = JsonSerializer.Deserialize<FavoriteRequest>(reader.ReadToEnd(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (string.IsNullOrWhiteSpace(req?.path))
            {
                WriteJson(res, 400, new { error = "body must be { \"path\": \"...\", \"name\": \"optional\" }" });
                return;
            }
            var full = Path.GetFullPath(req.path);
            var isFolder = Directory.Exists(full);
            if (!isFolder && !System.IO.File.Exists(full))
            {
                WriteJson(res, 404, new { error = "file or folder not found" });
                return;
            }
            var name = !string.IsNullOrWhiteSpace(req.name) ? req.name.Trim()
                : isFolder ? new DirectoryInfo(full).Name
                : Path.GetFileNameWithoutExtension(full);
            if (!_favorites.Add(name, full))
            {
                WriteJson(res, 409, new { error = "already pinned" });
                return;
            }
            Log.Info("control", $"favorite pinned: {name} → {full}");
            WriteJson(res, 200, new { added = name });
        }
        catch (Exception ex)
        {
            WriteJson(res, 400, new { error = ex.Message });
        }
    }

    private sealed record LibraryRequest(string? folder);

    /// <summary>POST /api/library {folder} — add a library root folder.</summary>
    private void AddLibraryFolder(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var req = JsonSerializer.Deserialize<LibraryRequest>(reader.ReadToEnd(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (string.IsNullOrWhiteSpace(req?.folder))
            {
                WriteJson(res, 400, new { error = "body must be { \"folder\": \"...\" }" });
                return;
            }
            var folder = Path.GetFullPath(req.folder);
            if (!Directory.Exists(folder))
            {
                WriteJson(res, 404, new { error = "folder not found" });
                return;
            }
            if (!_library.Add(folder))
            {
                WriteJson(res, 409, new { error = "already in the library" });
                return;
            }
            Log.Info("control", $"library folder added: {folder}");
            WriteJson(res, 200, new { added = folder });
        }
        catch (Exception ex)
        {
            WriteJson(res, 400, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/thumb?path= — cached JPEG thumbnail for a video or picture,
    /// generated by ffmpeg. 404 when ffmpeg is missing or the file has no
    /// visual (the dashboard falls back to an icon).
    /// </summary>
    private void ServeThumbnail(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var path = ctx.Request.QueryString["path"] ?? "";
        try
        {
            var thumb = _ffmpeg?.GetThumbnail(Path.GetFullPath(path));
            if (thumb is null)
            {
                WriteJson(res, 404, new { error = "no thumbnail" });
                return;
            }
            res.StatusCode = 200;
            res.ContentType = "image/jpeg";
            res.Headers["Cache-Control"] = "max-age=86400";
            using var fs = System.IO.File.OpenRead(thumb);
            res.ContentLength64 = fs.Length;
            fs.CopyTo(res.OutputStream);
        }
        catch (Exception ex)
        {
            WriteJson(res, 400, new { error = ex.Message });
        }
    }

    private sealed record PlaylistRequest(string? name, string? folder);

    /// <summary>POST /api/playlists {name, folder} — remember a folder as a playlist.</summary>
    private void SavePlaylist(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var req = JsonSerializer.Deserialize<PlaylistRequest>(reader.ReadToEnd(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (string.IsNullOrWhiteSpace(req?.name) || string.IsNullOrWhiteSpace(req?.folder))
            {
                WriteJson(res, 400, new { error = "body must be { \"name\": \"...\", \"folder\": \"...\" }" });
                return;
            }
            var folder = Path.GetFullPath(req.folder);
            if (!Directory.Exists(folder))
            {
                WriteJson(res, 404, new { error = "folder not found" });
                return;
            }
            _playlists.Save(req.name.Trim(), folder);
            Log.Info("control", $"playlist saved: {req.name} → {folder}");
            WriteJson(res, 200, new { saved = req.name.Trim() });
        }
        catch (Exception ex)
        {
            WriteJson(res, 400, new { error = ex.Message });
        }
    }

    private sealed record PlayRequest(string? file, int? height);
    private sealed record ChannelRequest(string? name, string? url);

    /// <summary>POST /api/play {file} — transcode any media file to HLS and return the stream name.</summary>
    private void PlayFile(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        if (_ffmpeg is null || !_ffmpeg.Available)
        {
            WriteJson(res, 503, new { error = "ffmpeg is not available — install it (winget install Gyan.FFmpeg) and restart" });
            return;
        }
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var req = JsonSerializer.Deserialize<PlayRequest>(reader.ReadToEnd(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (string.IsNullOrWhiteSpace(req?.file))
            {
                WriteJson(res, 400, new { error = "body must be { \"file\": \"...\", \"height\": 0|360|480|720|1080 }" });
                return;
            }
            var height = req.height ?? 0;
            if (height is not (0 or 360 or 480 or 720 or 1080))
            {
                WriteJson(res, 400, new { error = "height must be 0 (source), 360, 480, 720, or 1080" });
                return;
            }
            var (stream, ready) = _ffmpeg.StartVod(req.file, height);
            WriteJson(res, 200, new { stream, ready, playlist = $"/{stream}/index.m3u8" });
        }
        catch (FileNotFoundException)
        {
            WriteJson(res, 404, new { error = "file not found" });
        }
        catch (Exception ex)
        {
            WriteJson(res, 400, new { error = ex.Message });
        }
    }

    /// <summary>POST /api/channels {name,url} — add and start a live channel.</summary>
    private void AddChannel(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        if (_ffmpeg is null || !_ffmpeg.Available)
        {
            WriteJson(res, 503, new { error = "ffmpeg is not available — install it (winget install Gyan.FFmpeg) and restart" });
            return;
        }
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var req = JsonSerializer.Deserialize<ChannelRequest>(reader.ReadToEnd(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (string.IsNullOrWhiteSpace(req?.name) || string.IsNullOrWhiteSpace(req?.url))
            {
                WriteJson(res, 400, new { error = "body must be { \"name\": \"...\", \"url\": \"...\" }" });
                return;
            }
            var scheme = Uri.TryCreate(req.url, UriKind.Absolute, out var u) ? u.Scheme.ToLowerInvariant() : "";
            if (scheme is not ("http" or "https" or "rtsp" or "rtmp" or "udp" or "rtp" or "srt"))
            {
                WriteJson(res, 400, new { error = "url must be http(s)/rtsp/rtmp/udp/rtp/srt" });
                return;
            }
            var stream = _ffmpeg.AddChannel(req.name.Trim(), req.url.Trim());
            Log.Info("control", $"channel added: {req.name} ← {req.url}");
            WriteJson(res, 200, new { stream, playlist = $"/{stream}/index.m3u8" });
        }
        catch (InvalidOperationException ex) { WriteJson(res, 409, new { error = ex.Message }); }
        catch (Exception ex) { WriteJson(res, 400, new { error = ex.Message }); }
    }

    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml", [".avif"] = "image/avif",
    };

    /// <summary>GET /api/image?path= — serves a picture for the library viewer.</summary>
    private void ServeImage(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var path = ctx.Request.QueryString["path"] ?? "";
        try
        {
            var full = Path.GetFullPath(path);
            if (!System.IO.File.Exists(full) || !ImageTypes.TryGetValue(Path.GetExtension(full), out var mime))
            {
                WriteJson(res, 404, new { error = "not an image" });
                return;
            }
            res.StatusCode = 200;
            res.ContentType = mime;
            using var fs = System.IO.File.OpenRead(full);
            res.ContentLength64 = fs.Length;
            fs.CopyTo(res.OutputStream);
        }
        catch (Exception ex)
        {
            WriteJson(res, 400, new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/mounts — add a mount at runtime. Body:
    /// { "path": "/music", "source": "tone"|"file", "file": "...",
    ///   "toneFrequencyHz": 440, "description": "" }
    /// Takes effect immediately (the RTSP server resolves mounts per
    /// request) and persists to the mounts.json sidecar.
    /// </summary>
    private void AddMount(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        MountConfig? mount;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            mount = JsonSerializer.Deserialize<MountConfig>(reader.ReadToEnd(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            WriteJson(res, 400, new { error = "bad JSON: " + ex.Message });
            return;
        }

        if (mount is null || string.IsNullOrWhiteSpace(mount.Path) || !mount.Path.StartsWith('/'))
        {
            WriteJson(res, 400, new { error = "mount path must start with '/'" });
            return;
        }
        mount.Path = "/" + mount.Path.Trim().Trim('/');
        if (mount.Path == "/" || mount.Path.Any(char.IsWhiteSpace))
        {
            WriteJson(res, 400, new { error = "invalid mount path" });
            return;
        }
        if (mount.Path.Equals("/annc", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(res, 400, new { error = "/annc is reserved for the announcement service" });
            return;
        }

        switch (mount.Source.ToLowerInvariant())
        {
            case "tone":
                if (mount.ToneFrequencyHz is < 20 or > 4000)
                {
                    WriteJson(res, 400, new { error = "tone frequency must be 20–4000 Hz (8 kHz sampling)" });
                    return;
                }
                break;

            case "file":
                if (string.IsNullOrWhiteSpace(mount.File))
                {
                    WriteJson(res, 400, new { error = "file source requires a file path" });
                    return;
                }
                var full = Path.IsPathRooted(mount.File) ? mount.File : Path.Combine(_baseDirectory, mount.File);
                if (!System.IO.File.Exists(full))
                {
                    WriteJson(res, 400, new { error = "file not found: " + full });
                    return;
                }
                // RTSP mounts stream the file as-is (raw G.711 µ-law) —
                // an MP3 here would play as static. Other formats belong in
                // the Media Library, or convert first.
                var ext = Path.GetExtension(full).ToLowerInvariant();
                if (ext is not (".ulaw" or ".ul" or ".raw" or ".g711" or ".pcmu" or ".mulaw"))
                {
                    WriteJson(res, 400, new
                    {
                        error = $"'{ext}' is not raw G.711 µ-law — RTSP mounts play headerless 8 kHz µ-law only. " +
                                "Use the Media Library to play this file, or convert it: " +
                                "ffmpeg -i input -ar 8000 -ac 1 -f mulaw output.ulaw",
                    });
                    return;
                }
                mount.File = Path.GetFullPath(full);
                break;

            default:
                WriteJson(res, 400, new { error = "source must be 'tone' or 'file'" });
                return;
        }

        try
        {
            _serverConfig.AddDynamicMount(mount);
        }
        catch (InvalidOperationException ex)
        {
            WriteJson(res, 409, new { error = ex.Message });
            return;
        }

        Log.Info("control", $"mount added via dashboard: {mount.Path} ({mount.Source})");
        WriteJson(res, 200, new { added = mount.Path });
    }

    /// <summary>
    /// Filesystem browser backing the dashboard's pickPath() library:
    /// GET /api/browse            → drive list
    /// GET /api/browse?path=C:\x  → folders and files of that directory
    /// Loopback/token-gated like the rest of the API; this is the operator's
    /// own machine, so no path restriction beyond what the OS enforces.
    /// </summary>
    private void Browse(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var path = ctx.Request.QueryString["path"];

        if (string.IsNullOrWhiteSpace(path))
        {
            var drives = DriveInfo.GetDrives().Select(d =>
            {
                string label = "", free = "";
                try
                {
                    if (d.IsReady)
                    {
                        label = d.VolumeLabel;
                        free = $"{d.AvailableFreeSpace / (1024.0 * 1024 * 1024):0.#} GB free";
                    }
                }
                catch { /* removable drive not ready */ }
                return new { name = d.Name, type = "drive", label, detail = free, ready = d.IsReady };
            });
            WriteJson(res, 200, new { path = "", parent = (string?)null, entries = drives });
            return;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full))
            {
                WriteJson(res, 404, new { error = "directory not found", path = full });
                return;
            }

            var dir = new DirectoryInfo(full);
            var entries = new List<object>();
            foreach (var d in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                entries.Add(new { name = d.Name, type = "folder", label = "", detail = "", ready = true });
            foreach (var f in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                entries.Add(new
                {
                    name = f.Name,
                    type = "file",
                    label = "",
                    detail = f.Length >= 1024 * 1024
                        ? $"{f.Length / (1024.0 * 1024):0.#} MB"
                        : $"{Math.Max(1, f.Length / 1024)} KB",
                    ready = true,
                });

            WriteJson(res, 200, new
            {
                path = full,
                parent = dir.Parent?.FullName, // null at a drive root → back to drive list
                entries,
            });
        }
        catch (UnauthorizedAccessException)
        {
            WriteJson(res, 403, new { error = "access denied" });
        }
        catch (Exception ex)
        {
            WriteJson(res, 400, new { error = ex.Message });
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

