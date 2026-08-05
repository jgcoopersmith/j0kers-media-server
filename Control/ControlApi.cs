using System.Net;
using System.Text;
using System.Text.Json;
using J0kersMediaServer.Auth;
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
public sealed partial class ControlApi : IDisposable
{
    private readonly ControlConfig _config;
    private readonly Auth.AuthService _auth;
    private readonly Auth.MediaLink _mediaLinks;
    private readonly ServerConfig _serverConfig;
    private readonly Services.ServiceController _services;
    private readonly string _baseDirectory;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private HttpListener? _listener;
    private readonly CancellationTokenSource _cts = new();

    private readonly Media.FfmpegManager? _ffmpeg;
    private readonly Media.SubtitleManager? _subtitles;
    private readonly Action? _requestShutdown;
    private Timer? _closeShutdownTimer;
    private readonly object _shutdownLock = new();

    private RtspServer? RtspServer => _services.Rtsp;
    private readonly Media.PlaylistStore _playlists;
    private readonly Media.LibraryStore _library;
    private readonly Media.FavoritesStore _favorites;
    private readonly Media.WatchHistory _history;

    /// <summary>
    /// Free ad-supported TV: the provider lineups and the proxy that makes
    /// their playlists playable from here. One HttpClient for all of it —
    /// these talk to a handful of hosts continuously, so a per-call client
    /// would burn sockets for nothing.
    /// </summary>
    private readonly HttpClient _providerHttp;
    private readonly Media.Providers.ProviderRegistry _providers;
    private readonly Media.Providers.HlsProxy _tvProxy;
    private readonly HashSet<string> _relayProviders;

    public ControlApi(ServerConfig serverConfig, Services.ServiceController services, string baseDirectory,
        Auth.AuthService auth, Auth.MediaLink mediaLinks,
        Media.FfmpegManager? ffmpeg = null, Action? requestShutdown = null)
    {
        _config = serverConfig.Control;
        _auth = auth;
        _mediaLinks = mediaLinks;
        _serverConfig = serverConfig;
        _services = services;
        _baseDirectory = baseDirectory;
        _ffmpeg = ffmpeg;
        _subtitles = ffmpeg is not null ? new Media.SubtitleManager(ffmpeg) : null;
        _requestShutdown = requestShutdown;
        _playlists = new Media.PlaylistStore(baseDirectory);
        _library = new Media.LibraryStore(baseDirectory);
        _favorites = new Media.FavoritesStore(baseDirectory);
        _history = new Media.WatchHistory(baseDirectory);

        _providerHttp = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        _providers = Media.Providers.ProviderStore.Load(baseDirectory, _providerHttp);
        _relayProviders = Media.Providers.ProviderStore.RelaySet(baseDirectory);
        _tvProxy = new Media.Providers.HlsProxy(_providerHttp, mediaLinks);
    }

    /// <summary>The host the listener actually bound (may differ from config after the Windows ACL fallback).</summary>
    public string BoundHost { get; private set; } = "localhost";

    /// <summary>
    /// Turns background/tray mode on or off while running (set by Program).
    /// Returns true if the mode is now active.
    /// </summary>
    public Func<bool, bool>? SetTrayMode { get; set; }

    /// <summary>
    /// Network announcement, set by Program. Held here so the dashboard can
    /// switch it on and off, and so /description.xml — which whatever found
    /// us over SSDP fetches next — can be served.
    /// </summary>
    public Discovery.DiscoveryService? Discovery { get; set; }

    /// <summary>
    /// Cancels a pending shutdown-on-close. Called for dashboard polls and
    /// for any HLS request, so navigating to a watch page — or a phone
    /// streaming a movie — keeps the server alive.
    /// </summary>
    public void NoteActivity()
    {
        lock (_shutdownLock)
        {
            if (_closeShutdownTimer is null) return;
            Log.Info("control", "activity detected — shutdown cancelled");
            _closeShutdownTimer.Dispose();
            _closeShutdownTimer = null;
        }
    }

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
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private static byte[] LoadResource(string name)
    {
        using var s = typeof(ControlApi).Assembly.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static readonly Lazy<byte[]> Dashboard = new(() => LoadResource("dashboard.html"));
    private static readonly Lazy<byte[]> LoginPage = new(() => LoadResource("login.html"));
    private static readonly Lazy<byte[]> HlsJs = new(() => LoadResource("hls.min.js"));

    /// <summary>
    /// True when a state-changing request originates from a different site.
    /// Uses Sec-Fetch-Site (sent by every modern browser) and, as a fallback,
    /// an Origin whose host doesn't match ours. curl/VLC/etc. send neither
    /// and are treated as first-party (they can't be a CSRF vector).
    /// </summary>
    private static bool IsCrossSite(HttpListenerContext ctx)
    {
        var fetchSite = ctx.Request.Headers["Sec-Fetch-Site"];
        if (fetchSite is not null)
            return fetchSite is not ("same-origin" or "same-site" or "none");

        var origin = ctx.Request.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) && Uri.TryCreate(origin, UriKind.Absolute, out var o))
            return !string.Equals(o.Host, ctx.Request.Url?.Host, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    /// <summary>
    /// The authorization table, in three tiers.
    ///
    /// Admin is anything that changes how the server *runs* — its
    /// configuration, its power state, and who may use it. Edit is anything
    /// that changes what the server *offers*: library folders, channels,
    /// mounts, playlists, pinned media, and the streams themselves, plus the
    /// filesystem picker those need to name a path. Read is everything
    /// left — listing and watching, which any signed-in account may do.
    /// </summary>
    private static AccessLevel RequiredLevel(string method, string path)
    {
        // accounts are the administrator's alone, including creating them
        if (path.StartsWith("/api/users", StringComparison.Ordinal)) return AccessLevel.Admin;

        switch (path)
        {
            case "/api/config":
            case "/api/settings":
            case "/api/server/start":
            case "/api/server/stop":
                return AccessLevel.Admin;

            // picking a path off this machine, and the codec list that the
            // add-content forms show
            case "/api/browse":
            case "/api/codecs":
            case "/api/channels/restart":
            case "/api/channels/start":
            case "/api/channels/stop":
            case "/api/subtitles":
            // pinning a provider channel adds a restreaming job, same as
            // adding one by hand; browsing and watching a lineup is Read
            case "/api/tv/pin":
                return AccessLevel.Edit;
        }

        // adding or removing what the server offers; listing it stays Read
        if (method is "POST" or "PUT" or "DELETE"
            && path is "/api/mounts" or "/api/channels" or "/api/library"
                or "/api/favorites" or "/api/playlists" or "/api/hls")
            return AccessLevel.Edit;

        // cutting off somebody else's stream is an operator action
        if (method == "DELETE" && path.StartsWith("/api/sessions/", StringComparison.Ordinal))
            return AccessLevel.Admin;

        return AccessLevel.Read;
    }

    /// <summary>
    /// Whether a read-only account may touch this file. Edit and above name
    /// paths for a living — they add the library folders — so the machine is
    /// open to them; a read account is confined to what has actually been
    /// shared: the library folders, pinned favorites, and saved playlists.
    /// Without this, /api/play would transcode any file on the disk and
    /// /api/image would serve any picture, for anyone with an account.
    /// </summary>
    private bool IsShared(string full)
    {
        static bool Under(string root, string candidate)
        {
            if (root.Length == 0) return false;
            var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return candidate.Equals(r, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        var target = Path.TrimEndingDirectorySeparator(full);
        return _library.All.Any(f => Under(f, target))
            || _favorites.All.Any(f => Under(f.Path, target))
            || _playlists.All.Any(p => Under(p.Folder, target));
    }

    /// <summary>Refuses a read-only account's request for a path outside the shared library.</summary>
    private bool DenyUnshared(HttpListenerContext ctx, AuthResult auth, string full)
    {
        if (auth.Level >= AccessLevel.Edit || IsShared(full)) return false;
        Log.Warn("control", $"{auth.Name} was refused a path outside the library: {full}");
        WriteJson(ctx.Response, 403, new { error = "that file is not in the shared library" });
        return true;
    }

    /// <summary>
    /// Serves one request. Async only for the free-TV endpoints, which wait on
    /// a remote service — blocking a thread-pool thread on that call meant one
    /// tied-up thread per playlist fetch, and a live player refetches every few
    /// seconds per viewer. Everything else here is synchronous file and memory
    /// work and stays that way.
    /// </summary>
    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            // configured loopback-only? enforce it even if the listener had
            // to bind wider (Windows wildcard-ACL quirk)
            if (Hls.HttpListenerBinder.IsLoopbackBind(_config.BindAddress) &&
                !Hls.HttpListenerBinder.IsLoopbackRequest(ctx))
            {
                WriteJson(res, 403, new { error = "control API is bound to localhost only" });
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;

            // Who is this? Session cookie, API key, or the legacy
            // control.authToken — all resolved to one access level.
            var auth = _auth.Authenticate(ctx);

            // The dashboard is not served to anyone who hasn't signed in —
            // they get the sign-in page instead (or, on a server with no
            // accounts yet, the form that creates the first administrator).
            if (method == "GET" && path is "/" or "/index.html")
            {
                var page = auth.Level == AccessLevel.None || _auth.SetupRequired
                    ? LoginPage.Value
                    : Dashboard.Value;
                res.StatusCode = 200;
                res.ContentType = "text/html; charset=utf-8";
                res.Headers["Cache-Control"] = "no-store";
                res.ContentLength64 = page.Length;
                res.OutputStream.Write(page);
                return;
            }

            // The UPnP description, fetched by whatever found us over SSDP.
            // Unauthenticated of necessity: the fetcher is a device browser,
            // not an account holder, and it has to read this to list us at
            // all. It carries only what the announcement already broadcast —
            // a name, a port and an id — and grants nothing.
            if (method == "GET" && path == "/description.xml" && Discovery is not null)
            {
                var host = ctx.Request.Url?.Host ?? BoundHost;
                var xml = Encoding.UTF8.GetBytes(Discovery.DescriptionXml(host));
                res.StatusCode = 200;
                res.ContentType = "text/xml; charset=utf-8";
                res.ContentLength64 = xml.Length;
                res.OutputStream.Write(xml);
                return;
            }

            // the player library is a third-party asset with nothing in it
            // to protect, and the sign-in page must render before any login
            if (method == "GET" && path == "/hls.min.js")
            {
                res.StatusCode = 200;
                res.ContentType = "text/javascript";
                res.Headers["Cache-Control"] = "max-age=86400";
                res.ContentLength64 = HlsJs.Value.Length;
                res.OutputStream.Write(HlsJs.Value);
                return;
            }

            // CSRF: a state-changing request from another website must be
            // refused even on loopback with no token, or any page the user
            // visits could POST to us. Browsers stamp cross-site requests;
            // non-browser clients (curl/VLC) send neither header and pass.
            if (method is "POST" or "DELETE" or "PUT")
            {
                if (IsCrossSite(ctx))
                {
                    WriteJson(res, 403, new { error = "cross-site request refused" });
                    return;
                }
                // Belt and braces for the cookie case: a cookie rides along
                // automatically, so a state-changing request authenticated by
                // one must also prove it was made by our own JavaScript. A
                // custom header can't be set cross-origin without a preflight
                // the browser would refuse. Key/token callers aren't browsers
                // and carry no ambient credential, so they skip this.
                // …except the page-close beacon: sendBeacon cannot set
                // headers at all, and Sec-Fetch-Site already covers it.
                if (auth.IsCookie && path != "/api/server/closing"
                    && ctx.Request.Headers["X-J0kers-CSRF"] is null)
                {
                    WriteJson(res, 403, new { error = "missing X-J0kers-CSRF header" });
                    return;
                }
            }

            // login, logout, setup and self-service key management
            if (path.StartsWith("/api/auth/", StringComparison.Ordinal))
            {
                if (HandleAuthRoutes(ctx, auth, method, path)) return;
                WriteJson(res, 404, new { error = "not found" });
                return;
            }

            // The TV proxy authorizes by signature as well as by account: the
            // ffmpeg process restreaming a pinned channel is not signed in and
            // cannot be, and neither is a player following a rewritten
            // playlist. A signature this install minted is proof enough, and
            // it names exactly one channel or one upstream URL.
            if (method == "GET" && path is "/api/tv/watch" or "/api/tv/r" && IsSignedTvRequest(ctx, path))
            {
                await TvProxy(ctx, entry: path == "/api/tv/watch");
                return;
            }

            // Everything else is gated. Administration (configuration, the
            // power button, accounts, the filesystem picker) needs an admin;
            // watching needs any account.
            var required = RequiredLevel(method, path);
            if (auth.Level < required)
            {
                WriteJson(res, auth.Level == AccessLevel.None ? 401 : 403, new
                {
                    error = auth.Level == AccessLevel.None
                        ? "unauthorized"
                        : required == AccessLevel.Admin
                            ? "administrator rights are required for this"
                            : "this account is read-only",
                });
                return;
            }

            if (path.StartsWith("/api/users", StringComparison.Ordinal))
            {
                if (HandleUserRoutes(ctx, auth, method, path)) return;
                WriteJson(res, 404, new { error = "not found" });
                return;
            }

            // the dashboard signals page close with a beacon; any dashboard
            // heartbeat within the grace period cancels the shutdown (page
            // refreshes and multi-tab setups reconnect within a second)
            if (method == "POST" && path == "/api/server/closing")
            {
                if (_config.ShutdownOnClose && _requestShutdown is not null)
                {
                    lock (_shutdownLock)
                    {
                        Log.Info("control", "dashboard closed — shutting down in 5 s unless it reconnects");
                        _closeShutdownTimer?.Dispose();
                        _closeShutdownTimer = new Timer(_ =>
                        {
                            Log.Info("control", "no dashboard reconnected — shutting down");
                            _requestShutdown();
                        }, null, 5000, Timeout.Infinite);
                    }
                }
                WriteJson(res, 200, new { scheduled = _config.ShutdownOnClose });
                return;
            }
            if (method == "GET" && path == "/api/status") NoteActivity();

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
                        hls = new
                        {
                            enabled = _serverConfig.Hls.Enabled,
                            port = _serverConfig.Hls.Port,
                            // people watching over HTTP right now; RTSP
                            // sessions above are counted separately
                            viewers = _services.Viewers.Count,
                            // every address this port answers on, so the
                            // dashboard can offer a link per network rather
                            // than only the one you happen to be browsing
                            addresses = MediaAddresses(),
                        },
                        ffmpeg = new
                        {
                            available = _ffmpeg?.Available ?? false,
                            version = _ffmpeg?.VersionLine ?? "not configured",
                            videoCodec = _ffmpeg?.VideoEncoder,
                            audioCodec = _ffmpeg?.AudioEncoder,
                        },
                        // monotonic: the dashboard differences consecutive
                        // readings into a live rate
                        bytesServed = _services.Served.TotalBytes,
                        transcodes = _ffmpeg?.ActiveVodStreams ?? (IReadOnlyList<string>)Array.Empty<string>(),
                        // the same conversions with how far each has got
                        transcoding = (_ffmpeg?.VodProgressSnapshot
                                       ?? Array.Empty<Media.FfmpegManager.VodProgress>())
                            .Select(v => new
                            {
                                stream = v.Stream,
                                title = v.Title,
                                percent = v.Percent,
                                doneSeconds = (int)v.DoneSeconds,
                                durationSeconds = (int)v.DurationSeconds,
                            }),
                    });
                    return;

                case ("GET", "/api/config"):
                    // redact by replacing the value before serialization, not
                    // by string-replacing the output (which missed tokens
                    // containing +, ", or non-ASCII once JSON-escaped)
                    var savedToken = _serverConfig.Control.AuthToken;
                    _serverConfig.Control.AuthToken = savedToken.Length > 0 ? "***" : "";
                    JsonElement redacted;
                    try { redacted = JsonSerializer.Deserialize<JsonElement>(_serverConfig.ToJson()); }
                    finally { _serverConfig.Control.AuthToken = savedToken; }
                    WriteJson(res, 200, new { config = redacted, note = "control.authToken redacted" });
                    return;

                case ("GET", "/api/mounts"):
                    WriteJson(res, 200, new
                    {
                        mounts = _serverConfig.MountsSnapshot().Select(m => new
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
                    // Both kinds of viewing in one list. RTSP has real
                    // sessions; HLS viewers are inferred from request
                    // traffic, which is the only thing plain HTTP gives us.
                    WriteJson(res, 200, new
                    {
                        sessions = (RtspServer?.Sessions.All ?? Array.Empty<RtspSession>()).Select(s => new
                        {
                            protocol = "rtsp",
                            id = s.Id,
                            mount = s.MountPath,
                            state = s.State.ToString().ToLowerInvariant(),
                            client = s.ClientAddress,
                            player = "",
                            user = "",
                            startedUtc = (DateTime?)null,
                            lastActivityUtc = s.LastActivity,
                            bytes = s.Sender.Stats.octets,
                            // a real session can be torn down; an HTTP
                            // viewer has nothing to tear down
                            terminable = true,
                            rtp = new { packetsSent = s.Sender.Stats.packets, octetsSent = s.Sender.Stats.octets },
                        }).Concat<object>(_services.Viewers.Active.Select(v => new
                        {
                            protocol = "hls",
                            id = v.Id,
                            mount = v.Stream,
                            state = v.State,
                            client = v.Client,
                            player = v.Player,
                            user = v.User,
                            startedUtc = (DateTime?)v.StartedUtc,
                            lastActivityUtc = v.LastSeenUtc,
                            bytes = v.Bytes,
                            terminable = false,
                            rtp = new { packetsSent = 0L, octetsSent = v.Bytes },
                        })),
                    });
                    return;
            }

            // A media token for the caller's own playback. The dashboard
            // takes one all-streams token at startup and appends it to every
            // HLS URL it builds; ?stream= narrows it to a single stream for
            // a link you mean to hand to VLC, a TV, or someone else.
            if (method == "GET" && path == "/api/media/token")
            {
                var stream = ctx.Request.QueryString["stream"];
                var scope = string.IsNullOrWhiteSpace(stream) ? Auth.MediaLink.AllStreams : stream.Trim();
                var hours = _serverConfig.Hls.LinkLifetimeHours;
                var minted = _mediaLinks.Sign(scope, TimeSpan.FromHours(hours));
                // also split out, because JSON escapes the '&' in `token`
                // and a shell script shouldn't have to un-escape it
                var pieces = minted.Split('&');
                WriteJson(res, 200, new
                {
                    token = minted,
                    exp = pieces[0]["exp=".Length..],
                    sig = pieces[1]["sig=".Length..],
                    scope,
                    expiresUtc = DateTime.UtcNow.AddHours(hours),
                    port = _serverConfig.Hls.Port,
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
                    minimizeToTray = _serverConfig.MinimizeToTray,
                    linkLifetimeHours = _serverConfig.Hls.LinkLifetimeHours,
                    // the tray lives in the Windows notification area
                    traySupported = OperatingSystem.IsWindows(),
                    // network announcement, and the name it publishes, so the
                    // dialog can show the .local address the switch produces
                    discoveryEnabled = _serverConfig.Discovery.Enabled,
                    discoveryHostName = Discovery?.HostName ?? _serverConfig.Discovery.HostName,
                    // logging: level, the rotating file sink, and what it has
                    // written so far, so the dialog can show the real cost
                    logLevel = _serverConfig.Logging.Level,
                    logToFile = _serverConfig.Logging.ToFile,
                    logDirectory = _serverConfig.Logging.Directory,
                    logDirectoryResolved = _serverConfig.Logging.ResolveDirectory(_baseDirectory),
                    logRotateSizeMb = _serverConfig.Logging.RotateSizeMb,
                    logRotatePeriod = _serverConfig.Logging.RotatePeriod,
                    logMaxFiles = _serverConfig.Logging.MaxFiles,
                    logFiles = Log.Files(_serverConfig.Logging.ResolveDirectory(_baseDirectory))
                        .Select(f => new { name = f.Name, bytes = f.Bytes, modified = f.Modified }),
                    // what "0.0.0.0" actually resolves to right now, so the
                    // Config dialog can show which addresses are reachable
                    interfaces = Services.NetworkInfo.Active().Select(i => new
                    {
                        name = i.Name,
                        address = i.Address,
                        kind = i.Kind,
                        primary = i.Primary,
                    }),
                });
                return;
            }

            if (method == "POST" && path == "/api/settings")
            {
                SaveSettings(ctx);
                return;
            }

            // ---- what has been watched lately ----
            if (method == "GET" && path == "/api/history")
            {
                var take = int.TryParse(ctx.Request.QueryString["count"], out var n) ? Math.Clamp(n, 1, 50) : 10;
                WriteJson(res, 200, new
                {
                    history = _history.Recent(auth.Name, take).Select(e => new
                    {
                        name = e.Name,
                        path = e.Path,
                        kind = e.Kind,
                        plays = e.Plays,
                        startedUtc = e.StartedUtc,
                        // gone from disk since — replaying it would only 404
                        missing = e.Kind == "file" && !File.Exists(e.Path),
                    }),
                });
                return;
            }

            if (method == "DELETE" && path == "/api/history")
            {
                // no path clears the caller's whole history
                var target = ctx.Request.QueryString["path"] ?? "";
                WriteJson(res, 200, new { removed = _history.Forget(auth.Name, target) });
                return;
            }

            // ---- media engine (ffmpeg) ----
            if (method == "POST" && path == "/api/play")
            {
                PlayFile(ctx, auth);
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
                        .Select(c => new { name = c.Item1.Name, url = c.Item1.Url, stream = c.Item2,
                                           status = c.Item3, started = c.Item1.Started }),
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

            if (method == "POST" && path == "/api/channels/start")
            {
                var name = ctx.Request.QueryString["name"] ?? "";
                if (_ffmpeg?.StartChannel(name) == true)
                {
                    Log.Info("control", $"channel started: {name}");
                    WriteJson(res, 200, new { started = name });
                }
                else WriteJson(res, 404, new { error = "unknown channel" });
                return;
            }

            if (method == "POST" && path == "/api/channels/stop")
            {
                var name = ctx.Request.QueryString["name"] ?? "";
                if (_ffmpeg?.StopChannel(name) == true)
                {
                    Log.Info("control", $"channel stopped: {name}");
                    WriteJson(res, 200, new { stopped = name });
                }
                else WriteJson(res, 404, new { error = "unknown channel" });
                return;
            }

            // ---- free ad-supported TV (Pluto TV + playlist providers) ----
            if (method == "GET" && path == "/api/tv/providers")
            {
                WriteJson(res, 200, new
                {
                    providers = _providers.All
                        .Where(p => p.Enabled)
                        .Select(p => new { id = p.Id, name = p.Name }),
                });
                return;
            }

            if (method == "GET" && path == "/api/tv/lineup")
            {
                await TvLineup(ctx);
                return;
            }

            if (method == "GET" && (path == "/api/tv/watch" || path == "/api/tv/r"))
            {
                await TvProxy(ctx, entry: path == "/api/tv/watch");
                return;
            }

            if (method == "POST" && path == "/api/tv/pin")
            {
                await TvPin(ctx);
                return;
            }

            if (method == "GET" && path == "/api/codecs")
            {
                WriteJson(res, 200, new
                {
                    active = new { video = _ffmpeg?.VideoEncoder, audio = _ffmpeg?.AudioEncoder },
                    videoEncoders = _ffmpeg?.VideoEncoders.OrderBy(x => x) ?? Enumerable.Empty<string>(),
                    audioEncoders = _ffmpeg?.AudioEncoders.OrderBy(x => x) ?? Enumerable.Empty<string>(),
                    note = "set ffmpeg.videoCodec / ffmpeg.audioCodec in the config (friendly name, raw encoder name, or 'copy')",
                });
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
                ServeThumbnail(ctx, auth);
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

            // attach a subtitle file the user picked to an existing stream
            if (method == "POST" && path == "/api/subtitles")
            {
                AttachSubtitle(ctx);
                return;
            }

            if (method == "GET" && path == "/api/image")
            {
                ServeImage(ctx, auth);
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
                // capture once — a concurrent /api/server/stop can null Rtsp
                var rtsp = RtspServer;
                var session = rtsp?.Sessions.Get(id);
                if (session is null)
                {
                    WriteJson(res, 404, new { error = "session not found" });
                    return;
                }
                rtsp!.Sessions.Remove(id);
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
        var mount = _serverConfig.MountsSnapshot().FirstOrDefault(m =>
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
    /// POST /api/settings — save hostname/port settings to the settings.json
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
            s = JsonSerializer.Deserialize<ServerConfig.SettingsOverrides>(ReadBody(ctx), BodyJson);
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
                WriteJson(res, 400, new { error = $"{name} must be 1–65535" });
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

        // an hour at the short end still covers a film; a year at the long
        // end is effectively "never expires", which is the caller's call
        if (s.LinkLifetimeHours is int lifetime and (< 1 or > 8760))
        {
            WriteJson(res, 400, new { error = "link lifetime must be 1–8760 hours (up to a year)" });
            return;
        }

        // ---- logging ----
        if (s.LogLevel is { Length: > 0 } lvl &&
            lvl.ToLowerInvariant() is not ("trace" or "debug" or "info" or "warn" or "error"))
        {
            WriteJson(res, 400, new { error = "log level must be trace, debug, info, warn, or error" });
            return;
        }
        if (s.LogRotatePeriod is { Length: > 0 } per &&
            per.ToLowerInvariant() is not ("none" or "hourly" or "daily" or "weekly" or "monthly"))
        {
            WriteJson(res, 400, new { error = "rotation period must be none, hourly, daily, weekly, or monthly" });
            return;
        }
        // 0 means "don't rotate on size"; 4 GB is past anything a text log
        // should reach before the period or the file count catches it
        if (s.LogRotateSizeMb is int mb and (< 0 or > 4096))
        {
            WriteJson(res, 400, new { error = "rotation size must be 0–4096 MB (0 = no size limit)" });
            return;
        }
        if (s.LogMaxFiles is int keep and (< 0 or > 1000))
        {
            WriteJson(res, 400, new { error = "kept log files must be 0–1000" });
            return;
        }
        // an unwritable directory has to fail here, not silently later
        if (s.LogDirectory is { Length: > 0 })
        {
            try
            {
                var probe = new LoggingConfig { Directory = s.LogDirectory }.ResolveDirectory(_baseDirectory);
                Directory.CreateDirectory(probe);
            }
            catch (Exception ex)
            {
                WriteJson(res, 400, new { error = "log directory unusable: " + ex.Message });
                return;
            }
        }

        var ports = new[] { s.RtspPort ?? _serverConfig.Rtsp.Port, s.HlsPort ?? _serverConfig.Hls.Port, s.ControlPort ?? _serverConfig.Control.Port };
        if (ports.Distinct().Count() != 3)
        {
            WriteJson(res, 400, new { error = "rtsp, hls, and control ports must all be different" });
            return;
        }

        var controlPortChanged = s.ControlPort is int ncp && ncp != _serverConfig.Control.Port;

        // background/tray mode toggles live — no restart needed
        bool? trayNow = null;
        if (s.MinimizeToTray is bool wantTray)
        {
            trayNow = SetTrayMode?.Invoke(wantTray) ?? false;
            if (wantTray && trayNow == false)
            {
                WriteJson(res, 400, new
                {
                    error = OperatingSystem.IsWindows()
                        ? "could not create the tray icon"
                        : "background tray mode is Windows-only; use systemd/launchd or nohup here",
                });
                return;
            }
            s.MinimizeToTray = trayNow; // persist what actually happened
        }

        // Network announcement toggles live too. Applied before the config is
        // saved so a responder that can't take its ports — another Bonjour
        // stack already holding them — is reported rather than persisted as
        // on while nothing is actually announcing.
        if (s.DiscoveryEnabled is bool wantAnnounce && Discovery is not null
            && wantAnnounce != _serverConfig.Discovery.Enabled)
        {
            _serverConfig.Discovery.Enabled = wantAnnounce;
            try
            {
                Discovery.Restart();
            }
            catch (Exception ex)
            {
                _serverConfig.Discovery.Enabled = !wantAnnounce;   // put it back
                WriteJson(res, 500, new { error = "could not change network announcement: " + ex.Message });
                return;
            }
        }

        _serverConfig.UpdateSettings(s);

        // logging applies live — the level immediately, and the file sink
        // reopened only when something about it actually changed, so saving
        // an unrelated setting doesn't rotate the log out from under a viewer
        Log.SetLevel(_serverConfig.Logging.Level);
        if (s.LogToFile is not null || s.LogDirectory is not null || s.LogRotateSizeMb is not null
            || s.LogRotatePeriod is not null || s.LogMaxFiles is not null)
        {
            Log.ConfigureFile(_serverConfig.Logging.ToFile,
                              _serverConfig.Logging.ResolveDirectory(_baseDirectory),
                              _serverConfig.Logging.RotateSizeMb,
                              _serverConfig.Logging.RotatePeriod,
                              _serverConfig.Logging.MaxFiles);
        }

        // only a bind/port change needs the listeners rebuilt
        var needsRestart = s.BindAddress is not null || s.RtspPort is not null
                           || s.HlsPort is not null || s.ControlPort is not null;
        if (needsRestart)
        {
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
        }

        Log.Info("control", $"settings saved: bind={_serverConfig.Rtsp.BindAddress} rtsp={_serverConfig.Rtsp.Port} hls={_serverConfig.Hls.Port} control={_serverConfig.Control.Port} tray={_serverConfig.MinimizeToTray}");
        WriteJson(res, 200, new
        {
            saved = true,
            servicesRestarted = needsRestart,
            controlPortChanged,
            minimizeToTray = trayNow,
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

        // a still-running conversion holds the files open — stop it first
        if (_ffmpeg?.CancelVod(name) == true)
            Thread.Sleep(300); // give the killed process a moment to release handles

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                Log.Info("control", $"HLS stream removed via dashboard: {name}");
                WriteJson(res, 200, new { removed = name });
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(300);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(300);
            }
            catch (Exception ex)
            {
                WriteJson(res, 500, new { error = "could not delete — files still in use: " + ex.Message });
                return;
            }
        }
    }

    private sealed record FavoriteRequest(string? name, string? path);

    /// <summary>POST /api/favorites {path, name?} — pin a media file as a quick button.</summary>
    private void AddFavorite(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            var req = JsonSerializer.Deserialize<FavoriteRequest>(ReadBody(ctx), BodyJson);
            if (string.IsNullOrWhiteSpace(req?.path))
            {
                WriteJson(res, 400, new { error = "body must be { \"path\": \"...\", \"name\": \"optional\" }" });
                return;
            }
            if (!TryLocalPath(req.path, out var full))
            {
                WriteJson(res, 400, new { error = "network paths are not allowed" });
                return;
            }
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

    private sealed record SubtitleRequest(string? stream, string? file, string? label);

    /// <summary>
    /// POST /api/subtitles {stream, file, label?} — attach a subtitle file
    /// the user picked to a stream; it is converted to WebVTT and appears
    /// in that stream's track list.
    /// </summary>
    private void AttachSubtitle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        if (_subtitles is null || _ffmpeg?.Available != true)
        {
            WriteJson(res, 503, new { error = "ffmpeg is not available" });
            return;
        }
        try
        {
            var req = JsonSerializer.Deserialize<SubtitleRequest>(ReadBody(ctx), BodyJson);
            if (string.IsNullOrWhiteSpace(req?.stream) || string.IsNullOrWhiteSpace(req?.file))
            {
                WriteJson(res, 400, new { error = "body must be { \"stream\": \"...\", \"file\": \"...\" }" });
                return;
            }
            if (req.stream.Contains("..") || req.stream.Contains('/') || req.stream.Contains('\\'))
            {
                WriteJson(res, 400, new { error = "invalid stream name" });
                return;
            }

            var mediaRoot = Path.GetFullPath(Path.IsPathRooted(_serverConfig.Hls.MediaRoot)
                ? _serverConfig.Hls.MediaRoot
                : Path.Combine(_baseDirectory, _serverConfig.Hls.MediaRoot));
            var streamDir = Path.GetFullPath(Path.Combine(mediaRoot, req.stream));
            if (!streamDir.StartsWith(mediaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(streamDir))
            {
                WriteJson(res, 404, new { error = "unknown stream" });
                return;
            }
            if (!TryLocalPath(req.file, out var file))
            {
                WriteJson(res, 400, new { error = "network paths are not allowed" });
                return;
            }
            // only accept real subtitle files, so this can't be used to read
            // arbitrary text files off disk as "subtitles"
            var subExt = Path.GetExtension(file).ToLowerInvariant();
            if (subExt is not (".srt" or ".vtt" or ".ass" or ".ssa" or ".sub" or ".smi" or ".sbv" or ".ttml" or ".dfxp"))
            {
                WriteJson(res, 400, new { error = "not a subtitle file (.srt, .ass, .vtt, .sub, .ssa, .smi)" });
                return;
            }
            if (!System.IO.File.Exists(file))
            {
                WriteJson(res, 404, new { error = "subtitle file not found" });
                return;
            }

            var track = _subtitles.AttachFile(file, Path.Combine(streamDir, "subs"), req.label);
            if (track is null)
            {
                WriteJson(res, 400, new { error = "could not convert that file to WebVTT — is it a subtitle file?" });
                return;
            }
            Log.Info("control", $"subtitle attached to {req.stream}: {Path.GetFileName(file)}");
            WriteJson(res, 200, new { added = track.Id, label = track.Label });
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
            var req = JsonSerializer.Deserialize<LibraryRequest>(ReadBody(ctx), BodyJson);
            if (string.IsNullOrWhiteSpace(req?.folder))
            {
                WriteJson(res, 400, new { error = "body must be { \"folder\": \"...\" }" });
                return;
            }
            if (!TryLocalPath(req.folder, out var folder))
            {
                WriteJson(res, 400, new { error = "network paths are not allowed" });
                return;
            }
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
    private void ServeThumbnail(HttpListenerContext ctx, AuthResult auth)
    {
        var res = ctx.Response;
        var path = ctx.Request.QueryString["path"] ?? "";
        try
        {
            if (!TryLocalPath(path, out var full))
            {
                WriteJson(res, 400, new { error = "network paths are not allowed" });
                return;
            }
            if (DenyUnshared(ctx, auth, full)) return;
            var thumb = _ffmpeg?.GetThumbnail(full);
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
            var req = JsonSerializer.Deserialize<PlaylistRequest>(ReadBody(ctx), BodyJson);
            if (string.IsNullOrWhiteSpace(req?.name) || string.IsNullOrWhiteSpace(req?.folder))
            {
                WriteJson(res, 400, new { error = "body must be { \"name\": \"...\", \"folder\": \"...\" }" });
                return;
            }
            if (!TryLocalPath(req.folder, out var folder))
            {
                WriteJson(res, 400, new { error = "network paths are not allowed" });
                return;
            }
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
    private void PlayFile(HttpListenerContext ctx, AuthResult auth)
    {
        var res = ctx.Response;
        if (_ffmpeg is null || !_ffmpeg.Available)
        {
            WriteJson(res, 503, new { error = "ffmpeg is not available — install it (winget install Gyan.FFmpeg) and restart" });
            return;
        }
        try
        {
            var req = JsonSerializer.Deserialize<PlayRequest>(ReadBody(ctx), BodyJson);
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
            if (!TryLocalPath(req.file, out var file))
            {
                WriteJson(res, 400, new { error = "network paths are not allowed" });
                return;
            }
            if (DenyUnshared(ctx, auth, file)) return;
            var (stream, ready) = _ffmpeg.StartVod(file, height);
            // every library play funnels through here, so this is the one
            // place that knows what was watched and by whom
            _history.Record(Path.GetFileName(file), file, "file", auth.Name);
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

    // ---- free ad-supported TV -------------------------------------------

    /// <summary>
    /// The capability string a /api/tv/watch signature covers. Naming the
    /// channel rather than the resolved URL is what lets the link outlive the
    /// provider's session token — the URL is re-resolved on every fetch.
    /// </summary>
    private static string TvScope(string provider, string channel) => $"tv:{provider}:{channel}";

    /// <summary>True when a proxy request carries a signature we minted.</summary>
    private bool IsSignedTvRequest(HttpListenerContext ctx, string path)
    {
        var q = ctx.Request.QueryString;
        var sig = q["s"];
        if (string.IsNullOrEmpty(sig)) return false;

        return path == "/api/tv/watch"
            ? _mediaLinks.VerifyUrl(TvScope(q["provider"] ?? "pluto", q["id"] ?? ""), sig)
            : _mediaLinks.VerifyUrl(q["u"] ?? "", sig);
    }

    /// <summary>
    /// GET /api/tv/lineup?provider=&amp;q=&amp;group= — one provider's channels,
    /// optionally filtered. The lineup is cached by the provider itself, so
    /// this is cheap to call while someone types in the search box.
    /// </summary>
    private async Task TvLineup(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var id = ctx.Request.QueryString["provider"] ?? "pluto";
        var provider = _providers.Get(id);
        if (provider is null || !provider.Enabled)
        {
            WriteJson(res, 404, new { error = $"unknown provider '{id}'" });
            return;
        }

        try
        {
            var all = await provider.LineupAsync(_cts.Token);

            var q = (ctx.Request.QueryString["q"] ?? "").Trim();
            var group = (ctx.Request.QueryString["group"] ?? "").Trim();
            var matches = all.Where(c =>
                (q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                               || (c.Summary?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)) &&
                (group.Length == 0 || c.Group.Equals(group, StringComparison.OrdinalIgnoreCase)));

            WriteJson(res, 200, new
            {
                provider = provider.Id,
                name = provider.Name,
                total = all.Count,
                groups = all.Select(c => c.Group).Where(g => g.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g),
                channels = matches.Select(c => new
                {
                    id = c.Id, name = c.Name, group = c.Group,
                    logo = c.LogoUrl, number = c.Number, summary = c.Summary,
                    // signed so playback works however the caller signed in —
                    // a session cookie, a device key, or an external player
                    // handed the link — without re-authorizing every segment
                    watch = $"/api/tv/watch?provider={Uri.EscapeDataString(provider.Id)}" +
                            $"&id={Uri.EscapeDataString(c.Id)}" +
                            $"&s={Uri.EscapeDataString(_mediaLinks.SignUrl(TvScope(provider.Id, c.Id)))}",
                }),
            });
        }
        catch (Exception ex)
        {
            Log.Warn("tv", $"lineup for {id} failed: {ex.Message}");
            WriteJson(res, 502, new { error = $"could not reach {provider.Name}: {ex.Message}" });
        }
    }

    /// <summary>
    /// The proxy, in its two forms.
    ///
    /// <c>/api/tv/watch?provider=&amp;id=</c> is the entry point: it resolves a
    /// freshly authorized master playlist for that channel and rewrites it.
    /// <c>/api/tv/r?u=&amp;s=</c> is every URL that rewriting produced —
    /// signed, so this cannot be pointed at anything the server did not
    /// itself hand out.
    /// </summary>
    private async Task TvProxy(HttpListenerContext ctx, bool entry)
    {
        var res = ctx.Response;
        string url;
        bool relay;
        string providerId;

        if (entry)
        {
            var id = ctx.Request.QueryString["provider"] ?? "pluto";
            var channel = ctx.Request.QueryString["id"] ?? "";
            var provider = _providers.Get(id);
            if (provider is null || !provider.Enabled)
            {
                WriteJson(res, 404, new { error = $"unknown provider '{id}'" });
                return;
            }

            string? resolved;
            try { resolved = await provider.ResolveAsync(channel, _cts.Token); }
            catch (Exception ex)
            {
                Log.Warn("tv", $"resolve {id}/{channel} failed: {ex.Message}");
                WriteJson(res, 502, new { error = ex.Message });
                return;
            }

            if (resolved is null)
            {
                WriteJson(res, 404, new { error = "unknown channel" });
                return;
            }
            url = resolved;
            providerId = provider.Id;
            relay = _relayProviders.Contains(provider.Id);
        }
        else
        {
            url = ctx.Request.QueryString["u"] ?? "";
            providerId = ctx.Request.QueryString["p"] ?? "";
            var sig = ctx.Request.QueryString["s"];
            relay = ctx.Request.QueryString["relay"] == "1";
            if (!_mediaLinks.VerifyUrl(url, sig))
            {
                // an unsigned target is either tampering or a stale link from
                // before the key was regenerated; neither is worth fetching
                WriteJson(res, 403, new { error = "bad or missing signature" });
                return;
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) ||
            target.Scheme is not ("http" or "https"))
        {
            WriteJson(res, 400, new { error = "target must be an http(s) URL" });
            return;
        }

        try
        {
            // The signature covers the URL as it was minted, so it is checked
            // above against the original; only then is the stale token in it
            // swapped for a live one.
            var owner = _providers.Get(providerId);
            if (owner is not null) url = await owner.RefreshAsync(url, _cts.Token);

            var result = await _tvProxy.FetchAsync(url, providerId, "/api/tv/r", relay, _cts.Token);
            res.StatusCode = result.Status;
            res.ContentType = result.ContentType;
            // live playlists change every few seconds; nothing here may be held
            res.Headers["Cache-Control"] = "no-store";
            res.ContentLength64 = result.Body.Length;
            await res.OutputStream.WriteAsync(result.Body, _cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warn("tv", $"proxy failed: {ex.Message}");
            try { WriteJson(res, 502, new { error = ex.Message }); } catch { }
        }
    }

    private sealed record TvPinRequest(string? provider, string? id, string? name);

    /// <summary>
    /// POST /api/tv/pin {provider,id,name} — turn a provider channel into a
    /// permanent local channel.
    ///
    /// The channel is pointed at this server's own proxy URL rather than at
    /// the provider, so the restream keeps working after the provider's
    /// session token has rolled over.
    /// </summary>
    private async Task TvPin(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        if (_ffmpeg is null || !_ffmpeg.Available)
        {
            WriteJson(res, 503, new { error = "ffmpeg is not available — install it (winget install Gyan.FFmpeg) and restart" });
            return;
        }

        try
        {
            var req = JsonSerializer.Deserialize<TvPinRequest>(ReadBody(ctx), BodyJson);
            var providerId = req?.provider ?? "pluto";
            var channelId = req?.id ?? "";
            var provider = _providers.Get(providerId);
            if (provider is null || !provider.Enabled || string.IsNullOrWhiteSpace(channelId))
            {
                WriteJson(res, 400, new { error = "body must be { \"provider\": \"…\", \"id\": \"…\" }" });
                return;
            }

            var lineup = await provider.LineupAsync(_cts.Token);
            var channel = lineup.FirstOrDefault(c => c.Id == channelId);
            if (channel is null)
            {
                WriteJson(res, 404, new { error = "unknown channel" });
                return;
            }

            var name = string.IsNullOrWhiteSpace(req?.name) ? channel.Name : req!.name!.Trim();
            var sig = _mediaLinks.SignUrl(TvScope(provider.Id, channel.Id));
            var url = $"http://127.0.0.1:{_config.Port}/api/tv/watch" +
                      $"?provider={Uri.EscapeDataString(provider.Id)}&id={Uri.EscapeDataString(channel.Id)}" +
                      $"&s={Uri.EscapeDataString(sig)}";

            // saved idle: pinning is bookmarking, and a restream is a
            // transcode that runs until it's stopped — starting one is the
            // user's call, from the Live channels card
            var stream = _ffmpeg.AddChannel(name, url, start: false);
            Log.Info("control", $"pinned {provider.Name} channel: {name} (idle)");
            WriteJson(res, 200, new { stream, playlist = $"/{stream}/index.m3u8", name, started = false });
        }
        catch (InvalidOperationException ex) { WriteJson(res, 409, new { error = ex.Message }); }
        catch (Exception ex) { WriteJson(res, 400, new { error = ex.Message }); }
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
            var req = JsonSerializer.Deserialize<ChannelRequest>(ReadBody(ctx), BodyJson);
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
    private void ServeImage(HttpListenerContext ctx, AuthResult auth)
    {
        var res = ctx.Response;
        var path = ctx.Request.QueryString["path"] ?? "";
        try
        {
            if (!TryLocalPath(path, out var full))
            {
                WriteJson(res, 400, new { error = "network paths are not allowed" });
                return;
            }
            if (DenyUnshared(ctx, auth, full)) return;
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
            mount = JsonSerializer.Deserialize<MountConfig>(ReadBody(ctx), BodyJson);
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
                var rawFile = Path.IsPathRooted(mount.File) ? mount.File : Path.Combine(_baseDirectory, mount.File);
                if (!TryLocalPath(rawFile, out var full))
                {
                    WriteJson(res, 400, new { error = "network paths are not allowed" });
                    return;
                }
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
            if (!TryLocalPath(path, out var full))
            {
                WriteJson(res, 400, new { error = "network paths are not allowed" });
                return;
            }
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
                    // readable label for the library; paths still use `name`
                    title = Media.StreamTitle.PrettifyFile(f.Name),
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

    /// <summary>
    /// Canonicalizes a path and rejects network paths. UNC targets
    /// (\\host\share, //host/share) make the server open an outbound SMB
    /// connection to an attacker-chosen host, leaking its NTLM credentials —
    /// so those are refused on every path-taking endpoint regardless of who
    /// is calling. Returns false (with full=null) when the path is unsafe.
    /// </summary>
    private static bool TryLocalPath(string? path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        // reject UNC before canonicalizing (GetFullPath preserves the \\ prefix)
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try
        {
            var resolved = Path.GetFullPath(path);
            if (resolved.StartsWith(@"\\", StringComparison.Ordinal)) return false; // e.g. \\?\UNC, mapped
            full = resolved;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Addresses the media port is reachable on. Bound to a single address
    /// that's the only answer; bound to a wildcard it is every connected
    /// network, which is the case where a link built from whatever host the
    /// dashboard happens to be open on is the wrong one to hand to a phone
    /// on a different subnet.
    /// </summary>
    private object[] MediaAddresses()
    {
        var bind = _serverConfig.Hls.BindAddress;
        if (bind is not ("0.0.0.0" or "::" or "*"))
            return new object[] { new { name = "", address = bind, kind = "", primary = true } };

        return Services.NetworkInfo.Active()
            .Select(i => (object)new { name = i.Name, address = i.Address, kind = i.Kind, primary = i.Primary })
            .ToArray();
    }

    /// <summary>Bodies here are small JSON objects; nothing legitimate comes close.</summary>
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>
    /// Reads a request body, refusing an oversized one before it costs
    /// anything. ReadToEnd on an HttpListener stream reads whatever the
    /// client cares to send — a 39 MB body turned into roughly 660 MB of
    /// process memory once decoded to UTF-16 and handed to the parser, so a
    /// handful of concurrent requests could take the server down.
    /// </summary>
    private static string ReadBody(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxBodyBytes) throw TooLarge();

        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var buffer = new char[MaxBodyBytes];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        // filled the buffer and there is still more coming (covers chunked
        // bodies, which advertise no length at all)
        if (read == buffer.Length && reader.Peek() >= 0) throw TooLarge();
        return new string(buffer, 0, read);
    }

    private static InvalidDataException TooLarge() =>
        new($"request body is larger than {MaxBodyBytes / 1024} KB");

    private void WriteJson(HttpListenerResponse res, int status, object body)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true });
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
        try { _providers.Dispose(); } catch { }
        try { _providerHttp.Dispose(); } catch { }
    }
}

