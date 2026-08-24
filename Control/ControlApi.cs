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
    /// <summary>Which conversions are listed; see StreamLinks. Removing a link keeps the files.</summary>
    private readonly Media.StreamLinks _links;

    /// <summary>The same store, for the HLS server to filter its listing by.</summary>
    public Media.StreamLinks Listed => _links;
    private readonly Media.LibraryStore _library;
    private readonly Media.FavoritesStore _favorites;
    private readonly Media.WatchHistory _history;

    /// <summary>
    /// The DLNA server, or null when it is off — which is also the switch
    /// the request path checks, so turning it off leaves nothing listening
    /// rather than something that refuses politely.
    /// </summary>
    private Dlna.DlnaService? _dlna;

    /// <summary>
    /// Which library folders DLNA may show. Kept outside the service so the
    /// choice survives switching DLNA off and on again.
    /// </summary>
    private readonly Dlna.DlnaShare _dlnaShare;

    /// <summary>
    /// The file a stream was transcoded from, or empty when it wasn't one —
    /// a live channel, or a directory of segments dropped in by hand.
    ///
    /// The transcoder already leaves a source.txt in each stream directory
    /// (SubtitleManager reads it too), so this needs no bookkeeping of its
    /// own and keeps working across a restart, which an in-memory map of
    /// "streams prepared this run" would not.
    /// </summary>
    private string SourceFileFor(string stream)
    {
        if (string.IsNullOrWhiteSpace(stream) || stream.Contains("..")
            || stream.Contains('/') || stream.Contains('\\')) return "";
        try
        {
            var root = Path.GetFullPath(Path.IsPathRooted(_serverConfig.Hls.MediaRoot)
                ? _serverConfig.Hls.MediaRoot
                : Path.Combine(_baseDirectory, _serverConfig.Hls.MediaRoot));
            var marker = Path.Combine(root, stream, "source.txt");
            if (!File.Exists(marker)) return "";
            var file = File.ReadAllText(marker).Trim();
            return File.Exists(file) ? file : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Whether a restream is writing subtitles alongside its video. ffmpeg
    /// names the subtitle playlist after the media one — index.m3u8 gets
    /// index_vtt.m3u8 — and refers to it from nowhere, so its presence on
    /// disk is what says the channel has subtitles to offer.
    /// </summary>
    private bool HasSubtitleRendition(string stream)
    {
        try
        {
            var root = Path.GetFullPath(Path.IsPathRooted(_serverConfig.Hls.MediaRoot)
                ? _serverConfig.Hls.MediaRoot
                : Path.Combine(_baseDirectory, _serverConfig.Hls.MediaRoot));
            return File.Exists(Path.Combine(root, stream, "index_vtt.m3u8"));
        }
        catch { return false; }
    }

    private Dlna.DlnaService NewDlna()
    {
        var dlna = new Dlna.DlnaService(
            _library, _dlnaShare, () => _serverConfig.ServerName,
            Discovery?.Uuid ?? _serverConfig.Discovery.HostName);
        dlna.FindTranscode = FullResTranscodeFor;
        return dlna;
    }

    /// <summary>
    /// A finished, full-resolution conversion of a library file, or null.
    ///
    /// Full resolution only, and that is the whole point of the check. The
    /// quality picker produces downscaled conversions —
    /// <c>vod-dune-720p-…</c> beside a full-size <c>vod-dune-…</c> — and
    /// handing a 720p copy to a 4K television because it happened to be in
    /// the cache would quietly cost picture nobody asked to lose. The name
    /// says which is which by construction: StartVod appends -{height}p only
    /// when it scaled, and forces height to 0 when the video is copied
    /// rather than encoded, so no suffix means no scaling.
    ///
    /// Unfinished conversions are skipped too: a playlist without
    /// EXT-X-ENDLIST is still being written, and serving it would hand over
    /// a film that stops partway with no explanation.
    /// </summary>
    private Dlna.DlnaService.Transcode? FullResTranscodeFor(string sourceFile)
    {
        try
        {
            // Per file, not per preference. A conversion is a re-encode, so
            // substituting one for a file the television could have played
            // by itself costs picture for nothing — and never substituting
            // one takes away the only copy of an XviD rip the set can play.
            // Neither is a setting: which of the two is right is a fact
            // about the file, and the codec answers it.
            //
            // dlnaUseTranscode forces substitution regardless, for a set that
            // rejects something this check thinks is fine.
            if (!_serverConfig.Discovery.DlnaUseTranscode
                && _tvCodecs?.NeedsConversion(sourceFile) != true)
                return null;

            var mediaRoot = Path.GetFullPath(Path.IsPathRooted(_serverConfig.Hls.MediaRoot)
                ? _serverConfig.Hls.MediaRoot
                : Path.Combine(_baseDirectory, _serverConfig.Hls.MediaRoot));
            if (!Directory.Exists(mediaRoot)) return null;

            foreach (var dir in Directory.EnumerateDirectories(mediaRoot, "vod-*"))
            {
                // "vod-dune-720p-a1b2c3d4" was scaled; "vod-dune-a1b2c3d4" was not
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        Path.GetFileName(dir), @"-\d+p-[0-9a-f]{8}$")) continue;

                string src;
                try { src = File.ReadAllText(Path.Combine(dir, "source.txt")).Trim(); }
                catch { continue; }
                if (!src.Equals(sourceFile, StringComparison.OrdinalIgnoreCase)) continue;

                var playlist = Path.Combine(dir, "index.m3u8");
                string text;
                try { text = File.ReadAllText(playlist); }
                catch { continue; }
                if (!text.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal)) continue;  // still converting

                var parts = new List<(string, long)>();
                long total = 0;
                // the fMP4 initialisation segment belongs first, or the
                // fragments after it are not a playable stream
                var init = Path.Combine(dir, "init.mp4");
                if (File.Exists(init)) { var l = new FileInfo(init).Length; parts.Add((init, l)); total += l; }

                foreach (var line in text.Split('\n'))
                {
                    var name = line.Trim();
                    if (name.Length == 0 || name[0] == '#') continue;
                    var seg = Path.Combine(dir, name.Split('?')[0]);
                    if (!File.Exists(seg)) return null;      // a gap would be a corrupt stream
                    var len = new FileInfo(seg).Length;
                    parts.Add((seg, len));
                    total += len;
                }
                if (parts.Count == 0 || total == 0) continue;

                var mp4 = parts[^1].Item1.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) || File.Exists(init);
                return new Dlna.DlnaService.Transcode(parts, total, mp4 ? "video/mp4" : "video/mp2t");
            }
        }
        catch (Exception ex) { Log.Debug("dlna", $"could not look for a conversion: {ex.Message}"); }
        return null;
    }

    /// <summary>Turns DLNA on or off while the server runs; returns what it now is.</summary>
    public bool SetDlna(bool on)
    {
        _dlna = on ? (_dlna ?? NewDlna()) : null;
        // Under TLS, DLNA lives on a plain-HTTP port of its own, which only
        // exists while DLNA does. Switching it on in the dashboard has to
        // open that port too, or the switch would do nothing until a restart.
        if (_dlna is not null) StartDlnaListener();
        else StopDlnaListener();
        return _dlna is not null;
    }

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
    private readonly Media.TvCodecs? _tvCodecs;

    /// <summary>
    /// Writes out whatever this API is holding in memory. Called on a timer
    /// and on the way down — see Services.StateSaver for why both.
    ///
    /// Only the codec cache needs it today: the other stores here write on
    /// every change, because a favourite or a channel is one small edit and
    /// there is no reason to defer it. Probing is the opposite — thousands
    /// of results accumulated over an hour — so it is the one that would
    /// hurt to lose.
    /// </summary>
    public void FlushState() => _tvCodecs?.Save();

    /// <summary>The HLS media cache, absolute. Conversions live here.</summary>
    private string MediaRootPath() => Path.GetFullPath(Path.IsPathRooted(_serverConfig.Hls.MediaRoot)
        ? _serverConfig.Hls.MediaRoot
        : Path.Combine(_baseDirectory, _serverConfig.Hls.MediaRoot));

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
        _links = new Media.StreamLinks(baseDirectory);
        _favorites = new Media.FavoritesStore(baseDirectory);
        _history = new Media.WatchHistory(baseDirectory);
        _dlnaShare = new Dlna.DlnaShare(baseDirectory);

        // What a television can and cannot decode — needs ffmpeg to probe
        // with, so it stays null without one and the DLNA path falls back
        // to handing over originals, as it always did.
        if (ffmpeg is not null)
            _tvCodecs = new Media.TvCodecs(baseDirectory, ffmpeg.FfprobePath);

        if (serverConfig.Discovery.Dlna) _dlna = NewDlna();
        // The moment somebody starts watching anything — dashboard, phone,
        // VLC, a shared link — it goes in the history. Preparing a file
        // through /api/play records it too, but most playback never touches
        // that endpoint: an existing stream is simply requested.
        _services.Viewers.ViewingStarted += v =>
        {
            // The file behind it, when there is one — that is what makes the
            // entry replayable after the transcode cache has been swept. DLNA
            // hands over the file directly, so there is nothing to look up;
            // an HLS stream keeps it in a source.txt beside the segments.
            var file = v.File ?? SourceFileFor(v.Stream);
            var name = file.Length > 0
                ? Media.StreamTitle.PrettifyFile(Path.GetFileName(file))
                : Media.StreamTitle.Prettify(v.Stream);
            // a DLNA viewing has no stream directory, and recording the file
            // path as one would make the entry unplayable from the dashboard
            var stream = v.Protocol == "dlna" ? "" : v.Stream;
            // and no account either — v.User carries a label for the sessions
            // table, not a name the history can file anything under
            var user = v.Protocol == "dlna" ? "" : v.User;
            _history.Record(name, file, stream, file.Length > 0 ? "file" : "stream", user);
        };

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
            // a refresh reconnects within a second: no shutdown, and no
            // "still running in the background" for a page that never left
            if (_closedNoticeTimer is not null)
            {
                _closedNoticeTimer.Dispose();
                _closedNoticeTimer = null;
            }
            if (_closeShutdownTimer is null) return;
            Log.Info("control", "activity detected — shutdown cancelled");
            _closeShutdownTimer.Dispose();
            _closeShutdownTimer = null;
        }
    }

    /// <summary>
    /// Raised a couple of seconds after the last dashboard closes while the
    /// server is set to keep running — where something has to say so.
    /// </summary>
    public Action? OnDashboardClosed { get; set; }

    private Timer? _closedNoticeTimer;

    public void Start()
    {
        (var listener, var bound) = Hls.HttpListenerBinder.Start(_config.BindAddress, _config.Port, "control");
        _listener = listener;
        BoundHost = bound;
        Log.Info("control", $"listening on {Services.UrlScheme.Prefix}{bound}:{_config.Port}/api/");
        _ = AcceptLoopAsync();
        StartDlnaListener();
    }

    /// <summary>
    /// The port DLNA is actually served on: its own when the dashboard has
    /// moved to TLS, otherwise the control port like everything else.
    /// </summary>
    public int DlnaPort => Services.DlnaEndpoint.PortFor(_serverConfig);

    private HttpListener? _dlnaListener;

    /// <summary>
    /// A second listener, in the clear, carrying nothing but DLNA.
    ///
    /// Only when the control port has gone to TLS: a TV cannot speak it, and
    /// DLNA has no credentials to protect anyway. Everything else — the
    /// dashboard, the API, the media — stays on the encrypted port. This one
    /// answers /dlna/* and the UPnP description that points at them, refuses
    /// anything else outright, and is still restricted to private addresses.
    /// </summary>
    private void StartDlnaListener()
    {
        if (_dlna is null || DlnaPort == _config.Port || _dlnaListener is not null) return;
        try
        {
            var (listener, bound) = Hls.HttpListenerBinder.StartPlain(_config.BindAddress, DlnaPort, "dlna");
            _dlnaListener = listener;
            Log.Info("dlna", $"serving DLNA in the clear on http://{bound}:{DlnaPort}/ — " +
                             "TVs cannot do TLS, and DLNA has no sign-in to protect");
            _ = DlnaAcceptLoopAsync(listener);
        }
        catch (Exception ex)
        {
            Log.Error("dlna", $"could not open the DLNA port {DlnaPort}: {ex.Message}");
        }
    }

    private void StopDlnaListener()
    {
        var listener = Interlocked.Exchange(ref _dlnaListener, null);
        if (listener is null) return;
        try { listener.Close(); } catch { }
        Log.Info("dlna", $"closed the plain-HTTP DLNA port {DlnaPort}");
    }

    /// <summary>
    /// Takes its listener as an argument rather than reading the field: the
    /// field is cleared when DLNA is switched off, and a loop still awaiting
    /// the old listener must end rather than spin on a disposed object.
    /// </summary>
    private async Task DlnaAcceptLoopAsync(HttpListener listener)
    {
        while (!_cts.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception) when (_cts.IsCancellationRequested || !listener.IsListening) { break; }
            catch (Exception ex) { Log.Warn("dlna", $"accept failed: {ex.Message}"); continue; }
            _ = Task.Run(() => HandleDlnaOnly(ctx));
        }
    }

    /// <summary>
    /// This port's entire vocabulary: the description document a client
    /// fetches after discovery, and the DLNA services it names. Anything
    /// else here is a 404 — the dashboard, the API and the media are on the
    /// TLS port and are not reachable through this door.
    /// </summary>
    private void HandleDlnaOnly(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var dlna = _dlna;
            if (dlna is null) { res.StatusCode = 404; res.Close(); return; }

            if (!Dlna.DlnaService.IsLocalClient(ctx.Request.RemoteEndPoint?.Address))
            {
                Log.Warn("dlna", $"refused a non-local request from {ctx.Request.RemoteEndPoint?.Address}");
                res.StatusCode = 403;
                res.Close();
                return;
            }

            if (ctx.Request.HttpMethod == "GET" && path == "/description.xml" && Discovery is not null)
            {
                var host = ctx.Request.Headers["Host"] ?? $"{BoundHost}:{DlnaPort}";
                WriteXml(res, 200, Discovery.DescriptionXml(host.Split(':')[0], DlnaPort, "http"));
                return;
            }

            if (path.StartsWith("/dlna/", StringComparison.OrdinalIgnoreCase))
            {
                ServeDlna(ctx, path, ctx.Request.HttpMethod, dlna);
                return;
            }

            res.StatusCode = 404;
            res.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("dlna", $"request failed: {ex.Message}");
            try { res.StatusCode = 500; res.Close(); } catch { }
        }
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
            // the log names paths, accounts and client addresses — the most
            // revealing thing the server holds, so it belongs to whoever
            // runs the machine rather than to whoever runs the library
            case "/api/log":
            // batch transcoding runs the machine hard and reaches any path on
            // disk, the same class of power as the log and the config
            case "/api/transcode":
            case "/api/transcode/scan":
            case "/api/transcode/config":
            case "/api/transcode/remove":
            case "/api/transcode/delete":
                return AccessLevel.ServerAdmin;

            // what an unauthenticated protocol is allowed to see is an
            // administrator's decision, not an editor's
            case "/api/dlna":
            case "/api/config":
            case "/api/settings":
            case "/api/server/start":
            case "/api/server/stop":
            case "/api/server/restart":
                return AccessLevel.Admin;

            // picking a path off this machine, and the codec list that the
            // add-content forms show
            case "/api/browse":
            case "/api/codecs":
            // reading a tuner's lineup and saving channels from it is the
            // same act as adding one by hand
            case "/api/tuner":
            case "/api/channels/import":
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
                or "/api/favorites" or "/api/playlists" or "/api/hls" or "/api/hls/retranscode")
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

            // ---- DLNA ----
            // Unauthenticated, because the protocol has no way to carry a
            // credential: a TV browsing a media server sends SOAP and plain
            // GETs and nothing else. The compensating controls are that it
            // is off unless switched on, that only private LAN addresses are
            // answered, and that every object id is checked against the
            // library roots before it names a file.
            if (path.StartsWith("/dlna/", StringComparison.OrdinalIgnoreCase))
            {
                // captured once: DLNA can be switched off from the Config
                // dialog on another thread, and re-reading the field after
                // this check is how a served request becomes a null reference
                var dlna = _dlna;
                if (dlna is null)
                {
                    WriteJson(res, 404, new { error = "DLNA is off — enable discovery.dlna to serve the library to TVs" });
                    return;
                }
                if (!Dlna.DlnaService.IsLocalClient(ctx.Request.RemoteEndPoint?.Address))
                {
                    Log.Warn("dlna", $"refused a non-local request from {ctx.Request.RemoteEndPoint?.Address}");
                    WriteJson(res, 403, new { error = "DLNA is served to the local network only" });
                    return;
                }
                ServeDlna(ctx, path, method, dlna);
                return;
            }

            // A player on its own page, opened in a tab of its own so the
            // picture gets the whole window. It takes the source as a
            // parameter rather than a stream name because not everything
            // playable is a stream on disk — a free-TV channel comes through
            // the proxy on this same port — and it sits here rather than on
            // the HLS port so both kinds are same-origin with the dashboard.
            if (method == "GET" && path == "/player")
            {
                if (auth.Level == AccessLevel.None) { WriteJson(res, 401, new { error = "sign in first" }); return; }
                var src = ctx.Request.QueryString["src"] ?? "";
                var title = ctx.Request.QueryString["title"] ?? "";
                // This machine only. A path is one of ours by definition;
                // an absolute URL has to name this same host, because the
                // media it plays is served from another port on it — the
                // HLS port — and the dashboard builds those links absolute.
                // What it must never be is a way to point the browser at
                // somewhere else entirely.
                if (!IsOwnMediaUrl(src, ctx))
                {
                    WriteJson(res, 400, new { error = "src must be a path or a URL on this server" });
                    return;
                }
                var page = Encoding.UTF8.GetBytes(PlayerPage(src, title));
                res.StatusCode = 200;
                res.ContentType = "text/html; charset=utf-8";
                res.Headers["Cache-Control"] = "no-store";
                res.ContentLength64 = page.Length;
                res.OutputStream.Write(page);
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
                await TvProxy(ctx, entry: path == "/api/tv/watch", auth);
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
                else if (OnDashboardClosed is not null)
                {
                    // Background mode: closing the page leaves the server
                    // running, which is worth saying — it is the opposite of
                    // what closing a window usually means. Held for a moment
                    // first, because this same beacon fires on a refresh and
                    // on a tab switch, and a balloon for those would be noise.
                    lock (_shutdownLock)
                    {
                        _closedNoticeTimer?.Dispose();
                        _closedNoticeTimer = new Timer(_ =>
                        {
                            Log.Info("control", "dashboard closed — still running in the background");
                            OnDashboardClosed?.Invoke();
                        }, null, 2000, Timeout.Infinite);
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
                        // the machine's own clock, for the dashboard's header:
                        // an instant, the offset that turns it into local time
                        // here, and what this zone is called
                        timeUtc = DateTime.UtcNow,
                        utcOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,
                        timeZone = ZoneAbbreviation(),
                        timeZoneFull = TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now)
                            ? TimeZoneInfo.Local.DaylightName
                            : TimeZoneInfo.Local.StandardName,
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
                        // files waiting to convert, in order — the dashboard
                        // lists these with a remove button (running ones can't
                        // be removed here, only what hasn't started)
                        transcodeQueue = (_ffmpeg?.VodQueueSnapshot ?? (IReadOnlyList<string>)Array.Empty<string>())
                            .Select(p => new { path = p, title = Media.StreamTitle.PrettifyFile(Path.GetFileName(p)) }),
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
                            // an RTSP mount path is already the readable name
                            title = s.MountPath,
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
                            protocol = v.Protocol,
                            id = v.Id,
                            // a DLNA viewing is identified by the file itself;
                            // the folder above it is the useful part to show
                            mount = v.File is not null ? Path.GetFileName(v.File) : v.Stream,
                            // "vod-skyfall-2012-1080p-brrip-df019bf7" tells
                            // you nothing at a glance; "Skyfall (2012)" does,
                            // and a free-TV channel is named by its lineup
                            title = v.File is not null
                                ? Media.StreamTitle.PrettifyFile(Path.GetFileName(v.File))
                                : v.Protocol == "tv"
                                    ? _tvNames.TryGetValue(v.Stream, out var chName) ? chName : v.Stream
                                    : Media.StreamTitle.Prettify(v.Stream),
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

            if (method == "GET" && path == "/api/transcode/scan")
            {
                TranscodeScan(ctx);
                return;
            }
            if (method == "POST" && path == "/api/transcode")
            {
                TranscodeBatch(ctx);
                return;
            }
            if (path == "/api/transcode/config" && method is "GET" or "POST")
            {
                TranscodeConfig(ctx, method == "POST");
                return;
            }
            if (method == "POST" && path == "/api/transcode/remove")
            {
                TranscodeRemove(ctx);
                return;
            }
            if (method == "POST" && path == "/api/transcode/delete")
            {
                TranscodeDelete(ctx);
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

            // Restarting the *process*, not the streaming services: the
            // settings that only apply at startup — the control port, TLS —
            // otherwise leave the dashboard telling someone to go and do it
            // themselves, which on a tray-mode server means hunting for the
            // icon. Not on Unix: there the server belongs to systemd or
            // launchd, and relaunching itself would fight whatever supervises
            // it.
            if (method == "POST" && path == "/api/server/restart")
            {
                if (!OperatingSystem.IsWindows())
                {
                    WriteJson(res, 501, new { error = "restart the service through systemd/launchd on this platform" });
                    return;
                }
                var comeBackTo = Services.NetworkInfo.DashboardUrls(_config.BindAddress, _config.Port)[0];
                if (!ScheduleRestart())
                {
                    WriteJson(res, 500, new { error = "could not schedule the restart — start the server again yourself" });
                    return;
                }
                // answered before the shutdown begins, or the caller sees the
                // connection drop instead of the address to come back to
                WriteJson(res, 200, new { restarting = true, url = comeBackTo });
                _ = Task.Run(async () =>
                {
                    await Task.Delay(400);      // let the response reach the browser
                    Log.Info("control", "restarting at the dashboard's request");
                    _requestShutdown?.Invoke();
                });
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
                    dlnaEnabled = _serverConfig.Discovery.Dlna,
                    // what is configured, and what is actually being served
                    // right now — they differ between saving and restarting
                    httpsEnabled = _serverConfig.Https.Enabled,
                    httpsActive = Services.UrlScheme.Https,
                    httpsOwnCertificate = string.IsNullOrWhiteSpace(_serverConfig.Https.Certificate),
                    // DLNA serves the library folders and nothing else, so an
                    // empty library is worth saying before the switch is
                    // thrown rather than after a TV shows an empty list
                    dlnaFolders = _library.All.Count,
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

            // ---- the log, for the dashboard's panel ----
            if (method == "GET" && path == "/api/log")
            {
                _ = long.TryParse(ctx.Request.QueryString["since"], out var since);
                var take = int.TryParse(ctx.Request.QueryString["max"], out var m) ? Math.Clamp(m, 1, 500) : 200;
                var (entries, last, missed) = Log.Since(since, take);
                WriteJson(res, 200, new
                {
                    entries = entries.Select(e => new
                    {
                        seq = e.Seq,
                        level = e.Level,
                        area = e.Area,
                        message = e.Message,
                        at = e.At.ToString("HH:mm:ss.fff"),
                    }),
                    last,
                    // the ring wrapped past what they had — there is a hole
                    missed,
                    level = _serverConfig.Logging.Level,
                    file = Log.FilePath,
                });
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
                        stream = e.Stream,
                        kind = e.Kind,
                        plays = e.Plays,
                        startedUtc = e.StartedUtc,
                        // empty = watched with no account, which today means
                        // a television over DLNA; the list says so rather
                        // than leaving it looking like the caller's own play
                        viaDlna = e.User.Length == 0,
                        // a file that has been deleted, or a stream that has
                        // since been evicted from the cache: either way there
                        // is nothing left to replay
                        missing = e.Kind == "file"
                            ? !File.Exists(e.Path)
                            : !StreamExists(e.Stream),
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
                        // subtitles: whether the restream is writing a
                        // subtitle rendition, so the dashboard links the
                        // master playlist that names it rather than the bare
                        // media one. Channels are not in the HLS listing —
                        // this card is the only place that can report it.
                        .Select(c => new { name = c.Item1.Name, url = c.Item1.Url, stream = c.Item2,
                                           status = c.Item3, started = c.Item1.Started,
                                           subtitles = HasSubtitleRendition(c.Item2) }),
                });
                return;
            }

            if (method == "POST" && path == "/api/channels")
            {
                AddChannel(ctx);
                return;
            }

            // ---- what DLNA is allowed to show ----
            if (method == "GET" && path == "/api/dlna")
            {
                var roots = _library.All;
                var shared = _dlnaShare.Shared(roots).ToHashSet(StringComparer.OrdinalIgnoreCase);
                WriteJson(res, 200, new
                {
                    enabled = _serverConfig.Discovery.Dlna,
                    port = DlnaPort,
                    // true when DLNA sits on a plain-HTTP port of its own
                    // because the rest of the server moved to TLS
                    plainPort = Services.DlnaEndpoint.IsSeparate(_serverConfig),
                    sharingAll = _dlnaShare.SharingAll(roots),
                    folders = roots.Select(f => new
                    {
                        path = f,
                        name = Path.GetFileName(Path.TrimEndingDirectorySeparator(f)) is { Length: > 0 } n ? n : f,
                        shared = shared.Contains(f),
                        missing = !Directory.Exists(f),
                    }),
                });
                return;
            }

            if (method == "POST" && path == "/api/dlna")
            {
                SetDlnaShare(ctx);
                return;
            }

            // ---- HDHomeRun: read a tuner's lineup, import what's picked ----
            if (method == "GET" && path == "/api/tuner")
            {
                await ReadTunerLineup(ctx);
                return;
            }

            if (method == "POST" && path == "/api/channels/import")
            {
                ImportChannels(ctx);
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
                await TvProxy(ctx, entry: path == "/api/tv/watch", auth);
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

            if (method == "GET" && path == "/api/library/search")
            {
                SearchLibrary(ctx);
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

            // Convert this media again from scratch: for a conversion that
            // came out wrong, or one made before the codec settings changed.
            // Unlinking keeps a conversion precisely because rebuilding it
            // would produce the same bytes — this is the case where that is
            // not true and the old one has to go.
            if (method == "POST" && path == "/api/hls/retranscode")
            {
                var stream = ctx.Request.QueryString["stream"] ?? "";
                if (stream.Length == 0 || stream.Contains("..") || stream.Contains('/') || stream.Contains('\\'))
                {
                    WriteJson(res, 400, new { error = "invalid stream name" });
                    return;
                }
                var root = Path.GetFullPath(Path.IsPathRooted(_serverConfig.Hls.MediaRoot)
                    ? _serverConfig.Hls.MediaRoot
                    : Path.Combine(_baseDirectory, _serverConfig.Hls.MediaRoot));
                var sdir = Path.GetFullPath(Path.Combine(root, stream));
                if (!sdir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !Directory.Exists(sdir))
                {
                    WriteJson(res, 404, new { error = "unknown stream" });
                    return;
                }
                // the source is only knowable from inside the directory, so
                // read it before the directory is removed
                string source;
                try { source = File.ReadAllText(Path.Combine(sdir, "source.txt")).Trim(); }
                catch { source = ""; }
                if (source.Length == 0 || !File.Exists(source))
                {
                    WriteJson(res, 400, new
                    {
                        error = "this stream has no source file on record, so it cannot be rebuilt — "
                              + "removing it would lose it for good",
                    });
                    return;
                }

                _ffmpeg?.DiscardVod(stream);
                var (rebuilt, ready) = _ffmpeg?.StartVod(source) ?? (stream, false);
                _links.Show(rebuilt);
                Log.Info("control", $"retranscoding {stream} from {Path.GetFileName(source)}");
                WriteJson(res, 200, new { stream = rebuilt, ready });
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
        // TLS is a restart: the listeners are bound already, and binding the
        // certificate to the ports needs the elevation prompt that startup
        // asks for. Saving it here only records the decision.
        var httpsChanged = s.HttpsEnabled is bool wantTls && wantTls != _serverConfig.Https.Enabled;

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

        // DLNA changes what the server announces itself as — a Basic device
        // or a MediaServer — so the responders have to be rebuilt, same as
        // the announcement switch itself.
        var dlnaChanged = s.DlnaEnabled is bool wantDlna && wantDlna != _serverConfig.Discovery.Dlna;
        if (s.DlnaEnabled is bool dlnaOn)
        {
            _serverConfig.Discovery.Dlna = dlnaOn;
            SetDlna(dlnaOn);
            if (dlnaChanged && Discovery is not null && _serverConfig.Discovery.Enabled)
            {
                try { Discovery.Restart(); }
                catch (Exception ex) { Log.Warn("dlna", $"could not re-announce: {ex.Message}"); }
            }
            if (dlnaChanged)
                Log.Info("dlna", dlnaOn
                    ? $"serving {_library.All.Count} library folder(s) to the local network — DLNA has no sign-in"
                    : "off");
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
        if (httpsChanged)
            Log.Info("tls", _serverConfig.Https.Enabled
                ? "HTTPS switched on — takes effect when the server restarts"
                : "HTTPS switched off — takes effect when the server restarts");

        WriteJson(res, 200, new
        {
            saved = true,
            servicesRestarted = needsRestart,
            controlPortChanged,
            httpsChanged,
            httpsEnabled = _serverConfig.Https.Enabled,
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

        // Unlink, don't delete. The conversion is expensive and the file it
        // came from has not changed, so throwing it away means running the
        // identical job again the next time anyone plays that media. The row
        // goes; the directory stays, and StartVod re-links it. The LRU cap
        // (ffmpeg.vodCacheMaxGb) reclaims the disk when it needs to, and an
        // unlinked conversion is the right thing for it to evict first.
        //
        // Except when there is nothing to keep: a conversion still running
        // is a part-finished directory, so that one is cancelled and removed
        // as it always was.
        // ?purge=1 deletes the conversion instead of unlinking it. Keeping
        // the work is the right default — rebuilding produces the same bytes
        // — but it means removing things never frees disk, and a cache at
        // its cap stays at its cap. Reclaiming space has to be possible on
        // purpose, not only as a side effect of eviction.
        var purge = ctx.Request.QueryString["purge"] == "1";
        var running = _ffmpeg?.VodInProgress(name) == true;
        if (!running && !purge)
        {
            _links.Hide(name);
            Log.Info("control", $"HLS stream unlinked (conversion kept): {name}");
            WriteJson(res, 200, new { removed = name, kept = true });
            return;
        }

        if (_ffmpeg?.CancelVod(name) == true)
            Thread.Sleep(300); // give the killed process a moment to release handles

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                _links.Forget(name);
                Log.Info("control", purge
                    ? $"conversion deleted from disk: {name}"
                    : $"unfinished conversion cancelled and removed: {name}");
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
    /// <summary>
    /// GET /api/library/search?q=… — every playable file under the library
    /// roots whose name matches, plus matching folders.
    ///
    /// The walk is the whole point and also the risk: a library can be a
    /// network drive with a hundred thousand files on it, so it is bounded
    /// by both a result cap and a wall clock, and says which one it hit
    /// rather than quietly returning a short list. Terms are matched against
    /// the readable title as well as the file name, so "skyfall 2012" finds
    /// Skyfall.2012.1080p.BluRay.x264.mkv.
    /// </summary>
    private void SearchLibrary(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var query = (ctx.Request.QueryString["q"] ?? "").Trim();
        if (query.Length < 2)
        {
            WriteJson(res, 400, new { error = "search needs at least two characters" });
            return;
        }

        // No folder means the whole library, which is the useful default —
        // "where is that film" rarely knows the folder. A folder narrows it,
        // and has to be inside a library root: this walks the disk, so the
        // scope is not the caller's to choose freely.
        var scope = ctx.Request.QueryString["folder"] ?? "";
        IReadOnlyList<string> roots;
        if (scope.Length > 0)
        {
            if (!TryLocalPath(scope, out var full) || !IsShared(full) || !Directory.Exists(full))
            {
                WriteJson(res, 400, new { error = "that folder is not in the library" });
                return;
            }
            roots = new[] { full };
        }
        else
        {
            roots = _library.All;
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        const int cap = 300;
        var deadline = DateTime.UtcNow.AddSeconds(5);

        var files = new List<object>();
        var folders = new List<object>();
        var truncated = false;
        var timedOut = false;
        var scanned = 0;

        bool Matches(string name) =>
            terms.All(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));

        foreach (var root in roots)
        {
            if (truncated || timedOut) break;
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                if (files.Count + folders.Count >= cap) { truncated = true; break; }
                if (DateTime.UtcNow > deadline) { timedOut = true; break; }

                var dir = stack.Pop();
                string[] subdirs, entries;
                try
                {
                    subdirs = Directory.GetDirectories(dir);
                    entries = Directory.GetFiles(dir);
                }
                catch
                {
                    continue; // unreadable folder: skip it, don't fail the search
                }

                foreach (var sub in subdirs)
                {
                    stack.Push(sub);
                    var name = Path.GetFileName(sub);
                    if (Matches(name))
                        folders.Add(new { path = sub, name, folder = dir });
                }

                foreach (var file in entries)
                {
                    scanned++;
                    var name = Path.GetFileName(file);
                    var title = Media.StreamTitle.PrettifyFile(name);
                    if (!Matches(name) && !Matches(title)) continue;
                    var kind = KindOf(name);
                    if (kind is null) continue; // not playable — not a result
                    long size;
                    try { size = new FileInfo(file).Length; } catch { size = 0; }
                    files.Add(new { path = file, name, title, kind, size, folder = dir });
                }
            }
        }

        WriteJson(res, 200, new
        {
            query,
            // echoed back so a result arriving late can be matched to the
            // scope it was asked for, not the one now on screen
            folder = scope,
            files,
            folders,
            scanned,
            // "300 of them" and "as far as I got in 5 seconds" are different
            // answers and the dashboard says which
            truncated,
            timedOut,
        });
    }

    private static string? KindOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" or ".mkv" or ".avi" or ".mov" or ".webm" or ".ts" or ".m2ts" or ".mts" or ".wmv"
            or ".flv" or ".f4v" or ".mpg" or ".mpeg" or ".mpe" or ".m1v" or ".m2v" or ".vob" or ".3gp"
            or ".3g2" or ".ogv" or ".ogm" or ".mxf" or ".asf" or ".rm" or ".rmvb" or ".divx" or ".dv" => "video",
        ".mp3" or ".flac" or ".wav" or ".m4a" or ".m4b" or ".ogg" or ".oga" or ".aac" or ".wma" or ".opus"
            or ".aiff" or ".aif" or ".ape" or ".wv" or ".mka" or ".ac3" or ".eac3" or ".dts" or ".amr" => "audio",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".avif" or ".tif" or ".tiff"
            or ".heic" or ".heif" => "image",
        _ => null,
    };

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
    private sealed record ImportRequest(List<ChannelRequest>? channels);

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
            // Playing it is what re-links it. An unlinked conversion is still
            // on disk, so asking for this media again brings the row back
            // with the work already done — no separate "restore" to find.
            _links.Show(stream);
            // Deliberately not history: preparing a stream is not watching
            // it, and adding one to the HLS list would otherwise show up as
            // watched before anyone pressed play. The viewing that may follow
            // records itself, and finds this file again through source.txt.
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
    /// <summary>
    /// Readable names for the free-TV channels being watched, by tag.
    ///
    /// Only the entry request names the channel; every request after it is a
    /// player refetching a playlist it was handed. The name is remembered
    /// here at entry so the sessions table can say "Flicks of Fury" rather
    /// than "pluto/5e1a…" without a lineup lookup on the media path.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _tvNames = new();

    /// <summary>
    /// This server's own channel restream, rather than somebody watching.
    ///
    /// A pinned channel is pulled by ffmpeg through this same proxy, so
    /// without this it would sit in the sessions list permanently as a
    /// viewer and its bytes would be counted twice — once arriving here and
    /// again leaving over HLS to whoever is actually watching. The user
    /// agent is a label ffmpeg was told to send, so loopback is required
    /// too; neither grants anything, they only decide whether this request
    /// is counted as a person.
    /// </summary>
    private static bool IsOwnRestream(HttpListenerContext ctx) =>
        string.Equals(ctx.Request.UserAgent, Media.FfmpegManager.RestreamUserAgent, StringComparison.Ordinal)
        && (ctx.Request.RemoteEndPoint?.Address is { } a && System.Net.IPAddress.IsLoopback(a));

    private async Task TvProxy(HttpListenerContext ctx, bool entry, AuthResult auth)
    {
        var res = ctx.Response;
        string url;
        bool relay;
        string providerId;
        string channelTag;

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
            // Tried and reverted: relaying the restream's segments through
            // this proxy. The theory was sound — ffmpeg's HTTP client
            // handles the segment CDN's dropped connections badly, and this
            // process talks to that host all day without trouble — but
            // measured, wedges got more frequent, not less. Every segment
            // then crossed loopback TLS and was buffered whole in this
            // process, and the ingest paid for it. If it is ever revisited
            // it needs streamed relaying, not this.
            relay = _relayProviders.Contains(provider.Id);
            channelTag = $"{provider.Id}/{channel}";
            await RememberChannelName(provider, channel, channelTag);
        }
        else
        {
            url = ctx.Request.QueryString["u"] ?? "";
            providerId = ctx.Request.QueryString["p"] ?? "";
            var sig = ctx.Request.QueryString["s"];
            relay = ctx.Request.QueryString["relay"] == "1";
            // put there by the rewrite; falls back to the provider so a link
            // minted before this existed still groups into one viewing
            channelTag = ctx.Request.QueryString["c"] is { Length: > 0 } tag ? tag : providerId;
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

            var result = await _tvProxy.FetchAsync(url, providerId, "/api/tv/r", relay, _cts.Token, channelTag);
            res.StatusCode = result.Status;
            res.ContentType = result.ContentType;
            // live playlists change every few seconds; nothing here may be held
            res.Headers["Cache-Control"] = "no-store";
            res.ContentLength64 = result.Body.Length;
            await res.OutputStream.WriteAsync(result.Body, _cts.Token);

            if (result.Status == 200) NoteTvViewing(ctx, entry, channelTag, result.Body.Length, auth);
        }
        catch (Exception ex)
        {
            Log.Warn("tv", $"proxy failed: {ex.Message}");
            try { WriteJson(res, 502, new { error = ex.Message }); } catch { }
        }
    }

    /// <summary>
    /// Files the channel's name under its tag, from the provider's cached
    /// lineup. Best effort: a name is a label on a row, and failing to find
    /// one must not stop the channel playing.
    /// </summary>
    private async Task RememberChannelName(Media.Providers.IChannelProvider provider, string channelId, string tag)
    {
        if (_tvNames.ContainsKey(tag)) return;
        try
        {
            var lineup = await provider.LineupAsync(_cts.Token);
            var found = lineup.FirstOrDefault(c => c.Id == channelId);
            if (found is null) return;
            // a lineup is hundreds of channels and only the watched ones land
            // here, but a long-running server should still not grow forever
            if (_tvNames.Count > 200) _tvNames.Clear();
            _tvNames[tag] = found.Name;
        }
        catch (Exception ex) { Log.Debug("tv", $"could not name {tag}: {ex.Message}"); }
    }

    /// <summary>
    /// Counts a free-TV request as watching.
    ///
    /// Which requests count is the whole question here. A proxied channel is
    /// unlike the rest of the server: for most providers the segments go
    /// straight from the CDN to the player and never pass through this
    /// process at all, so waiting for media to arrive — the rule that keeps
    /// an HLS playlist fetch from inventing a viewer — would mean never
    /// counting anyone.
    ///
    /// What separates looking from watching here is which playlist. The
    /// entry request is the master, fetched once by anything that merely
    /// opens a channel; the requests after it are a player refetching its
    /// variant playlist every few seconds, which nothing does unless it is
    /// playing. So the entry keeps an existing viewing alive and the ones
    /// that follow begin one.
    /// </summary>
    private void NoteTvViewing(HttpListenerContext ctx, bool entry, string tag, long bytes, AuthResult auth)
    {
        if (IsOwnRestream(ctx)) return;
        _services.Served.Add(bytes);
        // signature-authorized requests carry no account; an empty name is
        // what the viewer list turns into "share link"
        var user = auth.Level == AccessLevel.None ? "" : auth.Name;
        _services.Viewers.Note(ctx, tag, user, bytes, create: !entry, protocol: "tv");
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
    /// <summary>
    /// POST /api/dlna — sets which library folders DLNA may show. Takes the
    /// whole list every time rather than add/remove: what is shared with an
    /// unauthenticated network is exactly what was last confirmed, and an
    /// empty list means nothing, not "unchanged".
    /// </summary>
    private void SetDlnaShare(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        DlnaShareRequest? req;
        try { req = JsonSerializer.Deserialize<DlnaShareRequest>(ReadBody(ctx), BodyJson); }
        catch (Exception ex) { WriteJson(res, 400, new { error = "bad JSON: " + ex.Message }); return; }
        if (req?.folders is null)
        {
            WriteJson(res, 400, new { error = "body must be { \"folders\": [ \"C:\\\\path\", … ] }" });
            return;
        }

        var roots = _library.All;
        _dlnaShare.Set(req.folders, roots);
        var shared = _dlnaShare.Shared(roots);
        Log.Info("dlna", shared.Count == 0
            ? "no library folders are shared — a client will find an empty server"
            : $"sharing {shared.Count} of {roots.Count} library folder(s)");
        WriteJson(res, 200, new { shared = shared.Count, of = roots.Count });
    }

    private sealed record DlnaShareRequest(List<string>? folders);

    /// <summary>
    /// The DLNA surface: two service descriptions, one SOAP control endpoint
    /// for both services, an event subscription that is accepted and never
    /// used, and the files themselves.
    /// </summary>
    private void ServeDlna(HttpListenerContext ctx, string path, string method, Dlna.DlnaService dlna)
    {
        var res = ctx.Response;

        switch (path.ToLowerInvariant())
        {
            case "/dlna/cds.xml":
                WriteXml(res, 200, Dlna.DlnaService.ContentDirectoryScpd);
                return;

            case "/dlna/cm.xml":
                WriteXml(res, 200, Dlna.DlnaService.ConnectionManagerScpd);
                return;

            case "/dlna/control":
            {
                if (method != "POST") { res.StatusCode = 405; res.Close(); return; }
                var action = ctx.Request.Headers["SOAPACTION"] ?? "";
                var body = ReadBody(ctx);
                // From the request, not from UrlScheme: this same handler
                // answers on the encrypted control port and on the plain
                // DLNA port, and the media URLs it hands back have to point
                // at whichever one the client actually reached us on.
                var host = ctx.Request.Headers["Host"]
                           ?? $"{BoundHost}:{ctx.Request.LocalEndPoint?.Port ?? _config.Port}";
                var scheme = ctx.Request.Url?.Scheme ?? Services.UrlScheme.Name;
                var (status, xml) = dlna.HandleSoap(action, body, $"{scheme}://{host}");
                WriteXml(res, status, xml);
                return;
            }

            // Eventing (GENA). Nothing here changes under a client's feet
            // mid-browse, so there is nothing to notify — but a subscription
            // that is refused makes some clients abandon the device, so it
            // is accepted and quietly never fires.
            case "/dlna/events":
                if (method is "SUBSCRIBE" or "UNSUBSCRIBE")
                {
                    res.StatusCode = 200;
                    res.Headers["SID"] = "uuid:" + Guid.NewGuid();
                    res.Headers["TIMEOUT"] = "Second-1800";
                    res.Close();
                    return;
                }
                res.StatusCode = 405;
                res.Close();
                return;

            case "/dlna/file":
            {
                if (method is not ("GET" or "HEAD")) { res.StatusCode = 405; res.Close(); return; }
                var id = ctx.Request.QueryString["id"] ?? "";
                var file = dlna.ResolvePath(id);
                if (file is null || !File.Exists(file))
                {
                    // an id outside the library is the interesting case: it is
                    // either a stale bookmark or someone trying paths
                    Log.Debug("dlna", $"no such object: {id}");
                    res.StatusCode = 404;
                    res.Close();
                    return;
                }
                // A finished full-resolution conversion, if there is one: the
                // point of it is a television that cannot decode the original
                // — an HEVC file, an unfamiliar container — being handed
                // H.264/AAC instead, at the same picture size.
                var transcode = dlna.FindTranscode?.Invoke(file);

                // The cache-eviction sweep deletes whichever VOD directory
                // was written to least recently, to make room for a new
                // conversion — and until now, nothing serving DLNA ever
                // touched that timestamp. The HLS web path does, on every
                // segment; a television reading the exact same cached
                // conversion over DLNA never did, so it looked idle no
                // matter how long it had been streaming. Pressing play on
                // one film could evict the very directory a television was
                // mid-way through another one from — its connection breaks,
                // the file is gone, and the stream disappears from the
                // dashboard while someone is still watching it. Touched on
                // every request, the same as the web path: a HEAD is asked
                // before anybody is watching, same reasoning as there, but
                // it costs nothing to mark early and is one less place a
                // television's first request could lose the race.
                if (transcode is not null)
                {
                    try { Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(transcode.Parts[0].Path)!, DateTime.UtcNow); }
                    catch { }
                }

                // A HEAD is the TV asking how big the file is and whether it
                // may seek — the same reasoning as an HLS playlist fetch, and
                // not yet anybody watching. The GET that follows is.
                if (method == "HEAD")
                {
                    if (transcode is not null) dlna.ServeTranscode(ctx, transcode);
                    else dlna.ServeFile(ctx, file);
                    return;
                }

                // The viewing is opened before a byte goes out, so a two-hour
                // film shows up as a session while it plays rather than when
                // it ends — and it lands in Recently Watched at the moment
                // somebody presses play, as every other protocol does.
                var viewing = _services.Viewers.Note(
                    ctx, file, user: null, bytes: 0, create: true, protocol: "dlna", file: file);
                void Sent(long sent)
                {
                    _services.Served.Add(sent);
                    // the sweep can retire a viewing the TV left paused past
                    // the window; the response is still open, so start it again
                    if (!_services.Viewers.Progress(viewing, sent))
                        viewing = _services.Viewers.Note(
                            ctx, file, user: null, bytes: sent, create: true, protocol: "dlna", file: file);
                }

                if (transcode is not null)
                {
                    Log.Debug("dlna", $"serving the conversion of {Path.GetFileName(file)} " +
                                      $"({transcode.TotalBytes / (1024 * 1024)} MB, full resolution)");
                    dlna.ServeTranscode(ctx, transcode, Sent);
                }
                else dlna.ServeFile(ctx, file, Sent);
                return;
            }

            default:
                res.StatusCode = 404;
                res.Close();
                return;
        }
    }

    /// <summary>
    /// Arranges for this server to be started again once it has exited.
    ///
    /// A detached waiter rather than a launch-and-race: the new copy must not
    /// start while this one still holds the ports, or it meets the
    /// single-instance guard, decides a server is already running, and
    /// helpfully opens the dashboard of the copy that is in the middle of
    /// shutting down.
    /// </summary>
    private static bool ScheduleRestart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var dir = Directory.GetCurrentDirectory();
            var pid = Environment.ProcessId;

            var script =
                $"Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue; " +
                "Start-Sleep -Milliseconds 800; " +
                $"Start-Process -FilePath '{exe.Replace("'", "''")}' -WorkingDirectory '{dir.Replace("'", "''")}'";

            var psi = new System.Diagnostics.ProcessStartInfo("powershell")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script })
                psi.ArgumentList.Add(a);
            return System.Diagnostics.Process.Start(psi) is not null;
        }
        catch (Exception ex)
        {
            Log.Warn("control", $"could not schedule a restart: {ex.Message}");
            return false;
        }
    }

    private static void WriteXml(HttpListenerResponse res, int status, string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        res.StatusCode = status;
        res.ContentType = "text/xml; charset=\"utf-8\"";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
        res.Close();
    }

    /// <summary>
    /// GET /api/tuner?host=… — an HDHomeRun's identity and channel lineup,
    /// with each channel marked if it is already saved here, so the dashboard
    /// can offer only what would actually be new.
    /// </summary>
    private async Task ReadTunerLineup(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var host = Media.Providers.HdhrTuner.NormalizeHost(ctx.Request.QueryString["host"]);
        if (host is null)
        {
            WriteJson(res, 400, new { error = "host must be a tuner address, e.g. 192.168.1.50 or hdhomerun.local" });
            return;
        }
        // A tuner is a box on this network: not the internet (where this
        // endpoint would be a probe — the timing and the error text say
        // whether something answers), not this machine, and emphatically not
        // the cloud metadata address.
        if (!Services.PrivateNetwork.IsLanDevice(host.Split(':')[0]))
        {
            WriteJson(res, 400, new
            {
                error = "a tuner has to be a device on this network — that address is not one",
            });
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // a tuner on the LAN answers instantly or not at all
            var lineup = await Media.Providers.HdhrTuner.ReadAsync(host, _providerHttp, cts.Token);

            var existing = (_ffmpeg?.Channels ?? new List<(Media.FfmpegManager.ChannelDef, string, string)>())
                .Select(c => c.Item1.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);

            Log.Info("control", $"tuner {host}: {lineup.Device.Name} with {lineup.Channels.Count} channel(s)");
            WriteJson(res, 200, new
            {
                host,
                device = new
                {
                    name = lineup.Device.Name,
                    model = lineup.Device.Model,
                    tuners = lineup.Device.TunerCount,
                },
                channels = lineup.Channels.Select(c => new
                {
                    number = c.Number,
                    name = c.Name,
                    channelName = Media.Providers.HdhrTuner.ChannelName(c),
                    url = c.Url,
                    hd = c.Hd,
                    // a copy-protected cable channel can be listed but never
                    // restreamed — say so rather than importing a dead row
                    drm = c.Drm,
                    favorite = c.Favorite,
                    alreadyAdded = existing.Contains(c.Url),
                }),
            });
        }
        catch (OperationCanceledException)
        {
            WriteJson(res, 504, new { error = $"no answer from {host} — check the address and that the tuner is powered on" });
        }
        catch (Exception ex)
        {
            WriteJson(res, 502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/channels/import — saves a batch of channels idle, the way
    /// pinning does. One bad row (a duplicate name, usually) must not lose
    /// the other thirty-nine, so each is tried on its own and the failures
    /// are reported rather than thrown.
    /// </summary>
    private void ImportChannels(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        if (_ffmpeg is null || !_ffmpeg.Available)
        {
            WriteJson(res, 503, new { error = "ffmpeg is not available — install it (winget install Gyan.FFmpeg) and restart" });
            return;
        }

        ImportRequest? req;
        try { req = JsonSerializer.Deserialize<ImportRequest>(ReadBody(ctx), BodyJson); }
        catch (Exception ex) { WriteJson(res, 400, new { error = "bad JSON: " + ex.Message }); return; }

        var wanted = req?.channels ?? new List<ChannelRequest>();
        if (wanted.Count == 0)
        {
            WriteJson(res, 400, new { error = "body must be { \"channels\": [ { \"name\": \"…\", \"url\": \"…\" } ] }" });
            return;
        }

        var added = new List<string>();
        var skipped = new List<object>();
        foreach (var c in wanted)
        {
            var name = (c.name ?? "").Trim();
            var url = (c.url ?? "").Trim();
            if (name.Length == 0 || url.Length == 0
                || !Uri.TryCreate(url, UriKind.Absolute, out var u)
                || u.Scheme is not ("http" or "https" or "rtsp" or "udp" or "rtp"))
            {
                skipped.Add(new { name, reason = "needs a name and a playable url" });
                continue;
            }
            try
            {
                _ffmpeg.AddChannel(name, url, start: false);
                added.Add(name);
            }
            catch (Exception ex)
            {
                skipped.Add(new { name, reason = ex.Message });
            }
        }

        Log.Info("control", $"imported {added.Count} channel(s) from a tuner lineup" +
                            (skipped.Count > 0 ? $", skipped {skipped.Count}" : ""));
        WriteJson(res, 200, new { added = added.Count, names = added, skipped });
    }

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
            var url = $"{Services.UrlScheme.Prefix}127.0.0.1:{_config.Port}/api/tv/watch" +
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

    /// Video files worth offering a conversion for. Audio and images are left
    /// out — the Transcode panel is about films a TV can't decode.
    private static readonly HashSet<string> TranscodableExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".ts",
        ".m2ts", ".webm", ".flv", ".3gp", ".vob", ".divx", ".rm", ".rmvb", ".ogm", ".asf",
    };

    /// <summary>One video file's row for the Transcode panel: name, readable
    /// title, size, whether a TV needs it converted, and its conversion state.
    /// <paramref name="cacheOnly"/> reads codecs from the cache only (no probe),
    /// used by the recursive search so it can't launch hundreds of probes.</summary>
    private object TranscodeFileEntry(FileInfo f, bool cacheOnly)
    {
        var state = _ffmpeg?.VodStatusFor(f.FullName) ?? Media.FfmpegManager.VodState.None;
        int? percent = null;
        if (state == Media.FfmpegManager.VodState.Converting)
        {
            var stream = _ffmpeg?.VodStreamName(f.FullName);
            percent = _ffmpeg?.VodProgressSnapshot
                .FirstOrDefault(p => string.Equals(p.Stream, stream, StringComparison.OrdinalIgnoreCase))?.Percent;
        }
        var needs = cacheOnly
            ? (_tvCodecs?.NeedsConversionCached(f.FullName) == true)
            : (_tvCodecs?.NeedsConversion(f.FullName) ?? false);
        return new
        {
            name = f.Name,
            path = f.FullName,   // so search results (and delete) have the real path
            title = Media.StreamTitle.PrettifyFile(f.Name),
            type = "file",
            detail = f.Length >= 1024 * 1024 ? $"{f.Length / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, f.Length / 1024)} KB",
            needs,
            state = state.ToString().ToLowerInvariant(),   // none | converting | done
            percent,
        };
    }

    /// <summary>
    /// GET /api/transcode/scan?path=&lt;dir&gt; — a directory listing for the
    /// Transcode panel: sub-folders, plus each video file tagged with whether
    /// a TV needs it converted and whether a conversion exists, is running, or
    /// has never been made. No path lists the drives, same as the picker.
    /// A <c>q</c> query does a recursive name search under the folder instead.
    /// </summary>
    private void TranscodeScan(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var path = ctx.Request.QueryString["path"];

        if (string.IsNullOrWhiteSpace(path))
        {
            Browse(ctx);   // drive list is identical to the picker's
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
            var q = ctx.Request.QueryString["q"];

            // Search: recursively find video files under this folder whose name
            // matches, like the library search. Codecs are read cache-only so a
            // search never launches a probe; results are capped.
            if (!string.IsNullOrWhiteSpace(q))
            {
                Log.Info("control", $"[searchdebug] rawQuery='{ctx.Request.Url?.Query}'  parsed q='{q}'");
                const int searchCap = 500; var searchCapped = false;
                var sopts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
                };
                var dbgSamples = new List<string>();
                try
                {
                    foreach (var fp in Directory.EnumerateFiles(full, "*", sopts))
                    {
                        if (!TranscodableExt.Contains(Path.GetExtension(fp))) continue;
                        if (Path.GetFileName(fp).IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (entries.Count >= searchCap) { searchCapped = true; break; }
                        if (dbgSamples.Count < 5) dbgSamples.Add(Path.GetFileName(fp));
                        entries.Add(TranscodeFileEntry(new FileInfo(fp), cacheOnly: true));
                    }
                }
                catch { /* report whatever matched before the walk failed */ }
                Log.Info("control", $"[searchdebug] q='{q}' matched={entries.Count} samples=[{string.Join(" | ", dbgSamples)}]");
                WriteJson(res, 200, new { path = full, parent = dir.Parent?.FullName, entries, search = q, capped = searchCapped });
                return;
            }

            foreach (var d in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                entries.Add(new { name = d.Name, type = "folder", summary = FolderMediaSummary(d.FullName) });

            foreach (var f in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!TranscodableExt.Contains(f.Extension)) continue;
                entries.Add(TranscodeFileEntry(f, cacheOnly: false));
            }

            long? freeBytes = null, totalBytes = null; string? driveName = null;
            try
            {
                var root = Path.GetPathRoot(full);
                if (!string.IsNullOrEmpty(root))
                {
                    var di = new DriveInfo(root);
                    if (di.IsReady) { freeBytes = di.AvailableFreeSpace; totalBytes = di.TotalSize; driveName = di.Name; }
                }
            }
            catch { /* space is a nicety; a drive that won't report it just omits it */ }

            WriteJson(res, 200, new { path = full, parent = dir.Parent?.FullName, entries, driveName, freeBytes, totalBytes });
        }
        catch (UnauthorizedAccessException) { WriteJson(res, 403, new { error = "access denied" }); }
        catch (Exception ex) { WriteJson(res, 400, new { error = ex.Message }); }
    }

    /// <summary>
    /// A one-line count of what a folder holds, walked recursively, for the
    /// pills beside it: how many videos are inside, how many a TV needs
    /// converted, how many already have a converted copy, how many play as-is,
    /// and how many haven't been probed yet. Codecs are read from the cache
    /// only (see <see cref="Media.TvCodecs.NeedsConversionCached"/>) so opening
    /// a directory never launches a probe; inaccessible sub-folders and symlink
    /// loops are skipped, and the walk stops at a cap so a huge tree can't
    /// stall the listing.
    /// </summary>
    private object FolderMediaSummary(string dir)
    {
        int media = 0, needs = 0, done = 0, ready = 0, unknown = 0;
        const int cap = 4000;
        var capped = false;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        };
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
            {
                if (!TranscodableExt.Contains(Path.GetExtension(f))) continue;
                if (media >= cap) { capped = true; break; }
                media++;
                if ((_ffmpeg?.VodStatusFor(f) ?? Media.FfmpegManager.VodState.None) == Media.FfmpegManager.VodState.Done)
                { done++; continue; }
                var nc = _tvCodecs?.NeedsConversionCached(f);
                if (nc == true) needs++;
                else if (nc == false) ready++;
                else unknown++;
            }
        }
        catch { /* report whatever was counted before the walk failed */ }
        return new { media, needs, done, ready, unknown, capped };
    }

    /// <summary>
    /// POST /api/transcode { "paths": [ ... ] } — queues conversions for the
    /// chosen files and folders. A folder is walked for video files; anything
    /// already converted or already converting is skipped. The queue runs a
    /// couple at a time so picking a whole library doesn't launch fifty
    /// encoders at once.
    /// </summary>
    private void TranscodeBatch(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        if (_ffmpeg is null || !_ffmpeg.Available)
        {
            WriteJson(res, 503, new { error = "ffmpeg is not available" });
            return;
        }

        TranscodeRequest? req;
        try { req = JsonSerializer.Deserialize<TranscodeRequest>(ReadBody(ctx), BodyJson); }
        catch (Exception ex) { WriteJson(res, 400, new { error = "bad JSON: " + ex.Message }); return; }
        if (req?.Paths is null || req.Paths.Count == 0) { WriteJson(res, 400, new { error = "no paths given" }); return; }

        var files = new List<string>();
        foreach (var p in req.Paths)
        {
            if (string.IsNullOrWhiteSpace(p) || !TryLocalPath(p, out var full)) continue;
            try
            {
                if (Directory.Exists(full))
                    files.AddRange(Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                        .Where(f => TranscodableExt.Contains(Path.GetExtension(f))));
                else if (File.Exists(full) && TranscodableExt.Contains(Path.GetExtension(full)))
                    files.Add(full);
            }
            catch (Exception ex) { Log.Warn("control", $"transcode scan of {full} failed: {ex.Message}"); }
        }

        var unique = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Only convert what a TV can't already play. Selecting a folder that
        // holds a film plus its existing H.264 copies should convert the one
        // that needs it, not re-encode the copies too. Unprobeable files are
        // left alone (NeedsConversion answers false), matching the pills.
        var needsConv = unique.Where(f => _tvCodecs?.NeedsConversion(f) ?? true).ToList();
        var alreadyGood = unique.Count - needsConv.Count;
        var queued = _ffmpeg.QueueVod(needsConv);
        Log.Info("control", $"transcode: {queued} file(s) queued from {req.Paths.Count} selection(s) "
            + $"({unique.Count} video file(s) found, {alreadyGood} already play on a TV)");
        WriteJson(res, 200, new { queued, found = unique.Count, needs = needsConv.Count, alreadyGood });
    }

    private sealed class TranscodeRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("paths")] public List<string>? Paths { get; set; }
    }

    private sealed class TranscodeConfigRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("maxParallel")] public int? MaxParallel { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("staggerSeconds")] public int? StaggerSeconds { get; set; }
    }

    /// <summary>
    /// POST /api/transcode/delete { paths:[...] } — moves the chosen files and
    /// folders to the Windows Recycle Bin (an undoable delete). The dashboard
    /// confirms first. The server's own config/state directory is refused, so a
    /// stray tick can't recycle the library index or the accounts.
    /// </summary>
    private void TranscodeDelete(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        TranscodeRequest? req;
        try { req = JsonSerializer.Deserialize<TranscodeRequest>(ReadBody(ctx), BodyJson); }
        catch (Exception ex) { WriteJson(res, 400, new { error = "bad JSON: " + ex.Message }); return; }
        if (req?.Paths is null || req.Paths.Count == 0) { WriteJson(res, 400, new { error = "no paths given" }); return; }

        var configDir = Path.GetFullPath(_baseDirectory);
        int deleted = 0; var errors = new List<string>();
        foreach (var p in req.Paths)
        {
            if (string.IsNullOrWhiteSpace(p) || !TryLocalPath(p, out var full)) { errors.Add($"{p}: not a local path"); continue; }
            var fp = Path.GetFullPath(full);
            // never let the app recycle its own config/state (users, library, keys)
            if (fp.Equals(configDir, StringComparison.OrdinalIgnoreCase)
                || fp.StartsWith(configDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { errors.Add($"{Path.GetFileName(fp)}: refused (inside the server's own folder)"); continue; }
            try { Services.RecycleBin.Send(fp); deleted++; }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(fp)}: {ex.Message}"); }
        }
        Log.Info("control", $"delete to recycle bin: {deleted} item(s), {errors.Count} error(s)");
        WriteJson(res, 200, new { deleted, errors });
    }

    private sealed class TranscodeRemoveRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("path")] public string? Path { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("stream")] public string? Stream { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("clear")] public bool Clear { get; set; }
    }

    /// <summary>
    /// POST /api/transcode/remove — manage the conversion list:
    ///   { path }        drop one waiting file from the queue
    ///   { stream }      cancel one running conversion (its partial is removed)
    ///   { clear:true }  empty the whole waiting queue (running ones keep going)
    /// </summary>
    private void TranscodeRemove(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        if (_ffmpeg is null) { WriteJson(res, 503, new { error = "ffmpeg is not available" }); return; }

        TranscodeRemoveRequest? req;
        try { req = JsonSerializer.Deserialize<TranscodeRemoveRequest>(ReadBody(ctx), BodyJson); }
        catch (Exception ex) { WriteJson(res, 400, new { error = "bad JSON: " + ex.Message }); return; }

        if (req?.Clear == true)
        {
            var cleared = _ffmpeg.ClearVodQueue();
            Log.Info("control", $"transcode queue cleared ({cleared} waiting file(s) removed)");
            WriteJson(res, 200, new { cleared });
            return;
        }
        if (!string.IsNullOrWhiteSpace(req?.Stream))
        {
            var cancelled = _ffmpeg.CancelVod(req.Stream);
            WriteJson(res, 200, new { cancelled });
            return;
        }
        if (string.IsNullOrWhiteSpace(req?.Path)) { WriteJson(res, 400, new { error = "no path or stream given" }); return; }
        var removed = _ffmpeg.RemoveFromVodQueue(req.Path);
        WriteJson(res, 200, new { removed });
    }

    /// <summary>
    /// GET /api/transcode/config — current queue settings.
    /// POST /api/transcode/config { maxParallel?, staggerSeconds? } — how many
    /// conversions run at once (1–8) and the gap between starting them (0–120 s).
    /// </summary>
    private void TranscodeConfig(HttpListenerContext ctx, bool write)
    {
        var res = ctx.Response;
        if (_ffmpeg is null) { WriteJson(res, 503, new { error = "ffmpeg is not available" }); return; }

        if (write)
        {
            TranscodeConfigRequest? req;
            try { req = JsonSerializer.Deserialize<TranscodeConfigRequest>(ReadBody(ctx), BodyJson); }
            catch (Exception ex) { WriteJson(res, 400, new { error = "bad JSON: " + ex.Message }); return; }
            _ffmpeg.SetQueueSettings(req?.MaxParallel, req?.StaggerSeconds);
            Log.Info("control", $"transcode queue: {_ffmpeg.MaxConcurrentVod} at a time, "
                + $"{_ffmpeg.VodStaggerSeconds}s between starts");
        }

        WriteJson(res, 200, new
        {
            maxParallel = _ffmpeg.MaxConcurrentVod,
            staggerSeconds = _ffmpeg.VodStaggerSeconds,
            cores = Environment.ProcessorCount,
        });
    }

    /// <summary>
    /// Whether the player may be pointed at this: a path on this server, or
    /// an http(s) URL on the same host it was asked from. Ports are not
    /// compared — the media is on a different one by design — but the host
    /// is, so the page cannot be turned into an open redirect or made to
    /// embed somebody else's video under this server's name.
    /// </summary>
    private static bool IsOwnMediaUrl(string src, HttpListenerContext ctx)
    {
        if (src.Length == 0) return false;
        if (src.StartsWith('/')) return !src.StartsWith("//", StringComparison.Ordinal);
        if (!Uri.TryCreate(src, UriKind.Absolute, out var u)) return false;
        if (u.Scheme is not ("http" or "https")) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;

        var asked = ctx.Request.Url?.Host ?? "";
        if (u.Host.Equals(asked, StringComparison.OrdinalIgnoreCase)) return true;
        // reaching the dashboard by name and the media by address (or the
        // other way round) is normal here, so accept this machine's own
        // addresses too
        return System.Net.IPAddress.TryParse(u.Host, out var ip)
               && (System.Net.IPAddress.IsLoopback(ip)
                   || Services.NetworkInfo.Active().Any(i => i.Address == u.Host));
    }

    /// <summary>
    /// The full-window player page. Black, chromeless, the video and nothing
    /// else — the point of opening a tab is that the picture gets all of it.
    ///
    /// It asks for fullscreen as it loads. Browsers only grant that off a
    /// user gesture and a tab opened from a click does not reliably carry
    /// one, so a refusal is expected rather than exceptional: the page is
    /// already edge-to-edge, and the first click anywhere tries again.
    /// </summary>
    private static string PlayerPage(string src, string title)
    {
        var srcJs = JsonSerializer.Serialize(src);
        var shown = System.Net.WebUtility.HtmlEncode(
            string.IsNullOrWhiteSpace(title) ? "j0kers Media Server" : title);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{shown}} — j0kers</title>
            <link rel="icon" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><text y='.9em' font-size='90'>🃏</text></svg>">
            <style>
              html, body { margin: 0; height: 100%; background: #000; color: #ddd;
                           font-family: system-ui, sans-serif; overflow: hidden; }
              video { width: 100vw; height: 100vh; display: block; background: #000; }
              #msg {
                position: fixed; left: 50%; top: 50%; transform: translate(-50%, -50%);
                font-size: 14px; color: #bbb; text-align: center; pointer-events: none;
              }
              /* only until the first click, which is also what earns fullscreen */
              #hint {
                position: fixed; left: 50%; bottom: 22px; transform: translateX(-50%);
                background: rgba(0,0,0,.6); border: 1px solid rgba(255,255,255,.18);
                border-radius: 999px; padding: 6px 14px; font-size: 12.5px; color: #ddd;
                transition: opacity .4s; pointer-events: none;
              }
              /* Skip buttons of our own. The browser's controls have none,
                 and its arrow keys move by an amount it chooses — which is
                 why skipping felt different from one film to the next. These
                 are a fixed 30 seconds, the same in every stream. Placed
                 above the native control bar so they do not cover the
                 scrubber, and faded until the pointer is near. */
              #skip {
                position: fixed; left: 50%; bottom: 76px; transform: translateX(-50%);
                display: flex; gap: 10px; opacity: .25; transition: opacity .25s;
              }
              #skip:hover, body.busy #skip { opacity: 1; }
              #skip button {
                background: rgba(0,0,0,.62); color: #eee; cursor: pointer;
                border: 1px solid rgba(255,255,255,.22); border-radius: 999px;
                padding: 7px 15px; font-size: 13px; font-family: inherit;
              }
              #skip button:hover { background: rgba(0,0,0,.85); border-color: rgba(255,255,255,.45); }
              /* a moment's confirmation of where a skip landed */
              #seeknote {
                position: fixed; left: 50%; top: 12%; transform: translateX(-50%);
                background: rgba(0,0,0,.7); border-radius: 8px; padding: 8px 16px;
                color: #fff; font-size: 20px; opacity: 0; transition: opacity .25s;
                pointer-events: none;
              }
            </style>
            <script src="/hls.min.js"></script>
            </head>
            <body>
            <video id="v" controls autoplay playsinline></video>
            <div id="msg">loading…</div>
            <div id="seeknote"></div>
            <div id="skip">
              <button id="sk-back" title="Back 15 seconds (← or J)">⏪ 15s</button>
              <button id="sk-fwd" title="Forward 15 seconds (→ or L)">15s ⏩</button>
            </div>
            <div id="hint">click for fullscreen</div>
            <script>
              const src = {{srcJs}}, v = document.getElementById("v");
              const msg = document.getElementById("msg"), hint = document.getElementById("hint");
              const done = () => { msg.style.display = "none"; };

              // A live channel, where falling behind is fatal and going back
              // to where you were is the wrong answer. Guessed from the URL
              // so it is known before the first playlist arrives, then
              // corrected from the playlist itself, which actually knows.
              let live = /\/ch-[^/]*\//.test(src) || src.indexOf("/api/tv/") === 0;

              // hls.js first. Chromium answers "maybe" to the native HLS
              // question and then cannot play it — asking politely gets a
              // black screen and a MEDIA_ELEMENT_ERROR. Native is the
              // fallback, which is where Safari lands.
              if (window.Hls && Hls.isSupported()) {
                const hls = new Hls({
                  enableWorker: true,
                  // a channel left on all evening keeps every played-out
                  // second otherwise; half an hour is a generous DVR
                  backBufferLength: 1800,
                });
                hls.loadSource(src);
                hls.attachMedia(v);

                hls.on(Hls.Events.LEVEL_LOADED, (_, d) => {
                  if (d && d.details) live = !!d.details.live;
                });

                /* Recover, rather than announce the death.
                   A fatal hls.js error is usually a moment — one segment that
                   timed out, one playlist reload that missed — and the fix is
                   the same one the dashboard's own player has always used.
                   Without it a single hiccup ends playback for good, which on
                   a live channel with a 25-second window happens within the
                   first minute almost every time. */
                let recoveries = 0, healthy = 0;
                v.addEventListener("playing", () => {
                  // a spell of real playback means the last trouble is over,
                  // so a channel watched for hours is not slowly spending a
                  // fixed allowance of retries
                  clearTimeout(healthy);
                  healthy = setTimeout(() => { recoveries = 0; }, 60000);
                });

                hls.on(Hls.Events.ERROR, (_, d) => {
                  if (!d || !d.fatal) return;            // non-fatal: hls.js copes
                  if (++recoveries > 6) {
                    msg.style.display = "";
                    msg.textContent = "playback failed: " + (d.details || d.type);
                    return;
                  }
                  const was = v.currentTime;
                  msg.style.display = "";
                  msg.textContent = "reconnecting…";
                  try {
                    if (d.type === Hls.ErrorTypes.NETWORK_ERROR) hls.startLoad();
                    else if (d.type === Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
                    else { msg.textContent = "playback failed: " + (d.details || d.type); return; }
                  } catch { return; }
                  // A recording resumes where it was. A channel does not:
                  // the position it stopped at has already fallen out of the
                  // playlist, and asking for it again is how it stops twice.
                  if (!live && was > 1) {
                    v.addEventListener("canplay", function restore() {
                      v.removeEventListener("canplay", restore);
                      if (Math.abs(v.currentTime - was) > 2) { try { v.currentTime = was; } catch {} }
                    });
                  }
                });

                /* Falling off the back of the window.
                   Pluto's playlists hold about five segments — twenty-five
                   seconds of live — so a stall of any length leaves the
                   player asking for segments the playlist no longer lists,
                   and it stops with no error worth the name. Every few
                   seconds, if we are live and further behind than the window
                   is long, skip to the edge: a jump forward is what watching
                   live means, and the alternative is a frozen picture. */
                setInterval(() => {
                  if (!live || v.paused || v.seekable.length === 0) return;
                  const edge = v.seekable.end(v.seekable.length - 1);
                  if (edge - v.currentTime > 30) {
                    try { v.currentTime = edge - 6; } catch {}
                  }
                }, 5000);
              } else {
                v.src = src;
              }
              v.addEventListener("playing", done);
              v.addEventListener("loadeddata", done);

              // Try immediately — some browsers honour the opener's click —
              // and fall back to earning it from the first one here.
              function goFullscreen() {
                const el = document.documentElement;
                const ask = el.requestFullscreen || el.webkitRequestFullscreen;
                if (!ask || document.fullscreenElement) return;
                try { const p = ask.call(el); if (p && p.catch) p.catch(() => {}); } catch {}
              }
              /* Skipping: a fixed 30 seconds, the same in every stream.
                 The browser's own arrow keys move by an amount of its
                 choosing, and with HLS a seek lands on a segment boundary —
                 so on a film whose segments are uneven, "a little forward"
                 could be minutes. This asks for an exact position, and the
                 note says where it landed, so a stream that still snaps is
                 visible rather than mystifying. */
              const SKIP = 15;   // same as the dashboard player: one skip, everywhere
              const note = document.getElementById("seeknote");
              let noteTimer = 0;
              const clock = s => {
                s = Math.max(0, Math.floor(s));
                const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = s % 60;
                return (h ? h + ":" + String(m).padStart(2, "0") : String(m))
                       + ":" + String(x).padStart(2, "0");
              };
              /* How far playback can actually go right now.
                 seekable first, duration only as a fallback — and that order
                 is the whole point. While a file is still being converted the
                 playlist grows as ffmpeg writes it, and duration is the
                 optimistic figure: it names an end that has not been written
                 yet. Clamping a skip to it seeks onto a fragment that does
                 not exist, hls.js raises a fatal error, the recovery above
                 reloads and restores the position, and the next press does it
                 again — forward a few times, then a stall, a pause and a
                 restart. seekable is the honest bound; the dashboard player
                 and the watch page both take it, and this one now agrees. */
              function playableEnd() {
                if (v.seekable && v.seekable.length) return v.seekable.end(v.seekable.length - 1);
                return isFinite(v.duration) ? v.duration : Infinity;
              }
              function skip(by) {
                if (!isFinite(v.duration) && by > 0 && live) return;   // no seeking past a live edge
                if (by < 0) {
                  const back = Math.max(0, v.currentTime + by);
                  try { v.currentTime = back; } catch { return; }
                  showNote(by, back);
                  return;
                }
                // Skip anywhere in the film, converted or not. The playlist
                // covers the whole length from the start now, and a segment
                // that has not been made yet is made when the player asks
                // for it — so there is no converted edge to stop at and
                // nothing to clamp to. The only bound left is the film.
                const end = isFinite(v.duration) ? v.duration : playableEnd();
                const want = Math.min(Math.max(0, v.currentTime + by),
                                      isFinite(end) ? Math.max(0, end - 0.5) : v.currentTime + by);
                try { v.currentTime = want; } catch { return; }
                showNote(by, want);
              }
              function showNote(by, want) {
                note.textContent = (by > 0 ? "⏩ +" : "⏪ −") + Math.abs(by) + "s · " + clock(want);
                note.style.opacity = "1";
                clearTimeout(noteTimer);
                noteTimer = setTimeout(() => { note.style.opacity = "0"; }, 1100);
              }
              document.getElementById("sk-back").addEventListener("click", e => { e.stopPropagation(); skip(-SKIP); });
              document.getElementById("sk-fwd").addEventListener("click", e => { e.stopPropagation(); skip(SKIP); });

              goFullscreen();
              addEventListener("click", () => { goFullscreen(); hint.style.opacity = "0"; }, { once: true });
              // Capture phase, and an intention held for half a second.
              //
              // Measured in Chrome 148: a keydown reaches document capture,
              // then any listener on the video, then document bubble. This
              // was a bubble listener, so the video's own control bar had
              // already toggled play/pause by the time it ran — it then read
              // the state the video had just changed and toggled it straight
              // back. Press space while paused and it started, then stopped.
              // Capture reads the state before anything else touches it, and
              // holding the intention undoes a second toggle from either
              // side, whoever fires it.
              let want = null, wantAt = 0;
              function hold() {
                if (want === "play") { if (v.paused) v.play().catch(() => {}); }
                else if (want === "pause") { if (!v.paused) v.pause(); }
              }
              for (const ev of ["play", "pause"]) v.addEventListener(ev, () => {
                if (!want) return;
                if (performance.now() - wantAt > 500) { want = null; return; }
                hold();
              });
              v.addEventListener("pointerdown", () => { want = null; });  // a click outranks the key

              addEventListener("keydown", e => {
                if (e.key === "f") { goFullscreen(); return; }
                const t = e.target;
                if (t && (t.tagName === "SELECT" || t.tagName === "INPUT" || t.tagName === "TEXTAREA")) return;
                // preventDefault, or the browser adds its own 5-second seek
                // on top of ours and the skip is neither 15 nor predictable
                if (e.key === "ArrowLeft"  || e.key === "j" || e.key === "J") { e.preventDefault(); if (!e.repeat) skip(-SKIP); }
                if (e.key === "ArrowRight" || e.key === "l" || e.key === "L") { e.preventDefault(); if (!e.repeat) skip(SKIP); }
                if (e.key === " " || e.key === "Spacebar" || e.key === "k" || e.key === "K") {
                  e.preventDefault();
                  if (e.repeat) return;
                  want = v.paused ? "play" : "pause";
                  wantAt = performance.now();
                  hold();
                }
              }, true);
              // it stops being useful the moment fullscreen happens
              document.addEventListener("fullscreenchange",
                () => { if (document.fullscreenElement) hint.style.display = "none"; });
              setTimeout(() => { hint.style.opacity = "0"; }, 6000);
            </script>
            </body>
            </html>
            """;
    }

    /// <summary>
    /// A short name for this machine's timezone. Windows has no
    /// abbreviations — it calls this "Mountain Daylight Time" — so the
    /// initials of the words are taken, which is exactly where MDT, GMT and
    /// AEST come from. Anything that doesn't reduce sensibly (a name with
    /// one word, or an offset-style name like "UTC+03:00") is given as the
    /// offset instead, which is never wrong even when it isn't idiomatic.
    /// </summary>
    private static string ZoneAbbreviation()
    {
        try
        {
            var zone = TimeZoneInfo.Local;
            var name = zone.IsDaylightSavingTime(DateTime.Now) ? zone.DaylightName : zone.StandardName;
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Where(w => char.IsLetter(w[0]))
                            .ToArray();
            if (words.Length >= 2)
            {
                var initials = string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
                if (initials.Length is >= 2 and <= 5) return initials;
            }

            var offset = zone.GetUtcOffset(DateTime.Now);
            return offset == TimeSpan.Zero
                ? "UTC"
                : $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Whether a stream still has a playlist under the media root. History
    /// outlives the transcode cache, so an entry can name a stream that was
    /// evicted weeks ago.
    /// </summary>
    private bool StreamExists(string stream)
    {
        if (string.IsNullOrWhiteSpace(stream) || stream.Contains("..")
            || stream.Contains('/') || stream.Contains('\\')) return false;
        try
        {
            var root = Path.GetFullPath(Path.IsPathRooted(_serverConfig.Hls.MediaRoot)
                ? _serverConfig.Hls.MediaRoot
                : Path.Combine(_baseDirectory, _serverConfig.Hls.MediaRoot));
            return File.Exists(Path.Combine(root, stream, "index.m3u8"));
        }
        catch
        {
            return false;
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
        try { StopDlnaListener(); } catch { }
        try { _providers.Dispose(); } catch { }
        try { _providerHttp.Dispose(); } catch { }
    }
}

