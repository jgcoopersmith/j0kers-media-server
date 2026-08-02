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
        Log.Info("hls", $"listening on http://{bound}:{_config.Port}/ (media root: {_mediaRoot})");
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
            res.Headers["Access-Control-Allow-Origin"] = _config.CorsAllowOrigin;
            res.Headers["Cache-Control"] = "no-cache";

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            Log.Debug("hls", $"{ctx.Request.RemoteEndPoint} GET {path}");

            if (path == "/")
            {
                WriteText(res, 200, "application/json", ListStreamsJson());
                return;
            }

            var parts = path.Trim('/').Split('/');

            // the watch page needs hls.js on this origin
            if (parts.Length == 1 && parts[0] == "hls.min.js")
            {
                res.StatusCode = 200;
                res.ContentType = "text/javascript";
                res.Headers["Cache-Control"] = "max-age=86400";
                res.ContentLength64 = HlsJsAsset.Value.Length;
                res.OutputStream.Write(HlsJsAsset.Value);
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
                WriteText(res, 200, "text/html; charset=utf-8", WatchPage(parts[1]));
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

            // /<stream>/subs.json — the track list for this stream
            if (parts[1] == "subs.json")
            {
                WriteText(res, 200, "application/json", SubtitleListJson(streamDir));
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
                // A playlist written by a segmenter (ffmpeg VOD/live jobs)
                // wins — it has exact durations and live-window state. The
                // generated playlist covers plain directories of segments.
                var onDisk = Path.Combine(streamDir, parts[1]);
                if (File.Exists(onDisk) && !parts[1].Contains(".."))
                {
                    // Serve ffmpeg's own playlist verbatim. A still-running
                    // VOD job writes an EXT-X-PLAYLIST-TYPE:EVENT playlist
                    // (no ENDLIST): players keep reloading and extend the
                    // seek bar as segments are added, then stop when ffmpeg
                    // finishes and writes ENDLIST. An earlier version
                    // rewrote EVENT→VOD+ENDLIST here, which told every player
                    // the movie was already complete — truncating it to
                    // however much had transcoded. Do NOT do that.
                    WriteText(res, 200, "application/vnd.apple.mpegurl", ReadSharedText(onDisk));
                    return;
                }
                if (parts[1] is "index.m3u8" or "playlist.m3u8")
                {
                    WriteText(res, 200, "application/vnd.apple.mpegurl", BuildPlaylist(streamDir));
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
        return full.StartsWith(_mediaRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
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
        // dot-directories are internal (.thumbs thumbnail cache), not streams
        var dirs = Directory.Exists(_mediaRoot)
            ? Directory.GetDirectories(_mediaRoot)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
            : Enumerable.Empty<string>();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            streams = dirs.Select(d => new
            {
                name = Path.GetFileName(d),
                playlist = $"/{Path.GetFileName(d)}/index.m3u8",
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

    /// <summary>Raised on every request so a pending shutdown can be cancelled.</summary>
    public Action? OnActivity { get; set; }

    private string SubtitleListJson(string streamDir)
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
                url = $"/{Path.GetFileName(streamDir)}/subs/{t.Id}.vtt",
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
    private static string WatchPage(string stream)
    {
        var name = System.Net.WebUtility.HtmlEncode(stream);   // for HTML text
        // for the <script> string literal: HTML entities aren't decoded inside
        // <script>, so an HTML-encoded name would arrive as literal "&amp;" and
        // its subs.json fetch would 404. JSON-encode it instead.
        var nameJs = System.Text.Json.JsonSerializer.Serialize(stream);
        var src = "/" + Uri.EscapeDataString(stream) + "/index.m3u8";
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{name}} — j0kers</title>
            <link rel="icon" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><text y='.9em' font-size='90'>🃏</text></svg>">
            <style>
              body { margin: 0; background: #0d0d0d; color: #c3c2b7; font-family: system-ui, sans-serif; }
              video { display: block; width: 100vw; max-height: 88vh; background: #000; }
              .bar { padding: 10px 14px; font-size: 13px; }
              a { color: #3987e5; }
            </style>
            <script src="/hls.min.js"></script>
            </head>
            <body>
            <video id="v" controls playsinline></video>
            <div class="bar">🃏 {{name}} · <a href="{{src}}">raw playlist</a> (for VLC etc.)</div>
            <script>
              const v = document.getElementById("v");
              const src = "{{src}}";
              const stream = {{nameJs}};
              if (window.Hls && Hls.isSupported()) {
                const h = new Hls();
                h.loadSource(src);
                h.attachMedia(v);
              } else if (v.canPlayType("application/vnd.apple.mpegurl")) {
                v.src = src;
              } else {
                document.querySelector(".bar").textContent = "This browser cannot play HLS — open the raw playlist in VLC.";
              }
              // subtitles: same-origin tracks, exposed through the native CC menu
              fetch("/" + encodeURIComponent(stream) + "/subs.json")
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
