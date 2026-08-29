using System.Globalization;
using System.Net;
using System.Text;
using J0kersMediaServer.Config;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Hls;

/// <summary>
/// HTTP Live Streaming endpoint (RFC 8216). Serves Media Playlists generated
/// on the fly from segment files on disk, plus the segments themselves.
///
/// Layout: each subdirectory of <c>hls.mediaRoot</c> is one stream.
/// Segments are the *.ts / *.mp4 / *.m4s / *.aac files inside it, ordered by
/// name. GET /&lt;stream&gt;/index.m3u8 returns the playlist;
/// GET /&lt;stream&gt;/&lt;segment&gt; returns a segment. GET / lists streams.
///
/// With <c>liveWindowSegments</c> = 0 the playlist is a VOD presentation
/// (EXT-X-PLAYLIST-TYPE:VOD + EXT-X-ENDLIST, §6.2.1); otherwise it is a
/// sliding-window live playlist over the newest N segments (§6.2.2).
/// </summary>
public sealed class HlsServer : IDisposable
{
    private static readonly string[] SegmentExtensions = { ".ts", ".mp4", ".m4s", ".aac" };

    private readonly HlsConfig _config;
    private readonly string _mediaRoot;
    private HttpListener? _listener;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Signed-URL verifier; null leaves this port open as it was before accounts existed.</summary>
    public Auth.MediaLink? Links { get; set; }

    /// <summary>Lets a signed-in browser reach media directly; cookies are not port-scoped.</summary>
    public Auth.AuthService? Sessions { get; set; }

    /// <summary>Who is watching right now, inferred from request traffic (see HlsViewers).</summary>
    public HlsViewers? Viewers { get; set; }

    /// <summary>Server-wide byte counter behind the dashboard's throughput figure.</summary>
    public Services.Throughput? Served { get; set; }

    public HlsServer(HlsConfig config, string baseDirectory)
    {
        _config = config;
        _mediaRoot = Path.GetFullPath(Path.IsPathRooted(config.MediaRoot)
            ? config.MediaRoot
            : Path.Combine(baseDirectory, config.MediaRoot));
    }

    public void Start()
    {
        Directory.CreateDirectory(_mediaRoot);
        (var listener, var bound) = HttpListenerBinder.Start(_config.BindAddress, _config.Port, "hls");
        _listener = listener;
        Log.Info("hls", $"listening on {Services.UrlScheme.Prefix}{bound}:{_config.Port}/ (media root: {_mediaRoot})");
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync(); }
            catch (Exception) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Warn("hls", $"accept failed: {ex.Message}"); continue; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    /// <summary>
    /// Media is authorized by a signed URL, or by a control-port session for
    /// someone browsing here directly — cookies are scoped by host, not by
    /// port, so a signed-in browser is already carrying one. With no signer
    /// wired up (a server with no accounts) this port stays open exactly as
    /// it was before.
    /// </summary>
    private bool Authorized(HttpListenerContext ctx, string scope, Auth.AuthResult? identity)
    {
        // follows account existence live: claiming the server mid-session
        // starts enforcing here too, without a restart
        if (Links is null || Sessions is null || !Sessions.Enforcing) return true;

        var q = ctx.Request.QueryString;
        if (Links.Verify(scope, q["exp"], q["sig"])) return true;

        return identity is not null && identity.Level != Auth.AccessLevel.None;
    }

    /// <summary>The caller's token as a query suffix ("?exp=…&amp;sig=…"), or "" when they used a cookie.</summary>
    private static string TokenQuery(HttpListenerContext ctx)
    {
        var q = ctx.Request.QueryString;
        var exp = q["exp"];
        var sig = q["sig"];
        return exp is null || sig is null
            ? ""
            : $"?exp={Uri.EscapeDataString(exp)}&sig={Uri.EscapeDataString(sig)}";
    }

    /// <summary>
    /// Appends the caller's token to every URI line of a playlist. A player
    /// resolves segment URIs against the playlist's own URL but does *not*
    /// inherit its query string, so without this the playlist would load and
    /// then every segment would 401.
    /// </summary>
    private static string AppendTokenToUris(string playlist, string token)
    {
        if (token.Length == 0) return playlist;
        var sb = new StringBuilder(playlist.Length + 64);
        foreach (var line in playlist.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            // comments and tags pass through; everything else is a URI.
            // (EXT-X-MAP / EXT-X-MEDIA carry URIs in attributes — handled below.)
            if (trimmed.Length == 0 || trimmed[0] == '#')
                sb.Append(AppendTokenToUriAttribute(trimmed, token)).Append('\n');
            else
                sb.Append(trimmed).Append(trimmed.Contains('?') ? '&' : '?')
                  .Append(token.AsSpan(1)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Same treatment for URI="…" attributes (fMP4 init segments, subtitle renditions).</summary>
    private static string AppendTokenToUriAttribute(string line, string token)
    {
        const string marker = "URI=\"";
        var at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return line;
        var start = at + marker.Length;
        var end = line.IndexOf('"', start);
        if (end < 0) return line;
        var uri = line[start..end];
        if (uri.Length == 0) return line;
        var joined = uri + (uri.Contains('?') ? "&" : "?") + token[1..];
        return line[..start] + joined + line[end..];
    }

    /// <summary>
    /// Reflects the requesting origin when it is this same machine, so the
    /// dashboard on the control port can read playlists cross-port, without
    /// handing every website on the internet the same access. A configured
    /// value other than the default still wins.
    /// </summary>
    private void ApplyCors(HttpListenerContext ctx)
    {
        var configured = _config.CorsAllowOrigin;
        if (configured.Length > 0 && configured != "*")
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = configured;
            return;
        }

        var origin = ctx.Request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin))
        {
            // no Origin at all: a player, not a page — nothing to gate
            if (configured == "*") ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            return;
        }
        if (Uri.TryCreate(origin, UriKind.Absolute, out var o)
            && string.Equals(o.Host, ctx.Request.Url?.Host, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
            ctx.Response.Headers["Vary"] = "Origin";
            return;
        }
        if (configured == "*") ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
    }

    private void Handle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        // Named out here so the access-log line in the finally can say who
        // this was, whatever the request went on to do or throw.
        string? who = null;
        try
        {
            if (HttpListenerBinder.IsLoopbackBind(_config.BindAddress) &&
                !HttpListenerBinder.IsLoopbackRequest(ctx))
            {
                WriteText(res, 403, "text/plain", "bound to localhost only");
                return;
            }

            OnActivity?.Invoke(); // watching keeps the server alive
            ApplyCors(ctx);
            res.Headers["Cache-Control"] = "no-cache";

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (Log.Enabled(LogLevel.Debug))
                Log.Debug("hls", $"{ctx.Request.RemoteEndPoint} GET {path}");

            var parts = path.Trim('/').Split('/');

            // the watch page needs hls.js on this origin; a third-party
            // library file with nothing in it to protect stays open, or the
            // player page can't bootstrap
            if (parts.Length == 1 && parts[0] == "hls.min.js")
            {
                res.StatusCode = 200;
                res.ContentType = "text/javascript";
                res.Headers["Cache-Control"] = "max-age=86400";
                res.ContentLength64 = HlsJsAsset.Value.Length;
                res.OutputStream.Write(HlsJsAsset.Value);
                return;
            }

            // The stream this request is for decides which token authorizes
            // it; the index is only reachable with an all-streams token.
            var scope = parts.Length >= 2 && parts[0] == "watch" ? parts[1]
                : parts.Length >= 1 && parts[0].Length > 0 ? parts[0]
                : Auth.MediaLink.AllStreams;

            // Resolved once and reused: authorizing and then naming the
            // viewer used to authenticate the same request twice, taking the
            // account-store lock each time, on every segment of every stream.
            var identity = Sessions?.Authenticate(ctx);
            if (!Authorized(ctx, scope, identity))
            {
                Log.Warn("hls", $"unauthorized {ctx.Request.RemoteEndPoint} GET {path}");
                WriteText(res, 401, "text/plain",
                    "unauthorized — open this stream from the dashboard, or use a share link");
                return;
            }

            // the token the caller arrived with, carried through to every URL
            // this response hands back (segments, subtitles, the poster) so
            // playback doesn't stall on the second request
            var token = TokenQuery(ctx);

            // Name the account behind this request where there is one. A
            // signed link deliberately carries no identity, so those show up
            // as a share link rather than as somebody.
            var watcher = identity?.User?.Username;
            who = watcher;

            if (path == "/")
            {
                WriteText(res, 200, "application/json", ListStreamsJson());
                return;
            }

            // /watch/<stream>: a self-contained player page that works in any
            // browser (Android Chrome can't play a bare .m3u8 link natively)
            if (parts.Length == 2 && parts[0] == "watch")
            {
                var wd = SafeStreamDirectory(parts[1]);
                if (wd is null || !Directory.Exists(wd))
                {
                    WriteText(res, 404, "text/plain", "unknown stream");
                    return;
                }
                WriteText(res, 200, "text/html; charset=utf-8", WatchPage(parts[1], token));
                return;
            }

            // /<stream>/subs/<id>.vtt — a WebVTT track for the player
            if (parts.Length == 3 && parts[1] == "subs")
            {
                var sd = SafeStreamDirectory(parts[0]);
                if (sd is null || !Directory.Exists(sd) || parts[2].Contains(".."))
                {
                    WriteText(res, 404, "text/plain", "unknown stream");
                    return;
                }
                var vtt = ResolveSubtitle(sd, Path.GetFileNameWithoutExtension(parts[2]));
                if (vtt is null)
                {
                    WriteText(res, 404, "text/plain", "subtitle unavailable");
                    return;
                }
                WriteText(res, 200, "text/vtt; charset=utf-8", ReadSharedText(vtt));
                return;
            }

            if (parts.Length != 2)
            {
                WriteText(res, 404, "text/plain", "not found");
                return;
            }

            var streamDir = SafeStreamDirectory(parts[0]);
            // Tune-on-demand: a request for a channel's playlist starts its
            // restream if it is not already running, so picking a channel in
            // any player brings it up like live TV — the reason a channel that
            // was merely "off" looked broken. Wait briefly for the first
            // playlist so the player gets a real answer, not a 404 it gives up
            // on. Transient: it does not persist the channel as auto-start.
            if (streamDir is not null && parts.Length == 2
                && (parts[1] is "index.m3u8" or "master.m3u8" or "playlist.m3u8")
                && Ffmpeg?.EnsureChannelRunning(parts[0]) == true)
            {
                var firstPlaylist = Path.Combine(streamDir, "index.m3u8");
                for (var i = 0; i < 48 && !File.Exists(firstPlaylist); i++)
                    System.Threading.Thread.Sleep(250);            // up to ~12s for the first playlist
            }
            if (streamDir is null || !Directory.Exists(streamDir))
            {
                WriteText(res, 404, "text/plain", "unknown stream");
                return;
            }

            // touch the directory so the VOD cache eviction treats a stream
            // being watched right now as recently-used, not stale
            try { Directory.SetLastWriteTimeUtc(streamDir, DateTime.UtcNow); } catch { }

            // /<stream>/master.m3u8 — the media playlist and its subtitle
            // rendition, tied together
            if (parts[1] == "master.m3u8")
            {
                var master = MasterPlaylist(streamDir, token);
                if (master is null)
                {
                    WriteText(res, 404, "text/plain", "no subtitle rendition for this stream");
                    return;
                }
                ApplyCors(ctx);
                WriteText(res, 200, "application/vnd.apple.mpegurl", master);
                return;
            }

            // /<stream>/subs.json — the track list for this stream
            if (parts[1] == "subs.json")
            {
                WriteText(res, 200, "application/json", SubtitleListJson(streamDir, token));
                return;
            }

            // /<stream>/thumb.jpg — poster frame for the dashboard list
            if (parts[1] == "thumb.jpg")
            {
                var poster = Ffmpeg?.GetStreamThumbnail(streamDir);
                if (poster is null)
                {
                    WriteText(res, 404, "text/plain", "no thumbnail");
                    return;
                }
                res.StatusCode = 200;
                res.ContentType = "image/jpeg";
                res.Headers["Cache-Control"] = "max-age=3600";
                using var pfs = new FileStream(poster, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                res.ContentLength64 = pfs.Length;
                pfs.CopyTo(res.OutputStream);
                return;
            }

            if (parts[1].EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                // A playlist fetch is not yet watching: the dashboard reads
                // playlists to list streams, and a player reads one to decide
                // whether it can play at all. It refreshes a viewing that has
                // already started; the first segment is what starts one.
                Viewers?.Note(ctx, parts[0], watcher, 0, create: false);
                // A playlist written by a segmenter (ffmpeg VOD/live jobs)
                // wins — it has exact durations and live-window state. The
                // generated playlist covers plain directories of segments.
                // A conversion still running: serve the whole film's worth of
                // segments rather than the encoder's own playlist, which lists
                // only what it has written. Every segment is a fixed length
                // beginning at a forced keyframe, so segment i is [6i, 6i+6)
                // whether or not it exists yet — and one that does not exist
                // is made when it is asked for. That is what lets the seek bar
                // cover the film from the start and a skip land past the
                // encoder instead of stopping the stream.
                if (parts[1] is "index.m3u8" or "playlist.m3u8")
                {
                    var whole = WholeVodPlaylist(streamDir, parts[0]);
                    if (whole is not null)
                    {
                        WriteText(res, 200, "application/vnd.apple.mpegurl",
                            AppendTokenToUris(whole, token));
                        return;
                    }
                }

                var onDisk = Path.Combine(streamDir, parts[1]);
                if (File.Exists(onDisk) && !parts[1].Contains(".."))
                {
                    var text = ReadSharedText(onDisk);

                    // A running VOD job writes an EXT-X-PLAYLIST-TYPE:EVENT
                    // playlist (no ENDLIST) so players keep reloading and
                    // extend the seek bar as it converts — that must be
                    // served as-is, or the movie gets truncated to whatever
                    // had transcoded.
                    //
                    // But if no job is running and there's still no ENDLIST,
                    // the conversion ended without finishing (interrupted,
                    // crashed, server restarted). The file will never grow
                    // again, yet players still treat it as live: playback
                    // joins at the last segment instead of the beginning and
                    // there's no seek bar. Close it off so it behaves like
                    // the finite recording it now is.
                    var transcoding = Ffmpeg?.ActiveVodStreams
                        .Contains(parts[0], StringComparer.OrdinalIgnoreCase) ?? false;
                    if (!transcoding && parts[0].StartsWith("vod-", StringComparison.OrdinalIgnoreCase))
                    {
                        // The written playlist can go stale while the job that
                        // was still growing it keeps producing segment files —
                        // seen for real, on two separate finished conversions:
                        // ffmpeg exited cleanly and left 1121 and 1136 real,
                        // contiguous segments on disk, but its own index.m3u8
                        // had stopped being extended at 15 and 11 entries.
                        // Nothing is ever going to fix that file again — the
                        // job is gone — so a player reading it sees a "film"
                        // a minute and a half long, plays to the real end of
                        // that, and pressing play again after the end is what
                        // restarts a video from zero. Every other, older
                        // conversion checked had a playlist that matched its
                        // segments exactly; this is not the common case, but
                        // it is a silent, permanent one once it happens, so
                        // it is worth the one extra directory listing to rule
                        // out on every request to a finished stream.
                        //
                        // BuildPlaylist scans the directory itself, so it is
                        // right by construction — it can behave only as its
                        // own comment says, listing what is actually there. A
                        // real, on-disk mismatch is what decides whether it's
                        // used, not this comment's say-so.
                        var real = CountRealSegments(streamDir);
                        var listed = CountExtinf(text);
                        if (real >= 0 && real != listed)
                        {
                            Log.Warn("hls", $"{parts[0]}: playlist listed {listed} segments, "
                                + $"{real} exist — rebuilding from disk");
                            text = BuildPlaylist(streamDir);
                        }
                        else if (!text.Contains("#EXT-X-ENDLIST"))
                        {
                            text = text.Replace("#EXT-X-PLAYLIST-TYPE:EVENT", "#EXT-X-PLAYLIST-TYPE:VOD");
                            if (!text.EndsWith('\n')) text += "\n";
                            text += "#EXT-X-ENDLIST\n";
                        }
                    }

                    WriteText(res, 200, "application/vnd.apple.mpegurl", AppendTokenToUris(text, token));
                    return;
                }
                if (parts[1] is "index.m3u8" or "playlist.m3u8")
                {
                    WriteText(res, 200, "application/vnd.apple.mpegurl",
                        AppendTokenToUris(BuildPlaylist(streamDir), token));
                    return;
                }
                WriteText(res, 404, "text/plain", "not found");
                return;
            }

            var segment = Path.GetFullPath(Path.Combine(streamDir, parts[1]));
            if (!segment.StartsWith(streamDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !SegmentExtensions.Contains(Path.GetExtension(segment).ToLowerInvariant()))
            {
                WriteText(res, 404, "text/plain", "not found");
                return;
            }

            // Asked for a part of the film nobody has converted yet — which is
            // what skipping forward past the encoder looks like from here.
            // Rather than the 404 that used to stop playback, put an encoder
            // on it at that exact point and hand the segment over when it
            // lands. This is the whole of skipping ahead: the player never
            // learns the segment was not already there.
            //
            // What actually gets served is resolved here rather than assumed
            // to be `segment` itself: a seek job never writes the canonical
            // name — see SeekSegmentPath — so the file worth opening below
            // may be a differently-named stand-in. The canonical name wins
            // once it exists, since that is the in-order job's own
            // continuous encode and the authoritative one.
            var toServe = segment;
            if (!File.Exists(toServe))
            {
                toServe = FindServableSegment(parts[0], streamDir, segment);
                if (toServe is null)
                {
                    WriteText(res, 404, "text/plain", "not found");
                    return;
                }
            }
            segment = toServe;

            res.StatusCode = 200;
            res.ContentType = Path.GetExtension(segment).ToLowerInvariant() switch
            {
                ".ts" => "video/mp2t",
                ".mp4" or ".m4s" => "video/mp4",
                ".aac" => "audio/aac",
                _ => "application/octet-stream",
            };
            // write-sharing: ffmpeg may still be appending/rewriting files
            // in this directory while clients stream it
            using var fs = new FileStream(segment, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            res.ContentLength64 = fs.Length;
            // the segment is the actual media — this is what makes the
            // dashboard's byte counter mean anything
            Viewers?.Note(ctx, parts[0], watcher, fs.Length);
            Served?.Add(fs.Length);
            fs.CopyTo(res.OutputStream);
        }
        catch (Exception ex)
        {
            Log.Warn("hls", $"request failed: {ex.Message}");
            try { res.StatusCode = 500; } catch { }
        }
        finally
        {
            // Before Close: the status and the length are what the line
            // reports, and a closed response no longer answers for them.
            Logging.AccessLog.Served("hls", ctx, who);
            try { res.Close(); } catch { }
        }
    }

    private string? SafeStreamDirectory(string name)
    {
        if (name.Contains("..") || name.Contains('\\') || name.Contains('/')) return null;
        if (name.StartsWith('.')) return null; // internal dirs (.thumbs) are never streams
        var full = Path.GetFullPath(Path.Combine(_mediaRoot, name));
        // The separator matters: a bare prefix test also accepts a sibling
        // whose name merely starts with the root's — "…/media-old" passes a
        // check meant for "…/media". The filters above already make that
        // unreachable, so this is the guard being right on its own terms
        // rather than by the grace of another one, and it matches how the
        // segment path is checked further up.
        // TrimEnd first: GetFullPath leaves a trailing separator on a drive
        // root ("C:\"), and appending another would match nothing.
        var prefix = _mediaRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>
    /// The playlist for a conversion still in progress: every segment the
    /// finished film will have, listed now.
    /// </summary>
    /// <remarks>
    /// The encoder's own playlist grows behind it, which is why the seek bar
    /// used to stop where the conversion had reached and a skip past it had
    /// nowhere to land. Segment length is fixed and every segment starts at a
    /// forced keyframe, so segment i covers [6i, 6i+6) by arithmetic — the
    /// list can be written before the segments exist, and a request for one
    /// that doesn't makes it.
    ///
    /// Null when this isn't such a stream: an unconverted directory, a live
    /// channel, a conversion that has finished, or one whose length was never
    /// recorded. Those keep the playlist they had.
    /// </remarks>
    private string? WholeVodPlaylist(string streamDir, string stream)
    {
        if (!stream.StartsWith("vod-", StringComparison.OrdinalIgnoreCase)) return null;
        var transcoding = Ffmpeg?.ActiveVodStreams
            .Contains(stream, StringComparer.OrdinalIgnoreCase) ?? false;
        if (!transcoding) return null;

        double duration;
        try
        {
            var text = File.ReadAllText(Path.Combine(streamDir, "duration.txt")).Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out duration))
                return null;
        }
        catch { return null; }
        if (duration <= 0) return null;

        var seconds = Media.FfmpegManager.SegmentSeconds;
        var count = (int)Math.Ceiling(duration / seconds);
        if (count <= 0) return null;
        var fmp4 = File.Exists(Path.Combine(streamDir, "init.mp4"));
        var ext = fmp4 ? "m4s" : "ts";

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:3\n");
        sb.Append($"#EXT-X-TARGETDURATION:{seconds}\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        if (fmp4) sb.Append("#EXT-X-MAP:URI=\"init.mp4\"\n");
        for (var i = 0; i < count; i++)
        {
            // the last one is whatever is left over, not a full segment
            var len = i == count - 1 ? Math.Max(0.1, duration - (double)i * seconds) : seconds;
            sb.Append("#EXTINF:").Append(len.ToString("0.000", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append($"seg_{i:D5}.{ext}\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>
    /// Finds (starting production if nothing is under way) the file that
    /// answers a request for a segment nobody has converted yet — the
    /// canonical name once the in-order job reaches it, or a seek job's own
    /// tagged stand-in before that. Null when neither turns up in time.
    /// </summary>
    /// <remarks>
    /// A seek job never writes the canonical name — see
    /// Media.FfmpegManager.SeekSegmentPath — so what gets served is
    /// resolved by what actually exists on disk, not assumed from the URL.
    /// That split is itself a fix: this and the in-order job used to write
    /// the identical filename, and on a machine that encodes a film in a
    /// few minutes the in-order job reaches a seek's target well within a
    /// single visit, so whichever finished writing last silently overwrote
    /// the other mid-flight. The corrupt fragment that produced is what a
    /// player answers by reloading the entire film from the start — which
    /// is what skipping forward kept doing, reliably, after however long it
    /// took the in-order job to catch up. Separate names make that
    /// collision impossible rather than merely unlikely.
    ///
    /// The wait itself is bounded: a player left waiting indefinitely gives
    /// up on its own terms and shows a failure, which is worse than a gap
    /// it can retry. A segment counts as ready when the job producing it
    /// has moved past it — the one after it exists, under the same tag —
    /// or when it has stopped growing, which is how the last segment of a
    /// run finishes.
    /// </remarks>
    private string? FindServableSegment(string stream, string streamDir, string canonicalSegment)
    {
        var name = Path.GetFileNameWithoutExtension(canonicalSegment);
        if (!name.StartsWith("seg_", StringComparison.OrdinalIgnoreCase)) return null;
        if (!int.TryParse(name.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return null;
        if (Ffmpeg is null || !Ffmpeg.EnsureVodSegment(stream, index)) return null;

        // Measured, in wwwroot/hls.min.js's bundled default policy, not
        // guessed: a fragment request gets maxTimeToFirstByteMs = 10000
        // before hls.js calls it a timeout — and this used to hold the
        // connection open, sending nothing at all, for up to 30 seconds.
        // Every seek into unconverted film broke the same way regardless of
        // how fast the encode actually was, because silence past ten
        // seconds is a timeout on its own terms, not a matter of finishing
        // in time.
        //
        // A timeout and an honest miss are not answered the same way by
        // the player, and that difference is the fix. A timeout gets
        // hls.js's timeoutRetry — four attempts with no delay between them,
        // which just repeats the same ten-second wait for however long the
        // segment takes to finish, each one still silent, still capable of
        // being the one that runs out. A fast 404 gets errorRetry instead —
        // six attempts with backoff up to eight seconds apart — and that is
        // what actually survives an encode slower than one window: it
        // hangs up and calls back rather than sitting on hold. Answering
        // within the window, ready or not, is what earns that better
        // handling. The encoder job itself is untouched by any of this and
        // keeps running underneath every attempt.
        var ext = Path.GetExtension(canonicalSegment).TrimStart('.');
        var deadline = DateTime.UtcNow.AddSeconds(7);
        long lastSize = -1;
        var stableFor = 0;
        string? lastSeen = null;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(canonicalSegment)) return canonicalSegment;   // in-order job got there

            string? found;
            try { found = Directory.EnumerateFiles(streamDir, $"seg_{index:D5}.seek*.{ext}").FirstOrDefault(); }
            catch { found = null; }

            if (found is not null)
            {
                if (found != lastSeen) { lastSeen = found; stableFor = 0; lastSize = -1; }
                // "the job producing this has moved past it" — parse the
                // same tag back out and check for that job's next segment,
                // the seek-job equivalent of the in-order case above.
                var tag = Path.GetFileNameWithoutExtension(found).Split('.', 3).ElementAtOrDefault(1);
                if (tag is not null && tag.StartsWith("seek", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(streamDir, $"seg_{index + 1:D5}.{tag}.{ext}")))
                    return found;

                long size;
                try { size = new FileInfo(found).Length; } catch { size = -1; }
                if (size > 0 && size == lastSize)
                {
                    if (++stableFor >= 3) return found;       // finished, nothing following it
                }
                else stableFor = 0;
                lastSize = size;
            }
            Thread.Sleep(200);
        }
        // Not a failure — a job is still running and the next request
        // (hls.js's own retry, a second or so from now) will very likely
        // find it done. Only worth a log line if it keeps happening.
        return null;
    }

    /// <summary>Real segment files on disk — the ground truth a stale playlist is checked against.</summary>
    private static int CountRealSegments(string streamDir)
    {
        try
        {
            return Directory.EnumerateFiles(streamDir)
                .Where(f => SegmentExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Count(f => !Path.GetFileName(f).Equals("init.mp4", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileNameWithoutExtension(f).Contains(".seek", StringComparison.OrdinalIgnoreCase));
        }
        catch { return -1; }   // unreadable: never claim a mismatch over that
    }

    /// <summary>How many segments a playlist's own text claims to have.</summary>
    private static int CountExtinf(string playlistText)
    {
        var count = 0;
        var idx = 0;
        while ((idx = playlistText.IndexOf("#EXTINF", idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += 7;
        }
        return count;
    }

    private string BuildPlaylist(string streamDir)
    {
        var segments = Directory.GetFiles(streamDir)
            .Where(f => SegmentExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            // init.mp4 is an initialization segment, not media — listing it
            // (it also sorts first) produced an unplayable playlist
            .Where(f => !Path.GetFileName(f).Equals("init.mp4", StringComparison.OrdinalIgnoreCase))
            // A seek job's own stand-ins (seg_NNNNN.seekMMMMM.ext — see
            // Media.FfmpegManager.SeekSegmentPath) are not segments of their
            // own; they are a temporary cover for a canonical name until
            // the in-order job writes it, or leftovers once it has. Once a
            // conversion finishes this method is what serves it — a
            // directory-scan fallback, unlike the arithmetic whole-film
            // playlist used while still converting — and it would otherwise
            // list every leftover stand-in as an extra, bogus segment
            // alongside the real ones.
            .Where(f => !Path.GetFileNameWithoutExtension(f).Contains(".seek", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var live = _config.LiveWindowSegments > 0;
        var firstIndex = 0;
        if (live && segments.Count > _config.LiveWindowSegments)
        {
            firstIndex = segments.Count - _config.LiveWindowSegments;
            segments = segments.Skip(firstIndex).ToList();
        }

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:3\n");
        sb.Append($"#EXT-X-TARGETDURATION:{_config.TargetDurationSeconds}\n");
        sb.Append($"#EXT-X-MEDIA-SEQUENCE:{firstIndex}\n");
        if (!live)
            sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");

        foreach (var seg in segments)
        {
            // Segment duration is assumed = target duration; a segmenter that
            // knows exact durations can drop a sidecar "<name>.duration" file.
            var duration = (double)_config.TargetDurationSeconds;
            var sidecar = seg + ".duration";
            if (File.Exists(sidecar) &&
                double.TryParse(File.ReadAllText(sidecar).Trim(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                duration = d;

            // RFC 8216 §4.3.2.1: EXTINF is a decimal-point number — never locale-formatted
            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{duration:0.000},\n");
            sb.Append(Uri.EscapeDataString(Path.GetFileName(seg))).Append('\n');
        }

        if (!live)
            sb.Append("#EXT-X-ENDLIST\n");

        return sb.ToString();
    }

    private string ListStreamsJson()
    {
        // A live channel writes its segments into this same root, so listing
        // every directory listed the channels too — each one appearing on the
        // dashboard twice, on the card that owns it and again here, with two
        // delete buttons meaning different things. This list is the streams
        // the user prepared; Live channels owns the channels, start to finish.
        //
        // Asked of the channel list rather than matched against the "ch-"
        // prefix ChannelStream() produces: a stream a user named "ch-foo"
        // themselves is theirs, and should stay here.
        var channelDirs = new HashSet<string>(
            (Ffmpeg?.Channels ?? Array.Empty<(Media.FfmpegManager.ChannelDef, string, string)>())
                .Select(c => c.Item2),
            StringComparer.OrdinalIgnoreCase);

        // dot-directories are internal (.thumbs thumbnail cache), not streams
        var dirs = Directory.Exists(_mediaRoot)
            ? Directory.GetDirectories(_mediaRoot)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Where(d => !channelDirs.Contains(Path.GetFileName(d)))
                // unlinked: the conversion is kept, the row is not shown
                .Where(d => Listed?.IsHidden(Path.GetFileName(d)) != true)
            : Enumerable.Empty<string>();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            streams = dirs.Select(d => new
            {
                name = Path.GetFileName(d),
                // readable label for display only — the name above is still
                // the identity used in every URL
                title = Media.StreamTitle.Prettify(Path.GetFileName(d)),
                // the master when there are subtitles to point at, so every
                // link the dashboard builds carries them without each caller
                // having to know; the media playlist otherwise
                playlist = SubtitlePlaylist(d) is null
                    ? $"/{Path.GetFileName(d)}/index.m3u8"
                    : $"/{Path.GetFileName(d)}/master.m3u8",
                subtitles = SubtitlePlaylist(d) is not null,
                // the media this stream was made from, so the dashboard can
                // show its thumbnail (null for hand-made segment folders)
                source = Media.SubtitleManager.SourceFile(d),
            }),
        });
    }

    /// <summary>
    /// Reads a text file that ffmpeg may be rewriting concurrently: opens
    /// with full write-sharing and retries briefly on a sharing violation
    /// (ffmpeg replaces playlists via delete+rename on some platforms).
    /// </summary>
    private static string ReadSharedText(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Set by Program so streams can expose their subtitles.</summary>
    public Media.SubtitleManager? Subtitles { get; set; }

    /// <summary>Set by Program so streams can render their own poster frame.</summary>
    public Media.FfmpegManager? Ffmpeg { get; set; }

    /// <summary>
    /// Which converted streams have been unlinked. Their directories stay on
    /// disk — playing that media again re-links them rather than converting
    /// it a second time — but they are not listed until then.
    ///
    /// Named for what it holds rather than "Links", which on this class is
    /// already the signer for share URLs and means something else entirely.
    /// </summary>
    public Media.StreamLinks? Listed { get; set; }

    /// <summary>Raised on every request so a pending shutdown can be cancelled.</summary>
    public Action? OnActivity { get; set; }

    /// <summary>
    /// The subtitle playlist a live restream writes beside its own, if there
    /// is one. ffmpeg names it after the media playlist — index.m3u8 gets
    /// index_vtt.m3u8 — and then never refers to it from anywhere, which is
    /// why subtitles on a restreamed channel existed as files and were
    /// invisible to every player.
    /// </summary>
    private static string? SubtitlePlaylist(string streamDir)
    {
        try
        {
            var vtt = Path.Combine(streamDir, "index_vtt.m3u8");
            return File.Exists(vtt) ? "index_vtt.m3u8" : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// A master playlist naming the media playlist and its subtitle
    /// rendition, so a player can actually find the subtitles. Null when the
    /// stream has none, in which case index.m3u8 is the whole story and this
    /// would only add a hop.
    /// </summary>
    private string? MasterPlaylist(string streamDir, string token)
    {
        var subs = SubtitlePlaylist(streamDir);
        if (subs is null) return null;
        return "#EXTM3U\n"
             + "#EXT-X-VERSION:3\n"
             + "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Subtitles\","
             + "DEFAULT=NO,AUTOSELECT=NO,URI=\"" + subs + token + "\"\n"
             // no RESOLUTION or CODECS: they would be a guess, and a player
             // reads them from the media playlist's own segments anyway
             + "#EXT-X-STREAM-INF:BANDWIDTH=3000000,SUBTITLES=\"subs\"\n"
             + "index.m3u8" + token + "\n";
    }

    private string SubtitleListJson(string streamDir, string token = "")
    {
        var tracks = new List<Media.SubtitleManager.Track>();
        if (Subtitles is not null)
        {
            var source = Media.SubtitleManager.SourceFile(streamDir);
            if (source is not null) tracks.AddRange(Subtitles.List(source));
            tracks.AddRange(Subtitles.UserTracks(Path.Combine(streamDir, "subs")));
        }
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            tracks = tracks.Select(t => new
            {
                id = t.Id,
                label = t.Label,
                language = t.Language,
                kind = t.Kind,
                supported = t.Supported,
                url = $"/{Path.GetFileName(streamDir)}/subs/{t.Id}.vtt{token}",
            }),
        });
    }

    /// <summary>Cached VTT for a track, generating it on first request.</summary>
    private string? ResolveSubtitle(string streamDir, string id)
    {
        if (Subtitles is null) return null;
        var subsDir = Path.Combine(streamDir, "subs");
        var cached = Path.Combine(subsDir, id + ".vtt");
        if (File.Exists(cached)) return cached; // includes user-attached tracks
        var source = Media.SubtitleManager.SourceFile(streamDir);
        return source is null ? null : Subtitles.GetVtt(source, id, subsDir);
    }

    private static readonly Lazy<byte[]> HlsJsAsset = new(() =>
    {
        using var s = typeof(HlsServer).Assembly.GetManifestResourceStream("hls.min.js")!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });

    // Text rather than bytes because the page is a template: WatchPage fills
    // its placeholders on every request, so the decode is done once here.
    private static readonly Lazy<string> WatchTemplate = new(() =>
    {
        using var s = typeof(HlsServer).Assembly.GetManifestResourceStream("watch.html")!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    });

    /// <summary>
    /// Minimal universal player page for one stream. The markup is
    /// wwwroot/watch.html, embedded in the assembly; all this does is fill in
    /// the handful of values the page needs from the server. Internal rather
    /// than private only so the test project can render it and check that no
    /// placeholder was left behind; nothing outside the server calls it.
    /// </summary>
    internal static string WatchPage(string stream, string token = "")
    {
        // readable label for the page; the stream id itself is unchanged
        var pretty = System.Net.WebUtility.HtmlEncode(Media.StreamTitle.Prettify(stream));
        var name = System.Net.WebUtility.HtmlEncode(stream);   // for HTML text
        // for the <script> string literal: HTML entities aren't decoded inside
        // <script>, so an HTML-encoded name would arrive as literal "&amp;" and
        // its subs.json fetch would 404. JSON-encode it instead.
        var nameJs = System.Text.Json.JsonSerializer.Serialize(stream);
        // a share link's token has to travel on to the playlist and the
        // subtitle list, or the page loads and then nothing plays
        var src = "/" + Uri.EscapeDataString(stream) + "/index.m3u8" + token;
        var tokenJs = System.Text.Json.JsonSerializer.Serialize(token);
        return Services.PageTemplate.Fill(WatchTemplate.Value,
            ("__PRETTY__", pretty), ("__NAME__", name), ("__NAME_JS__", nameJs),
            ("__SRC__", src), ("__TOKEN_JS__", tokenJs));
    }

    private static void WriteText(HttpListenerResponse res, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
