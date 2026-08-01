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

            if (parts[1].EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                // A playlist written by a segmenter (ffmpeg VOD/live jobs)
                // wins — it has exact durations and live-window state. The
                // generated playlist covers plain directories of segments.
                var onDisk = Path.Combine(streamDir, parts[1]);
                if (File.Exists(onDisk) && !parts[1].Contains(".."))
                {
                    WriteText(res, 200, "application/vnd.apple.mpegurl", File.ReadAllText(onDisk));
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
            using var fs = File.OpenRead(segment);
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
                double.TryParse(File.ReadAllText(sidecar).Trim(), out var d) && d > 0)
                duration = d;

            sb.Append($"#EXTINF:{duration:0.000},\n");
            sb.Append(Uri.EscapeDataString(Path.GetFileName(seg))).Append('\n');
        }

        if (!live)
            sb.Append("#EXT-X-ENDLIST\n");

        return sb.ToString();
    }

    private string ListStreamsJson()
    {
        // dot-directories are internal (.thumbs thumbnail cache), not streams
        var streams = Directory.Exists(_mediaRoot)
            ? Directory.GetDirectories(_mediaRoot).Select(Path.GetFileName)
                .Where(n => n is not null && !n.StartsWith('.'))
            : Enumerable.Empty<string?>();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            streams = streams.Select(n => new { name = n, playlist = $"/{n}/index.m3u8" }),
        });
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
