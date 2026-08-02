using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using J0kersMediaServer.Config;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// ffmpeg-backed media engine. Turns arbitrary media files (movies, music)
/// into HLS on demand, and ingests live sources (HDHomeRun tuners, IPTV
/// URLs, RTSP/RTMP cameras) as continuously restreamed HLS channels.
/// Output lands in the HLS media root, so the existing HLS server and
/// dashboard player pick it up with no extra plumbing.
/// </summary>
public sealed class FfmpegManager : IDisposable
{
    public sealed class ChannelDef
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
    }

    private readonly FfmpegConfig _config;
    private readonly string _mediaRoot;
    private readonly string _channelsFile;
    private readonly object _lock = new();
    private readonly Dictionary<string, Process> _vodJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Process> _liveJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ChannelDef> _channels = new();
    private bool _disposed;

    public bool Available { get; private set; }
    public string VersionLine { get; private set; } = "ffmpeg not found";
    public string FfmpegPath { get; private set; }

    private readonly HashSet<string> _videoEncoders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _audioEncoders = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> VideoEncoders => _videoEncoders;
    public IReadOnlyCollection<string> AudioEncoders => _audioEncoders;

    /// <summary>Resolved ffmpeg encoder names actually used for transcodes.</summary>
    public string VideoEncoder { get; private set; } = "libx264";
    public string AudioEncoder { get; private set; } = "aac";

    public FfmpegManager(FfmpegConfig config, string mediaRoot, string baseDirectory)
    {
        _config = config;
        _mediaRoot = mediaRoot;
        _channelsFile = Path.Combine(baseDirectory, "channels.json");
        FfmpegPath = config.Path;
        Directory.CreateDirectory(_mediaRoot);
        Detect();
        if (Available)
        {
            DiscoverEncoders();
            VideoEncoder = ResolveEncoder(_config.VideoCodec, video: true);
            AudioEncoder = ResolveEncoder(_config.AudioCodec, video: false);
            Log.Info("ffmpeg", $"transcode codecs: video={VideoEncoder} audio={AudioEncoder} " +
                               $"({_videoEncoders.Count} video / {_audioEncoders.Count} audio encoders available)");
        }
        LoadChannels();
    }

    /// <summary>Friendly codec names → ffmpeg encoders, in preference order.</summary>
    private static readonly Dictionary<string, string[]> VideoCodecMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["h264"] = new[] { "libx264" },
        ["h265"] = new[] { "libx265" },
        ["hevc"] = new[] { "libx265" },
        ["vp9"] = new[] { "libvpx-vp9" },
        ["vp8"] = new[] { "libvpx" },
        ["av1"] = new[] { "libsvtav1", "libaom-av1", "librav1e" },
        ["mpeg2"] = new[] { "mpeg2video" },
        ["mpeg4"] = new[] { "mpeg4" },
    };

    private static readonly Dictionary<string, string[]> AudioCodecMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aac"] = new[] { "aac" },
        ["mp3"] = new[] { "libmp3lame" },
        ["opus"] = new[] { "libopus" },
        ["vorbis"] = new[] { "libvorbis" },
        ["ac3"] = new[] { "ac3" },
        ["eac3"] = new[] { "eac3" },
        ["flac"] = new[] { "flac" },
        ["alac"] = new[] { "alac" },
        ["pcm"] = new[] { "pcm_s16le" },
    };

    private void DiscoverEncoders()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(FfmpegPath, "-hide_banner -encoders")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return;
            var inList = false;
            while (p.StandardOutput.ReadLine() is { } line)
            {
                if (!inList) { inList = line.TrimStart().StartsWith("----"); continue; }
                // format: " V....D libx264   H.264 / AVC ..." — flags, name, description
                var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[0].Length == 0) continue;
                if (parts[0][0] == 'V') _videoEncoders.Add(parts[1]);
                else if (parts[0][0] == 'A') _audioEncoders.Add(parts[1]);
            }
            p.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Log.Warn("ffmpeg", $"could not enumerate encoders: {ex.Message}");
        }
    }

    private string ResolveEncoder(string requested, bool video)
    {
        var available = video ? _videoEncoders : _audioEncoders;
        var fallback = video ? "libx264" : "aac";
        if (requested.Equals("copy", StringComparison.OrdinalIgnoreCase)) return "copy";

        var map = video ? VideoCodecMap : AudioCodecMap;
        if (map.TryGetValue(requested, out var candidates))
        {
            foreach (var c in candidates)
                if (available.Contains(c)) return c;
        }
        else if (available.Contains(requested))
        {
            return requested; // raw ffmpeg encoder name
        }

        Log.Warn("ffmpeg", $"{(video ? "video" : "audio")} codec '{requested}' is not available in this ffmpeg build — using {fallback}");
        return fallback;
    }

    /// <summary>x264/x265 take preset+crf; other encoders get sane defaults of their own.</summary>
    private string VideoQualityArgs() => VideoEncoder switch
    {
        "libx264" or "libx265" => $"-preset {_config.Preset} -crf {_config.Crf} ",
        "libvpx-vp9" => $"-crf {_config.Crf} -b:v 0 ",
        "libaom-av1" or "libsvtav1" or "librav1e" => $"-crf {_config.Crf} ",
        _ => "",
    };

    /// <summary>MPEG-TS only carries the classic codecs; anything newer needs fMP4 segments (RFC 8216 §3).</summary>
    private bool NeedsFmp4() =>
        VideoEncoder is "libvpx-vp9" or "libvpx" or "libaom-av1" or "libsvtav1" or "librav1e" or "libx265"
        || AudioEncoder is "libopus" or "libvorbis" or "flac" or "alac";

    private void Detect()
    {
        foreach (var candidate in Candidates())
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(candidate, "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p is null) continue;
                var first = p.StandardOutput.ReadLine() ?? "";
                p.WaitForExit(3000);
                if (first.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))
                {
                    Available = true;
                    VersionLine = first;
                    FfmpegPath = candidate;
                    Log.Info("ffmpeg", $"{first} ({candidate})");
                    return;
                }
            }
            catch { /* try next candidate */ }
        }
        Log.Warn("ffmpeg", "ffmpeg not found — movie/TV transcoding and live channels are disabled. " +
                           "Install it (winget install Gyan.FFmpeg) or set ffmpeg.path in the config.");
    }

    private IEnumerable<string> Candidates()
    {
        yield return _config.Path;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var links = Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        if (File.Exists(links)) yield return links;

        // winget package layout: Packages\Gyan.FFmpeg_*\ffmpeg-*\bin\ffmpeg.exe
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var pkg in Directory.GetDirectories(packages, "*FFmpeg*"))
            foreach (var exe in Directory.GetFiles(pkg, "ffmpeg.exe", SearchOption.AllDirectories))
                yield return exe;
        }
    }

    // ---- VOD: file → HLS ----------------------------------------------

    /// <summary>
    /// Starts (or reuses) an HLS conversion of a media file. Returns the
    /// stream directory name under the media root; the playlist inside it
    /// becomes playable within a couple of seconds while conversion runs.
    /// <paramref name="height"/> &gt; 0 scales the video to that height
    /// (each height caches as its own conversion).
    /// </summary>
    public (string stream, bool ready) StartVod(string file, int height = 0)
    {
        if (!Available) throw new InvalidOperationException("ffmpeg is not available");
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException("no such file", file);

        // cache key: same file+size+mtime+height+codecs → same output dir, converted once
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{height}|{VideoEncoder}|{AudioEncoder}")))[..8].ToLowerInvariant();
        // readable link: filename slug + optional quality + short hash for uniqueness
        var slug = Slugify(Path.GetFileNameWithoutExtension(info.Name));
        var stream = $"vod-{slug}{(height > 0 ? $"-{height}p" : "")}-{key}";
        var dir = Path.Combine(_mediaRoot, stream);
        var playlist = Path.Combine(dir, "index.m3u8");

        lock (_lock)
        {
            var running = _vodJobs.TryGetValue(stream, out var proc) && !proc.HasExited;
            if (File.Exists(playlist) && !running)
            {
                Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow); // LRU touch
                return (stream, true); // finished earlier
            }
            if (running)
                return (stream, File.Exists(playlist));

            EvictVodCache(keep: stream);
            Directory.CreateDirectory(dir);
            var video = VideoEncoder == "copy"
                ? "-c:v copy "
                : $"{(height > 0 ? $"-vf scale=-2:{height} " : "")}-c:v {VideoEncoder} {VideoQualityArgs()}-pix_fmt yuv420p ";
            var audio = AudioEncoder == "copy"
                ? "-c:a copy "
                : $"-c:a {AudioEncoder} -b:a 160k -ac 2 ";
            var segExt = NeedsFmp4() ? "m4s" : "ts";
            // keep the init filename relative so the EXT-X-MAP URI stays
            // fetchable; the job's working directory (below) puts the file
            // in the stream folder
            var segType = NeedsFmp4() ? "-hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 " : "";
            var args =
                $"-hide_banner -loglevel error -y -i \"{info.FullName}\" " +
                video + audio +
                $"-f hls -hls_time 6 -hls_list_size 0 -hls_playlist_type event {segType}" +
                $"-hls_segment_filename \"{Path.Combine(dir, $"seg_%05d.{segExt}")}\" \"{playlist}\"";
            var job = Spawn(args, $"vod {info.Name}", dir);
            _vodJobs[stream] = job;
            job.Exited += (_, _) =>
            {
                // keep the job table from accumulating finished processes
                lock (_lock)
                {
                    if (_vodJobs.TryGetValue(stream, out var q) && ReferenceEquals(q, job))
                        _vodJobs.Remove(stream);
                }
            };
            return (stream, false);
        }
    }

    /// <summary>
    /// Evicts least-recently-played vod-* conversions until the cache fits
    /// under ffmpeg.vodCacheMaxGb. Running jobs and <paramref name="keep"/>
    /// are never evicted.
    /// </summary>
    private void EvictVodCache(string keep)
    {
        if (_config.VodCacheMaxGb <= 0) return;
        var budget = (long)(_config.VodCacheMaxGb * 1024 * 1024 * 1024);

        List<(DirectoryInfo dir, long size)> entries;
        try
        {
            entries = new DirectoryInfo(_mediaRoot)
                .EnumerateDirectories("vod-*")
                .Select(d => (d, d.EnumerateFiles().Sum(f => f.Length)))
                .ToList();
        }
        catch { return; }

        var total = entries.Sum(e => e.size);
        foreach (var (dir, size) in entries.OrderBy(e => e.dir.LastWriteTimeUtc))
        {
            if (total <= budget) break;
            if (dir.Name.Equals(keep, StringComparison.OrdinalIgnoreCase)) continue;
            if (_vodJobs.TryGetValue(dir.Name, out var p) && !p.HasExited) continue;
            try
            {
                dir.Delete(recursive: true);
                total -= size;
                Log.Info("ffmpeg", $"evicted VOD cache entry {dir.Name} ({size / (1024.0 * 1024):0.#} MB)");
            }
            catch { /* files in use — try again next time */ }
        }
    }

    public bool IsVodReady(string stream) =>
        File.Exists(Path.Combine(_mediaRoot, stream, "index.m3u8"));

    /// <summary>Filename → URL-safe lowercase slug (letters/digits/dashes, ≤48 chars).</summary>
    private static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        return slug.Length > 0 ? slug : "media";
    }

    // ---- thumbnails -----------------------------------------------------

    private static readonly HashSet<string> ThumbSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // video (matches the dashboard's EXT.video list)
        ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".webm", ".ts", ".m2ts", ".mts", ".wmv", ".flv", ".f4v",
        ".mpg", ".mpeg", ".mpe", ".m1v", ".m2v", ".vob", ".3gp", ".3g2", ".ogv", ".mxf", ".asf",
        ".rm", ".rmvb", ".divx", ".dv", ".y4m", ".hevc", ".h264", ".264", ".265", ".av1", ".ivf", ".nut",
        // pictures
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".tif", ".tiff", ".ico",
        ".heic", ".heif", ".jxl", ".tga", ".dds", ".exr",
    };

    /// <summary>
    /// Returns a cached 320px JPEG thumbnail for a video (frame at ~3 s) or
    /// picture (scaled), generating it on first request. Null when ffmpeg
    /// is unavailable, the type has no visual, or generation fails.
    /// </summary>
    public string? GetThumbnail(string file)
    {
        if (!Available || !File.Exists(file)) return null;
        var ext = Path.GetExtension(file);
        if (!ThumbSourceExtensions.Contains(ext)) return null;

        var info = new FileInfo(file);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..16].ToLowerInvariant();
        var thumbDir = Path.Combine(_mediaRoot, ".thumbs");
        var thumb = Path.Combine(thumbDir, key + ".jpg");
        if (File.Exists(thumb)) return thumb;

        Directory.CreateDirectory(thumbDir);
        var isVideo = ext is not (".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".avif");
        // videos: seek before decode so a frame grab is cheap even on large files
        var seek = isVideo ? "-ss 3 " : "";
        var args = $"-hide_banner -loglevel error -y {seek}-i \"{info.FullName}\" " +
                   $"-frames:v 1 -vf \"scale=320:-2\" -q:v 5 \"{thumb}\"";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(FfmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            if (!p.WaitForExit(10_000)) { try { p.Kill(true); } catch { } return null; }
            // a very short video can have nothing at 3 s — retry from the start
            if ((p.ExitCode != 0 || !File.Exists(thumb)) && isVideo)
            {
                using var p2 = Process.Start(new ProcessStartInfo(FfmpegPath,
                    args.Replace("-ss 3 ", "")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
                p2?.WaitForExit(10_000);
            }
            return File.Exists(thumb) ? thumb : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- live channels: URL → continuous HLS ---------------------------

    public IReadOnlyList<(ChannelDef def, string stream, string status)> Channels
    {
        get
        {
            lock (_lock)
            {
                return _channels.Select(c =>
                {
                    var stream = ChannelStream(c.Name);
                    var status = _liveJobs.TryGetValue(stream, out var p)
                        ? (p.HasExited ? $"stopped (exit {p.ExitCode})" : "running")
                        : "stopped";
                    return (c, stream, status);
                }).ToList();
            }
        }
    }

    public static string ChannelStream(string name) =>
        "ch-" + string.Concat(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');

    public string AddChannel(string name, string url)
    {
        if (!Available) throw new InvalidOperationException("ffmpeg is not available");
        var stream = ChannelStream(name);
        if (stream == "ch-") throw new ArgumentException("channel name needs at least one letter or digit");

        lock (_lock)
        {
            if (_channels.Any(c => ChannelStream(c.Name) == stream))
                throw new InvalidOperationException($"a channel named '{name}' already exists");
            _channels.Add(new ChannelDef { Name = name, Url = url });
            SaveChannels();
            StartLiveJob(name, url);
        }
        return stream;
    }

    public bool RemoveChannel(string name)
    {
        var stream = ChannelStream(name);
        lock (_lock)
        {
            var def = _channels.FirstOrDefault(c => ChannelStream(c.Name) == stream);
            if (def is null) return false;
            _channels.Remove(def);
            SaveChannels();
            StopJob(_liveJobs, stream);
            try { Directory.Delete(Path.Combine(_mediaRoot, stream), recursive: true); } catch { }
            return true;
        }
    }

    /// <summary>(Re)starts the ffmpeg process for a channel; used at startup and on demand.</summary>
    public bool RestartChannel(string name)
    {
        lock (_lock)
        {
            var def = _channels.FirstOrDefault(c => ChannelStream(c.Name) == ChannelStream(name));
            if (def is null) return false;
            StopJob(_liveJobs, ChannelStream(def.Name));
            StartLiveJob(def.Name, def.Url);
            return true;
        }
    }

    private void StartLiveJob(string name, string url)
    {
        var stream = ChannelStream(name);
        var dir = Path.Combine(_mediaRoot, stream);
        Directory.CreateDirectory(dir);

        var input = url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
            ? $"-rtsp_transport tcp -i \"{url}\""
            : $"-i \"{url}\"";
        var zerolatency = VideoEncoder is "libx264" or "libx265" ? "-tune zerolatency " : "";
        var codecs = _config.LiveVideoMode.Equals("copy", StringComparison.OrdinalIgnoreCase)
            ? "-c copy"
            : (VideoEncoder == "copy" ? "-c:v copy " : $"-c:v {VideoEncoder} {VideoQualityArgs()}{zerolatency}-pix_fmt yuv420p ")
              + (AudioEncoder == "copy" ? "-c:a copy" : $"-c:a {AudioEncoder} -b:a 160k -ac 2");
        var liveSegExt = NeedsFmp4() ? "m4s" : "ts";
        var liveSegType = NeedsFmp4() ? "-hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 " : "";
        var args =
            $"-hide_banner -loglevel error -y {input} {codecs} " +
            $"-f hls -hls_time {_config.LiveSegmentSeconds} -hls_list_size {_config.LiveWindowSegments} " +
            $"-hls_flags delete_segments+independent_segments {liveSegType}" +
            $"-hls_segment_filename \"{Path.Combine(dir, $"seg_%05d.{liveSegExt}")}\" \"{Path.Combine(dir, "index.m3u8")}\"";
        _liveJobs[stream] = Spawn(args, $"channel {name}", dir);
    }

    // ---- plumbing -------------------------------------------------------

    private Process Spawn(string args, string label, string? workingDir = null)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(FfmpegPath, args)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? "",
            },
            EnableRaisingEvents = true,
        };
        var errTail = new Queue<string>(8);
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (errTail)
            {
                if (errTail.Count >= 8) errTail.Dequeue();
                errTail.Enqueue(e.Data);
            }
        };
        p.Exited += (_, _) =>
        {
            if (_disposed) return;
            string tail;
            lock (errTail) tail = string.Join(" | ", errTail);
            if (p.ExitCode == 0)
                Log.Info("ffmpeg", $"{label}: finished");
            else
                Log.Warn("ffmpeg", $"{label}: exited with code {p.ExitCode}{(tail.Length > 0 ? " — " + tail : "")}");
        };
        p.Start();
        p.BeginErrorReadLine();
        Log.Info("ffmpeg", $"started: {label}");
        return p;
    }

    private static void StopJob(Dictionary<string, Process> jobs, string key)
    {
        if (!jobs.Remove(key, out var p)) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        p.Dispose();
    }

    private void LoadChannels()
    {
        if (!File.Exists(_channelsFile)) return;
        try
        {
            var defs = JsonSerializer.Deserialize<List<ChannelDef>>(File.ReadAllText(_channelsFile)) ?? new();
            _channels.AddRange(defs);
            if (!Available) return;
            foreach (var c in defs)
            {
                Log.Info("ffmpeg", $"restoring channel: {c.Name}");
                StartLiveJob(c.Name, c.Url);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("ffmpeg", $"could not load channels.json: {ex.Message}");
        }
    }

    private void SaveChannels() =>
        File.WriteAllText(_channelsFile, JsonSerializer.Serialize(_channels, new JsonSerializerOptions { WriteIndented = true }));

    public void Dispose()
    {
        _disposed = true;
        lock (_lock)
        {
            foreach (var key in _vodJobs.Keys.ToList()) StopJob(_vodJobs, key);
            foreach (var key in _liveJobs.Keys.ToList()) StopJob(_liveJobs, key);
        }
    }
}
