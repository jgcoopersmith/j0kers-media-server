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
///   GET    /api/server/session   the live link an open dashboard holds (SSE)
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

    /// <summary>
    /// How many dashboards are holding the live link open right now — see
    /// <see cref="ServeDashboardSession"/>. This, not a beacon and not a
    /// gap in the polling, is how the server knows whether anybody has the
    /// page open.
    /// </summary>
    private int _liveDashboards;

    /// <summary>
    /// The client address behind every open live link, so the log can say who
    /// is keeping the server up when closing a page does not stop it.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _openPages = new();

    /// <summary>
    /// When the live-link count last fell to zero, or null while any page is
    /// open. The sweep turns this into a shutdown once it has stood for the
    /// grace period.
    /// </summary>
    private DateTime? _zeroSinceUtc;

    /// <summary>Distinct addresses currently holding a page open.</summary>
    private IEnumerable<string> OpenPageClients() =>
        _openPages.Values.Distinct().OrderBy(a => a, StringComparer.Ordinal);

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
            var root = MediaRootPath();
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
            var root = MediaRootPath();
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
        dlna.LiveChannels = () =>
        {
            if (!_serverConfig.Discovery.DlnaLiveTv || _ffmpeg is null)
                return Array.Empty<(string, string)>();
            // Only channels actually restreaming: an idle or stopped one has no
            // segments to serve, so listing it would offer a file that stalls.
            return _ffmpeg.Channels
                .Where(c => c.status == "running")
                .Select(c => (c.def.Name, c.stream))
                .ToList();
        };
        _dlnaLive ??= new Dlna.DlnaLive(MediaRootPath());
        dlna.LiveSizeOf = s => _dlnaLive?.CurrentSizeFor(s) ?? 0;
        return dlna;
    }

    /// <summary>
    /// The timeshift buffers behind DLNA "Live TV". Created with the DLNA
    /// service and kept for the process lifetime — it sweeps its own idle
    /// buffers, so there is nothing to tear down when DLNA toggles off.
    /// </summary>
    private Dlna.DlnaLive? _dlnaLive;

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

            var mediaRoot = MediaRootPath();
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
    private readonly HttpClient _proxyHttp;
    private readonly Media.Providers.ProviderRegistry _providers;
    private readonly Media.Providers.HlsProxy _tvProxy;
    private readonly HashSet<string> _relayProviders;
    private readonly Media.TvCodecs? _tvCodecs;

    /// <summary>What has gone wrong lately, for the Problems card.</summary>
    public readonly Services.Problems Problems = new();

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
        StartIdleShutdownWatch();
        StartLinkSweep();
        _playlists = new Media.PlaylistStore(baseDirectory);
        _library = new Media.LibraryStore(baseDirectory);
        _links = new Media.StreamLinks(baseDirectory);
        // Conversions made before unlinking existed are still listed; take them
        // out once so the list holds what was published rather than everything
        // that was ever converted.
        _links.HideExistingConversionsOnce(MediaRootPath());
        _favorites = new Media.FavoritesStore(baseDirectory);
        _history = new Media.WatchHistory(baseDirectory);
        _dlnaShare = new Dlna.DlnaShare(baseDirectory);

        // What a television can and cannot decode — needs ffmpeg to probe
        // with, so it stays null without one and the DLNA path falls back
        // to handing over originals, as it always did.
        if (ffmpeg is not null)
        {
            _tvCodecs = new Media.TvCodecs(baseDirectory, ffmpeg.FfprobePath);
            // Both places that already notice a failure now also record it
            // somewhere a person will actually see. They stay unaware of the
            // list itself; they just report.
            _tvCodecs.OnProblem = Problems.Record;
            ffmpeg.OnProblem = Problems.Record;
        }

        if (serverConfig.Discovery.Dlna) _dlna = NewDlna();

        // Fill the codec cache in the background so the transcode panel stops
        // saying "checking...". The folder summary reads the cache only, on
        // purpose - probing a whole library while somebody opens a directory
        // would turn a click into an hour of ffprobe launches. That was fine on
        // a server whose cache had filled in over months of browsing, and wrong
        // on a fresh install, where nothing is cached and every folder reads as
        // unknown until each one is visited by hand. So walk the library slowly
        // instead, well behind whatever else the machine is doing.
        StartCodecPrefetch();
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
        // The proxy gets its own client because it must not follow redirects
        // by itself: it hands upstream bytes straight back to the caller, so
        // each hop has to face the private-address check rather than being
        // obeyed silently. The providers keep the shared client and its
        // ordinary redirect handling - they fetch known vendor endpoints,
        // and GitHub-hosted playlists genuinely do redirect.
        _proxyHttp = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        _tvProxy = new Media.Providers.HlsProxy(_proxyHttp, mediaLinks);
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


    // The last sign that anybody is there: a dashboard poll (every couple of
    // seconds while a page is open) or an HLS request. Used to notice that the
    // dashboard has gone even when it never got to say so.
    private DateTime _lastSeenUtc = DateTime.UtcNow;
    private bool _sawDashboard;
    private Timer? _idleShutdownTimer;

    /// <summary>
    /// Shuts the server down once the dashboard has been gone a while, when it
    /// is not set to run in the background.
    ///
    /// Closing the page is supposed to announce itself - the browser sends a
    /// beacon on pagehide - but that is a best-effort message from a tab that
    /// is being destroyed, and it does not always arrive. Tested: closing the
    /// tab left the server running with no "dashboard closed" in the log at
    /// all. Waiting to be told is therefore not enough on its own; noticing the
    /// silence is what makes closing the browser reliably stop the server.
    ///
    /// Absence, not idleness: the dashboard polls every two seconds, so half a
    /// minute without one means the page is gone rather than quiet. Streaming
    /// counts as being there too (OnHlsActivity), so a television part way
    /// through a film is never cut off because nobody has the dashboard open.
    /// Nothing happens until a dashboard has been seen at least once, so a
    /// server started headless is left alone.
    /// </summary>
    /// <summary>
    /// How long the dashboard has to be silent before the server treats it as
    /// closed. The page polls every two seconds, so thirty covers roughly
    /// fifteen missed polls: far more than a slow reply, a garbage collection
    /// or a moment of packet loss can account for, and short enough that
    /// closing the browser stops the server while the user is still expecting
    /// it to. It is not an idleness measure - a dashboard sitting untouched
    /// still polls - so nothing here shortens with inactivity.
    /// </summary>
    private static readonly TimeSpan DashboardGoneAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Why the server must not shut itself down right now, or null when it may.
    ///
    /// Both shutdown paths have to ask this, and only one of them did. The
    /// idle timer checked for conversions and viewers; the close beacon did
    /// not, and the beacon is the path that actually fires when someone shuts
    /// the dashboard. Closing the page therefore stopped the server five
    /// seconds later no matter what it was doing: Dispose killed every running
    /// conversion, and each one's exit handler then removed its own
    /// part-finished directory as unplayable. Work that had been converting
    /// for hours disappeared out of the transcode folder while it was open in
    /// Explorer. A phone midway through a film was cut off the same way.
    ///
    /// Closing a window is not an instruction to throw work away.
    ///
    /// The two paths ask different questions, and conflating them was a
    /// mistake with a cost. Going silent is not the same act as closing the
    /// page, and only one of them is somebody telling the server to stop.
    /// </summary>
    private string? SilenceShutdownBlockedBy()
    {
        // Nobody said anything - the dashboard simply stopped answering.
        // That is a locked screen, a sleeping laptop, a dropped network. It
        // is not an instruction, so work in progress keeps the server up:
        // without this, locking the screen ended an overnight conversion run
        // half a minute later.
        if (_ffmpeg is not null)
        {
            var active = _ffmpeg.ActiveVodStreams.Count;
            var queued = _ffmpeg.VodQueueDepth;
            if (active > 0 || queued > 0)
                return $"{active} conversion(s) running, {queued} queued";
        }
        return StreamingBlockedBy();
    }

    /// <summary>
    /// Somebody is watching, so the *silence* watch must not act.
    ///
    /// This answers one question only, and it is no longer asked about a
    /// deliberate close — see CloseShutdownTick, which asks nothing. It is
    /// left for the silence path, where the server has been told nothing and
    /// is guessing from a gap in the polling: a television part way through a
    /// film is exactly the case where that guess would be wrong.
    ///
    /// It clears itself either way: a viewer is forgotten 90 seconds after
    /// their last request, so this can never be what wedges shutdown.
    /// </summary>
    private string? StreamingBlockedBy()
    {
        var viewers = _services.Viewers.Count;
        return viewers > 0 ? $"{viewers} viewer(s) streaming" : null;
    }

    private Timer? _linkSweepTimer;

    /// <summary>
    /// Shuts the server down once no page has been open for the grace period.
    ///
    /// The decision moved here from the moment a link ended, because a link
    /// ending no longer means what it used to. Links are closed and remade on
    /// purpose (see LinkLifetime), so the count dips to zero routinely and
    /// only staying at zero means anything. A browser that is still there
    /// reconnects within about half a second and clears the mark; one that has
    /// gone cannot.
    /// </summary>
    private void StartLinkSweep()
    {
        if (_requestShutdown is null) return;
        _linkSweepTimer = new Timer(_ =>
        {
            try
            {
                if (!_config.ShutdownOnClose || !_sawDashboard) return;
                if (Volatile.Read(ref _liveDashboards) > 0) return;
                DateTime? since;
                lock (_shutdownLock) since = _zeroSinceUtc;
                if (since is null || DateTime.UtcNow - since.Value < TimeSpan.FromMilliseconds(CloseGraceMs))
                    return;

                lock (_shutdownLock)
                {
                    if (_zeroSinceUtc is null) return;   // something reconnected
                    _zeroSinceUtc = null;
                }
                _linkSweepTimer?.Dispose();
                _linkSweepTimer = null;
                Log.Info("control", "no page has been open for "
                                    + (CloseGraceMs / 1000) + "s — shutting down");
                _requestShutdown();
            }
            catch (Exception ex) { Log.Warn("control", "link sweep failed: " + ex.Message); }
        }, null, dueTime: 1000, period: 1000);
    }

    private void StartIdleShutdownWatch()
    {
        if (_requestShutdown is null) return;
        _idleShutdownTimer = new Timer(_ =>
        {
          try
          {
            // Whether there is work in progress is asked here anyway, so it is
            // also where Windows is told - a machine that sleeps mid-encode
            // stops the work just as surely as shutting the server down did.
            var converting = _ffmpeg is not null
                             && (_ffmpeg.ActiveVodStreams.Count > 0 || _ffmpeg.VodQueueDepth > 0);
            Services.KeepAwake.Busy(converting);

            if (!_config.ShutdownOnClose || !_sawDashboard) return;
            // A page is open, and that is now a fact rather than a hope. It
            // used to be neither: a link was held until a write failed, so an
            // immortal one meant this check could never be reached — which is
            // why this watch was written to ignore the count and go by silence
            // instead. Links expire and are remade now, so a count above zero
            // cannot be a ghost, and the sweep owns the zero case.
            //
            // It has to defer, too. The sign-in page holds a link and does not
            // poll anything, so going by silence alone shut the server down
            // half a minute after somebody opened it and simply looked at it.
            if (Volatile.Read(ref _liveDashboards) > 0) return;
            if (DateTime.UtcNow - _lastSeenUtc < DashboardGoneAfter) return;
            // Converting counts as being in use just as much as watching does.
            // Without this, locking the screen stopped the work: the browser
            // stops polling behind a lock screen, nothing is streaming, and
            // half a minute later the server shut itself down and took every
            // running ffmpeg and the whole queue with it.
            if (SilenceShutdownBlockedBy() is string busy)
            {
                Log.Debug("control", $"dashboard gone, but staying up: {busy}");
                return;
            }
            _idleShutdownTimer?.Dispose();
            _idleShutdownTimer = null;
            Log.Info("control", "no dashboard for 30 s and nothing streaming - shutting down");
            _requestShutdown();
          }
          catch (Exception ex) { Log.Warn("control", "idle check failed: " + ex.Message); }
        }, null, dueTime: 10_000, period: 10_000);
    }
    /// <summary>
    /// A dashboard is there: cancels a pending shutdown-on-close.
    ///
    /// Only the page itself calls this — its poll and its live link. A media
    /// request no longer does. Somebody watching is a reason not to shut down
    /// *yet*, which <see cref="StreamingBlockedBy"/> answers at the moment of
    /// deciding; it is not evidence that the dashboard is still open, and
    /// treating it as such is what let a phone's stream cancel a close
    /// outright and leave nothing to reconsider it afterwards.
    /// </summary>
    public void NoteActivity()
    {
        _lastSeenUtc = DateTime.UtcNow;
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
            Log.Info("control", "dashboard still open — shutdown cancelled");
            _closeShutdownTimer.Dispose();
            _closeShutdownTimer = null;
        }
    }

    /// <summary>
    /// Media was served. Keeps the silence watch quiet — a television part
    /// way through a film is not a dead server — without pretending that a
    /// dashboard is open.
    /// </summary>
    public void NoteStreaming() => _lastSeenUtc = DateTime.UtcNow;

    /// <summary>
    /// GET /api/server/session — the live link every open dashboard holds.
    ///
    /// This is the fix for "closing the browser does not stop the server".
    /// The page used to announce its own death with a pagehide beacon, and
    /// measured here, that beacon does not arrive: closing the tab produced
    /// no request at all, and the process only went down thirty seconds
    /// later when the silence watch noticed. Thirty seconds is not what
    /// closing a window is supposed to feel like, and on a machine that was
    /// converting something it did not happen at all.
    ///
    /// A held-open connection needs nobody to announce anything. The browser
    /// closes, the socket goes with it, and the next heartbeat write fails —
    /// so the server learns the page is gone from the operating system, in
    /// about a second, whether the page got the chance to say so or not.
    ///
    /// It also tells apart the two states the polling never could: a page
    /// that has *gone* (connection dropped) and a page that has merely gone
    /// *quiet* — a locked screen, a backgrounded tab — whose connection is
    /// still there. That distinction is why the silence watch had to wait
    /// thirty seconds and then second-guess itself.
    /// </summary>
    private async Task ServeDashboardSession(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        res.StatusCode = 200;
        res.ContentType = "text/event-stream";
        res.Headers["Cache-Control"] = "no-store";
        res.Headers["X-Accel-Buffering"] = "no";   // in case anything proxies us
        res.SendChunked = true;

        var open = Interlocked.Increment(ref _liveDashboards);
        // A browser that can open a link is a browser that exists. This is the
        // only proof of life the server accepts now, and unlike a successful
        // write it cannot be produced by a socket nobody is holding.
        lock (_shutdownLock) _zeroSinceUtc = null;
        _lastSeenUtc = DateTime.UtcNow;
        // Who is holding it, so the log can name them when a close does not
        // stop the server. A page on another machine keeping it alive is
        // correct behaviour and completely invisible without this.
        var holder = Guid.NewGuid().ToString("n");
        _openPages[holder] = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
        _sawDashboard = true;
        NoteActivity();
        Log.Info("control", $"page opened from {_openPages[holder]} ({open} now open)");

        // "retry" is what the browser waits before reopening. Half a second,
        // which matters more than it looks: this link is deliberately closed
        // and remade, so the reconnect happens constantly rather than only
        // after a fault.
        var hello = System.Text.Encoding.ASCII.GetBytes("retry: 500\n\n");
        var beat = System.Text.Encoding.ASCII.GetBytes(":\n\n");
        // The lifetime is enforced by cancelling, not by checking the clock
        // between writes. A write to a socket whose far end has gone does not
        // reliably fail — that is the whole reason this design exists — and it
        // does not reliably RETURN either: it can simply never complete. A
        // loop that re-reads the time only after each write would then never
        // get to look. Cancellation reaches into the pending write itself, so
        // the link ends on time whatever the socket is doing.
        using var life = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        life.CancelAfter(LinkLifetime);
        var tok = life.Token;
        try
        {
            await res.OutputStream.WriteAsync(hello, tok);
            await res.OutputStream.FlushAsync(tok);
            while (!tok.IsCancellationRequested)
            {
                await Task.Delay(500, tok);
                await res.OutputStream.WriteAsync(beat, tok);
                await res.OutputStream.FlushAsync(tok);
            }
        }
        catch
        {
            // the page went away, or this server is shutting down anyway
        }
        finally
        {
            var left = Interlocked.Decrement(ref _liveDashboards);
            _openPages.TryRemove(holder, out _);
            // Note the moment the last one went, and let the sweep below
            // decide. Deciding here cannot work any more: a link that ends
            // because its lifetime ran out looks exactly like one that ended
            // because the browser closed, and the difference is only knowable
            // a moment later, by whether anything reconnected.
            if (left <= 0 && !_cts.IsCancellationRequested)
            {
                lock (_shutdownLock) _zeroSinceUtc ??= DateTime.UtcNow;
            }
            else if (!_cts.IsCancellationRequested)
            {
                // The line that was missing, and it cost a day.
                //
                // A page closing while others are still open is correct and
                // does nothing — but at DEBUG it said nothing either, so a
                // server that would not stop looked like a broken shutdown
                // rather than what it was: a forgotten tab somewhere else on
                // the network, on another machine, quietly holding it open.
                // The client address is here because "which page" is the only
                // useful thing to know at that point.
                Log.Info("control", $"a page closed, but {left} still open — staying up. " +
                                    $"Still held from: {string.Join(", ", OpenPageClients())}");
            }
        }
    }

    /// <summary>
    /// The last dashboard has gone. One path for both ways of learning it —
    /// the live link dropping and the pagehide beacon, when it arrives —
    /// so the two can never disagree about what closing the page means.
    ///
    /// Not in the background: closing the page is how this server is
    /// stopped, so it stops. The grace is only long enough for a refresh or
    /// a navigation to bring the link back, which takes well under a second.
    /// </summary>
    private void DashboardWentAway()
    {
        if (_config.ShutdownOnClose && _requestShutdown is not null)
        {
            lock (_shutdownLock)
            {
                _closeShutdownTimer?.Dispose();
                _closeShutdownTimer = new Timer(_ => CloseShutdownTick(), null,
                                                CloseGraceMs, Timeout.Infinite);
            }
            return;
        }

        if (OnDashboardClosed is null) return;
        // Background mode: closing the page leaves the server running, which
        // is worth saying — it is the opposite of what closing a window
        // usually means. Held for a moment first, because a refresh looks
        // exactly like this for the first half second, and a balloon for
        // every refresh would be noise.
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

    /// <summary>
    /// How long after the last dashboard goes before the server acts. Long
    /// enough that a reconnect — a refresh, or the deliberate rotation below —
    /// is not mistaken for a close, and short enough that closing the browser
    /// is over before anyone wonders whether it worked.
    ///
    /// Three seconds rather than the original one and a half, because links
    /// now rotate on purpose and the count dips to zero every time one does.
    /// The browser comes back in about half a second; the rest is margin for
    /// a machine that is busy.
    /// </summary>
    private const int CloseGraceMs = 3000;

    /// <summary>
    /// How long one live link is allowed to last before the server closes it
    /// and makes the browser open another.
    ///
    /// This exists because the previous design could not tell a closed browser
    /// from a socket that merely never reports being closed, and quietly bet
    /// everything on the difference. A page was counted as present until a
    /// 3-byte heartbeat write FAILED — the server's own write, to a socket it
    /// cannot see the far end of. Whenever that write kept succeeding after
    /// the browser had gone (TLS record buffering, an intermediary holding the
    /// connection, a half-open socket that never surfaces the reset), the link
    /// was immortal: the count never fell, so the close was never noticed.
    ///
    /// And the same beat refreshed the timestamp the thirty-second silence
    /// watch reads, so the fallback that existed for exactly this was
    /// suppressed by the very thing that had failed. One assumption, both
    /// safety nets. That is why "closing the browser does not stop it"
    /// survived several fixes: every one of them trusted the same write.
    ///
    /// So liveness is no longer something the server tells itself. The link is
    /// closed on a timer and a living browser proves it is living by opening
    /// another — which a closed one cannot do, whatever the socket says. It
    /// costs one request a minute per open page.
    /// </summary>
    /// Deliberately well inside DashboardGoneAfter below: every reconnect is
    /// also what refreshes the timestamp that watch reads, so a link must be
    /// remade comfortably before that watch would give up on the page.
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The moment of deciding, once the grace has run out. Nothing is asked
    /// except whether a dashboard came back.
    ///
    /// Closing the dashboard stops the server. Not "once the conversions
    /// finish", not "once the last phone stops watching" — then. It is the
    /// off switch, and an off switch that argues is not one: every condition
    /// that used to be consulted here was a way for the server to still be
    /// running after the user had told it to stop, which is the whole
    /// complaint this endpoint exists to answer.
    ///
    /// So both guards are gone, and they are worth naming so nobody
    /// reinstates them by accident:
    ///
    ///   • Conversions never vetoed it, and still do not. A queue can run for
    ///     hours, and blocking on it left no way to stop the server at all.
    ///     An interrupted conversion keeps its part-finished directory and is
    ///     replaced when it is converted again, so this costs encoding time,
    ///     not work on disk.
    ///   • Somebody else watching used to veto it, and no longer does. That
    ///     is a real cost — a film can cut out on another person's screen —
    ///     and it is the deliberate trade: an off switch that a television in
    ///     another room can hold shut is not one either. Leave the server in
    ///     the tray (background mode) when other people are watching; that is
    ///     what background mode is, and it turns this whole path off.
    ///
    /// The silence watch is a different act and keeps its own guards: going
    /// quiet is not somebody telling the server to stop, so a locked screen
    /// still must not kill a conversion. See SilenceShutdownBlockedBy.
    /// </summary>
    private void CloseShutdownTick()
    {
        lock (_shutdownLock)
        {
            if (_closeShutdownTimer is null) return;   // a dashboard came back
            _closeShutdownTimer.Dispose();
            _closeShutdownTimer = null;
        }
        Log.Info("control", "no dashboard open — shutting down");
        _requestShutdown?.Invoke();
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
        finally
        {
            // DLNA has no accounts, so there is never a name to record here -
            // the client address is the only identity a television has.
            Logging.AccessLog.Served("dlna", ctx);
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
    /// The dashboard's script, which used to be one block inside
    /// dashboard.html and is now a file per area of the page. The page pulls
    /// them in with ordinary script tags in the order listed there, so they
    /// are separate assets rather than one bundle and each is served exactly
    /// as the player library is. Keyed by the request path so one route
    /// covers all of them instead of a dozen copies of the same block; the
    /// resource name and the URL are deliberately the same string, so adding
    /// a file means adding it here and to the csproj and nowhere else.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Lazy<byte[]>> DashboardScripts =
        new[]
        {
            "dashboard-core.js", "dashboard-accounts.js", "dashboard-status.js",
            "dashboard-transcode.js", "dashboard-log.js", "dashboard-streams.js",
            "dashboard-player.js", "dashboard-config.js", "dashboard-library.js",
            "dashboard-channels.js", "dashboard-content.js", "dashboard-ui.js",
        }.ToDictionary(n => "/" + n, n => new Lazy<byte[]>(() => LoadResource(n)), StringComparer.Ordinal);

    // The player and watch pages are templates rather than finished bytes, so
    // this one is kept as text: the placeholder substitution runs on every
    // request and decoding the resource again each time would be waste.
    private static readonly Lazy<string> PlayerTemplate =
        new(() => Encoding.UTF8.GetString(LoadResource("player.html")));

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
            case "/api/log/files":   // listing the rotated history
            case "/api/log/file":    // reading one rotated file
            // the problem list names file paths, exactly as the log does
            case "/api/problems":
            case "/api/problems/clear":
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
        // Out here so the access-log line in the finally can name the account,
        // however the request ended.
        string? who = null;
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
            who = auth.User?.Username;

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
                    NotFound(res, "DLNA is off — enable discovery.dlna to serve the library to TVs");
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
                    BadRequest(res, "src must be a path or a URL on this server");
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

            // The dashboard's own script files, served the same way and from
            // the same place in the pipeline as the player library above.
            // The charset is spelled out where the player library leaves it
            // off because these carry the page's text - the emoji on the
            // buttons, the ellipses in the status lines - and a script with
            // no charset is only decoded as UTF-8 by falling back to the
            // encoding of the document that pulled it in.
            if (method == "GET" && DashboardScripts.TryGetValue(path, out var dashScript))
            {
                res.StatusCode = 200;
                res.ContentType = "text/javascript; charset=utf-8";
                // Not cached, exactly as the page that loads them is not.
                //
                // hls.min.js next door is cached for a day because it is a
                // third-party library that never changes. This is the
                // dashboard's own code, split out of the page it belongs to,
                // and it changes with every release. Caching it would leave a
                // browser running yesterday's script against today's markup
                // and API for a day after an upgrade - an upgrade that
                // appears to have done nothing, which is worse than fetching
                // a couple of hundred kilobytes again over a LAN.
                res.Headers["Cache-Control"] = "no-store";
                res.ContentLength64 = dashScript.Value.Length;
                res.OutputStream.Write(dashScript.Value);
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
                NotFound(res, "not found");
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

            // An M3U playlist of the live channels — the transport live TV
            // actually uses. A TV's IPTV app, or a PVR backend (Plex, Jellyfin,
            // Channels), consumes this as live TV, where the DLNA file-browser
            // never could. Authorized by a media token in the URL, the same
            // signature the stream links carry, so it is one self-contained
            // link an app can hold; each entry points at the channel's own HLS
            // output — the full-resolution, remuxed, now-stable restream —
            // carrying that same token so playback needs no separate sign-in.
            if (method == "GET" && path == "/api/tv/playlist.m3u")
            {
                if (!_mediaLinks.Verify(Auth.MediaLink.AllStreams,
                        ctx.Request.QueryString["exp"], ctx.Request.QueryString["sig"]))
                { res.StatusCode = 401; res.Close(); return; }
                WriteTvPlaylistM3u(ctx);
                return;
            }

            // The live link every open page holds — the dashboard and the
            // sign-in page alike. Dropping it is how the server learns the
            // page has gone; see ServeDashboardSession.
            //
            // Deliberately above the sign-in gate, and this is the whole fix
            // for "I close the browser and it keeps running". A server with no
            // administrator yet, or one whose session has expired, serves the
            // sign-in page rather than the dashboard — and that page held no
            // link, sent no beacon, and never set _sawDashboard, so even the
            // silence watch stayed disarmed. Closing it signalled nothing at
            // all and the process ran until it was killed. On a fresh install
            // that is not an edge case, it is every single launch.
            //
            // Being open costs nothing: this endpoint returns no information.
            // It says "a page is here" while held and "it has gone" when
            // dropped, which is exactly what an unauthenticated page needs to
            // be able to say. Same-origin only, so a page on another site
            // cannot open one and drop it to stop somebody's server.
            if (method == "GET" && path == "/api/server/session")
            {
                if (IsCrossSite(ctx)) { res.StatusCode = 403; res.Close(); return; }
                await ServeDashboardSession(ctx);
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
                NotFound(res, "not found");
                return;
            }

            // The pagehide beacon, kept as the fast path for when it does
            // arrive — it says "closed" a beat before the socket drops. It is
            // no longer the only signal, which is what made closing the
            // browser a coin toss: measured, the beacon simply did not turn
            // up. Only the last page leaving counts, so shutting one of two
            // tabs is not a close.
            if (method == "POST" && path == "/api/server/closing")
            {
                if (Volatile.Read(ref _liveDashboards) <= 1) DashboardWentAway();
                WriteJson(res, 200, new { scheduled = _config.ShutdownOnClose });
                return;
            }
            if (method == "GET" && path == "/api/status") { _sawDashboard = true; NoteActivity(); }

            // Everything past this point is a plain route. The table maps
            // one exact method-and-path pair to the code that answers it,
            // and the short list after it covers the paths that carry an id
            // on the end and so cannot be matched exactly. The table is
            // consulted first because that is the order the if-chain this
            // replaced ran in: every exact test came before the one
            // StartsWith test at the bottom of it.
            if (Routes.TryGetValue((method, path), out var route))
            {
                await route(this, ctx, auth);
                return;
            }

            foreach (var prefixed in PrefixRoutes)
            {
                if (method == prefixed.Method
                    && path.StartsWith(prefixed.Prefix, StringComparison.Ordinal))
                {
                    await prefixed.Handler(this, ctx, auth, path);
                    return;
                }
            }

            NotFound(res, "not found");
        }
        catch (Exception ex)
        {
            Log.Warn("control", $"request failed: {ex.Message}");
            try { WriteJson(res, 500, new { error = "internal error" }); } catch { }
        }
        finally
        {
            // DLNA reaches the library through this same listener, so the
            // line says which of the two it was rather than calling a
            // television's fetch a dashboard request.
            var isDlna = (ctx.Request.Url?.AbsolutePath ?? "/")
                .StartsWith("/dlna/", StringComparison.OrdinalIgnoreCase);
            Logging.AccessLog.Served(isDlna ? "dlna" : "control", ctx, who);
            try { res.Close(); } catch { }
        }
    }

    /// <summary>
    /// One route's answer. Every handler takes the same three things so that
    /// they can all live in one table; the ones with no interest in who is
    /// calling discard the third. Task-returning because a handful of the
    /// free-TV routes wait on a remote service, and a table cannot hold two
    /// shapes of handler at once.
    /// </summary>
    private delegate Task Route(ControlApi api, HttpListenerContext ctx, AuthResult auth);

    /// <summary>The same, for a path that carries an id on the end of it.</summary>
    private delegate Task PrefixRoute(ControlApi api, HttpListenerContext ctx, AuthResult auth, string path);

    /// <summary>
    /// Wraps a handler that finishes on the calling thread. Most of them do,
    /// because the work is memory and files. Awaiting a task that is already
    /// complete continues inline, so these still answer without the request
    /// ever leaving its thread, exactly as they did as inline code.
    /// </summary>
    private static Route Sync(Action<ControlApi, HttpListenerContext, AuthResult> handler) =>
        (api, ctx, auth) => { handler(api, ctx, auth); return Task.CompletedTask; };

    /// <summary>
    /// Every route that is one exact method and path, with the code that
    /// answers it. The table is static because it is the same for every
    /// request and every server: the handlers are named on the instance the
    /// dispatcher hands in rather than captured, so it is built once for the
    /// process instead of once per request.
    ///
    /// No two keys can collide, so the order of the entries here is for
    /// reading only and carries no meaning; it follows the order the routes
    /// were written in before this was a table. A miss falls through to the
    /// prefix list and then to the 404, which is what the chain's last else
    /// did.
    /// </summary>
    private static readonly Dictionary<(string Method, string Path), Route> Routes = new()
    {
        [("GET", "/api/status")] = Sync((api, ctx, auth) => api.WriteStatus(ctx, auth)),
        [("GET", "/api/problems")] = Sync((api, ctx, _) => api.WriteProblems(ctx)),
        [("POST", "/api/history/position")] = Sync((api, ctx, auth) => api.RecordPosition(ctx, auth)),
        [("POST", "/api/problems/clear")] = Sync((api, ctx, _) => api.ClearProblems(ctx)),
        [("GET", "/api/config")] = Sync((api, ctx, _) => api.WriteConfig(ctx)),
        [("GET", "/api/mounts")] = Sync((api, ctx, _) => api.WriteMounts(ctx)),
        [("GET", "/api/sessions")] = Sync((api, ctx, _) => api.WriteSessions(ctx)),
        [("GET", "/api/media/token")] = Sync((api, ctx, _) => api.MintMediaToken(ctx)),
        [("GET", "/api/preview")] = Sync((api, ctx, _) => api.StreamPreview(ctx)),
        [("GET", "/api/browse")] = Sync((api, ctx, _) => api.Browse(ctx)),

        // batch transcoding
        [("GET", "/api/transcode/scan")] = Sync((api, ctx, _) => api.TranscodeScan(ctx)),
        [("POST", "/api/transcode")] = Sync((api, ctx, _) => api.TranscodeBatch(ctx)),
        [("GET", "/api/transcode/config")] = Sync((api, ctx, _) => api.TranscodeConfig(ctx, write: false)),
        [("POST", "/api/transcode/config")] = Sync((api, ctx, _) => api.TranscodeConfig(ctx, write: true)),
        [("POST", "/api/transcode/remove")] = Sync((api, ctx, _) => api.TranscodeRemove(ctx)),
        [("POST", "/api/transcode/delete")] = Sync((api, ctx, _) => api.TranscodeDelete(ctx)),

        // service power + settings
        [("POST", "/api/server/start")] = Sync((api, ctx, _) => api.StartStreamingServices(ctx)),
        [("POST", "/api/server/restart")] = Sync((api, ctx, _) => api.RestartProcess(ctx)),
        [("POST", "/api/server/stop")] = Sync((api, ctx, _) => api.StopStreamingServices(ctx)),
        [("GET", "/api/settings")] = Sync((api, ctx, _) => api.WriteSettings(ctx)),
        [("POST", "/api/settings")] = Sync((api, ctx, _) => api.SaveSettings(ctx)),

        // the log, for the dashboard's panel
        [("GET", "/api/log")] = Sync((api, ctx, _) => api.WriteLogTail(ctx)),
        [("GET", "/api/log/files")] = Sync((api, ctx, _) => api.WriteLogFileList(ctx)),
        [("GET", "/api/log/file")] = Sync((api, ctx, _) => api.WriteLogFileText(ctx)),

        // what has been watched lately
        [("GET", "/api/history")] = Sync((api, ctx, auth) => api.WriteHistory(ctx, auth)),
        [("DELETE", "/api/history")] = Sync((api, ctx, auth) => api.ForgetHistory(ctx, auth)),

        // media engine (ffmpeg)
        [("POST", "/api/play")] = Sync((api, ctx, auth) => api.PlayFile(ctx, auth)),
        [("GET", "/api/play")] = Sync((api, ctx, _) => api.WriteVodReady(ctx)),
        [("GET", "/api/channels")] = Sync((api, ctx, _) => api.WriteChannels(ctx)),
        [("POST", "/api/channels")] = Sync((api, ctx, _) => api.AddChannel(ctx)),

        // what DLNA is allowed to show
        [("GET", "/api/dlna")] = Sync((api, ctx, _) => api.WriteDlnaShare(ctx)),
        [("POST", "/api/dlna")] = Sync((api, ctx, _) => api.SetDlnaShare(ctx)),

        // HDHomeRun: read a tuner's lineup, import what's picked
        [("GET", "/api/tuner")] = (api, ctx, _) => api.ReadTunerLineup(ctx),
        [("POST", "/api/channels/import")] = Sync((api, ctx, _) => api.ImportChannels(ctx)),
        [("DELETE", "/api/channels")] = Sync((api, ctx, _) => api.RemoveChannel(ctx)),
        [("POST", "/api/channels/restart")] = Sync((api, ctx, _) => api.RestartChannel(ctx)),
        [("POST", "/api/channels/start")] = Sync((api, ctx, _) => api.StartChannel(ctx)),
        [("POST", "/api/channels/stop")] = Sync((api, ctx, _) => api.StopChannel(ctx)),

        // free ad-supported TV (Pluto TV + playlist providers). The two proxy
        // routes appear earlier in HandleAsync as well, for a request that
        // carries one of this install's signatures instead of an account.
        // Reaching them here means an account is what authorized them.
        [("GET", "/api/tv/providers")] = Sync((api, ctx, _) => api.WriteTvProviders(ctx)),
        [("GET", "/api/tv/lineup")] = (api, ctx, _) => api.TvLineup(ctx),
        [("GET", "/api/tv/watch")] = (api, ctx, auth) => api.TvProxy(ctx, entry: true, auth),
        [("GET", "/api/tv/r")] = (api, ctx, auth) => api.TvProxy(ctx, entry: false, auth),
        [("POST", "/api/tv/pin")] = (api, ctx, _) => api.TvPin(ctx),
        [("GET", "/api/codecs")] = Sync((api, ctx, _) => api.WriteCodecs(ctx)),

        // pinned media (quick buttons)
        [("GET", "/api/favorites")] = Sync((api, ctx, _) => api.WriteFavorites(ctx)),
        [("POST", "/api/favorites")] = Sync((api, ctx, _) => api.AddFavorite(ctx)),
        [("DELETE", "/api/favorites")] = Sync((api, ctx, _) => api.RemoveFavorite(ctx)),

        // library root folders
        [("GET", "/api/library")] = Sync((api, ctx, _) => api.WriteLibraryFolders(ctx)),
        [("GET", "/api/library/search")] = Sync((api, ctx, _) => api.SearchLibrary(ctx)),
        [("POST", "/api/library")] = Sync((api, ctx, _) => api.AddLibraryFolder(ctx)),
        [("DELETE", "/api/library")] = Sync((api, ctx, _) => api.RemoveLibraryFolder(ctx)),
        [("GET", "/api/thumb")] = Sync((api, ctx, auth) => api.ServeThumbnail(ctx, auth)),

        // remembered playlists (media library folders)
        [("GET", "/api/playlists")] = Sync((api, ctx, _) => api.WritePlaylists(ctx)),
        [("POST", "/api/playlists")] = Sync((api, ctx, _) => api.SavePlaylist(ctx)),
        [("DELETE", "/api/playlists")] = Sync((api, ctx, _) => api.RemovePlaylist(ctx)),

        // attach a subtitle file the user picked to an existing stream
        [("POST", "/api/subtitles")] = Sync((api, ctx, _) => api.AttachSubtitle(ctx)),
        [("GET", "/api/image")] = Sync((api, ctx, auth) => api.ServeImage(ctx, auth)),
        [("POST", "/api/mounts")] = Sync((api, ctx, _) => api.AddMount(ctx)),
        [("DELETE", "/api/mounts")] = Sync((api, ctx, _) => api.RemoveMount(ctx)),
        [("DELETE", "/api/hls")] = Sync((api, ctx, _) => api.RemoveHlsStream(ctx)),
        [("POST", "/api/hls/retranscode")] = Sync((api, ctx, _) => api.RetranscodeStream(ctx)),
    };

    /// <summary>
    /// The routes whose path carries an id on the end, so that no exact key
    /// can match them. Ordered, and read only after the exact table has
    /// missed, which is where the StartsWith test sat in the chain this
    /// replaced. Today there is one; the shape is here so the next one does
    /// not go back to being an if at the bottom of the dispatcher.
    /// </summary>
    private static readonly (string Method, string Prefix, PrefixRoute Handler)[] PrefixRoutes =
    {
        ("DELETE", "/api/sessions/",
            (api, ctx, _, path) => { api.TerminateSession(ctx, path); return Task.CompletedTask; }),
    };

    /// <summary>GET /api/status - identity, uptime, and every live counter the dashboard polls.</summary>
    /// <summary>
    /// POST /api/history/position - how far through something a viewer has got.
    ///
    /// Read level: reporting your own progress is part of watching, and the
    /// store only ever matches a row this caller can already see. The player
    /// sends this every few seconds while playing and once more when it
    /// pauses or the page goes away, so it has to be cheap and it has to
    /// tolerate arriving for something that is no longer in the history.
    /// </summary>
    private void RecordPosition(HttpListenerContext ctx, AuthResult auth)
    {
        if (!TryReadJsonBody<PositionRequest>(ctx, out var req, out var error))
        { BadRequest(ctx.Response, error); return; }
        var key = req?.Key ?? "";
        var ok = _history.Position(auth.Name, key, req?.Seconds ?? 0, req?.Duration ?? 0);
        WriteJson(ctx.Response, 200, new { recorded = ok });
    }

    private sealed class PositionRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("key")] public string? Key { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("seconds")] public double? Seconds { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("duration")] public double? Duration { get; set; }
    }

    /// <summary>
    /// GET /api/problems - what has failed lately. Server Admin, the same tier
    /// as the log it is drawn from: these name file paths.
    /// </summary>
    private void WriteProblems(HttpListenerContext ctx)
    {
        WriteJson(ctx.Response, 200, new
        {
            problems = Problems.All.Select(p => new
            {
                kind = p.Kind,
                path = p.Path,
                name = Path.GetFileName(p.Path) is { Length: > 0 } n ? n : p.Path,
                detail = p.Detail,
                whenUtc = p.WhenUtc,
                count = p.Count,
            }),
        });
    }

    /// <summary>POST /api/problems/clear - forget one path, or all of them.</summary>
    private void ClearProblems(HttpListenerContext ctx)
    {
        var path = ctx.Request.QueryString["path"];
        Problems.Clear(path: string.IsNullOrWhiteSpace(path) ? null : path);
        WriteJson(ctx.Response, 200, new { ok = true, remaining = Problems.Count });
    }

    private void WriteStatus(HttpListenerContext ctx, AuthResult auth)
    {
        var res = ctx.Response;
        WriteJson(res, 200, new
        {
            server = _serverConfig.ServerName,
            version = typeof(ControlApi).Assembly.GetName().Version?.ToString(3),
            running = _services.Running,
            uptimeSeconds = (int)(DateTime.UtcNow - _startedUtc).TotalSeconds,
            // The header's Users button and the pill beside it, riding the
            // poll the header already makes rather than costing a request of
            // their own. Counts are plain numbers and go to anyone signed in;
            // signedInUsers names people and where they are connecting from,
            // so it is an administrator's to see and nobody else's. Hiding the
            // pill in CSS would not be enough — the answer must not be in the
            // response at all for a read-only account.
            problems = Problems.Count,
            accounts = _auth.AccountCount,
            signedIn = _auth.SignedInCount,
            signedInUsers = auth.IsAdmin
                ? _auth.SignedInSessions.Select(s => new
                  {
                      user = s.Username,
                      client = s.Client,
                      idleSeconds = s.IdleSeconds,
                  })
                : null,
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
                    // how much longer, at the rate this job is actually going
                    etaSeconds = v.EtaSeconds,
                }),
            // files waiting to convert, in order — the dashboard
            // lists these with a remove button (running ones can't
            // be removed here, only what hasn't started)
            transcodeQueue = (_ffmpeg?.VodQueueSnapshot ?? (IReadOnlyList<string>)Array.Empty<string>())
                .Select(p => new { path = p, title = Media.StreamTitle.PrettifyFile(Path.GetFileName(p)) }),
        });
    }

    /// <summary>GET /api/config - the effective configuration, with the auth token redacted.</summary>
    private void WriteConfig(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        // redact by replacing the value before serialization, not
        // by string-replacing the output (which missed tokens
        // containing +, ", or non-ASCII once JSON-escaped)
        var savedToken = _serverConfig.Control.AuthToken;
        _serverConfig.Control.AuthToken = savedToken.Length > 0 ? "***" : "";
        JsonElement redacted;
        try { redacted = JsonSerializer.Deserialize<JsonElement>(_serverConfig.ToJson()); }
        finally { _serverConfig.Control.AuthToken = savedToken; }
        WriteJson(res, 200, new { config = redacted, note = "control.authToken redacted" });
    }

    /// <summary>GET /api/mounts - the configured RTSP mounts and the URIs they answer on.</summary>
    private void WriteMounts(HttpListenerContext ctx)
    {
        var res = ctx.Response;
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
    }

    /// <summary>GET /api/sessions - who is watching, over RTSP and over HTTP alike.</summary>
    private void WriteSessions(HttpListenerContext ctx)
    {
        var res = ctx.Response;
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
    }

    // A media token for the caller's own playback. The dashboard
    // takes one all-streams token at startup and appends it to every
    // HLS URL it builds; ?stream= narrows it to a single stream for
    // a link you mean to hand to VLC, a TV, or someone else.
    private void MintMediaToken(HttpListenerContext ctx)
    {
        var res = ctx.Response;
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
    }

    /// <summary>POST /api/server/start - bring the streaming services up.</summary>
    private void StartStreamingServices(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try { _services.StartServices(); }
        catch (Exception ex) { WriteJson(res, 500, new { error = ex.Message }); return; }
        Log.Info("control", "services started via dashboard");
        WriteJson(res, 200, new { running = _services.Running });
    }

    // Restarting the *process*, not the streaming services: the
    // settings that only apply at startup — the control port, TLS —
    // otherwise leave the dashboard telling someone to go and do it
    // themselves, which on a tray-mode server means hunting for the
    // icon. Not on Unix: there the server belongs to systemd or
    // launchd, and relaunching itself would fight whatever supervises
    // it.
    private void RestartProcess(HttpListenerContext ctx)
    {
        var res = ctx.Response;
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
    }

    /// <summary>POST /api/server/stop - take the streaming services down.</summary>
    private void StopStreamingServices(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        _services.StopServices();
        Log.Info("control", "services stopped via dashboard");
        WriteJson(res, 200, new { running = _services.Running });
    }

    /// <summary>GET /api/settings - everything the Config dialog shows, configured and actual.</summary>
    private void WriteSettings(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        WriteJson(res, 200, new
        {
            serverName = _serverConfig.ServerName,
            bindAddress = _serverConfig.Rtsp.BindAddress,
            rtspPort = _serverConfig.Rtsp.Port,
            hlsPort = _serverConfig.Hls.Port,
            controlPort = _serverConfig.Control.Port,
            minimizeToTray = _serverConfig.MinimizeToTray,
            linkLifetimeHours = _serverConfig.Hls.LinkLifetimeHours,
            // where transcodes and live-channel streams are written; the
            // resolved path is what Browse opens at and what the box shows
            mediaRoot = _serverConfig.Hls.MediaRoot,
            mediaRootResolved = MediaRootPath(),
            // what removing a stream link does with an existing conversion
            streamRemoveAction = _serverConfig.StreamRemoveAction,
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
    }

    /// <summary>GET /api/log - the live ring buffer since the sequence number the caller holds.</summary>
    private void WriteLogTail(HttpListenerContext ctx)
    {
        var res = ctx.Response;
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
    }

    // The rotated history on disk, so the log window can review earlier
    // sessions — the crash last night, say — not only the live ring
    // buffer, which starts empty on every restart. Newest first; the
    // active file is flagged so the UI can label it "current".
    private void WriteLogFileList(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var dir = _serverConfig.Logging.ResolveDirectory(_baseDirectory);
        var activeName = Log.FilePath is null ? null : Path.GetFileName(Log.FilePath);
        WriteJson(res, 200, new
        {
            dir,
            files = Log.Files(dir).Select(f => new
            {
                name = f.Name,
                bytes = f.Bytes,
                modified = f.Modified,
                active = f.Name.Equals(activeName, StringComparison.OrdinalIgnoreCase),
            }),
        });
    }

    // One rotated file's tail, as raw text. Raw, not parsed into
    // entries: a crash's stack trace spans many lines and is the whole
    // reason to read history, so it is shown exactly as written rather
    // than flattened into one message per line.
    private void WriteLogFileText(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var name = ctx.Request.QueryString["name"] ?? "";
        var take = int.TryParse(ctx.Request.QueryString["max"], out var mx) ? Math.Clamp(mx, 50, 5000) : 2000;
        var dir = _serverConfig.Logging.ResolveDirectory(_baseDirectory);

        // Only a file the listing actually offers — no path the caller
        // typed. This blocks "../" and any name that is not a real log
        // file in the log directory.
        var known = Log.Files(dir).FirstOrDefault(f =>
            f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (known.Name is null)
        {
            NotFound(res, "no such log file");
            return;
        }

        var full = Path.Combine(dir, known.Name);
        string[] lines;
        try
        {
            // shared read: the active file is being written to right now
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            lines = sr.ReadToEnd().Split('\n');
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { error = "could not read: " + ex.Message });
            return;
        }

        var truncated = lines.Length > take;
        var tail = truncated ? lines[^take..] : lines;
        WriteJson(res, 200, new
        {
            name = known.Name,
            bytes = known.Bytes,
            truncated,
            shown = tail.Length,
            text = string.Join("\n", tail),
        });
    }

    /// <summary>GET /api/history - what this account has watched lately, newest first.</summary>
    private void WriteHistory(HttpListenerContext ctx, AuthResult auth)
    {
        var res = ctx.Response;
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
                // where the viewer got to, so the list can offer to pick it up
                positionSeconds = (int)e.PositionSeconds,
                durationSeconds = (int)e.DurationSeconds,
                canResume = e.CanResume,
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
    }

    /// <summary>DELETE /api/history - forget one entry, or the caller's whole history.</summary>
    private void ForgetHistory(HttpListenerContext ctx, AuthResult auth)
    {
        var res = ctx.Response;
        // no path clears the caller's whole history
        var target = ctx.Request.QueryString["path"] ?? "";
        WriteJson(res, 200, new { removed = _history.Forget(auth.Name, target) });
    }

    /// <summary>GET /api/play - whether a conversion has produced enough to start playing.</summary>
    private void WriteVodReady(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var stream = ctx.Request.QueryString["stream"] ?? "";
        WriteJson(res, 200, new { stream, ready = _ffmpeg?.IsVodReady(stream) ?? false });
    }

    /// <summary>GET /api/channels - the live restreams and how each one is faring.</summary>
    private void WriteChannels(HttpListenerContext ctx)
    {
        var res = ctx.Response;
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
    }

    /// <summary>GET /api/dlna - the switch, the port, and which library folders are shared.</summary>
    private void WriteDlnaShare(HttpListenerContext ctx)
    {
        var res = ctx.Response;
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
    }

    /// <summary>DELETE /api/channels - drop a live channel by name.</summary>
    private void RemoveChannel(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var name = ctx.Request.QueryString["name"] ?? "";
        if (_ffmpeg?.RemoveChannel(name) == true)
        {
            Log.Info("control", $"channel removed: {name}");
            WriteJson(res, 200, new { removed = name });
        }
        else NotFound(res, "unknown channel");
    }

    /// <summary>POST /api/channels/restart - bounce one channel's restream.</summary>
    private void RestartChannel(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var name = ctx.Request.QueryString["name"] ?? "";
        if (_ffmpeg?.RestartChannel(name) == true) WriteJson(res, 200, new { restarted = name });
        else NotFound(res, "unknown channel");
    }

    /// <summary>POST /api/channels/start - start a stopped channel.</summary>
    private void StartChannel(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var name = ctx.Request.QueryString["name"] ?? "";
        if (_ffmpeg?.StartChannel(name) == true)
        {
            Log.Info("control", $"channel started: {name}");
            WriteJson(res, 200, new { started = name });
        }
        else NotFound(res, "unknown channel");
    }

    /// <summary>POST /api/channels/stop - stop a running channel without removing it.</summary>
    private void StopChannel(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var name = ctx.Request.QueryString["name"] ?? "";
        if (_ffmpeg?.StopChannel(name) == true)
        {
            Log.Info("control", $"channel stopped: {name}");
            WriteJson(res, 200, new { stopped = name });
        }
        else NotFound(res, "unknown channel");
    }

    /// <summary>GET /api/tv/providers - the free-TV lineups that are switched on.</summary>
    private void WriteTvProviders(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        WriteJson(res, 200, new
        {
            providers = _providers.All
                .Where(p => p.Enabled)
                .Select(p => new { id = p.Id, name = p.Name }),
        });
    }

    /// <summary>GET /api/codecs - what ffmpeg is using now and everything it could use.</summary>
    private void WriteCodecs(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        WriteJson(res, 200, new
        {
            active = new { video = _ffmpeg?.VideoEncoder, audio = _ffmpeg?.AudioEncoder },
            videoEncoders = _ffmpeg?.VideoEncoders.OrderBy(x => x) ?? Enumerable.Empty<string>(),
            audioEncoders = _ffmpeg?.AudioEncoders.OrderBy(x => x) ?? Enumerable.Empty<string>(),
            note = "set ffmpeg.videoCodec / ffmpeg.audioCodec in the config (friendly name, raw encoder name, or 'copy')",
        });
    }

    /// <summary>GET /api/favorites - the pinned media, for the quick buttons.</summary>
    private void WriteFavorites(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        WriteJson(res, 200, new
        {
            favorites = _favorites.All.Select(f => new { name = f.Name, path = f.Path }),
        });
    }

    /// <summary>DELETE /api/favorites - unpin one by path.</summary>
    private void RemoveFavorite(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var favPath = ctx.Request.QueryString["path"] ?? "";
        if (_favorites.Remove(favPath))
        {
            Log.Info("control", $"favorite removed: {favPath}");
            WriteJson(res, 200, new { removed = favPath });
        }
        else NotFound(res, "unknown favorite");
    }

    /// <summary>GET /api/library - the library root folders.</summary>
    private void WriteLibraryFolders(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        WriteJson(res, 200, new { folders = _library.All });
    }

    /// <summary>DELETE /api/library - stop offering one root folder.</summary>
    private void RemoveLibraryFolder(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var folder = ctx.Request.QueryString["folder"] ?? "";
        if (_library.Remove(folder))
        {
            Log.Info("control", $"library folder removed: {folder}");
            WriteJson(res, 200, new { removed = folder });
        }
        else NotFound(res, "unknown library folder");
    }

    /// <summary>GET /api/playlists - the remembered media folders.</summary>
    private void WritePlaylists(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        WriteJson(res, 200, new
        {
            playlists = _playlists.All.Select(p => new { name = p.Name, folder = p.Folder }),
        });
    }

    /// <summary>DELETE /api/playlists - forget one remembered folder by name.</summary>
    private void RemovePlaylist(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var plName = ctx.Request.QueryString["name"] ?? "";
        if (_playlists.Remove(plName))
        {
            Log.Info("control", $"playlist removed: {plName}");
            WriteJson(res, 200, new { removed = plName });
        }
        else NotFound(res, "unknown playlist");
    }

    /// <summary>DELETE /api/mounts - remove one RTSP mount.</summary>
    private void RemoveMount(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var mountPath = ctx.Request.QueryString["path"] ?? "";
        if (_serverConfig.RemoveMount(mountPath))
        {
            Log.Info("control", $"mount removed via dashboard: {mountPath}");
            WriteJson(res, 200, new { removed = mountPath });
        }
        else
        {
            NotFound(res, "unknown mount");
        }
    }

    // Convert this media again from scratch: for a conversion that
    // came out wrong, or one made before the codec settings changed.
    // Unlinking keeps a conversion precisely because rebuilding it
    // would produce the same bytes — this is the case where that is
    // not true and the old one has to go.
    private void RetranscodeStream(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var stream = ctx.Request.QueryString["stream"] ?? "";
        if (stream.Length == 0 || stream.Contains("..") || stream.Contains('/') || stream.Contains('\\'))
        {
            BadRequest(res, "invalid stream name");
            return;
        }
        var root = MediaRootPath();
        var sdir = Path.GetFullPath(Path.Combine(root, stream));
        if (!sdir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(sdir))
        {
            NotFound(res, "unknown stream");
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
    }

    /// <summary>DELETE /api/sessions/{id} - tear one RTSP session down from the dashboard.</summary>
    private void TerminateSession(HttpListenerContext ctx, string path)
    {
        var res = ctx.Response;
        var id = path["/api/sessions/".Length..];
        // capture once — a concurrent /api/server/stop can null Rtsp
        var rtsp = RtspServer;
        var session = rtsp?.Sessions.Get(id);
        if (session is null)
        {
            NotFound(res, "session not found");
            return;
        }
        rtsp!.Sessions.Remove(id);
        Log.Info("control", $"session {id} terminated via control API");
        WriteJson(res, 200, new { terminated = id });
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
            NotFound(res, "unknown mount");
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
        if (!TryReadJsonBody<ServerConfig.SettingsOverrides>(ctx, out var s, out var bodyError))
        {
            BadRequest(res, bodyError);
            return;
        }
        if (s is null) { BadRequest(res, "empty body"); return; }

        foreach (var (port, name) in new[] { (s.RtspPort, "rtspPort"), (s.HlsPort, "hlsPort"), (s.ControlPort, "controlPort") })
        {
            if (port is int p and (< 1 or > 65535))
            {
                BadRequest(res, $"{name} must be 1–65535");
                return;
            }
        }
        if (!string.IsNullOrWhiteSpace(s.BindAddress) &&
            !System.Net.IPAddress.TryParse(s.BindAddress, out _) &&
            !s.BindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            BadRequest(res, "bindAddress must be an IP address (0.0.0.0 = all interfaces) or 'localhost'");
            return;
        }

        // an hour at the short end still covers a film; a year at the long
        // end is effectively "never expires", which is the caller's call
        if (s.LinkLifetimeHours is int lifetime and (< 1 or > 8760))
        {
            BadRequest(res, "link lifetime must be 1–8760 hours (up to a year)");
            return;
        }

        // ---- logging ----
        if (s.LogLevel is { Length: > 0 } lvl &&
            lvl.ToLowerInvariant() is not ("trace" or "debug" or "info" or "warn" or "error"))
        {
            BadRequest(res, "log level must be trace, debug, info, warn, or error");
            return;
        }
        if (s.LogRotatePeriod is { Length: > 0 } per &&
            per.ToLowerInvariant() is not ("none" or "hourly" or "daily" or "weekly" or "monthly"))
        {
            BadRequest(res, "rotation period must be none, hourly, daily, weekly, or monthly");
            return;
        }
        // 0 means "don't rotate on size"; 4 GB is past anything a text log
        // should reach before the period or the file count catches it
        if (s.LogRotateSizeMb is int mb and (< 0 or > 4096))
        {
            BadRequest(res, "rotation size must be 0–4096 MB (0 = no size limit)");
            return;
        }
        if (s.LogMaxFiles is int keep and (< 0 or > 1000))
        {
            BadRequest(res, "kept log files must be 0–1000");
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
                BadRequest(res, "log directory unusable: " + ex.Message);
                return;
            }
        }

        // the transcodes directory has to be creatable now, not fail silently
        // after a restart when the first conversion tries to write into it
        if (s.MediaRoot is { Length: > 0 })
        {
            try
            {
                var probe = Path.IsPathRooted(s.MediaRoot)
                    ? s.MediaRoot
                    : Path.Combine(_baseDirectory, s.MediaRoot);
                Directory.CreateDirectory(probe);
            }
            catch (Exception ex)
            {
                BadRequest(res, "transcodes directory unusable: " + ex.Message);
                return;
            }
        }
        if (s.StreamRemoveAction is { Length: > 0 } sra &&
            sra.ToLowerInvariant() is not ("ask" or "keep" or "delete"))
        {
            BadRequest(res, "stream remove action must be ask, keep, or delete");
            return;
        }

        // computed before UpdateSettings swaps the value in: a real change, so
        // the dialog can say the new directory needs a restart to take effect
        var mediaRootChanged = s.MediaRoot is { Length: > 0 }
            && !string.Equals(
                Path.GetFullPath(Path.IsPathRooted(s.MediaRoot) ? s.MediaRoot : Path.Combine(_baseDirectory, s.MediaRoot)),
                MediaRootPath(), StringComparison.OrdinalIgnoreCase);

        var ports = new[] { s.RtspPort ?? _serverConfig.Rtsp.Port, s.HlsPort ?? _serverConfig.Hls.Port, s.ControlPort ?? _serverConfig.Control.Port };
        if (ports.Distinct().Count() != 3)
        {
            BadRequest(res, "rtsp, hls, and control ports must all be different");
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
            mediaRootChanged,
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
            BadRequest(res, "invalid stream name");
            return;
        }

        if (_ffmpeg?.Channels.Any(c => c.stream.Equals(name, StringComparison.OrdinalIgnoreCase)) == true)
        {
            BadRequest(res, "that stream is a live channel — remove the channel instead");
            return;
        }

        var mediaRoot = MediaRootPath();
        var dir = Path.GetFullPath(Path.Combine(mediaRoot, name));
        if (!dir.StartsWith(mediaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(dir))
        {
            NotFound(res, "unknown stream");
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
                // Gone already counts as removed. Deleting a directory whose
                // files a dying ffmpeg is still releasing can throw
                // DirectoryNotFoundException on the retry *after* the delete
                // actually worked - and that derives from IOException, so it
                // went round the loop and came out as "could not delete -
                // files still in use" for a conversion that was already gone.
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
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
                BadRequest(res, "body must be { \"path\": \"...\", \"name\": \"optional\" }");
                return;
            }
            if (!TryLocalPath(req.path, out var full))
            {
                BadRequest(res, "network paths are not allowed");
                return;
            }
            var isFolder = Directory.Exists(full);
            if (!isFolder && !System.IO.File.Exists(full))
            {
                NotFound(res, "file or folder not found");
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
            BadRequest(res, ex.Message);
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
                BadRequest(res, "body must be { \"stream\": \"...\", \"file\": \"...\" }");
                return;
            }
            if (req.stream.Contains("..") || req.stream.Contains('/') || req.stream.Contains('\\'))
            {
                BadRequest(res, "invalid stream name");
                return;
            }

            var mediaRoot = MediaRootPath();
            var streamDir = Path.GetFullPath(Path.Combine(mediaRoot, req.stream));
            if (!streamDir.StartsWith(mediaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(streamDir))
            {
                NotFound(res, "unknown stream");
                return;
            }
            if (!TryLocalPath(req.file, out var file))
            {
                BadRequest(res, "network paths are not allowed");
                return;
            }
            // only accept real subtitle files, so this can't be used to read
            // arbitrary text files off disk as "subtitles"
            var subExt = Path.GetExtension(file).ToLowerInvariant();
            if (subExt is not (".srt" or ".vtt" or ".ass" or ".ssa" or ".sub" or ".smi" or ".sbv" or ".ttml" or ".dfxp"))
            {
                BadRequest(res, "not a subtitle file (.srt, .ass, .vtt, .sub, .ssa, .smi)");
                return;
            }
            if (!System.IO.File.Exists(file))
            {
                NotFound(res, "subtitle file not found");
                return;
            }

            var track = _subtitles.AttachFile(file, Path.Combine(streamDir, "subs"), req.label);
            if (track is null)
            {
                BadRequest(res, "could not convert that file to WebVTT — is it a subtitle file?");
                return;
            }
            Log.Info("control", $"subtitle attached to {req.stream}: {Path.GetFileName(file)}");
            WriteJson(res, 200, new { added = track.Id, label = track.Label });
        }
        catch (Exception ex)
        {
            BadRequest(res, ex.Message);
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
            BadRequest(res, "search needs at least two characters");
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
                BadRequest(res, "that folder is not in the library");
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
                BadRequest(res, "body must be { \"folder\": \"...\" }");
                return;
            }
            if (!TryLocalPath(req.folder, out var folder))
            {
                BadRequest(res, "network paths are not allowed");
                return;
            }
            if (!Directory.Exists(folder))
            {
                NotFound(res, "folder not found");
                return;
            }
            if (!_library.Add(folder))
            {
                WriteJson(res, 409, new { error = "already in the library" });
                return;
            }
            Log.Info("control", $"library folder added: {folder}");
            // A folder added now should have its codecs read now, not in
            // half an hour when the sweep next comes round.
            RequestCodecProbe(folder);
            WriteJson(res, 200, new { added = folder });
        }
        catch (Exception ex)
        {
            BadRequest(res, ex.Message);
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
                BadRequest(res, "network paths are not allowed");
                return;
            }
            if (DenyUnshared(ctx, auth, full)) return;
            var thumb = _ffmpeg?.GetThumbnail(full);
            if (thumb is null)
            {
                NotFound(res, "no thumbnail");
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
            BadRequest(res, ex.Message);
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
                BadRequest(res, "body must be { \"name\": \"...\", \"folder\": \"...\" }");
                return;
            }
            if (!TryLocalPath(req.folder, out var folder))
            {
                BadRequest(res, "network paths are not allowed");
                return;
            }
            if (!Directory.Exists(folder))
            {
                NotFound(res, "folder not found");
                return;
            }
            _playlists.Save(req.name.Trim(), folder);
            Log.Info("control", $"playlist saved: {req.name} → {folder}");
            WriteJson(res, 200, new { saved = req.name.Trim() });
        }
        catch (Exception ex)
        {
            BadRequest(res, ex.Message);
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
                BadRequest(res, "body must be { \"file\": \"...\", \"height\": 0|360|480|720|1080 }");
                return;
            }
            var height = req.height ?? 0;
            if (height is not (0 or 360 or 480 or 720 or 1080))
            {
                BadRequest(res, "height must be 0 (source), 360, 480, 720, or 1080");
                return;
            }
            if (!TryLocalPath(req.file, out var file))
            {
                BadRequest(res, "network paths are not allowed");
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
            NotFound(res, "file not found");
        }
        catch (Exception ex)
        {
            BadRequest(res, ex.Message);
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
            NotFound(res, $"unknown provider '{id}'");
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
                NotFound(res, $"unknown provider '{id}'");
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
                NotFound(res, "unknown channel");
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
            BadRequest(res, "target must be an http(s) URL");
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
        if (!TryReadJsonBody<DlnaShareRequest>(ctx, out var req, out var bodyError))
        { BadRequest(res, bodyError); return; }
        if (req?.folders is null)
        {
            BadRequest(res, "body must be { \"folders\": [ \"C:\\\\path\", … ] }");
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

            case "/dlna/live":
            {
                if (method is not ("GET" or "HEAD")) { res.StatusCode = 405; res.Close(); return; }
                if (!_serverConfig.Discovery.DlnaLiveTv || _ffmpeg is null || _dlnaLive is null)
                { res.StatusCode = 404; res.Close(); return; }

                var stream = ctx.Request.QueryString["ch"] ?? "";
                // Tune-on-demand: picking a channel on the TV starts its
                // restream if it is not already running, so it comes up like
                // live TV. Resolve against the channel list, never the raw
                // query — its "ch-…" name is the only thing allowed to form the
                // directory path.
                _ffmpeg.EnsureChannelRunning(stream);
                var ch = _ffmpeg.Channels.FirstOrDefault(c => c.stream == stream);
                if (ch.stream is null)
                {
                    Log.Debug("dlna", $"live: no such channel: {stream}");
                    res.StatusCode = 404;
                    res.Close();
                    return;
                }

                var channelDir = Path.Combine(MediaRootPath(), ch.stream);
                if (method == "HEAD")
                {
                    _dlnaLive.Serve(ctx, ch.stream, channelDir);
                    return;
                }

                var liveViewing = _services.Viewers.Note(
                    ctx, ch.stream, user: null, bytes: 0, create: true, protocol: "dlna", file: ch.def.Name);
                void LiveSent(long sent)
                {
                    _services.Served.Add(sent);
                    if (!_services.Viewers.Progress(liveViewing, sent))
                        liveViewing = _services.Viewers.Note(
                            ctx, ch.stream, user: null, bytes: sent, create: true, protocol: "dlna", file: ch.def.Name);
                }
                Log.Info("dlna", $"serving live channel {ch.def.Name} as a timeshift stream");
                _dlnaLive.Serve(ctx, ch.stream, channelDir, LiveSent);
                return;
            }

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

            // Restart with the arguments this copy was given, not without them.
            //
            // They were being dropped, and the config path is among them. The
            // restarted server then re-ran the "which server.json is there"
            // probe from scratch, and whichever it settled on decides where
            // users.json is read from — so a Restart could come back as a
            // server with no accounts, offering to create an administrator.
            // The working directory alone is not the same instruction.
            var args = Environment.GetCommandLineArgs().Skip(1)
                .Select(a => "'" + a.Replace("'", "''") + "'")
                .ToArray();
            var argList = args.Length > 0 ? " -ArgumentList " + string.Join(",", args) : "";

            var script =
                $"Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue; " +
                "Start-Sleep -Milliseconds 800; " +
                $"Start-Process -FilePath '{exe.Replace("'", "''")}' " +
                $"-WorkingDirectory '{dir.Replace("'", "''")}'{argList}";

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
            BadRequest(res, "host must be a tuner address, e.g. 192.168.1.50 or hdhomerun.local");
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

        if (!TryReadJsonBody<ImportRequest>(ctx, out var req, out var bodyError))
        { BadRequest(res, bodyError); return; }

        var wanted = req?.channels ?? new List<ChannelRequest>();
        if (wanted.Count == 0)
        {
            BadRequest(res, "body must be { \"channels\": [ { \"name\": \"…\", \"url\": \"…\" } ] }");
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
                BadRequest(res, "body must be { \"provider\": \"…\", \"id\": \"…\" }");
                return;
            }

            var lineup = await provider.LineupAsync(_cts.Token);
            var channel = lineup.FirstOrDefault(c => c.Id == channelId);
            if (channel is null)
            {
                NotFound(res, "unknown channel");
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
        catch (Exception ex) { BadRequest(res, ex.Message); }
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
                BadRequest(res, "body must be { \"name\": \"...\", \"url\": \"...\" }");
                return;
            }
            var scheme = Uri.TryCreate(req.url, UriKind.Absolute, out var u) ? u.Scheme.ToLowerInvariant() : "";
            if (scheme is not ("http" or "https" or "rtsp" or "rtmp" or "udp" or "rtp" or "srt"))
            {
                BadRequest(res, "url must be http(s)/rtsp/rtmp/udp/rtp/srt");
                return;
            }
            var stream = _ffmpeg.AddChannel(req.name.Trim(), req.url.Trim());
            Log.Info("control", $"channel added: {req.name} ← {req.url}");
            WriteJson(res, 200, new { stream, playlist = $"/{stream}/index.m3u8" });
        }
        catch (InvalidOperationException ex) { WriteJson(res, 409, new { error = ex.Message }); }
        catch (Exception ex) { BadRequest(res, ex.Message); }
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
                BadRequest(res, "network paths are not allowed");
                return;
            }
            if (DenyUnshared(ctx, auth, full)) return;
            if (!System.IO.File.Exists(full) || !ImageTypes.TryGetValue(Path.GetExtension(full), out var mime))
            {
                NotFound(res, "not an image");
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
            BadRequest(res, ex.Message);
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
        if (!TryReadJsonBody<MountConfig>(ctx, out var mount, out var bodyError))
        {
            BadRequest(res, bodyError);
            return;
        }

        if (mount is null || string.IsNullOrWhiteSpace(mount.Path) || !mount.Path.StartsWith('/'))
        {
            BadRequest(res, "mount path must start with '/'");
            return;
        }
        mount.Path = "/" + mount.Path.Trim().Trim('/');
        if (mount.Path == "/" || mount.Path.Any(char.IsWhiteSpace))
        {
            BadRequest(res, "invalid mount path");
            return;
        }
        if (mount.Path.Equals("/annc", StringComparison.OrdinalIgnoreCase))
        {
            BadRequest(res, "/annc is reserved for the announcement service");
            return;
        }

        switch (mount.Source.ToLowerInvariant())
        {
            case "tone":
                if (mount.ToneFrequencyHz is < 20 or > 4000)
                {
                    BadRequest(res, "tone frequency must be 20–4000 Hz (8 kHz sampling)");
                    return;
                }
                break;

            case "file":
                if (string.IsNullOrWhiteSpace(mount.File))
                {
                    BadRequest(res, "file source requires a file path");
                    return;
                }
                var rawFile = Path.IsPathRooted(mount.File) ? mount.File : Path.Combine(_baseDirectory, mount.File);
                if (!TryLocalPath(rawFile, out var full))
                {
                    BadRequest(res, "network paths are not allowed");
                    return;
                }
                if (!System.IO.File.Exists(full))
                {
                    BadRequest(res, "file not found: " + full);
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
                BadRequest(res, "source must be 'tone' or 'file'");
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
                BadRequest(res, "network paths are not allowed");
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
            BadRequest(res, ex.Message);
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
                BadRequest(res, "network paths are not allowed");
                return;
            }
            if (!Directory.Exists(full))
            {
                WriteJson(res, 404, new { error = "directory not found", path = full });
                return;
            }


            // Probe what is being looked at, in the background.
            //
            // This is the transcode panel's own listing - the one whose folder
            // pills say "checking..." until the codecs behind them are known.
            // The prefetch sweep covers the media library, but this panel
            // browses anywhere on the machine, and a folder it is showing is
            // the clearest possible signal that these are the files somebody
            // wants an answer about. Queue the directory; the listing itself
            // never waits on a probe.
            RequestCodecProbe(full);
            var dir = new DirectoryInfo(full);
            var entries = new List<object>();
            var q = ctx.Request.QueryString["q"];

            // Search: recursively find video files under this folder whose name
            // matches, like the library search. Codecs are read cache-only so a
            // search never launches a probe; results are capped.
            if (!string.IsNullOrWhiteSpace(q))
            {
                const int searchCap = 500; var searchCapped = false;
                var sopts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
                };
                try
                {
                    foreach (var fp in Directory.EnumerateFiles(full, "*", sopts))
                    {
                        if (!TranscodableExt.Contains(Path.GetExtension(fp))) continue;
                        if (Path.GetFileName(fp).IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (entries.Count >= searchCap) { searchCapped = true; break; }
                        entries.Add(TranscodeFileEntry(new FileInfo(fp), cacheOnly: true));
                    }
                }
                catch { /* report whatever matched before the walk failed */ }
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
        catch (Exception ex) { BadRequest(res, ex.Message); }
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
    /// <summary>
    /// How this server walks a folder of media, everywhere it walks one.
    ///
    /// It is shared because the two walks that mattered had drifted apart: the
    /// pill counted with these rules while the Convert button enumerated with
    /// a bare SearchOption.AllDirectories, which skips nothing and gives up on
    /// the first folder it is refused. So the number shown and the work done
    /// were answers to different questions — hidden and system files, and
    /// anything behind a junction, were converted without ever having been
    /// counted, a junction pointing back into the tree offered the same film
    /// twice under two paths, and one unreadable sub-folder threw away the
    /// rest of the selection. One set of rules, one answer.
    /// </summary>
    private static readonly EnumerationOptions MediaWalk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
    };

    private object FolderMediaSummary(string dir)
    {
        int media = 0, needs = 0, done = 0, ready = 0, unknown = 0;
        const int cap = 4000;
        var capped = false;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", MediaWalk))
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


    /// Directories the transcode panel has been asked to list, waiting to be
    /// probed. Bounded: a queue that grows without limit because somebody
    /// clicked through a hundred folders is a leak, and the sweep will reach
    /// the rest anyway.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _probeWanted = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _probeQueued =
        new(StringComparer.OrdinalIgnoreCase);

    /// When each directory was last finished, so the panel asking again every
    /// few seconds does not start the same walk over.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _probeDone =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a directory that has just been walked is left alone. The
    /// dashboard asks for a probe every time a folder is listed, and clicking
    /// back and forth between two folders would otherwise re-walk both of them
    /// several times a minute for nothing, since what is on disk does not
    /// change that fast. Forty-five seconds is short enough that a file dropped
    /// into a folder someone is looking at still turns up on their next visit.
    /// </summary>
    private static readonly TimeSpan ProbeRewalkSuppressed = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How old an entry in that record has to be before it is thrown away when
    /// the table is trimmed. It exists only to suppress repeats, so anything
    /// past the suppression window above is already doing nothing; ten minutes
    /// leaves a wide margin over it while still keeping the table from growing
    /// for as long as the server runs.
    /// </summary>
    private static readonly TimeSpan ProbeRecordExpiry = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Asks the background prober to look at this directory next. Cheap and
    /// non-blocking - the listing that triggered it must not wait for ffprobe.
    /// </summary>
    private void RequestCodecProbe(string dir)
    {
        if (_tvCodecs is null || string.IsNullOrWhiteSpace(dir)) return;
        if (_probeQueued.Count > 500) return;                 // already plenty to do
        // walked recently: nothing has changed in the seconds since
        if (_probeDone.TryGetValue(dir, out var done) && DateTime.UtcNow - done < ProbeRewalkSuppressed) return;
        // this only exists to suppress repeats, so old entries are worthless;
        // left alone it grows for as long as the server runs
        if (_probeDone.Count > 2000)
            foreach (var stale in _probeDone.Where(e => DateTime.UtcNow - e.Value > ProbeRecordExpiry).Select(e => e.Key).ToList())
                _probeDone.TryRemove(stale, out _);
        if (!_probeQueued.TryAdd(dir, 0)) return;             // asked for already
        _probeWanted.Enqueue(dir);
    }
    /// <summary>
    /// Walks the library in the background and probes what the codec cache
    /// doesn't know yet, so the transcode panel's "checking..." pills settle
    /// into real counts on their own.
    ///
    /// Deliberately unhurried. Probing is an ffprobe launch per file, and the
    /// point of this is a library nobody has browsed yet - possibly thousands
    /// of files - on a machine that is also serving video. So it takes one file
    /// at a time with a pause between, skips anything already cached (the check
    /// is a dictionary hit, not a probe), and saves the cache as it goes so the
    /// work survives a restart rather than starting over.
    /// </summary>
    /// <summary>
    /// How long the prefetch waits before its first file. Everything that
    /// happens at startup - the dashboard opening, channels being restored, the
    /// library being read - wants the disk more than this does, and ten seconds
    /// is enough for that rush to be over. Nothing depends on the exact value;
    /// it only has to be long enough that the first ffprobe is not competing
    /// with the parts of startup the user is waiting on.
    /// </summary>
    private static readonly TimeSpan ProbePrefetchStartDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The gap between rounds of the prefetch, and the tick it waits in. Half
    /// an hour is a guess at how often a library gains files, and it is cheap
    /// to be wrong about: a round with nothing new is a walk of dictionary
    /// lookups. The wait is broken into two-second ticks rather than one long
    /// sleep so that a folder being browsed can cut it short - otherwise the
    /// "checking..." pills someone is looking at right now would sit there
    /// until the full period expired.
    /// </summary>
    private const int ProbeRoundSeconds = 1800;
    private const int ProbeIdleTickSeconds = 2;

    private void StartCodecPrefetch()
    {
        if (_tvCodecs is null) return;
        _ = Task.Run(async () =>
        {
            // Let startup finish first: the dashboard opening, channels being
            // restored and the library being read all want the disk more than
            // this does.
            await Task.Delay(ProbePrefetchStartDelay).ConfigureAwait(false);

            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            };

            var probed = 0;

            // One file, if its answer is not already known. The pause is what
            // keeps this in the background - ffprobe is one short process at a
            // time, and the gap is there so a sweep never competes with
            // playback. It was 250ms, which is fine for a folder and hopeless
            // for a library: at four files a second, a collection of any size
            // takes hours to come round, which is what "the pills never change"
            // actually was. Someone waiting on a folder they just opened gets
            // the short gap; the background sweep keeps a longer one.
            // Probe a batch at once.
            //
            // The original pacing assumed a probe was expensive, so it left a
            // gap between files and a library took hours. Measured, ffprobe
            // reads stream headers and nothing else: about 90ms per file, and
            // the same for a 1.6 GB film as for a 40 KB clip - the file's size
            // does not come into it. The cost is process startup, which is
            // exactly the kind of work that overlaps.
            //
            // So run several together and drop the gap. Four at a time is a
            // handful of short-lived processes, nothing next to an encode, and
            // it turns a thousand-file folder from minutes into seconds.
            // Scaled to the machine. Four was picked when the gap between files was
            // the thing being tuned; the real limit is how many short ffprobe
            // processes can be in flight, and a box with cores to spare can
            // have plenty. Four probes at 90ms each is 22ms a file, which is
            // two minutes for a five-thousand-file folder - the wait actually
            // reported. Sixteen brings the same folder under half a minute.
            var probeWidth = Math.Clamp(Environment.ProcessorCount / 2, 4, 16);
            async Task<int> ProbeMany(IEnumerable<string> files)
            {
                var batch = new List<string>(probeWidth);
                var done = 0;

                async Task Flush()
                {
                    if (batch.Count == 0) return;
                    var work = batch.ToArray();
                    batch.Clear();
                    await Task.WhenAll(work.Select(f => Task.Run(() => _tvCodecs.Codecs(f)))).ConfigureAwait(false);
                    done += work.Length;
                    probed += work.Length;
                    // No Save here. It serialises the entire cache and rewrites
                    // the file - on a settled install that is hundreds of KB,
                    // and once per batch of sixteen meant hundreds of full
                    // rewrites for one sweep. Codecs() already flushes every
                    // 200 files, and the caller saves when the folder is done.
                }

                foreach (var file in files)
                {
                    if (!TranscodableExt.Contains(Path.GetExtension(file))) continue;
                    if (_tvCodecs.NeedsConversionCached(file) is not null) continue;
                    batch.Add(file);
                    if (batch.Count >= probeWidth) await Flush().ConfigureAwait(false);
                }
                await Flush().ConfigureAwait(false);
                return done;
            }

            // Folders the panel has been asked to list, cleared before anything
            // else and *during* the sweep as well. Draining only between sweeps
            // would put a folder somebody just opened behind however many hours
            // the library takes - the same wait this is meant to remove.
            async Task DrainRequested()
            {
                while (_probeWanted.TryDequeue(out var wanted))
                {
                    // Deliberately still marked as queued while the walk runs.
                    // The panel asks again every few seconds while any pill is
                    // unresolved, and removing the mark here let each of those
                    // re-queue the same folder - so a large tree was walked from
                    // the top over and over and never got to the end of itself.
                    if (Directory.Exists(wanted))
                    {
                        try
                        {
                            await ProbeMany(Directory.EnumerateFiles(wanted, "*", opts)).ConfigureAwait(false);
                        }
                        catch { /* a folder that vanished or refused: on to the next */ }
                        _tvCodecs.Save();
                    }
                    // Finished: let it be asked for again, but not immediately.
                    _probeDone[wanted] = DateTime.UtcNow;
                    _probeQueued.TryRemove(wanted, out _);
                }
            }

            while (true)
            {
                try
                {
                    await DrainRequested().ConfigureAwait(false);

                    foreach (var root in _library.All)
                    {
                        if (!Directory.Exists(root)) continue;
                        // In chunks, so an open folder can cut in: the sweep is
                        // background work and the pills on screen are not.
                        var chunk = new List<string>(64);
                        foreach (var file in Directory.EnumerateFiles(root, "*", opts))
                        {
                            chunk.Add(file);
                            if (chunk.Count < 64) continue;
                            await ProbeMany(chunk).ConfigureAwait(false);
                            chunk.Clear();
                            if (!_probeWanted.IsEmpty) await DrainRequested().ConfigureAwait(false);
                        }
                        if (chunk.Count > 0) await ProbeMany(chunk).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("probe", $"codec prefetch stopped early: {ex.Message}");
                }

                if (probed > 0)
                {
                    _tvCodecs.Save();
                    Log.Info("probe", $"codec prefetch: read {probed} file(s) the transcode list was unsure about");
                    probed = 0;
                }

                // Round again later for whatever has been added since. Nothing
                // to do is cheap: the second pass is dictionary lookups.
                //
                // Woken early by a folder being browsed: waiting out the full
                // half hour would leave the pills someone is looking at right
                // now saying "checking..." until it expired.
                for (var waited = 0; waited < ProbeRoundSeconds && _probeWanted.IsEmpty; waited += ProbeIdleTickSeconds)
                    await Task.Delay(TimeSpan.FromSeconds(ProbeIdleTickSeconds)).ConfigureAwait(false);
            }
        });
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

        if (!TryReadJsonBody<TranscodeRequest>(ctx, out var req, out var bodyError))
        { BadRequest(res, bodyError); return; }
        if (req?.Paths is null || req.Paths.Count == 0) { BadRequest(res, "no paths given"); return; }

        var files = new List<string>();
        foreach (var p in req.Paths)
        {
            if (string.IsNullOrWhiteSpace(p) || !TryLocalPath(p, out var full)) continue;
            try
            {
                // The same rules the pill counted with — see MediaWalk. These
                // two disagreeing is how the number shown and the work done
                // came apart.
                if (Directory.Exists(full))
                    files.AddRange(Directory.EnumerateFiles(full, "*", MediaWalk)
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
        //
        // Deciding that costs a probe per file that isn't cached yet, and
        // deciding it for *every* file before queueing *any* is why picking a
        // folder of 700 sat for a minute with nothing converting: the work was
        // all in front of the first job rather than beside it. Settle a first
        // handful, queue them so a conversion starts at once, and let the rest
        // arrive behind it.
        const int firstBatch = 12;
        var head = unique.Take(firstBatch).ToList();
        var tail = unique.Skip(firstBatch).ToList();

        var needsConv = head.Where(f => _tvCodecs?.NeedsConversion(f) ?? true).ToList();
        var queued = _ffmpeg.QueueVod(needsConv);
        HideConversions(needsConv);

        // The rest, off the request thread. Queued in small groups so the
        // panel fills in steadily instead of in one lump at the end, and so a
        // long walk never holds the conversion queue empty.
        if (tail.Count > 0)
        {
            var ffmpeg = _ffmpeg;
            _ = Task.Run(() =>
            {
                var group = new List<string>();
                var added = 0;
                try
                {
                    foreach (var f in tail)
                    {
                        if (_tvCodecs?.NeedsConversion(f) ?? true) group.Add(f);
                        if (group.Count < 10) continue;
                        added += ffmpeg.QueueVod(group);
                        HideConversions(group);
                        group.Clear();
                    }
                    if (group.Count > 0)
                    {
                        added += ffmpeg.QueueVod(group);
                        HideConversions(group);
                    }
                }
                catch (Exception ex) { Log.Warn("control", $"transcode: queueing the rest failed: {ex.Message}"); }
                if (added > 0)
                    Log.Info("control", $"transcode: {added} more file(s) queued after the first {queued}");
            });
        }

        var alreadyGood = head.Count - needsConv.Count;
        Log.Info("control", $"transcode: {queued} file(s) queued from {req.Paths.Count} selection(s) "
            + $"({unique.Count} video file(s) found; the remaining {tail.Count} are being added)");
        WriteJson(res, 200, new
        {
            queued,
            found = unique.Count,
            needs = needsConv.Count,
            alreadyGood,
            // still arriving, so the panel can say so rather than looking done
            pending = tail.Count,
        });
    }

    /// <summary>
    /// Keeps a batch conversion out of the HLS list. It writes its output into
    /// the media root like any other, and that list shows every directory
    /// there, so converting a library would otherwise fill it with rows nobody
    /// added. The conversion itself is kept and does all its work.
    ///
    /// Nothing is lost: preparing or playing the media calls StreamLinks.Show,
    /// so adding it from the media library - the action that means "publish
    /// this" - brings the row back with the conversion already finished.
    /// </summary>
    private void HideConversions(IEnumerable<string> files)
    {
        if (_ffmpeg is null) return;
        var names = new List<string>();
        foreach (var f in files)
            if (_ffmpeg.VodStreamName(f) is string name) names.Add(name);
        if (names.Count > 0) _links.HideMany(names);
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
        if (!TryReadJsonBody<TranscodeRequest>(ctx, out var req, out var bodyError))
        { BadRequest(res, bodyError); return; }
        if (req?.Paths is null || req.Paths.Count == 0) { BadRequest(res, "no paths given"); return; }

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

        if (!TryReadJsonBody<TranscodeRemoveRequest>(ctx, out var req, out var bodyError))
        { BadRequest(res, bodyError); return; }

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
        if (string.IsNullOrWhiteSpace(req?.Path)) { BadRequest(res, "no path or stream given"); return; }
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
            if (!TryReadJsonBody<TranscodeConfigRequest>(ctx, out var req, out var bodyError))
            { BadRequest(res, bodyError); return; }
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
    ///
    /// The markup itself is wwwroot/player.html, embedded in the assembly the
    /// same way the dashboard is; all this does is fill in its placeholders.
    ///
    /// Internal rather than private only so the test project can render it and
    /// check that no placeholder was left behind; nothing outside calls it.
    /// </summary>
    internal static string PlayerPage(string src, string title)
    {
        var srcJs = JsonSerializer.Serialize(src);
        var shown = System.Net.WebUtility.HtmlEncode(
            string.IsNullOrWhiteSpace(title) ? "j0kers Media Server" : title);
        // __TITLE__ lands in HTML text and __SRC_JS__ inside the <script>,
        // which is why one is HTML-encoded and the other JSON-encoded. Entities
        // are not decoded inside a script element, so an HTML-encoded src would
        // arrive there as the literal "&amp;" and ask for the wrong playlist.
        return Services.PageTemplate.Fill(PlayerTemplate.Value,
            ("__TITLE__", shown), ("__SRC_JS__", srcJs));
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
            var root = MediaRootPath();
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

    /// <summary>
    /// The read-parse-or-say-why step the POST handlers in this file all
    /// repeated by hand: read the body, deserialize it, and on any failure
    /// hand back the sentence the caller reports as a 400. Anything the read
    /// or the parser throws is folded into that one message, because to the
    /// client a body too large to accept and a body that is not JSON are the
    /// same refusal.
    ///
    /// This is deliberately separate from the TryReadJson the auth endpoints
    /// use even though the two look alike. That one turns an empty or null
    /// body into its own "empty body" error, while the handlers here let an
    /// empty body come back out of the parser as "bad JSON" and test for a
    /// null result themselves afterwards. Both wordings are already what
    /// their clients see, so neither can be made to follow the other.
    /// </summary>
    private static bool TryReadJsonBody<T>(HttpListenerContext ctx, out T? value, out string error)
        where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(ReadBody(ctx), BodyJson);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            value = null;
            error = "bad JSON: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The two refusals this API sends more than a hundred times between
    /// them, each one a status and a body holding nothing but the sentence.
    /// They exist to name the shape, not to change it: what goes on the wire
    /// is byte for byte what WriteJson produced for the anonymous object
    /// every one of those call sites used to build inline. A response that
    /// carries anything else, a second field or a different status, keeps
    /// its explicit WriteJson.
    /// </summary>
    private void BadRequest(HttpListenerResponse res, string message)
        => WriteJson(res, 400, new { error = message });

    private void NotFound(HttpListenerResponse res, string message)
        => WriteJson(res, 404, new { error = message });

    private void WriteJson(HttpListenerResponse res, int status, object body)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true });
        var bytes = Encoding.UTF8.GetBytes(json);
        res.StatusCode = status;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
    }

    private void WriteText(HttpListenerResponse res, int status, string contentType, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
    }

    /// <summary>
    /// The live-channel M3U. Each channel points at its own HLS output on the
    /// media port, carrying the caller's media token so an app plays it with no
    /// separate sign-in. Host comes from the request so the links resolve to
    /// whatever address the app reached the server on.
    /// </summary>
    private void WriteTvPlaylistM3u(HttpListenerContext ctx)
    {
        var token = $"exp={ctx.Request.QueryString["exp"]}&sig={ctx.Request.QueryString["sig"]}";
        var host = (ctx.Request.Headers["Host"] ?? ctx.Request.Url?.Host ?? BoundHost).Split(':')[0];
        var baseUrl = $"{Services.UrlScheme.Prefix}{host}:{_serverConfig.Hls.Port}";

        var sb = new System.Text.StringBuilder();
        sb.Append("#EXTM3U\n");
        if (_ffmpeg is not null)
            foreach (var (def, stream, _) in _ffmpeg.Channels)
            {
                var name = def.Name.Replace('"', '\'');
                var url = $"{baseUrl}/{Uri.EscapeDataString(stream)}/index.m3u8?{token}";
                sb.Append($"#EXTINF:-1 tvg-name=\"{name}\" group-title=\"Live TV\",{name}\n");
                sb.Append(url).Append('\n');
            }
        WriteText(ctx.Response, 200, "application/x-mpegurl", sb.ToString());
    }

    public void Dispose()
    {
        _cts.Cancel();
        // Timers outlive the object otherwise, and one of them calls back into
        // services that are being torn down: an exception on a timer thread
        // takes the process with it rather than being caught anywhere.
        try { _idleShutdownTimer?.Dispose(); _idleShutdownTimer = null; } catch { }
        try { _linkSweepTimer?.Dispose(); _linkSweepTimer = null; } catch { }
        try { Services.KeepAwake.Busy(false); } catch { }   // let the machine sleep again
        lock (_shutdownLock)
        {
            try { _closeShutdownTimer?.Dispose(); _closeShutdownTimer = null; } catch { }
            try { _closedNoticeTimer?.Dispose(); _closedNoticeTimer = null; } catch { }
        }
        try { _listener?.Stop(); } catch { }
        try { StopDlnaListener(); } catch { }
        try { _providers.Dispose(); } catch { }
        try { _providerHttp.Dispose(); } catch { }
        try { _proxyHttp.Dispose(); } catch { }
    }
}

