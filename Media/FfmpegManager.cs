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

        /// <summary>
        /// Whether this channel should be restreaming. A restream is a
        /// permanent ffmpeg process pulling and transcoding around the clock,
        /// so it is started deliberately rather than by the act of saving the
        /// channel. Persisted, so the ones actually in use come back after a
        /// restart and the rest stay idle.
        ///
        /// Defaults to true: channels saved before this field existed were
        /// all running, and absent must not silently stop them.
        /// </summary>
        public bool Started { get; set; } = true;
    }

    private readonly FfmpegConfig _config;
    private readonly string _mediaRoot;
    private readonly string _channelsFile;
    /// <summary>Pids of children this run started, so a hard kill can be cleaned up next start.</summary>
    private readonly string _pidFile;
    private readonly object _lock = new();
    private readonly Dictionary<string, Process> _vodJobs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How far each running conversion has got, from ffmpeg's own -progress
    /// stream. DurationSeconds is 0 when the source length couldn't be
    /// probed — a live or malformed input — in which case only the elapsed
    /// position is meaningful and Percent stays null.
    /// </summary>
    public sealed record VodProgress(string Stream, string Title, double DoneSeconds, double DurationSeconds)
    {
        public int? Percent => DurationSeconds > 0
            ? Math.Clamp((int)Math.Round(DoneSeconds / DurationSeconds * 100), 0, 100)
            : null;
    }

    private readonly Dictionary<string, VodProgress> _vodProgress = new(StringComparer.OrdinalIgnoreCase);
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
        _pidFile = Path.Combine(baseDirectory, "ffmpeg-pids.txt");
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
        // before anything of ours starts, so a leftover writer is gone
        // rather than sharing a channel directory with its replacement
        KillOrphanedJobs();
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
            var psi = new ProcessStartInfo(FfmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-encoders");
            using var p = Process.Start(psi);
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
    private string[] VideoQualityArgs() => VideoEncoder switch
    {
        "libx264" or "libx265" => new[] { "-preset", _config.Preset, "-crf", Inv(_config.Crf) },
        "libvpx-vp9" => new[] { "-crf", Inv(_config.Crf), "-b:v", "0" },
        "libaom-av1" or "libsvtav1" or "librav1e" => new[] { "-crf", Inv(_config.Crf) },
        _ => Array.Empty<string>(),
    };

    private static string Inv(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Same, for a time or a length in seconds.</summary>
    private static string Inv(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Encoders/codecs MPEG-TS cannot carry — these need fMP4 segments (RFC 8216 §3.1).</summary>
    private static readonly HashSet<string> Fmp4Only = new(StringComparer.OrdinalIgnoreCase)
    {
        // encoder names
        "libvpx-vp9", "libvpx", "libaom-av1", "libsvtav1", "librav1e", "libx265",
        "libopus", "libvorbis", "flac", "alac",
        // decoder/codec names (used when remuxing with -c copy)
        "vp9", "vp8", "av1", "hevc", "opus", "vorbis",
    };

    /// <summary>
    /// Whether the segment container must be fMP4. In copy mode the
    /// configured encoders are irrelevant — the SOURCE codecs decide, so
    /// they are probed; guessing from config produced impossible command
    /// lines (e.g. remuxing MPEG-2 into fMP4).
    /// </summary>
    private bool NeedsFmp4(string? sourceFile)
    {
        var copyVideo = VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase);
        var copyAudio = AudioEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase);
        if (!copyVideo && !copyAudio)
            return Fmp4Only.Contains(VideoEncoder) || Fmp4Only.Contains(AudioEncoder);

        // remuxing: ask the source what it actually contains
        if (sourceFile is not null)
        {
            var (v, a) = ProbeCodecs(sourceFile);
            var effectiveV = copyVideo ? v : VideoEncoder;
            var effectiveA = copyAudio ? a : AudioEncoder;
            return Fmp4Only.Contains(effectiveV ?? "") || Fmp4Only.Contains(effectiveA ?? "");
        }
        // live URL with no cheap probe: MPEG-TS is what tuners and IPTV deliver
        return false;
    }

    /// <summary>First video and audio codec names of a media file, via ffprobe.</summary>
    private (string? video, string? audio) ProbeCodecs(string file)
    {
        try
        {
            var psi = new ProcessStartInfo(FfprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name",
                                      "-of", "csv=p=0", file })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return (null, null);
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15_000);
            string? video = null, audio = null;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(',');
                if (parts.Length < 2) continue;
                // csv order follows the -show_entries list: codec_name,codec_type
                var name = parts[0];
                var type = parts[1];
                if (type == "video" && video is null) video = name;
                else if (type == "audio" && audio is null) audio = name;
            }
            return (video, audio);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Length of a media file in seconds, or 0 when it can't be determined.</summary>
    private double ProbeDurationSeconds(string file)
    {
        try
        {
            var psi = new ProcessStartInfo(FfprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration",
                                      "-of", "default=noprint_wrappers=1:nokey=1", file })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return 0;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(15_000);
            return double.TryParse(output, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? seconds : 0;
        }
        catch
        {
            return 0;
        }
    }

    private string? _placeholderPath;
    private readonly object _placeholderLock = new();

    /// <summary>
    /// A short H.264/AAC clip that reads "Transcoding…", generated once and
    /// cached. It is what a television is handed the moment it presses play
    /// on a file that has to be converted first and has no finished
    /// conversion yet: DLNA has no other channel to tell the person at the
    /// set what is happening, so the message is the picture. Returns null if
    /// it cannot be made (no ffmpeg, or the draw failed), in which case the
    /// caller falls back to serving the original.
    /// </summary>
    public string? TranscodingPlaceholder()
    {
        if (!Available) return null;
        lock (_placeholderLock)
        {
            if (_placeholderPath is not null && File.Exists(_placeholderPath)) return _placeholderPath;

            var path = Path.Combine(_mediaRoot, ".transcoding.mp4");
            if (File.Exists(path)) { _placeholderPath = path; return path; }

            // A Windows font, escaped for ffmpeg's filter parser: the drive
            // colon becomes "\:" or drawtext reads it as an option separator.
            var font = OperatingSystem.IsWindows()
                ? @"C\:/Windows/Fonts/arialbd.ttf"
                : "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf";
            var draw = File.Exists(font.Replace(@"\:", ":"))
                ? $"drawtext=fontfile='{font}':text='Transcoding…':fontcolor=white:fontsize=64:x=(w-text_w)/2:y=(h-text_h)/2"
                : "drawtext=text='Transcoding…':fontcolor=white:fontsize=64:x=(w-text_w)/2:y=(h-text_h)/2";

            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=c=0x101418:s=1280x720:d=20:r=15",
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                "-vf", draw,
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-profile:v", "high", "-level", "4.0",
                "-preset", "veryfast", "-crf", "23",
                "-c:a", "aac", "-b:a", "128k", "-ac", "2",
                "-shortest", "-movflags", "+faststart", path,
            };
            try
            {
                var psi = new ProcessStartInfo(FfmpegPath)
                { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p is null) return null;
                var err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } return null; }
                if (p.ExitCode != 0 || !File.Exists(path))
                {
                    Log.Warn("dlna", $"could not make the Transcoding… placeholder: {err.Trim()}");
                    return null;
                }
                _placeholderPath = path;
                return path;
            }
            catch (Exception ex)
            {
                Log.Warn("dlna", $"could not make the Transcoding… placeholder: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>ffprobe lives beside ffmpeg; falls back to PATH.</summary>
    public string FfprobePath
    {
        get
        {
            var dir = Path.GetDirectoryName(FfmpegPath);
            var name = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
            if (!string.IsNullOrEmpty(dir))
            {
                var beside = Path.Combine(dir, name);
                if (File.Exists(beside)) return beside;
            }
            return "ffprobe";
        }
    }

    private void Detect()
    {
        foreach (var candidate in Candidates())
        {
            try
            {
                var probe = new ProcessStartInfo(candidate)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                probe.ArgumentList.Add("-version");
                using var p = Process.Start(probe);
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
    /// <summary>
    /// The cache directory name a conversion of this file would use, without
    /// starting one. Null when the file is gone.
    ///
    /// Kept apart from StartVod because callers need to ask "is this one
    /// already done?" without starting a conversion, and the only safe way
    /// to answer is to compute the name the same way the converter will —
    /// two copies of this arithmetic would eventually disagree.
    /// </summary>
    public string? VodStreamName(string file, int height = 0, bool forceFmp4 = false)
    {
        FileInfo info;
        try { info = new FileInfo(file); if (!info.Exists) return null; }
        catch { return null; }

        // scaling is impossible when the video is remuxed, so a requested
        // height must not fork the cache (it produced N identical copies
        // labelled with resolutions they didn't have)
        if (VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase)) height = 0;

        // cache key: same file+size+mtime+height+codecs → same output dir,
        // converted once. forceFmp4 adds a discriminator ONLY when set, so a
        // DLNA conversion (fMP4, servable as video/mp4) lands in its own dir
        // and never collides with the web player's .ts conversion of the
        // same file — and the web keys stay byte-identical to before, so no
        // existing cache is invalidated.
        var salt = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{height}|{VideoEncoder}|{AudioEncoder}"
                   + (forceFmp4 ? "|fmp4" : "");
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(salt)))[..8].ToLowerInvariant();
        // readable link: filename slug + optional quality + short hash for uniqueness
        var slug = Slugify(Path.GetFileNameWithoutExtension(info.Name));
        return $"vod-{slug}{(height > 0 ? $"-{height}p" : "")}-{key}";
    }

    public (string stream, bool ready) StartVod(string file, int height = 0, bool forceFmp4 = false)
    {
        if (!Available) throw new InvalidOperationException("ffmpeg is not available");
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException("no such file", file);

        var stream = VodStreamName(file, height, forceFmp4)
            ?? throw new FileNotFoundException("no such file", file);
        if (VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase)) height = 0;
        var dir = Path.Combine(_mediaRoot, stream);
        var playlist = Path.Combine(dir, "index.m3u8");

        string started;
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

            // Eviction is deliberately not done here. Sizing the cache means
            // stat-ing every file in it, and going over budget means deleting
            // directories of segments — seconds of work on a full cache. Under
            // this lock that stalls /api/status, /api/channels and every
            // playlist request behind a click, so the whole dashboard freezes
            // just as someone starts a film. It has nothing to do with
            // starting this conversion, so it runs after, off the lock.
            Directory.CreateDirectory(dir);
            // remember the source so subtitles can be found for this stream
            try { File.WriteAllText(Path.Combine(dir, "source.txt"), info.FullName); } catch { }
            // built as a list: a file name containing a quote must never be
            // able to become extra ffmpeg arguments
            // -progress writes machine-readable key=value lines to stdout;
            // -nostats drops the human progress bar that would otherwise
            // repeat the same information over stderr
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats",
                                          "-progress", "pipe:1", "-y", "-i", info.FullName };
            if (VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                args.AddRange(new[] { "-c:v", "copy" });
            }
            else
            {
                if (height > 0) args.AddRange(new[] { "-vf", $"scale=-2:{height}" });
                args.AddRange(new[] { "-c:v", VideoEncoder });
                args.AddRange(VideoQualityArgs());
                args.AddRange(new[] { "-pix_fmt", "yuv420p" });
                // A keyframe every segment, so a seek lands where it was aimed.
                //
                // Left to itself the encoder puts keyframes where scenes
                // change, and ffmpeg can only cut a segment at one — asking
                // for 6-second segments produced 3.4s, 8.7s and 13.8s in the
                // same film. Seeking snaps to those boundaries, so how far a
                // skip moves depends on how the film happens to be cut, and
                // differs from one title to the next. Forcing the interval
                // makes every segment the length it says and every seek
                // land within it.
                //
                // sc_threshold 0 stops the encoder adding extra keyframes at
                // scene changes on top of these, which would otherwise put
                // the segment lengths back where they were.
                args.AddRange(new[] { "-force_key_frames", $"expr:gte(t,n_forced*{VodSegmentSeconds})",
                                      "-sc_threshold", "0" });
            }
            args.AddRange(AudioEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase)
                ? new[] { "-c:a", "copy" }
                : new[] { "-c:a", AudioEncoder, "-b:a", "160k", "-ac", "2" });

            // DLNA wants fMP4: its init segment plus fragments concatenate
            // into a valid video/mp4 stream, where raw .ts would have to be
            // advertised as video/mp2t — which real TVs refuse to play.
            var fmp4 = forceFmp4 || NeedsFmp4(info.FullName);
            var segExt = fmp4 ? "m4s" : "ts";
            args.AddRange(new[] { "-f", "hls", "-hls_time", Inv(VodSegmentSeconds), "-hls_list_size", "0",
                                  "-hls_playlist_type", "event" });
            // keep the init filename relative so the EXT-X-MAP URI stays fetchable
            if (fmp4) args.AddRange(new[] { "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4" });
            args.AddRange(new[] { "-hls_segment_filename", Path.Combine(dir, $"seg_%05d.{segExt}"), playlist });

            var title = Media.StreamTitle.Prettify(stream);
            var duration = ProbeDurationSeconds(info.FullName);
            lock (_progressLock) _vodProgress[stream] = new VodProgress(stream, title, 0, duration);
            // The length of the film, next to the segments, so the HLS server
            // can list every segment the finished conversion will have before
            // it has them. That is what lets the seek bar cover the whole film
            // from the first moment instead of growing behind the encoder.
            try { File.WriteAllText(Path.Combine(dir, "duration.txt"), Inv(duration)); } catch { }

            var job = Spawn(args, $"vod {info.Name}", dir,
                onProgressLine: line => NoteVodProgress(stream, title, duration, line),
                onExited: p =>
                {
                    // keep the job table from accumulating finished processes.
                    // Matched by reference so a rerun that has already taken
                    // the slot isn't evicted by its predecessor's exit.
                    lock (_lock)
                    {
                        if (_vodJobs.TryGetValue(stream, out var q) && ReferenceEquals(q, p))
                            _vodJobs.Remove(stream);
                    }
                    lock (_progressLock) _vodProgress.Remove(stream);
                    SweepSupersededSeekSegments(dir);
                });
            _vodJobs[stream] = job;
            started = stream;
        }

        // Off the lock, and off the request: the caller is waiting to be told
        // playback can begin, and trimming the cache is not part of that.
        ScheduleEviction(started);
        return (started, false);
    }

    // ---- skipping forward into a film that has not been converted yet ----
    //
    // The conversion writes segments strictly in order, so until now a skip
    // could only land inside what the encoder had already reached. Past that
    // there was nothing to play, and asking for it stopped the film.
    //
    // A segment is a fixed six seconds and every one begins at a forced
    // keyframe, so segment i is exactly [6i, 6i+6) — its content is known
    // from its number alone, whether or not it exists. That makes a missing
    // segment something to go and make rather than a wall: seek to 6i in the
    // source, encode from there, and number the output from i so it lands in
    // the same timeline as the rest.
    //
    // The original conversion is left running. It is usually well ahead of
    // where it was when the skip happened, the viewer may skip back into
    // what it has done, and stopping it would mean re-encoding all of that.
    private readonly Dictionary<string, List<SeekJob>> _seekJobs = new(StringComparer.OrdinalIgnoreCase);

    private sealed record SeekJob(int StartIndex, Process Process);

    /// <summary>How many seconds of film one segment holds.</summary>
    public static int SegmentSeconds => VodSegmentSeconds;

    /// <summary>
    /// How far a specific seek job has actually reached: the first index
    /// from <paramref name="from"/> for which its own tagged file does not
    /// yet exist. Distinct from the in-order job's progress, which is read
    /// straight off the canonical filenames it alone writes.
    /// </summary>
    private static int NextIndexOnDisk(string dir, int from, int jobStart)
    {
        var i = from;
        while (File.Exists(SeekSegmentPath(dir, i, jobStart))) i++;
        return i;
    }

    private static string SegmentPath(string dir, int index) =>
        File.Exists(Path.Combine(dir, "init.mp4"))
            ? Path.Combine(dir, $"seg_{index:D5}.m4s")
            : Path.Combine(dir, $"seg_{index:D5}.ts");

    /// <summary>
    /// A seek job's own name for the segment at <paramref name="index"/>,
    /// tagged with the job's own start so two jobs — a seek job and the
    /// in-order conversion, or two seek jobs — can never write the same
    /// file.
    /// </summary>
    /// <remarks>
    /// The in-order conversion never stops: it runs from 0 to the end of
    /// the film regardless of any seek, so every index a seek job produces
    /// is one the in-order job will eventually reach too — not eventually
    /// in the sense of maybe, but on a machine that encodes this much
    /// faster than the film plays, in well under the length of a viewer's
    /// visit. Both processes used to write the identical filename, so
    /// whichever finished last simply overwrote the other's segment
    /// mid-flight — the corrupt fragment a player then chokes on and
    /// answers by reloading the entire film from the start. This tag is
    /// the fix: nothing but the in-order job ever writes the plain name,
    /// so it can never be overwritten by anything, and each seek job has
    /// a name only it uses, so two of those can't collide with each other
    /// either.
    /// </remarks>
    public static string SeekSegmentPath(string dir, int index, int jobStart) =>
        File.Exists(Path.Combine(dir, "init.mp4"))
            ? Path.Combine(dir, $"seg_{index:D5}.seek{jobStart:D5}.m4s")
            : Path.Combine(dir, $"seg_{index:D5}.seek{jobStart:D5}.ts");

    /// <summary>
    /// Deletes a seek job's stand-in wherever the in-order job has since
    /// written that same index's canonical file — the stand-in served its
    /// purpose and is now just a second copy of the same six seconds. Only
    /// ever removes a file whose canonical sibling provably exists, so an
    /// interrupted conversion loses nothing it was still covering for.
    /// Called when the in-order job exits, successfully or not — a partial
    /// run still leaves some segments genuinely superseded even if not all
    /// of them.
    /// </summary>
    private static void SweepSupersededSeekSegments(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "seg_*.seek*.*"))
            {
                var name = Path.GetFileNameWithoutExtension(f);           // seg_00016.seek00016
                var parts = name.Split('.', 3);
                if (parts.Length < 2 || !int.TryParse(parts[0].AsSpan(4), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var index))
                    continue;
                if (File.Exists(SegmentPath(dir, index)))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Any already-produced stand-in for this segment from a seek job,
    /// current or past — used both to avoid starting a redundant job and
    /// to serve a segment nobody has promoted to the canonical name yet.
    /// </summary>
    private static string? ExistingSeekSegment(string dir, int index, string segExt)
    {
        try
        {
            return Directory.EnumerateFiles(dir, $"seg_{index:D5}.seek*.{segExt}").FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Makes sure something is producing the segment at <paramref name="index"/>,
    /// starting an encoder at that point in the film if nothing already is.
    /// Returns false only when the stream cannot be converted at all.
    /// </summary>
    public bool EnsureVodSegment(string stream, int index)
    {
        if (index < 0) return false;
        var dir = Path.Combine(_mediaRoot, stream);
        if (!Directory.Exists(dir)) return false;
        var fmp4Check = File.Exists(Path.Combine(dir, "init.mp4"));
        var extCheck = fmp4Check ? "m4s" : "ts";
        if (File.Exists(SegmentPath(dir, index)) || ExistingSeekSegment(dir, index, extCheck) is not null)
            return true;

        string source;
        try { source = File.ReadAllText(Path.Combine(dir, "source.txt")).Trim(); }
        catch { return false; }
        if (!File.Exists(source)) return false;

        lock (_lock)
        {
            if (File.Exists(SegmentPath(dir, index)) || ExistingSeekSegment(dir, index, extCheck) is not null)
                return true;

            // Somebody already on their way there? A job counts as covering
            // this segment when it starts at or before it and has not yet
            // been overtaken by the request — within a few segments it is
            // quicker to let it arrive than to start another encoder.
            //
            // The in-order conversion used to get this same grace, treated
            // as covering anything within a few segments of wherever it had
            // reached. That assumed it moves roughly with the viewer, which
            // held right up until a machine fast enough to encode a film in
            // a few minutes made it false: the in-order job can be tens of
            // segments past where it looks, blowing straight through a
            // seek's target before this ever gets called. It doesn't need
            // the grace any more anyway — seek and in-order segments no
            // longer share a filename, so there is nothing left for the
            // in-order job's position to protect against.
            if (_seekJobs.TryGetValue(stream, out var jobs))
            {
                jobs.RemoveAll(j => { try { return j.Process.HasExited; } catch { return true; } });
                foreach (var j in jobs)
                {
                    if (j.StartIndex > index) continue;
                    if (NextIndexOnDisk(dir, j.StartIndex, j.StartIndex) + 4 >= index) return true;
                }
            }

            // Two at a time is plenty: one catching up to where the viewer
            // is, one left over from the skip before it. A third means
            // somebody is hammering the button, and three encoders would
            // slow down the one they are actually waiting for.
            jobs ??= _seekJobs[stream] = new List<SeekJob>();
            while (jobs.Count >= 2)
            {
                var oldest = jobs[0];
                jobs.RemoveAt(0);
                try { if (!oldest.Process.HasExited) oldest.Process.Kill(true); } catch { }
            }

            var at = (double)index * VodSegmentSeconds;
            var fmp4 = fmp4Check;
            var segExt = extCheck;

            // -ss before -i so ffmpeg seeks the input rather than decoding
            // and discarding everything up to the mark. -output_ts_offset
            // puts the result back on the film's own clock, so the player
            // reads these segments as the part of the timeline they are and
            // not as a second film starting at zero.
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats",
                                          "-ss", Inv(at), "-y", "-i", source,
                                          "-output_ts_offset", Inv(at) };
            if (VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                args.AddRange(new[] { "-c:v", "copy" });
            }
            else
            {
                args.AddRange(new[] { "-c:v", VideoEncoder });
                args.AddRange(VideoQualityArgs());
                args.AddRange(new[] { "-pix_fmt", "yuv420p" });
                args.AddRange(new[] { "-force_key_frames", $"expr:gte(t,n_forced*{VodSegmentSeconds})",
                                      "-sc_threshold", "0" });
            }
            args.AddRange(AudioEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase)
                ? new[] { "-c:a", "copy" }
                : new[] { "-c:a", AudioEncoder, "-b:a", "160k", "-ac", "2" });

            // A playlist of its own, never index.m3u8: the HLS server builds
            // the playlist this stream is served from, and letting a second
            // encoder rewrite the first one's would truncate the film to
            // whatever this job happens to have done.
            args.AddRange(new[] { "-f", "hls", "-hls_time", Inv(VodSegmentSeconds), "-hls_list_size", "0",
                                  "-hls_playlist_type", "event",
                                  "-start_number", Inv(index) });
            if (fmp4) args.AddRange(new[] { "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4" });
            // Tagged with this job's own start, not the canonical name the
            // in-order job uses — see SeekSegmentPath. %05d is still
            // ffmpeg's own per-segment counter; -start_number above makes
            // its first substitution equal index, same as before.
            args.AddRange(new[] { "-hls_segment_filename", Path.Combine(dir, $"seg_%05d.seek{index:D5}.{segExt}"),
                                  Path.Combine(dir, $"seek_{index:D5}.m3u8") });

            Log.Info("ffmpeg", $"seek-ahead: {stream} from segment {index} ({Inv(at)}s)");
            var proc = Spawn(args, $"seek {stream}@{index}", dir,
                onExited: p =>
                {
                    lock (_lock)
                        if (_seekJobs.TryGetValue(stream, out var list))
                            list.RemoveAll(j => ReferenceEquals(j.Process, p));
                });
            jobs.Add(new SeekJob(index, proc));
            return true;
        }
    }

    /// <summary>
    /// Trims the conversion cache in the background, one at a time. Serialized
    /// because two sweeps would size the same directories against each other
    /// and could both decide to delete the same one.
    /// </summary>
    private int _evicting;

    private void ScheduleEviction(string keep)
    {
        if (Interlocked.Exchange(ref _evicting, 1) == 1) return;   // one already queued
        _ = Task.Run(() =>
        {
            try
            {
                lock (_lock) EvictVodCache(keep);
            }
            catch (Exception ex)
            {
                Log.Warn("ffmpeg", $"cache eviction failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _evicting, 0);
            }
        });
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

    /// <summary>Streams whose conversion is currently running.</summary>
    private readonly object _progressLock = new();

    /// <summary>
    /// Parses one line of ffmpeg's -progress output. The useful key is
    /// out_time, which is an unambiguous HH:MM:SS.ffffff — out_time_ms is a
    /// long-standing misnomer that actually carries microseconds, so it is
    /// left alone.
    /// </summary>
    private void NoteVodProgress(string stream, string title, double duration, string line)
    {
        const string key = "out_time=";
        if (!line.StartsWith(key, StringComparison.Ordinal)) return;
        var value = line[key.Length..].Trim();
        if (!TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var at)) return;
        if (at < TimeSpan.Zero) return;   // ffmpeg emits N/A as a negative before the first frame

        lock (_progressLock)
        {
            // only present while the job is live; Exited removes it
            if (_vodProgress.ContainsKey(stream))
                _vodProgress[stream] = new VodProgress(stream, title, at.TotalSeconds, duration);
        }
    }

    /// <summary>Progress of every conversion running right now.</summary>
    public IReadOnlyList<VodProgress> VodProgressSnapshot
    {
        get
        {
            var running = ActiveVodStreams;
            lock (_progressLock)
                return running
                    .Select(s => _vodProgress.TryGetValue(s, out var p) ? p : new VodProgress(s, s, 0, 0))
                    .ToArray();
        }
    }

    public IReadOnlyList<string> ActiveVodStreams
    {
        get
        {
            lock (_lock)
                return _vodJobs.Where(kv => { try { return !kv.Value.HasExited; } catch { return false; } })
                               .Select(kv => kv.Key).ToList();
        }
    }

    /// <summary>
    /// Whether a conversion of this stream is running now. The difference
    /// between a directory worth keeping and a part-finished one: unlinking
    /// preserves the first, and the second has nothing to preserve.
    /// </summary>
    public bool VodInProgress(string stream)
    {
        lock (_lock)
        {
            if (!_vodJobs.TryGetValue(stream, out var p)) return false;
            try { return !p.HasExited; } catch { return false; }
        }
    }

    /// <summary>
    /// Throws away a finished conversion so the next play rebuilds it —
    /// what the dashboard's Retranscode offers. Used when the result is
    /// wrong rather than merely unwanted: a conversion made with codec
    /// settings since changed, or one that came out broken.
    /// </summary>
    public bool DiscardVod(string stream)
    {
        lock (_lock)
        {
            if (_vodJobs.TryGetValue(stream, out var p))
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                _vodJobs.Remove(stream);
            }
            StopSeekJobs(stream);
        }
        var dir = Path.Combine(_mediaRoot, stream);
        if (!Directory.Exists(dir)) return false;
        // the source pointer is what lets the caller rebuild, so read it
        // before the directory it lives in goes
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(dir, recursive: true); return true; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(200); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Stops any encoders started by skipping ahead in this stream. Call with
    /// the lock held. They are not the conversion, so nothing here decides
    /// whether the stream itself is still being made.
    /// </summary>
    private void StopSeekJobs(string stream)
    {
        if (!_seekJobs.Remove(stream, out var jobs)) return;
        foreach (var j in jobs)
        {
            try { if (!j.Process.HasExited) j.Process.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>Kills a running conversion (e.g. before deleting its stream). True if one was running.</summary>
    public bool CancelVod(string stream)
    {
        lock (_lock)
        {
            StopSeekJobs(stream);
            if (!_vodJobs.Remove(stream, out var p)) return false;
            KillAndRelease(p);
            Log.Info("ffmpeg", $"vod job cancelled: {stream}");
            return true;
        }
    }

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

        List<string> ThumbArgs(bool seek)
        {
            var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            // videos: seek before decode so a frame grab is cheap even on large files
            if (seek) a.AddRange(new[] { "-ss", "3" });
            a.AddRange(new[] { "-i", info.FullName, "-frames:v", "1", "-vf", "scale=320:-2", "-q:v", "5", thumb });
            return a;
        }

        try
        {
            if (RunFfmpeg(ThumbArgs(isVideo)) && File.Exists(thumb)) return thumb;
            // a very short video can have nothing at 3 s — retry from the start
            if (isVideo && RunFfmpeg(ThumbArgs(false)) && File.Exists(thumb)) return thumb;
            return File.Exists(thumb) ? thumb : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A cached thumbnail for an HLS stream, taken from the stream's own
    /// media rather than its source file — so it works for streams made
    /// before source tracking existed, hand-dropped segment folders, and
    /// live channels alike. Returns null if a frame can't be grabbed.
    /// </summary>
    public string? GetStreamThumbnail(string streamDir)
    {
        if (!Available || !Directory.Exists(streamDir)) return null;
        var thumb = Path.Combine(streamDir, "thumb.jpg");
        if (File.Exists(thumb)) return thumb;

        // Take the frame from the MIDDLE of the stream: the opening frames of
        // a movie are almost always black or a studio card, which makes a
        // useless poster. fMP4 segments can't be decoded on their own (they
        // need init.mp4), so those go through the playlist instead.
        var playlist = Path.Combine(streamDir, "index.m3u8");
        var isFmp4 = File.Exists(Path.Combine(streamDir, "init.mp4"));

        var attempts = new List<(string input, string? seek)>();
        if (!isFmp4)
        {
            var segs = Directory.EnumerateFiles(streamDir, "*.ts")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (segs.Count > 0)
            {
                var mid = segs[segs.Count / 2];
                attempts.Add((mid, "2"));   // a couple of seconds into a middle segment
                attempts.Add((mid, null));  // that segment's first frame
                attempts.Add((segs[0], null));
            }
        }
        if (File.Exists(playlist))
        {
            attempts.Add((playlist, "60"));
            attempts.Add((playlist, "5"));
            attempts.Add((playlist, null));
        }
        if (attempts.Count == 0) return null;

        var temp = Path.Combine(streamDir, $"thumb.{Environment.CurrentManagedThreadId}.tmp.jpg");
        foreach (var (input, seek) in attempts)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            if (seek is not null) args.AddRange(new[] { "-ss", seek });
            args.AddRange(new[] { "-i", input, "-frames:v", "1", "-vf", "scale=224:-2", "-q:v", "6", temp });

            if (!(RunFfmpeg(args, 20_000) && File.Exists(temp))) { try { File.Delete(temp); } catch { } continue; }
            // a fully black frame means we landed on a fade or leader — a real
            // frame compresses to far more than a flat colour does
            if (new FileInfo(temp).Length < 1200) { try { File.Delete(temp); } catch { } continue; }

            try
            {
                File.Move(temp, thumb, overwrite: true);
                return thumb;
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                return File.Exists(thumb) ? thumb : null;
            }
        }
        try { File.Delete(temp); } catch { }
        return null;
    }

    /// <summary>Runs ffmpeg to completion with a timeout; true on exit code 0.</summary>
    private bool RunFfmpeg(IEnumerable<string> args, int timeoutMs = 30_000)
    {
        try
        {
            var psi = new ProcessStartInfo(FfmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
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
                    // "idle" is saved but deliberately not restreaming, which
                    // is what a freshly pinned channel is — distinct from one
                    // that was running and fell over.
                    var status = _liveJobs.TryGetValue(stream, out var p)
                        ? (p.HasExited ? $"stopped (exit {p.ExitCode})" : "running")
                        : c.Started ? "stopped" : "idle";
                    return (c, stream, status);
                }).ToList();
            }
        }
    }

    /// <summary>
    /// Segment length for on-demand conversions, and the keyframe interval
    /// forced to match it. One number, because a segment can only begin at a
    /// keyframe: if they disagree, ffmpeg cuts at whichever keyframe it can
    /// find and the segments come out uneven — which is what makes seeking
    /// jump by different amounts in different films.
    /// </summary>
    private const int VodSegmentSeconds = 6;

    public static string ChannelStream(string name) =>
        "ch-" + string.Concat(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');

    /// <summary>
    /// Saves a channel. <paramref name="start"/> false records it without
    /// spawning ffmpeg — what pinning wants, since saving a channel to watch
    /// later shouldn't put a transcode on the machine straight away.
    /// </summary>
    public string AddChannel(string name, string url, bool start = true)
    {
        if (!Available) throw new InvalidOperationException("ffmpeg is not available");
        var stream = ChannelStream(name);
        if (stream == "ch-") throw new ArgumentException("channel name needs at least one letter or digit");

        lock (_lock)
        {
            if (_channels.Any(c => ChannelStream(c.Name) == stream))
                throw new InvalidOperationException($"a channel named '{name}' already exists");
            _channels.Add(new ChannelDef { Name = name, Url = url, Started = start });
            SaveChannels();
            if (start) StartLiveJob(name, url);
        }
        return stream;
    }

    /// <summary>Starts a saved channel's restream and remembers that it should be running.</summary>
    public bool StartChannel(string name)
    {
        lock (_lock)
        {
            var def = _channels.FirstOrDefault(c => ChannelStream(c.Name) == ChannelStream(name));
            if (def is null) return false;
            var stream = ChannelStream(def.Name);
            StopJob(_liveJobs, stream);      // no-op when it isn't running
            StartLiveJob(def.Name, def.Url);
            def.Started = true;
            SaveChannels();
            return true;
        }
    }

    /// <summary>Stops the restream but keeps the channel, so it can be started again.</summary>
    public bool StopChannel(string name)
    {
        lock (_lock)
        {
            var def = _channels.FirstOrDefault(c => ChannelStream(c.Name) == ChannelStream(name));
            if (def is null) return false;
            StopJob(_liveJobs, ChannelStream(def.Name));
            def.Started = false;
            SaveChannels();
            return true;
        }
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
            if (!def.Started) { def.Started = true; SaveChannels(); }
            return true;
        }
    }

    /// <summary>
    /// What a remote input is allowed to reach. ffmpeg follows the URIs
    /// inside what it is given, and an HLS playlist may name its segments as
    /// <c>file:///…</c> — so a hostile or hijacked stream URL can have ffmpeg
    /// read this machine's files and mux them into something watchable.
    /// The list below is everything a real network source needs and nothing
    /// that touches the disk: no <c>file</c>, no <c>concat</c>, no
    /// <c>subfile</c>. Local playback passes a path, not a URL, and is
    /// unaffected.
    /// </summary>
    private static readonly string[] RemoteProtocolWhitelist =
    {
        "-protocol_whitelist", "crypto,data,http,https,tcp,tls,udp,rtp,rtsp,srt,rtmp,rtmps,pipe",
    };

    /// <summary>
    /// How a channel restream announces itself when it pulls through this
    /// server's own free-TV proxy, so the proxy can tell its own ingest from
    /// somebody actually watching. It is a label, not a credential: the
    /// proxy also requires the request to come from loopback, and nothing is
    /// granted on the strength of it either way.
    /// </summary>
    public const string RestreamUserAgent = "j0kers-restream/1.0";

    /// <summary>
    /// Corrects the scheme of a channel that points back at this server.
    ///
    /// Pinning a free-TV channel stores an absolute URL through our own
    /// proxy — <c>http://127.0.0.1:9090/api/tv/watch?…</c> — captured at the
    /// moment it was pinned. Turning TLS on later changes what that port
    /// speaks, and the saved URL becomes unplayable: ffmpeg connects and
    /// gets a TLS handshake where it expected HTTP. Rewriting at use rather
    /// than at save means switching TLS on or off keeps every pinned channel
    /// working, with nothing to re-pin.
    ///
    /// Only loopback URLs into our own API are touched. Anything else — a
    /// tuner, a camera, someone's IPTV feed — is left exactly as given.
    /// </summary>
    private static string OwnSchemeFor(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        if (u.Scheme is not ("http" or "https")) return url;
        var loopback = u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                       || (System.Net.IPAddress.TryParse(u.Host, out var ip)
                           && System.Net.IPAddress.IsLoopback(ip));
        if (!loopback) return url;
        if (!u.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return url;

        var want = Services.UrlScheme.Name;
        if (u.Scheme.Equals(want, StringComparison.OrdinalIgnoreCase)) return url;
        var fixedUp = new UriBuilder(u) { Scheme = want }.Uri.ToString();
        Log.Debug("ffmpeg", $"channel points at this server — using {want} for it");
        return fixedUp;
    }

    private void StartLiveJob(string name, string url)
    {
        var stream = ChannelStream(name);
        var dir = Path.Combine(_mediaRoot, stream);
        Directory.CreateDirectory(dir);

        // At "error" a stalled job is silent by definition — it is not
        // failing, it is waiting — so tracing raises the level to verbose and
        // adds -stats. That is what makes a wedge readable: the last thing
        // it opened, and the moment its frame counter stopped moving.
        var tracing = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("J0KERS_FFMPEG_TRACE"));
        var args = new List<string> { "-hide_banner", "-loglevel", tracing ? "verbose" : "error", "-y" };
        if (tracing) args.Add("-stats");
        if (url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            args.AddRange(new[] { "-rtsp_transport", "tcp" });
        args.AddRange(RemoteProtocolWhitelist);
        // A pinned free-TV channel is pulled through this server's own proxy,
        // so the proxy sees the restream as just another client. Left
        // unnamed it would sit in the sessions list forever as somebody
        // watching, whether or not anyone is — and its bytes would be
        // counted twice, once coming in here and again going out over HLS.
        // Only for http(s): ffmpeg warns about the option on other inputs.
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(new[] { "-user_agent", RestreamUserAgent });
            // What was measured before these existed: the CDN drops a
            // connection mid-read, and ffmpeg — whose HTTP reads have no
            // timeout at all — blocks on the dead socket forever. Process
            // alive, zero CPU, newest segment minutes old, while the
            // upstream playlist (checked directly) is advancing fine.
            //
            // rw_timeout turns that eternal block into an error after 15s,
            // and the reconnect family turns errors — including the
            // transient 503s the stitcher serves — into a retry instead of
            // an exit. The HLS demuxer hands these down to every child
            // playlist and segment request.
            // 5xx only, never 4xx: a 5xx is the stitcher having a moment and
            // worth waiting out, while a 4xx is a deterministic answer — and
            // ad-stitched HLS serves plenty of them, because segments rotate
            // out of existence mid-programme. Retrying a permanent 404 just
            // parks the stream on a dead URL; failing fast hands it to the
            // demuxer, which skips the segment and moves on. Retries are
            // capped so even a real outage errors out within seconds — the
            // exit handler's restart, with its backoff, is the long-haul
            // recovery, and it comes back with a fresh session.
            // The retry budget has to finish inside the watchdog's window,
            // or the watchdog kills a job that was busy recovering.
            //
            // It did not: 15s per read × 3 retries, backing off up to 8s, is
            // ~70s worst case against a 90s watchdog — near enough that a
            // reconnect in progress could be shot. And it was the dominant
            // failure: 22 of Voyager's 30 deaths were watchdog kills, each
            // one a restart, and until append_list every restart was a
            // visible rewind.
            //
            // So: shorter attempts, more of them. 6s reads, backoff capped at
            // 2s, eight tries, total capped at 45s — about 61s worst case,
            // comfortably inside 90. Reconnecting in place keeps the same
            // ffmpeg, the same Pluto session and the same playlist, which is
            // the only recovery that costs the viewer nothing.
            args.AddRange(new[]
            {
                "-rw_timeout", "6000000",               // µs — 6s
                "-reconnect", "1",
                "-reconnect_streamed", "1",
                "-reconnect_on_network_error", "1",
                "-reconnect_on_http_error", "5xx",
                "-reconnect_delay_max", "2",
                "-reconnect_max_retries", "8",
                "-reconnect_delay_total_max", "45",
                // A fresh connection per segment, rather than one kept open.
                //
                // "Error reading HTTP response: End of file" is what the log
                // says every time a channel wedges, and it is what a stale
                // keep-alive socket looks like from the reading end: the HLS
                // demuxer holds one connection open between segments, the CDN
                // closes it quietly during the gap, and the next read finds
                // nothing there. Reconnect flags do not help, because as far
                // as ffmpeg is concerned the response simply ended.
                //
                // Turning persistence off costs a handshake per segment —
                // once every few seconds, against a CDN built for exactly
                // that — and removes the idle socket that keeps going away.
                "-http_persistent", "0",
                // No allowed_extensions/extension_picky override here. Both
                // were added so the relayed ingest could fetch segments
                // through /api/tv/r?u=… , which has no file extension for
                // the demuxer's allowlist to accept. The relay was reverted
                // the same evening and these were left behind: doing nothing
                // except disabling a check whose job is stopping a playlist
                // pointing ffmpeg at file:// or an executable. A weakened
                // guard with no remaining purpose is worse than no change.
            });
        }
        args.AddRange(new[] { "-i", OwnSchemeFor(url) });

        var remuxAll = _config.LiveVideoMode.Equals("copy", StringComparison.OrdinalIgnoreCase);
        if (remuxAll)
        {
            args.AddRange(new[] { "-c", "copy" });
        }
        else
        {
            if (VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                args.AddRange(new[] { "-c:v", "copy" });
            }
            else
            {
                args.AddRange(new[] { "-c:v", VideoEncoder });
                args.AddRange(VideoQualityArgs());
                // No -tune zerolatency. It was here from the day this engine
                // was written and it costs picture: it turns off B-frames and
                // lookahead, so at the same preset and CRF the encoder has
                // fewer tools and spends more bits for a worse result.
                //
                // What it buys is latency, and a restream has none to save.
                // Nobody is interacting with a television channel — it is
                // already seconds behind through segmenting alone, and the
                // viewer is watching, not steering. The trade only makes
                // sense for something like a camera being driven live, which
                // is not what this path serves.
                args.AddRange(new[] { "-pix_fmt", "yuv420p" });
            }
            args.AddRange(AudioEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase)
                ? new[] { "-c:a", "copy" }
                : new[] { "-c:a", AudioEncoder, "-b:a", "160k", "-ac", "2" });

            // Tried and reverted: -fps_mode cfr, to force a monotonic output
            // timeline over an input whose timestamps restart at every ad
            // splice. It stopped the channel dead instead. Caught in a
            // stderr trace:
            //
            //   19:59:28  frame=7183  speed=1.05x     healthy for 4 minutes
            //   20:00:01  Skip ('#EXT-X-DISCONTINUITY')
            //   20:00:07  frame=7406  ...             frozen, and stays frozen
            //
            // CFR has to emit a continuous timeline, so when the input jumps
            // backwards it waits for input time to climb past what it has
            // already written. The stitcher's resets repeat, so it never
            // catches up. The cure for the looping was the disease behind
            // the stalls.
            //
            // The discontinuity is what HLS has EXT-X-DISCONTINUITY for, and
            // our output playlist already carries it. Let the player do what
            // the format designed it to do.
        }

        // Deliberately no -map: ffmpeg's own selection picks the best video
        // and audio stream, and that is what this wants.
        //
        // "-map 0:v:0" was tried and it quietly wrecked the picture. A master
        // playlist offers several variants and 0:v:0 is the *first* of them,
        // usually the smallest: channels named 720p and 1080p were being
        // restreamed at 426x240, while one whose master happens to list the
        // largest first stayed at 1080p — which is why the damage looked
        // arbitrary instead of systematic.
        //
        // It was added to stop an advert's differing stream layout unmapping
        // the output mid-stream. That theory was measured straight afterwards
        // and made no difference whatever — 13 deaths in 15 minutes against
        // 13 before it — so there is nothing to weigh against the resolution
        // it cost.

        // Video and audio only — no subtitles, no data streams.
        //
        // Every wedge on a Pluto channel traced back to the same limb:
        // their webvtt subtitle endpoint, which 500s and hangs as a matter
        // of routine (the Tubi channel, whose stream carries no subtitle
        // rendition, never wedged once). The muxer interleaves its streams,
        // so a stalled subtitle track stalls the video that was arriving
        // fine beside it. Dropping the track also makes ffmpeg's demuxer
        // stop fetching those playlists at all — unmapped streams are
        // discarded, and discarded renditions are not downloaded. The
        // dashboard never surfaced live-channel subtitles anyway.
        // Subtitles ride along; only data streams are dropped.
        //
        // They were removed wholesale to dodge a provider whose subtitle
        // endpoint stalls, which deleted a capability to work around a fault
        // — not a call this should have made on its own. -dn stays because a
        // timed-metadata stream is not something anyone watches.
        //
        // Note the shape of this: no -map. Explicit mapping is what would let
        // the subtitle stream be named precisely, and it is also what picked
        // the smallest video variant and restreamed everything at 240p.
        // ffmpeg's own selection takes the best video, the best audio and one
        // subtitle track, which is exactly the wanted set.
        args.Add("-dn");
        if (!_config.LiveSubtitles) args.Add("-sn");

        // remuxed live sources (tuners, IPTV) are MPEG-TS friendly; only a
        // real transcode to a modern codec needs fMP4
        var fmp4 = !remuxAll && NeedsFmp4(null);
        var liveSegExt = fmp4 ? "m4s" : "ts";
        // append_list and omit_endlist are what stop a restart looking like a
        // rewind to whoever is watching.
        //
        // Without append_list a restarting channel begins again at
        // seg_00000 and rewrites the playlist with MEDIA-SEQUENCE:0. A
        // player mid-stream sees the sequence jump backwards and the segment
        // names it just played reappear carrying different video, so it
        // replays — the channel appears to loop. Voyager restarted 47 times
        // in one day, which is 47 rewinds. append_list continues the
        // numbering from the existing playlist instead.
        //
        // omit_endlist covers the other half: ffmpeg writes EXT-X-ENDLIST
        // when it exits, which turns a live channel into a finished VOD for
        // the seconds before its replacement starts, and a player that
        // reloads in that window stops for good rather than waiting.
        args.AddRange(new[] { "-f", "hls", "-hls_time", Inv(_config.LiveSegmentSeconds),
                              "-hls_list_size", Inv(_config.LiveWindowSegments),
                              // discont_start marks the first segment of each
                              // run as discontinuous, which is the honest
                              // description of a restart: append_list keeps
                              // the numbering, but the content on either side
                              // of the join is unrelated — the old run's
                              // programme, then wherever the new run rejoined,
                              // often mid-advert. Unmarked, a player decodes
                              // straight across and shows part of an advert,
                              // part of the programme, and back. Marked, it
                              // resets its decoder at the seam.
                              "-hls_flags", "delete_segments+independent_segments+append_list+omit_endlist+discont_start" });
        if (fmp4) args.AddRange(new[] { "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4" });
        args.AddRange(new[] { "-hls_segment_filename", Path.Combine(dir, $"seg_%05d.{liveSegExt}"),
                              Path.Combine(dir, "index.m3u8") });

        var proc = Spawn(args, $"channel {name}", dir, onExited: p => OnLiveJobExited(name, url, p));
        _liveJobs[stream] = proc;
        _liveStarted[stream] = DateTime.UtcNow;
    }

    /// <summary>When each live job's process began — the watchdog's grace period, and the backoff reset.</summary>
    private readonly Dictionary<string, DateTime> _liveStarted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Consecutive quick deaths per stream, for the restart backoff.</summary>
    private readonly Dictionary<string, int> _liveCrashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Brings a crashed channel back, because a live channel is a promise.
    ///
    /// A recording that fails is a job that failed; a channel that dies
    /// stays dead until somebody notices the picture is gone and finds the
    /// Start button, which on a channel left playing on a TV can be hours
    /// later. The network errors that kill these jobs are transient by
    /// nature — a dropped CDN connection, a stitcher 503 — so coming back is
    /// almost always the right thing.
    ///
    /// Deliberate stops must not come back, and they are told apart by the
    /// bookkeeping order StopJob already has: it removes the job from the
    /// table before killing it, so by the time Exited fires for a stop, the
    /// table no longer names this process. A crash leaves the entry in
    /// place, and that entry is the licence to restart.
    ///
    /// The backoff is for the channel whose URL has genuinely gone bad:
    /// doubling from 3s to a minute, reset by five minutes of survival, so
    /// a flapping channel settles into one quiet retry a minute rather than
    /// a tight loop of boot calls against the provider.
    /// </summary>
    /// <summary>
    /// Restarts due, and the single thread that performs them.
    ///
    /// This exists because the first version deadlocked the server. Exited
    /// fires on a thread-pool thread, and taking _lock there blocks that
    /// thread — while _lock is itself held across process kills, WaitForExit
    /// and spawns. Remove four channels at once, as one click each does, and
    /// every kill fires a handler that blocks; the pool answers by injecting
    /// more threads, which block too. Measured on the live server: 137
    /// threads, 127 of them waiting, 16ms of CPU in five seconds. HTTP
    /// requests are dispatched with Task.Run, so they never got a thread
    /// either — the ports still listened, because the kernel accepts
    /// connections whether or not anyone is left to answer them, and a
    /// browser sat on "connecting…" forever.
    ///
    /// So the exit path now blocks nothing: it drops a due time on a queue
    /// and returns. One worker drains it, and one worker is the most that
    /// can ever be waiting on the lock.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string stream, DateTime dueUtc)> _restarts = new();
    private readonly SemaphoreSlim _restartSignal = new(0);
    private Task? _restartWorker;

    private void OnLiveJobExited(string name, string url, Process p)
    {
        var stream = ChannelStream(name);
        int delay;

        // Never block here — this is a thread-pool thread and the lock is
        // held elsewhere across waits. TryEnter with no timeout: if the lock
        // is busy the restart is queued anyway and the worker sorts it out.
        if (!Monitor.TryEnter(_lock, TimeSpan.FromMilliseconds(250)))
        {
            // The entry stays in _liveJobs, naming a process that is already
            // dead. Whoever picks this up has to notice that and clear it —
            // both the worker and the watchdog do. EnsureRestartWorker
            // belongs here too: if the very first death of the run takes this
            // path, nothing else would ever start the worker and every queued
            // restart would sit in the queue forever.
            _restarts.Enqueue((stream, DateTime.UtcNow.AddSeconds(3)));
            _restartSignal.Release();
            EnsureRestartWorker();
            return;
        }
        try
        {
            if (_disposed) return;
            if (!_liveJobs.TryGetValue(stream, out var current) || !ReferenceEquals(current, p))
                return;                                   // stopped on purpose, or already replaced
            _liveJobs.Remove(stream);

            var lived = DateTime.UtcNow - (_liveStarted.TryGetValue(stream, out var t) ? t : DateTime.UtcNow);
            var crashes = lived > TimeSpan.FromMinutes(5) ? 0 : _liveCrashes.GetValueOrDefault(stream);
            _liveCrashes[stream] = crashes + 1;
            delay = Math.Min(60, 3 << Math.Min(crashes, 4));   // 3, 6, 12, 24, 48, 60…
        }
        finally { Monitor.Exit(_lock); }

        Log.Warn("ffmpeg", $"channel {name}: died — restarting in {delay}s");
        _restarts.Enqueue((stream, DateTime.UtcNow.AddSeconds(delay)));
        _restartSignal.Release();
        EnsureRestartWorker();
    }

    private void EnsureRestartWorker()
    {
        if (_restartWorker is not null) return;
        lock (_restarts)
        {
            _restartWorker ??= Task.Run(RestartWorkerAsync);
        }
    }

    /// <summary>
    /// Drains the restart queue, one channel at a time, forever. The only
    /// thread in the process allowed to wait on <c>_lock</c> for a restart.
    /// </summary>
    private async Task RestartWorkerAsync()
    {
        while (!_disposed)
        {
            try
            {
                await _restartSignal.WaitAsync(TimeSpan.FromSeconds(5));
                if (_disposed) return;
                if (!_restarts.TryDequeue(out var due)) continue;

                // Not due yet: put it back rather than sleeping on it. One
                // worker drains this queue, so waiting here for a channel on
                // a 48-second backoff would hold every other channel's
                // restart behind it — and the queue is arrival-ordered, not
                // due-ordered, so the one behind may be due immediately.
                var wait = due.dueUtc - DateTime.UtcNow;
                if (wait > TimeSpan.FromSeconds(2))
                {
                    _restarts.Enqueue(due);
                    _restartSignal.Release();
                    await Task.Delay(500);
                    continue;
                }
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                if (_disposed) return;

                string? name = null, url = null;
                lock (_lock)
                {
                    if (_disposed) return;
                    var def = _channels.FirstOrDefault(c => ChannelStream(c.Name) == due.stream);
                    if (def is null || !def.Started) continue;      // removed or stopped while waiting
                    if (_liveJobs.TryGetValue(due.stream, out var existing))
                    {
                        bool alive;
                        try { alive = !existing.HasExited; } catch { alive = false; }
                        if (alive) continue;                       // genuinely running again
                        // A dead process still listed: the exit handler could
                        // not take the lock in time to remove it. Left alone
                        // this entry blocks the restart forever, and the
                        // watchdog skips it too because the process has
                        // exited — a channel that never comes back and never
                        // says why.
                        _liveJobs.Remove(due.stream);
                    }
                    name = def.Name; url = def.Url;
                    StartLiveJob(name, url);
                }
            }
            catch (Exception ex) { Log.Warn("ffmpeg", $"restart worker: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Catches the failure mode the exit handler cannot: a job that is still
    /// running and producing nothing.
    ///
    /// rw_timeout should make a dead read error out, but "should" is not a
    /// property to build on — this was added the day three channels sat
    /// wedged with live processes, blocked reads and seven-minute-old
    /// segments. A live channel writes a segment every few seconds; one that
    /// has written nothing for 90 seconds is not slow, it is gone, and
    /// killing it hands it to the exit handler above, which brings it back.
    /// </summary>
    public void CheckLiveJobs()
    {
        List<(string stream, string name)> stale = new();
        lock (_lock)
        {
            foreach (var (stream, p) in _liveJobs)
            {
                // A listed process that has already exited is not "not stale",
                // it is a channel nobody is going to restart: the exit handler
                // failed to clear it, the restart worker refuses to act while
                // the entry exists, and skipping it here completes the circle.
                // Queue it instead — the worker clears the entry and restarts.
                bool gone;
                try { gone = p.HasExited; } catch { gone = true; }
                if (gone)
                {
                    _restarts.Enqueue((stream, DateTime.UtcNow));
                    _restartSignal.Release();
                    EnsureRestartWorker();
                    continue;
                }
                if (_liveStarted.TryGetValue(stream, out var started)
                    && DateTime.UtcNow - started < TimeSpan.FromSeconds(90))
                    continue;                                    // still coming up

                var dir = Path.Combine(_mediaRoot, stream);
                DateTime newest;
                try
                {
                    newest = new DirectoryInfo(dir).EnumerateFiles("seg_*")
                        .Select(f => f.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                }
                catch { continue; }

                // 90s. It was briefly 45s, and wedge reports roughly doubled:
                // a channel riding out an ad splice can legitimately go
                // quiet for most of a minute, and killing it then is not a
                // rescue, it is the interruption. The watchdog is for jobs
                // that are gone, not jobs that are slow.
                if (DateTime.UtcNow - newest > TimeSpan.FromSeconds(90))
                    stale.Add((stream, _channels.FirstOrDefault(c => ChannelStream(c.Name) == stream)?.Name ?? stream));
            }
        }

        foreach (var (stream, name) in stale)
        {
            Log.Warn("ffmpeg", $"channel {name}: running but wrote nothing for 90s — killing the wedged job");
            Process? p;
            lock (_lock) _liveJobs.TryGetValue(stream, out p);
            // Kill only — the Exited handler restarts it. The entry stays in
            // the table so the handler recognises the death as a crash.
            try { if (p is not null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }

    // ---- plumbing -------------------------------------------------------

    /// <summary>
    /// Starts ffmpeg. <paramref name="onExited"/> is wired before the process
    /// launches: attaching it afterwards races a job that dies immediately —
    /// a bad input fails in milliseconds — and a handler added after the event
    /// has already fired is never called, which would strand the job's entry
    /// in the tables it was meant to clean up.
    /// </summary>
    private Process Spawn(IEnumerable<string> args, string label, string? workingDir = null,
        Action<string>? onProgressLine = null, Action<Process>? onExited = null)
    {
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardError = true,
            // only opened when someone is listening: ffmpeg blocks once an
            // unread pipe fills, which would stall the transcode
            RedirectStandardOutput = onProgressLine is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? "",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (onProgressLine is not null)
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) onProgressLine(e.Data); };
        var errTail = new Queue<string>(8);
        // Optional per-job stderr file. The eight-line tail is enough to say
        // why a job exited, and useless for why one stalled — a job that is
        // wedged but alive has said nothing recent, and whatever it did say
        // has long since fallen out of an eight-line window. Diagnostics
        // config turns this on and every line goes to disk with a timestamp,
        // so the ninety seconds before a watchdog kill can be read back.
        var trace = TraceWriterFor(label);
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (errTail)
            {
                if (errTail.Count >= 8) errTail.Dequeue();
                errTail.Enqueue(e.Data);
            }
            if (trace is not null)
            {
                try
                {
                    lock (trace) trace.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {e.Data}");
                }
                catch { /* a diagnostic must never disturb the job it watches */ }
            }
        };
        if (trace is not null)
            p.Exited += (_, _) => { try { lock (trace) trace.Dispose(); } catch { } };
        p.Exited += (_, _) =>
        {
            // This runs on a thread-pool thread: an escaping exception would
            // terminate the whole server. Kill()+Dispose() elsewhere can make
            // ExitCode throw, so guard everything.
            try
            {
                if (_disposed) return;
                string tail;
                lock (errTail) tail = string.Join(" | ", errTail);
                var code = p.ExitCode;
                if (code == 0)
                    Log.Info("ffmpeg", $"{label}: finished");
                else
                    Log.Warn("ffmpeg", $"{label}: exited with code {code}{(tail.Length > 0 ? " — " + tail : "")}");
            }
            catch { /* process already reaped/disposed — nothing to report */ }

            // caller's cleanup, guarded for the same reason
            try { onExited?.Invoke(p); }
            catch (Exception ex) { Log.Warn("ffmpeg", $"{label}: exit handler failed: {ex.Message}"); }
        };
        p.Start();
        // Immediately, before it can do any work: a child that outlives a
        // hard kill of this server becomes a second writer in the same
        // channel directory the next time the server starts. See ProcessJob.
        Services.ProcessJob.Adopt(p);
        RememberPid(p);
        p.BeginErrorReadLine();
        if (onProgressLine is not null) p.BeginOutputReadLine();
        Log.Info("ffmpeg", $"started: {label}");
        return p;
    }

    /// <summary>
    /// Takes the job out of the table and kills it — but does the waiting on
    /// a thread of its own.
    ///
    /// Every caller holds <c>_lock</c>, and the two seconds this used to
    /// spend inside <see cref="KillAndRelease"/> were two seconds during
    /// which no request touching ffmpeg could be served and every Exited
    /// handler piled up behind it. Removing four channels in a row was
    /// enough to bury the thread pool. Removing the entry is the part that
    /// must be atomic with the caller's other bookkeeping; the kill is not.
    /// </summary>
    private static void StopJob(Dictionary<string, Process> jobs, string key)
    {
        if (!jobs.Remove(key, out var p)) return;
        // Kill promptly so nothing else writes to the directory, but hand the
        // waiting and disposing to the pool — the caller's lock is held.
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        _ = Task.Run(() => KillAndRelease(p));
    }

    /// <summary>
    /// Kills a job and releases it. Disposing immediately after Kill() races
    /// the Exited callback, so give it a moment to be raised first. Never
    /// call this while holding <c>_lock</c> — see <see cref="StopJob"/>.
    /// </summary>
    private static void KillAndRelease(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.WaitForExit(2000); } catch { }
        try { p.Dispose(); } catch { }
    }

    /// <summary>
    /// Kills ffmpeg processes left behind by a previous run of this server.
    ///
    /// The job object stops new orphans being created, but it cannot help
    /// with the ones already out there — from a build without it, or from a
    /// kill that beat this process to the punch. They are found by what they
    /// were told to write: any ffmpeg whose command line names this server's
    /// own media root belongs to a server that is no longer running, because
    /// this one has not started anything yet.
    ///
    /// Left alone, such a process shares a channel directory with the one
    /// about to start. Both number segments from seg_00000 and both delete
    /// what falls out of their window, so they erase each other's output and
    /// the channel stutters, repeats, or stops — worsening with every
    /// restart that adds another.
    /// </summary>
    private void KillOrphanedJobs()
    {
        var killed = 0;
        var ours = DateTime.MinValue;
        try { ours = Process.GetCurrentProcess().StartTime; } catch { }

        foreach (var line in ReadPidFile())
        {
            if (!int.TryParse(line, out var pid)) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                // A pid is reused the moment its owner exits, so identity is
                // checked twice before anything is killed: it must still be
                // an ffmpeg, and it must predate this server. Killing a
                // stranger that inherited the number would be far worse than
                // leaving an orphan behind.
                if (!p.ProcessName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase)) continue;
                if (ours != DateTime.MinValue && p.StartTime > ours) continue;
                p.Kill(entireProcessTree: true);
                killed++;
            }
            catch { /* already gone, or not ours to kill */ }
        }

        try { File.Delete(_pidFile); } catch { }

        if (killed > 0)
            Log.Warn("ffmpeg", $"killed {killed} ffmpeg process(es) left over from a previous run — " +
                               "two writers in one channel directory is what makes a channel stutter");
    }

    private IEnumerable<string> ReadPidFile()
    {
        try { return File.Exists(_pidFile) ? File.ReadAllLines(_pidFile) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Records a child's pid so the next run can clean it up if this one is
    /// killed outright. The job object should make this unnecessary on
    /// Windows; it is the belt to that pair of braces, and the only
    /// mechanism at all everywhere else.
    /// </summary>
    /// <summary>
    /// A stderr trace file for one job, when <c>J0KERS_FFMPEG_TRACE</c> names
    /// a directory. Off unless asked for: at ffmpeg's default log level this
    /// is a handful of lines a minute, but the whole point is to be able to
    /// raise that level while hunting something, and nobody wants a server
    /// quietly filling a disk for a fault that was fixed last week.
    ///
    /// Written with AutoFlush, because the interesting case is a process that
    /// has stopped talking — a buffered line still sitting in memory when the
    /// watchdog kills it is exactly the line worth reading.
    /// </summary>
    private static StreamWriter? TraceWriterFor(string label)
    {
        var dir = Environment.GetEnvironmentVariable("J0KERS_FFMPEG_TRACE");
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            Directory.CreateDirectory(dir);
            var safe = string.Concat(label.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
            var path = Path.Combine(dir, $"{safe}-{DateTime.Now:HHmmss}.log");
            return new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Log.Debug("ffmpeg", $"could not open a trace file: {ex.Message}");
            return null;
        }
    }

    private void RememberPid(Process p)
    {
        try { File.AppendAllText(_pidFile, p.Id + Environment.NewLine); }
        catch (Exception ex) { Log.Debug("ffmpeg", $"could not record pid: {ex.Message}"); }
    }

    private void LoadChannels()
    {
        try
        {
            var defs = JsonSidecar.Load<List<ChannelDef>>(_channelsFile, "ffmpeg");
            if (defs is null) return;
            _channels.AddRange(defs);
            var idle = defs.Count(c => !c.Started);
            if (idle > 0) Log.Info("ffmpeg", $"{idle} saved channel(s) idle — start them from the dashboard");
        }
        catch (Exception ex)
        {
            Log.Warn("ffmpeg", $"could not load channels.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Restarts the channels that were running when the server last stopped.
    ///
    /// Called once the listeners are up, not while loading: a pinned free-TV
    /// channel pulls through this server's own proxy, so starting it before
    /// the control API answers means ffmpeg connecting to a port with
    /// nothing behind it. That was survivable while startup took
    /// milliseconds, and stopped being survivable when the TLS setup put
    /// several seconds — and an elevation prompt — in between.
    /// </summary>
    public void RestoreRunningChannels()
    {
        if (!Available) return;
        List<ChannelDef> running;
        lock (_lock) running = _channels.Where(c => c.Started).ToList();
        foreach (var c in running)
        {
            Log.Info("ffmpeg", $"restoring channel: {c.Name}");
            lock (_lock) StartLiveJob(c.Name, c.Url);
        }
    }

    private void SaveChannels() => JsonSidecar.Save(_channelsFile, _channels, "ffmpeg");

    public void Dispose()
    {
        _disposed = true;
        lock (_lock)
        {
            foreach (var key in _seekJobs.Keys.ToList()) StopSeekJobs(key);
            foreach (var key in _vodJobs.Keys.ToList()) StopJob(_vodJobs, key);
            foreach (var key in _liveJobs.Keys.ToList()) StopJob(_liveJobs, key);
        }
    }
}
