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
                    if (!transcoding
                        && parts[0].StartsWith("vod-", StringComparison.OrdinalIgnoreCase)
                        && !text.Contains("#EXT-X-ENDLIST"))
                    {
                        text = text.Replace("#EXT-X-PLAYLIST-TYPE:EVENT", "#EXT-X-PLAYLIST-TYPE:VOD");
                        if (!text.EndsWith('\n')) text += "\n";
                        text += "#EXT-X-ENDLIST\n";
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
                !File.Exists(segment) ||
                !SegmentExtensions.Contains(Path.GetExtension(segment).ToLowerInvariant()))
            {
                WriteText(res, 404, "text/plain", "not found");
                return;
            }

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

    private string BuildPlaylist(string streamDir)
    {
        var segments = Directory.GetFiles(streamDir)
            .Where(f => SegmentExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            // init.mp4 is an initialization segment, not media — listing it
            // (it also sorts first) produced an unplayable playlist
            .Where(f => !Path.GetFileName(f).Equals("init.mp4", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Minimal universal player page for one stream.</summary>
    private static string WatchPage(string stream, string token = "")
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
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{pretty}} — j0kers</title>
            <link rel="icon" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><text y='.9em' font-size='90'>🃏</text></svg>">
            <style>
              /* The dashboard's own light/dark pick lives in its localStorage,
                 which this page cannot read: it is served from the HLS port,
                 and that is a different origin. Following the system is the
                 next best thing, and is what the dashboard does until the
                 viewer overrides it. Same palette either way. */
              :root {
                color-scheme: dark;
                --page: #0d0d0d; --surface: #222221; --ink: #ffffff;
                --ink-2: #c3c2b7; --muted: #898781; --line: rgba(255,255,255,0.10);
                --link: #3987e5;
              }
              @media (prefers-color-scheme: light) {
                :root {
                  color-scheme: light;
                  --page: #f9f9f7; --surface: #f0efec; --ink: #0b0b0b;
                  --ink-2: #52514e; --muted: #898781; --line: rgba(11,11,11,0.10);
                  --link: #2a78d6;
                }
              }
              body { margin: 0; background: var(--page); color: var(--ink-2); font-family: system-ui, sans-serif; }
              video { display: block; width: 100vw; max-height: 82vh; background: #000; }
              .bar { padding: 10px 14px; font-size: 13px; }
              a { color: var(--link); }
              .ctl { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; padding: 8px 14px 0; }
              .ctl button, .ctl select {
                font: inherit; font-size: 12.5px; border-radius: 8px;
                border: 1px solid var(--line); background: var(--surface); color: var(--ink-2);
              }
              .ctl button { padding: 4px 10px; cursor: pointer; }
              .ctl select { padding: 3px 6px; }
              .ctl button:hover { border-color: var(--muted); color: var(--ink); }
              .ctl label { color: var(--muted); font-size: 12px; }
              .ctl select:disabled { opacity: 0.5; cursor: not-allowed; }
              #msg { color: var(--muted); font-size: 12px; padding: 4px 14px 0; min-height: 1em; }
              .sub { color: var(--muted); font-size: 11px; margin-top: 2px; }
            </style>
            <script src="/hls.min.js"></script>
            </head>
            <body>
            <video id="v" controls playsinline></video>
            <div class="ctl">
              <button id="back" title="Back 15 seconds (← arrow) · space plays and pauses">⏪ 15s</button>
              <button id="fwd" title="Forward 15 seconds (→ arrow) · space plays and pauses">15s ⏩</button>
              <span style="flex:1"></span>
              <label>Speed
                <select id="speed">
                  <option value="0.5">0.5×</option><option value="0.75">0.75×</option>
                  <option value="1" selected>1×</option><option value="1.25">1.25×</option>
                  <option value="1.5">1.5×</option><option value="2">2×</option>
                </select></label>
              <label>Quality
                <select id="quality" disabled title="Reading the stream…">
                  <option value="-1">Auto</option>
                </select></label>
            </div>
            <div id="msg"></div>
            <div class="bar">🃏 {{pretty}} · <a href="{{src}}">raw playlist</a> (for VLC etc.)
              <div class="sub">{{name}}</div></div>
            <script>
              const v = document.getElementById("v");
              const speed = document.getElementById("speed");
              const quality = document.getElementById("quality");
              const msg = document.getElementById("msg");
              const src = "{{src}}";
              const stream = {{nameJs}};
              // recordings start at the beginning; live channels join the edge
              const live = /^ch-/i.test(stream);
              if (!live) {
                v.addEventListener("playing", function once() {
                  v.removeEventListener("playing", once);
                  if (v.currentTime > 2) { try { v.currentTime = 0; } catch (e) {} }
                });
              }
              let hls = null;
              if (window.Hls && Hls.isSupported()) {
                // startPosition is what actually pins the initial seek;
                // startLoad(0) alone still lets hls.js pick its own spot
                hls = new Hls(live ? {} : { startPosition: 0 });
                hls.loadSource(src);
                hls.attachMedia(v);
                // A stream's renditions are only known once the playlist is
                // read. Most streams here are a single rendition, so the
                // control stays disabled rather than offering a choice of one.
                hls.on(Hls.Events.MANIFEST_PARSED, function () {
                  const levels = hls.levels || [];
                  if (levels.length < 2) {
                    quality.title = "This stream has one quality only";
                    return;
                  }
                  levels.forEach(function (l, i) {
                    const o = document.createElement("option");
                    o.value = String(i);
                    o.textContent = l.height ? l.height + "p" : Math.round((l.bitrate || 0) / 1000) + "k";
                    quality.appendChild(o);
                  });
                  quality.disabled = false;
                  quality.title = "Pick a rendition, or let the player choose";
                });
              } else if (v.canPlayType("application/vnd.apple.mpegurl")) {
                v.src = src;
                // native HLS picks its own rendition and offers no way in
                quality.title = "This browser chooses the quality itself";
              } else {
                document.querySelector(".bar").textContent = "This browser cannot play HLS — open the raw playlist in VLC.";
              }
              quality.addEventListener("change", function () {
                if (hls) hls.currentLevel = parseInt(quality.value, 10);
              });
              // subtitles: same-origin tracks, exposed through the native CC menu
              fetch("/" + encodeURIComponent(stream) + "/subs.json" + {{tokenJs}})
                .then(r => r.ok ? r.json() : null)
                .then(d => {
                  for (const t of (d && d.tracks) || []) {
                    if (!t.supported) continue;
                    const tr = document.createElement("track");
                    tr.kind = "subtitles";
                    tr.label = t.label;
                    if (t.language) tr.srclang = t.language;
                    tr.src = t.url;
                    v.appendChild(tr);
                  }
                })
                .catch(() => {});
              // ---- seek, speed, keyboard ----
              // How far playback can actually go right now. duration is the
              // wrong bound while a file is still being converted: the playlist
              // grows as ffmpeg writes it, so duration reports only what exists
              // so far. Seeking past that lands on a fragment nobody has
              // written, which the player answers by restarting the stream.
              function playableEnd() {
                if (v.seekable && v.seekable.length) return v.seekable.end(v.seekable.length - 1);
                return isFinite(v.duration) ? v.duration : Infinity;
              }
              let msgTimer = 0;
              function say(text) {
                msg.textContent = text;
                clearTimeout(msgTimer);
                msgTimer = setTimeout(function () { if (msg.textContent === text) msg.textContent = ""; }, 4000);
              }
              function seekBy(delta) {
                if (delta < 0) { v.currentTime = Math.max(0, v.currentTime + delta); return; }
                // stay a moment short of the edge: landing exactly on it is
                // what a player reads as "past the end", and on a growing
                // playlist the edge moves anyway
                const limit = playableEnd() - 0.5;
                const wanted = v.currentTime + delta;
                if (!isFinite(limit)) { v.currentTime = wanted; return; }
                if (wanted > limit) {
                  if (v.currentTime >= limit - 1) {
                    say("That's as far as this stream goes right now.");
                    return;
                  }
                  v.currentTime = limit;
                  return;
                }
                v.currentTime = wanted;
              }
              document.getElementById("back").addEventListener("click", function () { seekBy(-15); });
              document.getElementById("fwd").addEventListener("click", function () { seekBy(15); });

              speed.addEventListener("change", function () {
                v.playbackRate = parseFloat(speed.value) || 1;
                try { localStorage.setItem("j0kers-speed", speed.value); } catch (e) {}
              });
              try {
                const saved = localStorage.getItem("j0kers-speed");
                if (saved) speed.value = saved;
              } catch (e) {}
              // a rate set before the stream loads does not survive it
              v.addEventListener("loadedmetadata", function () {
                v.playbackRate = parseFloat(speed.value) || 1;
              });

              // Arrow keys jump 15 seconds, space plays and pauses. The
              // browser only does any of this while its own control bar has
              // focus, which after clicking anywhere on the page it does not
              // — so the whole document listens instead. The dropdowns keep
              // their arrows: that is how a select is operated from the
              // keyboard.
              //
              // Capture phase, and an intention held for half a second.
              // Measured in Chrome 148: a keydown reaches document capture,
              // then any listener on the video, then document bubble. On
              // bubble, the video's own controls have already toggled
              // play/pause, so reading v.paused there reads the state they
              // just changed and toggling again undoes it — space starts the
              // film and stops it again. Capture reads the state first, and
              // the hold puts right a second toggle from either side.
              let want = null, wantAt = 0;
              function hold() {
                if (want === "play") { if (v.paused) v.play().catch(function () {}); }
                else if (want === "pause") { if (!v.paused) v.pause(); }
              }
              ["play", "pause"].forEach(function (ev) {
                v.addEventListener(ev, function () {
                  if (!want) return;
                  if (performance.now() - wantAt > 500) { want = null; return; }
                  hold();
                });
              });
              v.addEventListener("pointerdown", function () { want = null; });

              document.addEventListener("keydown", function (e) {
                if (e.altKey || e.ctrlKey || e.metaKey) return;
                const t = e.target;
                if (t && (t.tagName === "SELECT" || t.tagName === "INPUT" || t.tagName === "TEXTAREA")) return;
                if (t && (t.tagName === "BUTTON" || t.tagName === "A")) return;
                if (e.key === "ArrowRight") { e.preventDefault(); if (!e.repeat) seekBy(15); }
                else if (e.key === "ArrowLeft") { e.preventDefault(); if (!e.repeat) seekBy(-15); }
                else if (e.key === " " || e.key === "Spacebar" || e.key === "k" || e.key === "K") {
                  e.preventDefault();
                  if (e.repeat) return;
                  want = v.paused ? "play" : "pause";
                  wantAt = performance.now();
                  hold();
                }
              }, true);

              v.play().catch(() => {}); // autoplay may need a tap; controls are visible
            </script>
            </body>
            </html>
            """;
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
