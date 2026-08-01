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

    public FfmpegManager(FfmpegConfig config, string mediaRoot, string baseDirectory)
    {
        _config = config;
        _mediaRoot = mediaRoot;
        _channelsFile = Path.Combine(baseDirectory, "channels.json");
        FfmpegPath = config.Path;
        Directory.CreateDirectory(_mediaRoot);
        Detect();
        LoadChannels();
    }

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
    /// </summary>
    public (string stream, bool ready) StartVod(string file)
    {
        if (!Available) throw new InvalidOperationException("ffmpeg is not available");
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException("no such file", file);

        // cache key: same file+size+mtime → same output dir, converted once
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..12].ToLowerInvariant();
        var stream = "vod-" + key;
        var dir = Path.Combine(_mediaRoot, stream);
        var playlist = Path.Combine(dir, "index.m3u8");

        lock (_lock)
        {
            var running = _vodJobs.TryGetValue(stream, out var proc) && !proc.HasExited;
            if (File.Exists(playlist) && !running)
                return (stream, true); // finished earlier
            if (running)
                return (stream, File.Exists(playlist));

            Directory.CreateDirectory(dir);
            var args =
                $"-hide_banner -loglevel error -y -i \"{info.FullName}\" " +
                $"-c:v libx264 -preset {_config.Preset} -crf {_config.Crf} -pix_fmt yuv420p " +
                $"-c:a aac -b:a 160k -ac 2 " +
                $"-f hls -hls_time 6 -hls_list_size 0 -hls_playlist_type event " +
                $"-hls_segment_filename \"{Path.Combine(dir, "seg_%05d.ts")}\" \"{playlist}\"";
            _vodJobs[stream] = Spawn(args, $"vod {info.Name}");
            return (stream, false);
        }
    }

    public bool IsVodReady(string stream) =>
        File.Exists(Path.Combine(_mediaRoot, stream, "index.m3u8"));

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
        var codecs = _config.LiveVideoMode.Equals("copy", StringComparison.OrdinalIgnoreCase)
            ? "-c copy"
            : $"-c:v libx264 -preset {_config.Preset} -tune zerolatency -pix_fmt yuv420p -c:a aac -b:a 160k -ac 2";
        var args =
            $"-hide_banner -loglevel error -y {input} {codecs} " +
            $"-f hls -hls_time {_config.LiveSegmentSeconds} -hls_list_size {_config.LiveWindowSegments} " +
            $"-hls_flags delete_segments+independent_segments " +
            $"-hls_segment_filename \"{Path.Combine(dir, "seg_%05d.ts")}\" \"{Path.Combine(dir, "index.m3u8")}\"";
        _liveJobs[stream] = Spawn(args, $"channel {name}");
    }

    // ---- plumbing -------------------------------------------------------

    private Process Spawn(string args, string label)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(FfmpegPath, args)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
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
