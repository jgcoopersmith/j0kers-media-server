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

    /// How many conversions are on the books, readable without the lock.
    /// The folder pills ask about every file they count, and taking the
    /// manager's lock per file put a listing of a few thousand files in
    /// contention with the thing that actually starts and stops encoders.
    /// Nothing converting is the common case, and this answers it for free.
    private volatile int _vodJobCount;

    /// The same table, as an array, readable without the lock — and the
    /// reason the dashboard could stop loading altogether.
    ///
    /// /api/status reports what is converting, and asking for that used to
    /// take _lock. StartVod holds _lock for the whole of starting a
    /// conversion, and that span covers a recursive delete and an ffprobe
    /// with a fifteen second timeout (see StopVodJobs). On a server with a
    /// full queue there is nearly always a start in flight, so the status
    /// poll blocked behind it — and the page puts no timeout on that fetch,
    /// so it sat at "connecting…" for ever while the two-second poll piled
    /// another blocked request on top every two seconds. A server that was
    /// converting perfectly well looked like one with no web interface at all.
    ///
    /// Refreshed under _lock wherever _vodJobs changes, by RefreshVodJobView.
    private volatile KeyValuePair<string, Process>[] _vodJobsView =
        Array.Empty<KeyValuePair<string, Process>>();

    /// <summary>
    /// Republishes the lock-free views of the job table. Call with _lock
    /// held, immediately after any change to <see cref="_vodJobs"/>.
    /// </summary>
    /// <summary>
    /// Conversions that have created their directory but not yet published a
    /// job into the lock-free view.
    ///
    /// StartVod holds _lock across that whole span — a recursive delete, an
    /// ffprobe, argument building — so for a few hundred milliseconds the
    /// directory is on disk and ActiveVodStreams does not mention it. The
    /// cache sweep used to be safe from that only because it took the same
    /// _lock and therefore could not run at all; taking the lock off it
    /// without this set would let it size and delete a directory that is
    /// mid-start. Guarded by _lock, like _vodJobs.
    /// </summary>
    private readonly Dictionary<string, DateTime> _startingVod = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a "starting" mark is honoured. A start is hundreds of
    /// milliseconds; a minute is far beyond it. The bound is there because the
    /// mark is cleared where the job is published, and a start that throws on
    /// the way — Spawn failing — never reaches that line. Without an expiry
    /// that directory would be protected from eviction for the life of the
    /// process, with nothing writing to it.
    /// </summary>
    private static readonly TimeSpan StartingVodGrace = TimeSpan.FromMinutes(1);

    private void RefreshVodJobView()
    {
        _vodJobCount = _vodJobs.Count;
        _vodJobsView = _vodJobs.ToArray();
    }

    /// <summary>
    /// How far each running conversion has got, from ffmpeg's own -progress
    /// stream. DurationSeconds is 0 when the source length couldn't be
    /// probed — a live or malformed input — in which case only the elapsed
    /// position is meaningful and Percent stays null.
    /// </summary>
    public sealed record VodProgress(string Stream, string Title, double DoneSeconds, double DurationSeconds,
                                     DateTime StartedUtc = default)
    {
        public int? Percent => DurationSeconds > 0
            ? Math.Clamp((int)Math.Round(DoneSeconds / DurationSeconds * 100), 0, 100)
            : null;

        /// <summary>
        /// Seconds of encoding still to do, from how fast this job has
        /// actually been going.
        ///
        /// Measured against the wall clock rather than read from ffmpeg's
        /// speed= line, because the rate that matters is the one this job is
        /// achieving on this machine with everything else that is running -
        /// which is exactly what a queue of encodes competing for the same
        /// cores changes. A momentary speed= promises a finish time that the
        /// next four conversions starting immediately make untrue.
        ///
        /// Null until there is something worth dividing: no known duration,
        /// or the first few seconds where the ratio is still noise.
        /// </summary>
        public int? EtaSeconds
        {
            get
            {
                if (DurationSeconds <= 0 || StartedUtc == default) return null;
                var elapsed = (DateTime.UtcNow - StartedUtc).TotalSeconds;
                if (elapsed < 5 || DoneSeconds < 1) return null;
                var rate = DoneSeconds / elapsed;               // encoded seconds per wall second
                if (rate <= 0) return null;
                var left = DurationSeconds - DoneSeconds;
                return left <= 0 ? 0 : (int)Math.Round(left / rate);
            }
        }
    }

    /// <summary>
    /// Told when something goes wrong that a person may want to see — a
    /// conversion exiting non-zero, a source that has gone. Set by Program so
    /// this class stays unaware of where the list lives.
    /// </summary>
    public Action<string, string, string>? OnProblem { get; set; }

    private readonly Dictionary<string, VodProgress> _vodProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Process> _liveJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ChannelDef> _channels = new();
    private bool _disposed;

    public bool Available { get; private set; }

    /// <summary>
    /// Whether ffmpeg can still be started now. Available is decided once,
    /// at startup; an antivirus quarantine or an upgrade that moves the file
    /// takes it away afterwards, and everything that is about to delete or
    /// dequeue something on the strength of a rebuild should ask this
    /// instead. A bare name found on PATH cannot be checked this way and is
    /// taken on trust, as before.
    /// </summary>
    public bool CanLaunch => Available && (!Path.IsPathRooted(FfmpegPath) || File.Exists(FfmpegPath));

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
        _queueSettingsFile = Path.Combine(baseDirectory, "transcode-queue.json");
        FfmpegPath = config.Path;
        Directory.CreateDirectory(_mediaRoot);
        Detect();
        if (Available)
        {
            DiscoverEncoders();
            VideoEncoder = ResolveEncoder(_config.VideoCodec, video: true);
            AudioEncoder = ResolveEncoder(_config.AudioCodec, video: false);
            VerifyNvencOrFallBack();
            Log.Info("ffmpeg", $"transcode codecs: video={VideoEncoder} audio={AudioEncoder} " +
                               $"({_videoEncoders.Count} video / {_audioEncoders.Count} audio encoders available)");
        }
        // before anything of ours starts, so a leftover writer is gone
        // rather than sharing a channel directory with its replacement
        KillOrphanedJobs();
        // Behind the server, not in front of it. This counts unfinished
        // conversions and deletes nothing — it is a line in the log — but it
        // opens a file in every vod-* directory under the media root, and it
        // did so before the dashboard could bind. On this machine's own
        // library (811 conversions) that was 8.5 seconds of a 10.8 second
        // start, and all of it was spent with nothing on screen at all:
        // launched from the desktop icon there is no console, no window and
        // no tray icon until the browser opens, so a server that was working
        // perfectly looked like one that had not started. It says the same
        // thing a moment later now, with the server already up.
        // Off the startup path: it reads every conversion directory, and
        // nothing waits on the answer.
        _ = Task.Run(() => { CleanUpIncompleteVodDirs(); BackfillConversionHeights(); });
        MarkExistingConversionsKeptOnce(Path.Combine(baseDirectory, "vod-keep-migrated"));
        LoadChannels();
        LoadQueueSettings();
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
            using var p = Services.ProcessJob.Start(psi);
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

    /// <summary>True when the chosen video encoder runs on the GPU via NVENC.</summary>
    public bool IsNvenc => VideoEncoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase);

    /// <summary>How many times a file has been put back for want of a GPU session.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _nvencRetries =
        new(StringComparer.OrdinalIgnoreCase);

    private const int MaxNvencRetries = 20;

    private int _nvencRefusals;

    /// <summary>
    /// The most conversions this card has actually run at once without
    /// refusing a session. Learned, because it cannot be looked up: the
    /// number moves with the driver, and with whatever else on the machine is
    /// encoding. Starts unlimited and only ever comes down.
    /// </summary>
    private int _gpuSessionCeiling = int.MaxValue;

    /// <summary>
    /// How many conversions may run at once — the owner's setting, held under
    /// whatever the GPU has proved willing to serve. Retrying alone was not
    /// enough: it recovers the file but goes on asking for a session that is
    /// not there, so the queue keeps failing at the same wall. This stops it
    /// being asked for.
    /// </summary>
    private int EffectiveMaxConcurrentVod =>
        IsNvenc ? Math.Max(1, Math.Min(MaxConcurrentVod, _gpuSessionCeiling)) : MaxConcurrentVod;

    /// <summary>
    /// Whether ffmpeg failed because the card would not give it an encoder
    /// session, rather than because there was anything wrong with the file.
    ///
    /// A GeForce card serves a limited number of NVENC sessions at once, and
    /// the limit is not a number this server can know: it moves with the
    /// driver, and with what else on the machine is encoding. Ask for one too
    /// many and ffmpeg exits immediately with a message about the encoder
    /// failing to open, which reads exactly like a broken source file and is
    /// nothing of the kind.
    ///
    /// Only NVENC's own words for it count, and only from an NVENC job.
    /// Measured on this server's machine (ffmpeg 8.1.2, driver 616.56, RTX
    /// 4080 SUPER): the 13th session at once was refused, and ffmpeg said
    /// "OpenEncodeSessionEx failed: incompatible client key (21)" first,
    /// then nine more lines. Those nine are what this used to match -
    /// "Error while opening encoder", "Could not open encoder before EOF" -
    /// and they are not about sessions at all. An 8192-wide source, which
    /// h264_nvenc cannot take, prints exactly the same ones, "No capable
    /// devices found" included. So a file that could never convert was put
    /// back twenty times under a message blaming the GPU, and each attempt
    /// lowered the ceiling another step for the rest of the run; with
    /// libx264 it happened with no GPU involved at all.
    ///
    /// They were matched because the real line never arrived: the eight-line
    /// stderr tail had always scrolled it away by the time the job exited.
    /// It was not the log level, as this used to say. The tail now keeps it -
    /// see StderrTail.
    /// </summary>
    internal static bool IsGpuSessionRefusal(bool nvenc, string stderr) =>
        nvenc && stderr.Contains(NvencSessionRefused, StringComparison.OrdinalIgnoreCase);

    /// <summary>What NVENC says, and says only, when it will not open another session.</summary>
    private const string NvencSessionRefused = "OpenEncodeSessionEx failed";

    /// <summary>The learned ceiling, for tests: unlimited is int.MaxValue.</summary>
    internal int GpuSessionCeiling
    {
        get => _gpuSessionCeiling;
        set => _gpuSessionCeiling = value;
    }

    /// <summary>
    /// Puts a file back on the queue after the GPU refused it a session.
    ///
    /// Losing the conversion was the alternative, and it is what happened: 36
    /// files failed this way in one run, each in under half a second, each
    /// reported as an ffmpeg error and none of them retried. The queue emptied
    /// and the work was simply gone.
    ///
    /// It goes to the back, so the jobs already running get to finish and free
    /// the sessions this one needs, and it is bounded — a file that cannot be
    /// converted for some other reason must not circle for ever.
    /// </summary>
    private void RequeueForGpuSession(string file, string label, int height = 0)
    {
        var tries = _nvencRetries.AddOrUpdate(file, 1, (_, n) => n + 1);
        if (tries > MaxNvencRetries)
        {
            Log.Warn("ffmpeg", $"{label}: the GPU would not give it an encoder session after "
                             + $"{MaxNvencRetries} attempts — giving up on this one. Lower "
                             + "\"how many at a time\" in the Transcodes panel.");
            return;
        }
        // Under _pumpLock, because RemoveFromVodQueue drains the whole queue
        // and refills it from a snapshot: an enqueue landing inside that window
        // is drained away and never put back, and SaveQueueState then writes
        // the loss down. ConcurrentQueue makes each operation safe on its own;
        // it cannot make a snapshot-and-refill atomic.
        lock (_pumpLock)
        {
            if (height > 0) _vodQueueHeights[file] = height;
            _vodQueue.Enqueue(file);
        }
        Log.Info("ffmpeg", $"{label}: no free GPU encoder session — put back in the queue "
                         + $"(attempt {tries}). Conversions already running will free one.");
        SaveQueueState();
    }

    internal enum GpuRefusalRoute { Report, Batch, RetryHere }

    /// <summary>
    /// Where a conversion the GPU refused a session goes next.
    ///
    /// The batch queue starts everything it holds as a conversion to keep, so
    /// it is the right place only for one that was to be kept already - at
    /// whatever height, which its entries now carry. A play sent there came
    /// back as something nobody asked for: a 720p play for a television became
    /// a conversion marked never to be evicted, and at full resolution, while
    /// the 720p stream the TV was waiting for never appeared. A play is tried
    /// again as itself, a little later. A viewer's play is only reported (see
    /// requeueOnGpuRefusal).
    /// </summary>
    internal static GpuRefusalRoute RouteGpuRefusal(bool keep, int height, bool requeueOnGpuRefusal) =>
        !requeueOnGpuRefusal ? GpuRefusalRoute.Report
        : keep ? GpuRefusalRoute.Batch
        : GpuRefusalRoute.RetryHere;

    /// <summary>How long a refused conversion waits before trying again. Shortened by tests only.</summary>
    internal static TimeSpan GpuRetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Starts a refused conversion again, as the same request - same height,
    /// same keep - once conversions already running have had a moment to free
    /// a session. Bounded by the same count as the batch queue's retries.
    /// </summary>
    internal void RetryAfterGpuRefusal(string stream, string file, int height, bool keep, string label)
    {
        var tries = _nvencRetries.AddOrUpdate(file, 1, (_, n) => n + 1);
        if (tries > MaxNvencRetries)
        {
            Log.Warn("ffmpeg", $"{label}: the GPU would not give it an encoder session after "
                             + $"{MaxNvencRetries} attempts — giving up on this one.");
            return;
        }
        Log.Info("ffmpeg", $"{label}: no free GPU encoder session — trying again in "
                         + $"{(int)GpuRetryDelay.TotalSeconds}s as it was asked for"
                         + (height > 0 ? $" ({height}p)" : "") + $" (attempt {tries})");
        // Cancellable by the stream's name. In the gap there is no job and no
        // queue entry, so without this, cancelling or deleting the conversion
        // meanwhile did nothing - and the retry brought it back, twenty times
        // over if the card kept saying no.
        var cts = new CancellationTokenSource();
        _pendingRetries.AddOrUpdate(stream, cts, (_, old) => { old.Cancel(); return cts; });
        _ = Task.Delay(GpuRetryDelay, cts.Token).ContinueWith(t =>
        {
            _pendingRetries.TryRemove(new KeyValuePair<string, CancellationTokenSource>(stream, cts));
            if (t.IsCanceled || _disposed) return;
            try { StartVod(file, height, keep, requeueOnGpuRefusal: true); }
            catch (Exception ex) { Log.Warn("ffmpeg", $"{label}: could not start the retry: {ex.Message}"); }
        }, TaskScheduler.Default);
    }

    /// <summary>Retries waiting to start again after a GPU refusal, by stream. See RetryAfterGpuRefusal.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _pendingRetries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Calls off a retry waiting for this stream, if there is one. True if there was.</summary>
    private bool CancelPendingRetry(string stream)
    {
        if (!_pendingRetries.TryRemove(stream, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    /// <summary>
    /// Proves NVENC can actually open before anything depends on it, and
    /// falls back to software if it cannot.
    ///
    /// Being listed by <c>-encoders</c> only says the binary was built with
    /// it, not that this machine can run it. NVENC fails at the moment a
    /// session is opened, and for reasons that have nothing to do with the
    /// configuration: a driver older than the build expects (measured here,
    /// "the minimum required Nvidia driver for nvenc is 610.00 or newer"),
    /// no card, a card without an encoder, a remote session that cannot
    /// reach one.
    ///
    /// Without this check every one of those becomes hundreds of failed
    /// conversions with an ffmpeg error nobody sees, on a server that was
    /// working the day before. One two-frame encode at startup turns that
    /// into a line in the log and a working server on the software path.
    /// </summary>
    private void VerifyNvencOrFallBack()
    {
        if (!IsNvenc) return;
        var ok = RunFfmpeg(new[]
        {
            "-f", "lavfi", "-i", "color=c=black:s=256x144:r=25",
            "-frames:v", "2", "-c:v", VideoEncoder, "-f", "null", "-",
        }, timeoutMs: 20_000);

        if (ok)
        {
            Log.Info("ffmpeg", $"{VideoEncoder} is available — video encoding runs on the GPU");
            return;
        }

        var fallback = ResolveEncoder("h264", video: true);
        Log.Warn("ffmpeg", $"{VideoEncoder} could not open a session on this machine — " +
                           $"falling back to {fallback}. The card, its driver, or the session " +
                           "limit is the usual cause; conversions carry on in software.");
        VideoEncoder = fallback;
    }

    /// <summary>x264/x265 take preset+crf; other encoders get sane defaults of their own.</summary>
    private string[] VideoQualityArgs() => VideoEncoder switch
    {
        "libx264" or "libx265" => new[] { "-preset", _config.Preset, "-crf", Inv(_config.Crf) },
        "libvpx-vp9" => new[] { "-crf", Inv(_config.Crf), "-b:v", "0" },
        "libaom-av1" or "libsvtav1" or "librav1e" => new[] { "-crf", Inv(_config.Crf) },

        // NVENC shares none of x264's vocabulary, which is why naming the
        // encoder alone was never enough to switch to it: "veryfast" is not
        // one of its presets and it has no -crf at all, so the arguments
        // above are rejected outright rather than ignored.
        //
        // The quality number is its own setting rather than a reuse of crf,
        // because the two scales are not the same picture. Measured on this
        // library, nvenc cq 28 lands where x264 crf 23 does on size while
        // scoring slightly better; cq 25 is a real step up in quality. Making
        // crf do both jobs would mean switching encoders silently moved
        // quality, in whichever direction the number happened to mean.
        var nv when nv.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) =>
            new[] { "-preset", _config.NvencPreset, "-cq", Inv(_config.NvencCq), "-forced-idr", "1" },

        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// Keyframe placement, which is not the same request for every encoder.
    ///
    /// <c>-sc_threshold</c> is an x264 private option. NVENC does not have it
    /// and says so on every job, which is noise in the log of every
    /// conversion — and it is not needed there either, because NVENC does not
    /// add scene-change keyframes on top of the forced ones.
    ///
    /// <c>-forced-idr</c> is the opposite: without it NVENC treats
    /// -force_key_frames as a suggestion and writes its own GOP instead.
    /// Measured, that turned 6-second segments into 10.43-second ones, which
    /// silently breaks the arithmetic the whole-film playlist uses to say
    /// where a seek should land. It is set in VideoQualityArgs above, beside
    /// the rest of what NVENC needs, so no path can add the forced keyframes
    /// without it.
    /// </summary>
    private string[] KeyframeArgs(int everySeconds)
    {
        var a = new List<string> { "-force_key_frames", $"expr:gte(t,n_forced*{everySeconds})" };
        if (!IsNvenc) a.AddRange(new[] { "-sc_threshold", "0" });
        return a.ToArray();
    }

    /// <summary>
    /// Input-side arguments: hardware decoding, where it actually pays.
    ///
    /// Only for NVENC, and only for the codecs the card decodes well. The
    /// point is not that decoding is slow — it is not, and that was the
    /// mistake the last attempt at this made. Measured here: decoding 120
    /// seconds of XviD costs 0.9 s of CPU against 7.9 s to encode it, and
    /// asking NVDEC to do it made the whole job *slower* (2.8 s against
    /// 2.3 s) because the setup cost more than the decode it replaced.
    ///
    /// What it is for is keeping frames on the card. With an NVENC encoder
    /// the decoded frames are already where they need to be, so nothing
    /// crosses PCIe; feeding the same encoder from a CPU decoder pays for
    /// that copy on every frame. That only holds for HEVC and H.264, which
    /// is where the expensive decodes are anyway — a 1080p 10-bit HEVC
    /// source costs 14.5 s of CPU to decode against 3.9 s on the card.
    ///
    /// Anything else decodes on the CPU exactly as before.
    /// </summary>
    private string[] HardwareDecodeArgs(string? sourceFile)
    {
        if (!IsNvenc || sourceFile is null) return Array.Empty<string>();
        var (video, _) = ProbeCodecs(sourceFile);
        return video is "hevc" or "h264"
            ? new[] { "-hwaccel", "cuda" }
            : Array.Empty<string>();
    }

    /// <summary>
    /// How long a job ran, in the units somebody reading a log actually wants
    /// — "4m 12s", not "00:04:12.3471".
    /// </summary>
    private static string Elapsed(DateTime startedUtc)
    {
        var d = DateTime.UtcNow - startedUtc;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;      // clock moved under us
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes:00}m {d.Seconds:00}s";
        if (d.TotalMinutes >= 1) return $"{d.Minutes}m {d.Seconds:00}s";
        return $"{d.TotalSeconds:0.0}s";
    }

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
    /// <summary>
    /// Whether this file can be packaged for HLS without being encoded again.
    ///
    /// A conversion exists to put a film in a shape the player can stream, and
    /// for a file whose codecs already match the ones being asked for that is
    /// a container change and nothing more. Encoding it anyway costs three
    /// things and buys none of them back: hours of GPU, a second lossy
    /// generation on top of whatever the source already lost, and - through
    /// AudioArgs - a 5.1 soundtrack folded down to stereo.
    ///
    /// Measured here: Avengers Endgame, already h264/aac at 2.38 Mbps, was
    /// re-encoded to h264 at 2.30 Mbps in 13 minutes. Nothing about it needed
    /// converting; the player only needed it in segments.
    ///
    /// Scaling is the one thing that genuinely requires an encode, so a
    /// request for a specific height never takes this path.
    /// </summary>
    /// <summary>
    /// Which of the two streams the source already carries in the form being
    /// asked for, and can therefore be packaged rather than encoded.
    ///
    /// Decided per stream, because the two questions are independent and
    /// answering them together was throwing away work. A film that is already
    /// h264 but carries mp3 audio failed the combined test and had *both*
    /// streams re-encoded: a generation of picture destroyed to fix a
    /// soundtrack, 2m51s of GPU for a file whose video needed nothing done to
    /// it at all. Measured on Alice In Wonderland (H.264).mp4 — h264 video,
    /// mp3 audio, 352p.
    ///
    /// Scaling is a video operation, so a requested height rules out copying
    /// the picture and says nothing about the sound.
    /// </summary>
    internal (bool video, bool audio) CopyableStreams(string file, int height)
    {
        var (video, audio, _) = CopyDecision(file, height);
        return (video, audio);
    }

    /// <summary>
    /// CopyableStreams, plus whether the only picture is cover art - both from
    /// one probe. StartVod and seek-ahead run this under the lock that every
    /// status and playlist request waits on; asking ffprobe the same file
    /// twice there was a second process launch for nothing.
    /// </summary>
    private (bool video, bool audio, bool coverOnly) CopyDecision(string file, int height)
    {
        var s = ProbeStreamDetails(file);
        // "copy" as the configured encoder is handled by its own branch.
        if (VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase)) return (false, false, s.CoverArtOnly);
        // Unreadable means encode, exactly as before — never guess in the
        // direction that hands a player a stream it cannot decode.
        var copyVideo = height <= 0
                        && s.Video is not null
                        && CodecFamily(s.Video) == CodecFamily(VideoEncoder)
                        && PictureCopies(s.Video, s.PixFmt);
        var copyAudio = s.Audio is not null
                        && CodecFamily(s.Audio) == CodecFamily(AudioEncoder)
                        && SoundCopies(s.Audio, s.SampleRate);
        return (copyVideo, copyAudio, s.CoverArtOnly);
    }

    /// <summary>
    /// Whether a picture already in the right codec is also in a form that
    /// plays - which the codec name alone does not say.
    ///
    /// The encode path always writes 8-bit 4:2:0 (-pix_fmt yuv420p), and for
    /// h264 that is not a detail: 10-bit H.264 ("Hi10P", common in anime
    /// releases) and 4:2:2 or 4:4:4 H.264 are h264 by name and play almost
    /// nowhere - not in Chrome, not on a television. Copying matched on the
    /// name, so those went through untouched and the result was counted as a
    /// finished conversion that no player could show. yuvj420p is the same
    /// picture with full-range levels, what phones record, and plays.
    ///
    /// HEVC keeps its 10-bit: Main10 is what HEVC-capable sets decode, and it
    /// is where HDR lives, so encoding it down to 8 bits would lose picture
    /// for nothing. Anything else is copied as before, as long as the probe
    /// could read what it is.
    /// </summary>
    private static bool PictureCopies(string codec, string? pixFmt) => CodecFamily(codec) switch
    {
        "h264" => pixFmt is "yuv420p" or "yuvj420p",
        "hevc" => pixFmt is "yuv420p" or "yuvj420p" or "yuv420p10le",
        _ => pixFmt is not null,
    };

    /// <summary>
    /// The same question for sound. AAC at 16 or 24 kHz is AAC by name, and
    /// mute on a television - which is why AudioArgs encodes at 48 kHz (see
    /// there for the measurement). Copying carried those rates straight
    /// through. 44.1 and 48 kHz are what every decoder takes.
    /// </summary>
    private static bool SoundCopies(string codec, int sampleRate) =>
        CodecFamily(codec) != "aac" || sampleRate is 44100 or 48000;

    /// <summary>What <see cref="CopyableStreams"/> needs to know about a file, in one probe.</summary>
    /// <param name="CoverArtOnly">
    /// Its only "video" is a picture attached to the audio - an album cover.
    /// See <see cref="IsCoverArtOnly"/>.
    /// </param>
    private readonly record struct StreamDetails(string? Video, string? PixFmt, string? Audio, int SampleRate,
                                                 bool CoverArtOnly = false);

    /// <summary>
    /// Whether a file's only picture is cover art: an MP3 or M4A with an album
    /// cover attached. ffmpeg counts that as a video stream of one frame, and
    /// without being told otherwise it converted one: the cover became a
    /// one-frame video, the playlist listed the whole song as 0 seconds of
    /// segments (measured: 0 against 180), and a player had nothing to seek
    /// through. Such a file converts as the audio it is.
    /// </summary>
    internal bool IsCoverArtOnly(string file) => CopyDecision(file, 0).coverOnly;

    /// <summary>
    /// First video stream's codec and pixel format, first audio stream's
    /// codec and sample rate. JSON rather than csv: the two kinds of stream
    /// have different fields, and csv drops the names that would say which
    /// value is which.
    /// </summary>
    private StreamDetails ProbeStreamDetails(string file)
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
            foreach (var a in new[] { "-v", "error",
                                      "-show_entries", "stream=codec_type,codec_name,pix_fmt,sample_rate:stream_disposition=attached_pic",
                                      "-of", "json", file })
                psi.ArgumentList.Add(a);
            var run = Services.ProcessJob.Run(psi, 15_000);   // both pipes, real timeout
            if (run is null) return default;
            using var doc = System.Text.Json.JsonDocument.Parse(run.Value.StdOut);
            string? video = null, pixFmt = null, audio = null;
            var rate = 0;
            var cover = false;
            if (doc.RootElement.TryGetProperty("streams", out var streams))
                foreach (var st in streams.EnumerateArray())
                {
                    var type = st.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    var name = st.TryGetProperty("codec_name", out var n) ? n.GetString() : null;
                    // A picture attached to the audio is not the film's picture.
                    if (type == "video" && st.TryGetProperty("disposition", out var disp)
                        && disp.TryGetProperty("attached_pic", out var ap)
                        && ap.ValueKind == System.Text.Json.JsonValueKind.Number && ap.GetInt32() == 1)
                    {
                        cover = true;
                        continue;
                    }
                    if (type == "video" && video is null)
                    {
                        video = name;
                        pixFmt = st.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() : null;
                    }
                    else if (type == "audio" && audio is null)
                    {
                        audio = name;
                        // a string in ffprobe's JSON ("48000"); a number read too, in case
                        if (st.TryGetProperty("sample_rate", out var sr))
                            _ = sr.ValueKind == System.Text.Json.JsonValueKind.Number
                                ? sr.TryGetInt32(out rate)
                                : int.TryParse(sr.GetString(), System.Globalization.NumberStyles.Integer,
                                               System.Globalization.CultureInfo.InvariantCulture, out rate);
                    }
                }
            return new StreamDetails(video, pixFmt, audio, rate, CoverArtOnly: cover && video is null && audio is not null);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// The codec behind an encoder name, so a source can be compared with what
    /// is being asked for: h264_nvenc, libx264 and h264 are all h264.
    /// </summary>
    private static string CodecFamily(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.StartsWith("lib", StringComparison.Ordinal)) n = n[3..];
        var cut = n.IndexOf('_');            // h264_nvenc -> h264
        if (cut > 0) n = n[..cut];
        return n switch { "x264" => "h264", "x265" or "h265" => "hevc", _ => n };
    }

    private bool NeedsFmp4(string? sourceFile, bool copyingVideo = false, bool copyingAudio = false)
    {
        var copyVideo = copyingVideo || VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase);
        var copyAudio = copyingAudio || AudioEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Containers a browser's &lt;video&gt; element opens directly. VLC opens far
    /// more than this, but the dashboard player is the binding constraint and
    /// being wrong here means a black rectangle rather than a wait.
    /// </summary>
    private static readonly HashSet<string> DirectPlayExt =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".webm", ".mov" };

    private static readonly HashSet<string> DirectPlayVideo =
        new(StringComparer.OrdinalIgnoreCase) { "h264", "vp9", "vp8", "av1" };

    private static readonly HashSet<string> DirectPlayAudio =
        new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "opus", "vorbis" };

    /// <summary>
    /// Can this file be handed over as it is, instead of being converted?
    ///
    /// The question the Transcode window was never asking. A file already in a
    /// container and codecs a player opens needs nothing done to it — encoding
    /// it produces a second copy of something that was ready, costs a
    /// generation of picture, and makes somebody wait for both. The server has
    /// always known how to send a file untouched; only the television was ever
    /// offered it.
    ///
    /// Deliberately narrower than what VLC manages. Guessing wrong in this
    /// direction gives a browser a file it cannot decode, which looks like a
    /// broken server rather than a slow one, so anything uncertain converts.
    /// HEVC is left out for exactly that reason: Safari plays it, Chrome
    /// mostly does not.
    /// </summary>
    public bool CanPlayDirectly(string file)
    {
        try
        {
            if (!DirectPlayExt.Contains(Path.GetExtension(file))) return false;
            var s = ProbeStreamDetails(file);
            return PlayableAsIs(file, s.Video, s.Audio, s.PixFmt);
        }
        catch { return false; }
    }

    /// <summary>
    /// The same decision, from codecs somebody else already knows, so a
    /// listing can answer it without launching anything.
    ///
    /// This exists because the live version was called once per file while
    /// building a directory listing — and once per file in every sub-folder
    /// for the summary pills. ProbeCodecs has no cache and starts an ffprobe
    /// every time, so opening a folder became thousands of process launches
    /// and the Transcode window's Up and Refresh stopped answering.
    /// </summary>
    public static bool PlayableAsIs(string file, string? video, string? audio, string? pixFmt)
    {
        if (!DirectPlayExt.Contains(Path.GetExtension(file))) return false;
        if (video is null || audio is null) return false;   // unread: convert, as before
        // And a picture a browser can actually decode: 10-bit or 4:2:2 H.264
        // is h264 by name and a black rectangle in Chrome. The sound is not
        // held to TvCodecs' rates here - a browser plays AAC at any rate; it
        // is televisions that do not, and they are not handed this.
        return DirectPlayVideo.Contains(CodecFamily(video))
            && DirectPlayAudio.Contains(CodecFamily(audio))
            && TvCodecs.PlayablePicture(CodecFamily(video), pixFmt);
    }

    /// <summary>
    /// First video and audio codec names of a media file, via ffprobe - the
    /// same probe as everything else here, so an attached album cover is never
    /// taken for the picture. This read its own csv and did take it: with the
    /// cover listed first, the container decision below judged a PNG while the
    /// real picture was being copied (see NeedsFmp4).
    /// </summary>
    private (string? video, string? audio) ProbeCodecs(string file)
    {
        var s = ProbeStreamDetails(file);
        return (s.Video, s.Audio);
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
            var run = Services.ProcessJob.Run(psi, 15_000);   // both pipes, real timeout
            if (run is null) return 0;
            var output = run.Value.StdOut.Trim();
            return double.TryParse(output, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? seconds : 0;
        }
        catch
        {
            return 0;
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
                using var p = Services.ProcessJob.Start(probe);
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

        // A copy shipped next to our own executable — what a self-contained
        // install carries so a machine with no ffmpeg on PATH still works.
        var beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside)) yield return beside;

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
    public string? VodStreamName(string file, int height = 0)
    {
        FileInfo info;
        try { info = new FileInfo(file); if (!info.Exists) return null; }
        catch { return null; }

        // scaling is impossible when the video is remuxed, so a requested
        // height must not fork the cache (it produced N identical copies
        // labelled with resolutions they didn't have)
        if (VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase)) height = 0;

        // readable link: filename slug + optional quality + short hash for uniqueness
        var slug = Slugify(Path.GetFileNameWithoutExtension(info.Name));
        string Named(string videoEncoder)
        {
            // cache key: same file+size+mtime+height+codecs → same output dir, converted once
            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
                $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{height}|{videoEncoder}|{AudioEncoder}")))[..8].ToLowerInvariant();
            return $"vod-{slug}{(height > 0 ? $"-{height}p" : "")}-{key}";
        }

        var current = Named(VideoEncoder);
        if (Directory.Exists(Path.Combine(_mediaRoot, current))) return current;

        // A conversion already on disk is a conversion, whichever encoder made
        // it.
        //
        // The encoder name is part of the key, so changing it renames every
        // conversion this server has ever produced — not on disk, where they
        // sit untouched, but in the only place that looks for them. Switching
        // to h264_nvenc did exactly that: 905 finished conversions became
        // invisible in one restart, the library reported "needs converting"
        // for all of them, and converting again would have re-encoded the lot
        // over the top of work that was already there.
        //
        // The key has to keep the encoder, because a copy made by a different
        // one is a different copy and asking for h265 must not hand back an
        // h264 conversion. But looking for the file is a different question
        // from naming a new one: if the name this encoder would use is not on
        // disk and a name a previous encoder would have used is, that is the
        // conversion, and it is found rather than remade.
        // The same codec made another way - libx264 and h264_nvenc, libx265
        // and hevc_nvenc - and nothing else. Without this the loop took any
        // encoder on the list, which is the opposite of the paragraph above:
        // switching from HEVC to h264 so a browser could play the library
        // found every HEVC conversion again, called each one done, and the
        // new setting never applied to a single file.
        //
        // The codec the owner CONFIGURED counts as well as the one running.
        // They differ when the card fails its check at startup and hevc_nvenc
        // falls back to software h264 for the day - and a library converted
        // in HEVC, which is still what was asked for, must not vanish and be
        // re-encoded because of it. Copy mode asks for no codec at all, so
        // any earlier conversion still stands, as it always did.
        var copying = VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase);
        var running = CodecFamily(VideoEncoder);
        var configured = CodecFamily(_config.VideoCodec);
        foreach (var prior in PriorVideoEncoders)
        {
            if (prior.Equals(VideoEncoder, StringComparison.OrdinalIgnoreCase)) continue;
            var family = CodecFamily(prior);
            if (!copying && family != running && family != configured) continue;
            var name = Named(prior);
            var priorDir = Path.Combine(_mediaRoot, name);
            if (!Directory.Exists(priorDir)) continue;
            if (family == running) return name;
            // A codec other than the one running now - the configured one on
            // a day the card fell back, or anything under copy mode - is
            // taken only as a FINISHED conversion. An unfinished folder under
            // that name would be cleared and converted into by today's encoder,
            // and the h264 inside would later be found as the HEVC conversion
            // and counted done.
            try
            {
                var playlist = Path.Combine(priorDir, "index.m3u8");
                if (File.Exists(playlist) && IsFinished(priorDir, File.ReadAllText(playlist))) return name;
            }
            catch { /* unreadable: not taken */ }
        }
        return current;
    }

    /// <summary>
    /// Encoders this server has shipped with, newest first. Only used to find
    /// conversions made before the encoder changed — see VodStreamName. A name
    /// missing from here costs a re-conversion, not a fault, so it is a short
    /// list of what has actually been default rather than every encoder ffmpeg
    /// has.
    /// </summary>
    private static readonly string[] PriorVideoEncoders =
    {
        "libx264", "h264_nvenc", "libx265", "hevc_nvenc",
    };

    /// <summary>
    /// The file that marks a conversion as one the owner asked for, rather
    /// than one this server made by itself to play something.
    ///
    /// Both end up in the same directory, and until now the cache could not
    /// tell them apart, so it deleted whichever was least recently played.
    /// That is right for a copy made on the fly to get a film onto a
    /// television. It is completely wrong for a library the owner sat down
    /// and converted on purpose: an overnight run produced hundreds of files
    /// and the cache quietly ate the older half while the newer half was
    /// still being made, so the count went *down* over an hour of work with
    /// nothing in the interface to say why.
    ///
    /// A file on disk rather than a flag in memory, so it survives restarts,
    /// and so it is visible to anyone looking in the folder wondering which
    /// of these the server considers disposable.
    /// </summary>
    public const string KeepMarker = "keep";

    /// <summary>Whether this conversion is marked as one to keep for good.</summary>
    private static bool IsKept(string dir) => File.Exists(Path.Combine(dir, KeepMarker));

    private static void MarkKept(string dir, string stream)
    {
        try
        {
            var marker = Path.Combine(dir, KeepMarker);
            if (File.Exists(marker)) return;
            File.WriteAllText(marker,
                "This conversion was requested from the Transcodes window and is never "
                + "deleted to reclaim cache space. Delete this file to make it disposable.\r\n");
        }
        catch (Exception ex)
        {
            // Worth saying out loud: without the marker this conversion is
            // evictable, which is the whole thing being prevented here.
            Log.Warn("ffmpeg", $"could not mark {stream} as one to keep: {ex.Message}");
        }
    }

    /// <param name="requeueOnGpuRefusal">
    /// Whether a job the GPU refuses an encoder session is put back on the
    /// batch queue. True for everything the server's administrator starts -
    /// the queue itself, their plays, a retranscode - which is how it always
    /// behaved. False only for a viewer account's play: the batch queue is the
    /// administrator's, and a viewer being able to fill it (and lower the
    /// ceiling it runs at) is what this is for. It used to key on
    /// <paramref name="keep"/>, which is also false for a retranscode, so the
    /// owner's rebuilt conversion could be lost.
    /// </param>
    public (string stream, bool ready) StartVod(string file, int height = 0, bool keep = false,
                                                bool requeueOnGpuRefusal = true)
    {
        if (!Available) throw new InvalidOperationException("ffmpeg is not available");
        if (_disposed) throw new ObjectDisposedException(nameof(FfmpegManager));
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException("no such file", file);

        var stream = VodStreamName(file, height)
            ?? throw new FileNotFoundException("no such file", file);
        if (VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase)) height = 0;
        var dir = Path.Combine(_mediaRoot, stream);
        var playlist = Path.Combine(dir, "index.m3u8");

        string started;
        lock (_lock)
        {
            // Checked again under the lock Dispose takes to stop everything: a
            // start that was waiting on it must not launch a job after that.
            if (_disposed) throw new ObjectDisposedException(nameof(FfmpegManager));
            // Listed at all, exited or not: a job leaves the table once its
            // exit has been judged, and until then its end marker may yet be
            // found not to mean the end. See IsFinished.
            var running = _vodJobs.ContainsKey(stream);
            if (running)
                return (stream, File.Exists(playlist));   // playable once segments exist

            // "Finished earlier" must mean *finished*, not merely "a playlist
            // file is present". An interrupted run (a crash, or the server
            // stopped mid-encode) leaves a partial index.m3u8 with no
            // EXT-X-ENDLIST; treating that as done is why a re-run "did
            // nothing". Require the end marker, and clear any partial so the
            // conversion restarts cleanly instead of being pointed at a stale,
            // half-written directory.
            // And the marker has to have the film behind it: an input that
            // ended early gets one too. See Finished.
            var complete = File.Exists(playlist) && IsFinished(dir, File.ReadAllText(playlist));
            if (complete)
            {
                // Asking for it deliberately promotes a copy the server had
                // made for itself: converting a library the second time round
                // should not be punished for the first run having happened to
                // play something.
                if (keep) MarkKept(dir, stream);
                Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow); // LRU touch
                return (stream, true); // finished earlier
            }
            if (Directory.Exists(dir))
            {
                // Say it. This is a recursive delete of work the owner may
                // have waited an hour for, and it only ever spoke up when it
                // *failed* — so "did it throw away what I already had, or
                // convert something it had never seen?" could not be answered
                // from the log at all. It had to be reconstructed from
                // directory timestamps and a scan of every source.txt on disk.
                var going = 0L;
                var files = 0;
                try
                {
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles()) { going += f.Length; files++; }
                }
                catch { /* sizing is for the message, not the decision */ }
                Log.Info("ffmpeg", $"replacing an unfinished {stream}: deleting {files} file(s), {Bytes(going)} "
                                   + "— it had no end marker, or covered too little of the film to be "
                                   + "the whole of it, so there was nothing to resume");
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { Log.Warn("ffmpeg", $"could not clear partial {stream}: {ex.Message}"); }
            }

            // Eviction is deliberately not done here. Sizing the cache means
            // stat-ing every file in it, and going over budget means deleting
            // directories of segments — seconds of work on a full cache. Under
            // this lock that stalls /api/status, /api/channels and every
            // playlist request behind a click, so the whole dashboard freezes
            // just as someone starts a film. It has nothing to do with
            // starting this conversion, so it runs after, off the lock.
            Directory.CreateDirectory(dir);
            // Made again from here, so no longer known to be done - and no
            // earlier verdict that its input ran out can stand. The delete
            // above normally takes that file with it, but a delete that stops
            // part way (a player holding a segment) can leave it behind, and
            // it would then damn the whole new conversion.
            _vodDone.TryRemove(stream, out _);
            try { File.Delete(Path.Combine(dir, EndedEarlyMarker)); }
            catch (Exception ex) { Log.Warn("ffmpeg", $"could not clear the old ended-early note in {stream}: {ex.Message}"); }
            // From here the directory exists and no job names it yet. The
            // cache sweep no longer takes _lock, so without this it could size
            // and delete a conversion that is still being set up — the span
            // below includes an ffprobe and can run for hundreds of
            // milliseconds. Cleared where the job is published, and in the
            // catch below if the start never gets that far.
            _startingVod[stream] = DateTime.UtcNow;
            // Marked before a single segment is written, not after the job
            // finishes: the sweep that follows every other conversion's start
            // runs while this one is still encoding, and an unmarked directory
            // is one the sweep is entitled to delete.
            if (keep) MarkKept(dir, stream);
            // remember the source so subtitles can be found for this stream
            try { File.WriteAllText(Path.Combine(dir, "source.txt"), info.FullName); } catch { }
            // And the height it was asked for, because the NAME cannot be
            // trusted to say. VodStreamName writes a scaled marker as
            // "-720p-<hash>", and a film called "2012 Skyfall 720p.mkv"
            // produces exactly that shape from its own title — so the index
            // that decides what DLNA may use was rejecting full-resolution
            // conversions of any file whose name happens to end that way.
            // Zero means source height.
            try { File.WriteAllText(Path.Combine(dir, "height.txt"), Inv(height)); } catch { }
            // built as a list: a file name containing a quote must never be
            // able to become extra ffmpeg arguments
            // -progress writes machine-readable key=value lines to stdout;
            // -nostats drops the human progress bar that would otherwise
            // repeat the same information over stderr
            // Decided once, before anything is built: the container choice
            // below has to agree with it.
            var (copyVideo, copyAudio, coverOnly) = CopyDecision(info.FullName, height);
            if (copyVideo && copyAudio)
                Log.Info("ffmpeg", $"{info.Name} is already {CodecFamily(VideoEncoder)}/{CodecFamily(AudioEncoder)} - packaging it without re-encoding");
            else if (copyVideo)
                Log.Info("ffmpeg", $"{info.Name} is already {CodecFamily(VideoEncoder)} - keeping the picture as it is, converting only the audio");
            else if (copyAudio)
                Log.Info("ffmpeg", $"{info.Name} already carries {CodecFamily(AudioEncoder)} audio - keeping the soundtrack as it is");
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats",
                                          "-progress", "pipe:1", "-y" };
            // before -i, which is where input options belong: ffmpeg refuses
            // -hwaccel after the file it applies to. Copying the picture
            // decodes nothing, so there is no decoder to accelerate.
            if (!copyVideo && !coverOnly) args.AddRange(HardwareDecodeArgs(info.FullName));
            args.AddRange(new[] { "-i", info.FullName });
            if (coverOnly)
            {
                // A song with its cover attached: the audio is the whole of
                // it. See IsCoverArtOnly.
                args.Add("-vn");
            }
            else if (copyVideo || VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                // The picture already is what is being asked for, so it is
                // packaged rather than decoded and made again. Re-encoding it
                // would cost a generation of quality to arrive back where it
                // started.
                //
                // The cost of copying is that keyframes cannot be placed - a
                // segment can only end where the source already has one, so
                // segments come out uneven and a seek lands on the nearest.
                // That is a scrub bar that is a few seconds coarse, against a
                // generation of picture.
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
                // What that costs differs by encoder — see KeyframeArgs.
                args.AddRange(KeyframeArgs(VodSegmentSeconds));
            }
            // Copied audio carries the source's own soundtrack through — its
            // channel layout included, which AudioArgs would otherwise fold
            // down. Decided separately from the picture: a film that needs its
            // mp3 turned into aac does not need its h264 rebuilt as well.
            if (copyAudio) args.AddRange(new[] { "-c:a", "copy" });
            else args.AddRange(AudioArgs());

            var fmp4 = NeedsFmp4(info.FullName, copyVideo, copyAudio);
            var segExt = fmp4 ? "m4s" : "ts";
            args.AddRange(new[] { "-f", "hls", "-hls_time", Inv(VodSegmentSeconds), "-hls_list_size", "0",
                                  "-hls_playlist_type", "event" });
            // keep the init filename relative so the EXT-X-MAP URI stays fetchable
            if (fmp4) args.AddRange(new[] { "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4" });
            args.AddRange(new[] { "-hls_segment_filename", Path.Combine(dir, $"seg_%05d.{segExt}"), playlist });

            var title = Media.StreamTitle.Prettify(stream);
            var duration = ProbeDurationSeconds(info.FullName);
            lock (_progressLock) _vodProgress[stream] = new VodProgress(stream, title, 0, duration, DateTime.UtcNow);
            // The length of the film, next to the segments, so the HLS server
            // can list every segment the finished conversion will have before
            // it has them. That is what lets the seek bar cover the whole film
            // from the first moment instead of growing behind the encoder.
            try { File.WriteAllText(Path.Combine(dir, "duration.txt"), Inv(duration)); } catch { }

            // How far ffmpeg says it got, for JudgeEndedEarly.
            double produced = 0;
            Process job;
            try
            {
            job = Spawn(args, $"vod {info.Name}", dir, background: true,
                onProgressLine: line =>
                {
                    NoteVodProgress(stream, title, duration, line);
                    if (TryOutTime(line, out var at) && at > produced) produced = at;
                },
                onExited: (p, tail) =>
                {
                    // Judged BEFORE the job leaves the table: VodStatusFor
                    // answers "converting" for as long as it is listed, so
                    // nothing can read ffmpeg's end marker as "done" without
                    // this verdict beside it. See IsFinished.
                    (double Covered, double Expected)? endedEarly = null;
                    var exitCode = 0;
                    try { exitCode = p.ExitCode; } catch { /* reaped already: judged on stderr alone */ }
                    try { endedEarly = JudgeEndedEarly(dir, info.FullName, tail, produced, exitCode); }
                    catch (Exception ex) { Log.Warn("ffmpeg", $"could not check {stream} for an early end: {ex.Message}"); }

                    // keep the job table from accumulating finished processes.
                    // Matched by reference so a rerun that has already taken
                    // the slot isn't evicted by its predecessor's exit.
                    var superseded = false;
                    lock (_lock)
                    {
                        if (_vodJobs.TryGetValue(stream, out var q) && ReferenceEquals(q, p))
                        {
                            // Under the lock, and only while this job still
                            // owns the directory - StartVod takes the same lock
                            // to reuse or replace it, so a rerun can never
                            // inherit this job's verdict.
                            if (endedEarly is { } verdict)
                            {
                                try
                                {
                                    File.WriteAllText(Path.Combine(dir, EndedEarlyMarker),
                                        $"The input ended after {Inv(Math.Round(verdict.Covered))}s of a {Inv(Math.Round(verdict.Expected))}s "
                                        + $"film ({DateTime.Now:yyyy-MM-dd HH:mm}). This conversion is not counted as "
                                        + "finished; converting the file again replaces it.\r\n");
                                }
                                catch (Exception ex)
                                {
                                    endedEarly = null;
                                    Log.Warn("ffmpeg", $"{stream} ended early, but that could not be recorded: {ex.Message}");
                                }
                                if (endedEarly is { } recorded)
                                {
                                    // remembered only once it stands, and the
                                    // cache told the same
                                    _endedEarlyBefore[info.FullName] = recorded.Covered;
                                    _vodDone.TryRemove(stream, out _);
                                }
                            }
                            _vodJobs.Remove(stream);
                            RefreshVodJobView();
                        }
                        else
                            superseded = true;   // a rerun already owns this stream+dir
                    }
                    // Guarded the same way the job table above is, and for the
                    // same reason. The progress record is keyed by stream, not
                    // by process, so a superseded job's exit handler was
                    // deleting the record its *successor* had just written —
                    // and nothing rewrites it, so that conversion showed 0%
                    // for as long as it ran while the file itself converted
                    // perfectly normally.
                    if (!superseded) lock (_progressLock) _vodProgress.Remove(stream);
                    SweepSupersededSeekSegments(dir);
                    // Stopped, killed or crashed before ffmpeg wrote EXT-X-ENDLIST:
                    // the directory holds a partial copy that will never play.
                    // Remove it so a half-finished conversion doesn't linger on
                    // disk (and isn't mistaken for a real one). A rerun that took
                    // the slot owns the dir now, so leave that alone.
                    if (!superseded) ReportIfIncomplete(stream, dir);
                    // Finished by ffmpeg's account, short by the film's. Said
                    // out loud, and listed with the other problems: it is the
                    // one outcome that looks like success everywhere else.
                    if (!superseded && endedEarly is { } short_)
                        ReportEndedEarly(info.FullName, short_.Covered, short_.Expected, keep, height);

                    // Refused a GPU session is not the same as failed, and
                    // must not cost the conversion. Asking for more encoder
                    // sessions than the card will serve fails in well under a
                    // second, so a queue can burn through dozens of files this
                    // way in a minute with nothing to show for it.
                    var refused = false;
                    try { refused = !superseded && p.ExitCode != 0 && IsGpuSessionRefusal(IsNvenc, tail.ToString()); }
                    catch { /* reaped already */ }
                    // A viewer account's play is not requeued and does not
                    // teach the ceiling. It used to go the same way as the
                    // queue's own jobs: pushed into the batch queue as a
                    // full-resolution, permanently kept conversion, and
                    // allowed to lower the ceiling the owner's queue runs at
                    // until the next restart. That queue is the server
                    // administrator's; once a read account could press play,
                    // it could fill it. See requeueOnGpuRefusal.
                    var route = RouteGpuRefusal(keep, height, requeueOnGpuRefusal);
                    if (refused && route == GpuRefusalRoute.Report)
                    {
                        _nvencRefusals++;
                        Log.Warn("ffmpeg", $"the GPU refused an encoder session for {info.Name} - a viewer's play, "
                                         + "so it has not been added to the conversion queue; it can be played "
                                         + "again once the card is less busy");
                    }
                    else if (refused)
                    {
                        _nvencRefusals++;
                        // Whatever was running when the card said no is one
                        // more than it will serve. Learn it, so the queue
                        // stops walking into the same wall.
                        var running = Math.Max(1, ActiveVodStreams.Count);
                        // Only contention teaches a ceiling. A job that cannot
                        // open an encoder while it is the only one running is
                        // not being crowded out — that is the file or the
                        // arguments, and clamping the queue to one because of
                        // it would punish the whole library for one bad title.
                        if (running >= 2 && running < _gpuSessionCeiling)
                        {
                            _gpuSessionCeiling = running;
                            Log.Warn("ffmpeg", $"the GPU refused a {running + 1}th encoder session — "
                                             + $"holding conversions at {running} at a time. "
                                             + $"(\"How many at a time\" is set to {MaxConcurrentVod}.)");
                        }
                        if (route == GpuRefusalRoute.Batch)
                            RequeueForGpuSession(info.FullName, $"vod {info.Name}", height);
                        else
                            RetryAfterGpuRefusal(stream, info.FullName, height, keep, $"vod {info.Name}");
                    }
                    else
                    {
                        _nvencRetries.TryRemove(info.FullName, out _);
                    }

                    // one fewer file owed — write that down before starting the
                    // next, so a crash cannot resurrect a conversion that is
                    // already done. Outside the lock above: SaveQueueState
                    // takes _lock itself, via ActiveVodStreams.
                    SaveQueueState();
                    // a batch conversion just freed a slot — start the next
                    PumpVodQueue();
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Process.Start's failure - the only one that means nothing
                // is running. ffmpeg never started, so the folder made for it holds
                // nothing but the notes written above. Left, it was found by
                // the next attempt as "an unfinished conversion" and cleared
                // with a line in the log - once per retry, every watchdog
                // tick, for as long as ffmpeg could not be launched.
                try { Directory.Delete(dir, recursive: true); } catch { /* the next attempt clears it */ }
                _startingVod.Remove(stream);
                lock (_progressLock) _vodProgress.Remove(stream);
                throw;
            }
            _vodJobs[stream] = job;
            RefreshVodJobView();
            _vodStarted[stream] = DateTime.UtcNow;
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

    private sealed record SeekJob(int StartIndex, Process Process)
    {
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
    }

    /// <summary>Seek-ahead encoders running now for one stream. For tests.</summary>
    internal int SeekJobCountFor(string stream)
    {
        lock (_lock)
            return _seekJobs.TryGetValue(stream, out var list)
                ? list.Count(j => { try { return !j.Process.HasExited; } catch { return false; } })
                : 0;
    }

    /// <summary>Seek-ahead encoders running now, across every stream. For tests and the cap in EnsureVodSegment.</summary>
    internal int SeekJobCount
    {
        get
        {
            lock (_lock)
                return _seekJobs.Values.Sum(list => list.Count(j => { try { return !j.Process.HasExited; } catch { return false; } }));
        }
    }

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

    /// <summary>
    /// The height a conversion was made at, read back out of its stream name.
    ///
    /// VodStreamName writes it in as "-720p" before the eight-hex key (see
    /// Named), and it is the only place the figure survives — nothing stores
    /// the request beside the segments. A seek job filling in for that
    /// conversion has to scale to the same height or its stand-in segments are
    /// a different size from their neighbours.
    ///
    /// 0 for a source-height conversion, which is the common case.
    /// </summary>
    internal static int HeightFromStreamName(string stream)
    {
        // vod-<slug>[-<height>p]-<8 hex>
        var m = System.Text.RegularExpressions.Regex.Match(stream, @"-(\d+)p-[0-9a-f]{8}$");
        return m.Success && int.TryParse(m.Groups[1].Value, out var h) ? h : 0;
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
    /// Deletes the segments a seek job started at <paramref name="start"/>
    /// wrote but did not finish: those its own playlist does not list. (One
    /// it finished but had not listed yet goes too; it is made again when
    /// asked for, which costs a moment and serves nothing short.)
    /// </summary>
    private static void RemoveUnfinishedSeekSegments(string dir, int start, string segExt)
    {
        try
        {
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var playlist = Path.Combine(dir, $"seek_{start:D5}.m3u8");
            if (File.Exists(playlist))
                foreach (var line in File.ReadAllLines(playlist))
                {
                    var l = line.Trim();
                    if (l.Length > 0 && l[0] != '#') listed.Add(Path.GetFileName(l));
                }
            foreach (var f in Directory.EnumerateFiles(dir, $"seg_*.seek{start:D5}.{segExt}"))
                if (!listed.Contains(Path.GetFileName(f)))
                    try { File.Delete(f); } catch { /* still held; the next seek there remakes it */ }
        }
        catch { /* best effort: a left-over part is what happened before this existed */ }
    }

    /// <summary>The height a conversion recorded for itself in height.txt (0 = source height), or null without one.</summary>
    private static int? RecordedHeight(string dir)
    {
        try
        {
            return int.TryParse(File.ReadAllText(Path.Combine(dir, "height.txt")).Trim(),
                                System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out var h) && h >= 0
                ? h : null;
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

            // And across every stream: no more than "how many at a time" in
            // all, the owner's own figure for how many encoders this machine
            // should run. Two per stream bounded one film, and nothing bounded
            // the films - skipping around in a few partly converted streams
            // started encoders outside every limit, the viewer ceiling on
            // conversions included, because a seek is not a conversion. This
            // film's own earlier skip goes first - it is the one the person
            // skipping has just left behind - and only then the oldest of
            // anybody else's, rather than someone else's that is being watched.
            var cap = Math.Max(2, MaxConcurrentVod);
            var running = _seekJobs
                .SelectMany(kv => kv.Value.Select(j => (Stream: kv.Key, Job: j)))
                .Where(x => { try { return !x.Job.Process.HasExited; } catch { return false; } })
                .OrderBy(x => string.Equals(x.Stream, stream, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(x => x.Job.StartedUtc)
                .ToList();
            while (running.Count >= cap)
            {
                var (oldStream, oldJob) = running[0];
                running.RemoveAt(0);
                if (_seekJobs.TryGetValue(oldStream, out var theirs)) theirs.Remove(oldJob);
                try { oldJob.Process.Kill(true); } catch { }
                Log.Info("ffmpeg", $"seek-ahead: {cap} running across all streams - stopped the oldest, "
                                 + $"{oldStream}@{oldJob.StartIndex}, for {stream}@{index}");
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
            // The same decisions the conversion itself made, not a fresh set.
            //
            // This asked VideoEncoder == "copy" and called AudioArgs()
            // unconditionally, so a seek job could differ from the conversion
            // it is filling in for in three ways at once: re-encoding a
            // picture StartVod had copied, re-encoding and downmixing a
            // soundtrack it had copied, and — because it never looked at the
            // height — writing full-resolution stand-ins into a 720p
            // conversion. All three produce segments that do not match their
            // neighbours, and with fMP4 they do not match the init either.
            // The height the conversion was made at, from its own record - not
            // from the name, which StartVod explains cannot be trusted: a film
            // called "Blade Runner (2017) 1080p" names its full-resolution
            // conversion "...-2017-1080p-<hash>", and reading 1080 off that
            // made every stand-in segment a scaled re-encode dropped between
            // copied full-size neighbours. The name is only a fallback for a
            // conversion made before the record existed (the backfill writes
            // one for those at startup).
            var seekHeight = RecordedHeight(dir) ?? HeightFromStreamName(stream);
            var (seekCopyV, seekCopyA, seekCoverOnly) = CopyDecision(source, seekHeight);
            if (seekCoverOnly)
            {
                args.Add("-vn");   // as the conversion did - see IsCoverArtOnly
            }
            else if (seekCopyV || VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                args.AddRange(new[] { "-c:v", "copy" });
            }
            else
            {
                if (seekHeight > 0) args.AddRange(new[] { "-vf", $"scale=-2:{seekHeight}" });
                args.AddRange(new[] { "-c:v", VideoEncoder });
                args.AddRange(VideoQualityArgs());
                args.AddRange(new[] { "-pix_fmt", "yuv420p" });
                args.AddRange(KeyframeArgs(VodSegmentSeconds));
            }
            if (seekCopyA) args.AddRange(new[] { "-c:a", "copy" });
            else args.AddRange(AudioArgs());

            // A playlist of its own, never index.m3u8: the HLS server builds
            // the playlist this stream is served from, and letting a second
            // encoder rewrite the first one's would truncate the film to
            // whatever this job happens to have done.
            args.AddRange(new[] { "-f", "hls", "-hls_time", Inv(VodSegmentSeconds), "-hls_list_size", "0",
                                  "-hls_playlist_type", "event",
                                  "-start_number", Inv(index) });
            // Its own init, never the shared one. ffmpeg rewrites whatever it is
            // pointed at, so naming init.mp4 here meant every seek job
            // overwrote the file the whole stream's EXT-X-MAP refers to — and
            // it stayed overwritten after the conversion finished, because
            // nothing rewrites it afterwards. The canonical init.mp4 the
            // in-order job writes is the one that describes these segments,
            // now that the decisions above match it.
            if (fmp4) args.AddRange(new[] { "-hls_segment_type", "fmp4",
                                            "-hls_fmp4_init_filename", $"init.seek{index:D5}.mp4" });
            // Tagged with this job's own start, not the canonical name the
            // in-order job uses — see SeekSegmentPath. %05d is still
            // ffmpeg's own per-segment counter; -start_number above makes
            // its first substitution equal index, same as before.
            args.AddRange(new[] { "-hls_segment_filename", Path.Combine(dir, $"seg_%05d.seek{index:D5}.{segExt}"),
                                  Path.Combine(dir, $"seek_{index:D5}.m3u8") });

            Log.Info("ffmpeg", $"seek-ahead: {stream} from segment {index} ({Inv(at)}s)");
            var proc = Spawn(args, $"seek {stream}@{index}", dir,
                onExited: (p, tail) =>
                {
                    lock (_lock)
                        if (_seekJobs.TryGetValue(stream, out var list))
                            list.RemoveAll(j => ReferenceEquals(j.Process, p));
                    // A job stopped part way - overtaken, capped, cancelled -
                    // can leave the segment it was writing under its finished
                    // name: seen in a test, 0.27 s of a 6-second segment. Found
                    // later, it was taken as made ("already there") and served
                    // cut short. Its own playlist lists only the segments it
                    // finished, so anything of its not in that list goes.
                    RemoveUnfinishedSeekSegments(dir, index, segExt);
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
                // No longer under _lock. Sizing the cache stats every file in
                // it and evicting deletes whole directories of segments —
                // seconds of work on a full cache, and every one of those
                // seconds blocked /api/status, /api/channels and any playlist
                // request, which is exactly what the comment in StartVod says
                // this was moved off the lock to avoid. It takes _lock only to
                // read the protected set now.
                EvictVodCache(keep);
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

        // Which conversions are running, taken once under the lock.
        //
        // This used to ask _vodJobs directly, here, with no lock at all - a
        // plain Dictionary read racing the starts and exits that mutate it
        // under _lock. This sweep runs immediately after every conversion
        // starts, which is exactly when others are starting and finishing, and
        // an unsynchronised read of a dictionary being written is not merely
        // stale: it can throw, or walk a half-rebuilt bucket chain. It is the
        // same fault as the "Collection was modified" that ended an overnight
        // batch, seen again in this run's log.
        // Plus the conversions that exist on disk but have not published a job
        // yet: StartVod creates the directory early and holds _lock for a long
        // time afterwards, and without this a sweep could delete a directory
        // that is being started. Read together, once, under the lock.
        HashSet<string> running;
        lock (_lock)
        {
            running = new HashSet<string>(ActiveVodStreams, StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.UtcNow - StartingVodGrace;
            foreach (var kv in _startingVod.ToArray())
            {
                if (kv.Value < cutoff) { _startingVod.Remove(kv.Key); continue; }
                running.Add(kv.Key);
            }
        }

        var keptBytes = 0L;
        var evicted = 0;

        foreach (var (dir, size) in entries.OrderBy(e => e.dir.LastWriteTimeUtc))
        {
            if (total <= budget) break;
            if (dir.Name.Equals(keep, StringComparison.OrdinalIgnoreCase)) continue;
            if (running.Contains(dir.Name)) continue;
            // Asked for on purpose: not this sweep's to delete, at any size.
            if (IsKept(dir.FullName)) { keptBytes += size; continue; }
            try
            {
                dir.Delete(recursive: true);
                total -= size;
                evicted++;
                Log.Info("ffmpeg", $"evicted VOD cache entry {dir.Name} ({size / (1024.0 * 1024):0.#} MB)");
            }
            catch { /* files in use — try again next time */ }
        }

        // Over budget with nothing left that may be deleted. Silence here is
        // what made this hard to see from the outside: the sweep either ate
        // the owner's work or did nothing, and said the same amount about
        // both. Now it says which, and what to do about it.
        if (total > budget)
        {
            Log.Warn("ffmpeg",
                $"conversions total {Bytes(total)}, over the {Bytes(budget)} limit, and {Bytes(keptBytes)} of that "
                + "was requested from the Transcodes window and is never deleted. "
                + "Raise ffmpeg.vodCacheMaxGb (0 = no limit) or remove conversions you no longer want.");
        }
        else if (evicted > 0)
        {
            Log.Info("ffmpeg",
                $"cache trimmed: {evicted} conversion(s) removed, now {Bytes(total)} of {Bytes(budget)}");
        }
    }

    /// <summary>
    /// A size a person can read. Fixed GB made every message about a small
    /// cache read "0 GB, over the 0 GB limit", which says nothing at all.
    /// </summary>
    private static string Bytes(long b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):0.##} GB",
        >= 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{b / 1024.0:0.#} KB",
        _ => $"{b} B",
    };

    public bool IsVodReady(string stream) =>
        File.Exists(Path.Combine(_mediaRoot, stream, "index.m3u8"));

    /// <summary>
    /// Finished, as opposed to <see cref="IsVodReady"/>'s "has a playlist".
    /// A conversion stopped part way has a playlist too, and StartVod treats
    /// that as new work - it clears the directory and encodes from the top -
    /// so anything deciding whether a request costs an encode has to ask this.
    /// </summary>
    public bool IsVodComplete(string stream)
    {
        try
        {
            var dir = Path.Combine(_mediaRoot, stream);
            var playlist = Path.Combine(dir, "index.m3u8");
            return File.Exists(playlist) && IsFinished(dir, File.ReadAllText(playlist));
        }
        catch { return false; }
    }

    /// <summary>
    /// Whether a conversion's playlist is a finished one: ffmpeg's end marker,
    /// and no verdict beside it that the input ran out under it.
    ///
    /// The marker alone is not proof. ffmpeg writes it whenever its input
    /// ends, and an input that ends early - a USB disk or a share dropping out
    /// mid-read, a file that is damaged or still being copied - is an input
    /// that ended. Measured: a 60-second source cut off at 40% converts with
    /// exit code 0 and the end marker, 24 seconds of film behind it, as MP4
    /// and as MKV alike. That conversion was then done for good: the
    /// Transcodes window called it converted, the queue skipped it, and the
    /// film stopped a third of the way in with nothing anywhere to say why.
    ///
    /// The verdict is reached once, when the conversion ends
    /// (JudgeEndedEarly), and left beside it as a file. It is not worked out
    /// again whenever somebody asks, because the length a source was probed
    /// at cannot be trusted by itself: an MP3 with cover art converts to 0
    /// seconds of segments against a probed 180, and a VBR MP3 with no Xing
    /// header probes at 441 seconds and holds 180 (both measured) - and
    /// ffprobe estimates rather than reads the length of plenty of VOBs.
    /// Judged by length alone, every one of those would count as cut short
    /// each time it was looked at, and be thrown away and converted again on
    /// every play. So nothing already on disk is re-judged or rewritten.
    ///
    /// Everything that decides "is this conversion done?" asks this: StartVod,
    /// the Transcodes status, the Shelf, and the TV's choice of what to play.
    /// </summary>
    public static bool IsFinished(string dir, string playlistText) =>
        playlistText.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal)
        && !File.Exists(Path.Combine(dir, EndedEarlyMarker));

    /// <summary>Left in a conversion whose input ran out before the film did. See IsFinished.</summary>
    public const string EndedEarlyMarker = "ended-early.txt";

    private const double ShortfallFloorSeconds = 30;
    private const double ShortfallFraction = 0.05;

    /// <summary>
    /// Sources whose conversion has ended early once, and how much of them it
    /// covered - so a second that stops in the same place is recognised as
    /// the file, not an outage. In memory: a restart costs at most one more
    /// conversion of such a file.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _endedEarlyBefore =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the conversion that just exited ran out of input before the
    /// film did. It takes both halves.
    ///
    /// Evidence that it stopped for want of input: the input said it ran out
    /// (StderrTail.InputEnded - "File ended prematurely", "partial file", a
    /// failed read), or ffmpeg itself failed (measured: an MP4 damaged 30% in
    /// and copied stops there, 35 seconds of 120, and exits with an error while
    /// still writing the end marker). A whole file gives neither, which is what
    /// keeps the cover-art MP3s, the headerless VBR files and the estimated
    /// VOBs out of it - and so does a file with a damaged patch that ffmpeg
    /// reads past to its real end (a complaint, but not "ended", and exit 0).
    ///
    /// AND what was produced falls well short of the length the source was
    /// probed at: 30 seconds, or 5% of a long film, so a file missing its last
    /// few kilobytes is not thrown away over a second.
    ///
    /// A source that ends early twice in the same place is the file itself -
    /// damaged, or really that short - and is accepted as it is rather than
    /// converted for ever. Returns what was covered and expected when it ended
    /// early, or null.
    /// </summary>
    private (double Covered, double Expected)? JudgeEndedEarly(string dir, string source, StderrTail tail,
                                                              double producedSeconds, int exitCode)
    {
        if (!tail.InputEnded && exitCode == 0)
        {
            _endedEarlyBefore.TryRemove(source, out _);   // read to its end: nothing to remember
            return null;
        }
        var playlist = Path.Combine(dir, "index.m3u8");
        if (!File.Exists(playlist)) return null;
        var text = File.ReadAllText(playlist);
        if (!text.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal)) return null;   // unfinished anyway
        double expected;
        try
        {
            if (!double.TryParse(File.ReadAllText(Path.Combine(dir, "duration.txt")).Trim(),
                                 System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out expected))
                return null;
        }
        catch { return null; }   // no recorded length: nothing to hold it against
        if (expected <= 0) return null;
        var covered = Math.Max(PlaylistSeconds(text), producedSeconds);
        if (expected - covered <= Math.Max(ShortfallFloorSeconds, expected * ShortfallFraction))
        {
            _endedEarlyBefore.TryRemove(source, out _);   // whole this time: nothing to remember
            return null;
        }
        if (_endedEarlyBefore.TryGetValue(source, out var before)
            && Math.Abs(before - covered) <= Math.Max(2, covered * 0.01))
        {
            _endedEarlyBefore.TryRemove(source, out _);
            Interlocked.Increment(ref _sameEndAccepted);
            Log.Info("ffmpeg", $"{Path.GetFileName(source)} ended at {Inv(Math.Round(covered))}s again, the same place "
                             + "as last time - that is where the file itself ends, so the conversion is kept as it is");
            return null;
        }
        // Recorded by the caller, once the verdict is actually written down.
        return (covered, expected);
    }

    private int _sameEndAccepted;

    /// <summary>How many conversions were accepted for ending where their file really ends. For tests.</summary>
    internal int SameEndAccepted => Volatile.Read(ref _sameEndAccepted);

    /// <summary>The out_time on one line of ffmpeg's -progress output, in seconds.</summary>
    private static bool TryOutTime(string line, out double seconds)
    {
        seconds = 0;
        const string key = "out_time=";
        if (!line.StartsWith(key, StringComparison.Ordinal)) return false;
        if (!TimeSpan.TryParse(line[key.Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var at)
            || at < TimeSpan.Zero)
            return false;
        seconds = at.TotalSeconds;
        return true;
    }

    /// <summary>The seconds of film a playlist lists: the sum of its #EXTINF durations.</summary>
    internal static double PlaylistSeconds(string playlistText)
    {
        double total = 0;
        foreach (var line in playlistText.Split('\n'))
        {
            var l = line.Trim();
            if (!l.StartsWith("#EXTINF:", StringComparison.Ordinal)) continue;
            var value = l["#EXTINF:".Length..];
            var comma = value.IndexOf(',');
            if (comma >= 0) value = value[..comma];
            if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                total += seconds;
        }
        return total;
    }

    /// <summary>
    /// True while the queue pump is between taking a file off the queue and
    /// that file's job being registered as running. Setting a conversion up
    /// can take tens of seconds - clearing a partial directory off an archive
    /// disk has been measured at 26 to 35 - and in that gap the file is
    /// neither queued nor running. Anything asking "is there work in
    /// progress" must count this, or it sees nothing at exactly the wrong time.
    /// </summary>
    public bool VodStarting => Volatile.Read(ref _pumping) > 0;
    private int _pumping;

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
            // Carry the start time forward: it is what the ETA divides by, and
            // a fresh record on every progress line would keep resetting it.
            if (_vodProgress.TryGetValue(stream, out var prior))
                _vodProgress[stream] = new VodProgress(stream, title, at.TotalSeconds, duration,
                                                       prior.StartedUtc == default ? DateTime.UtcNow : prior.StartedUtc);
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
            // Snapshot first, ask afterwards.
            //
            // Process.HasExited raises the Exited event on the calling thread
            // when it finds the process gone. The handler removes the job from
            // this very dictionary, and a lock is re-entrant for the thread
            // that already holds it - so asking HasExited *during* the
            // enumeration let the handler mutate the collection being walked,
            // which is the "Collection was modified" that has been ending
            // overnight batches. Materialising the pairs finishes the
            // enumeration before any of that can happen.
            // No lock. This is on the /api/status path, which must never
            // wait on _lock — see _vodJobsView. The array is published under
            // the lock and never mutated afterwards, so reading it here is
            // safe, and the "Collection was modified" hazard described above
            // is unchanged: this is still a materialised snapshot.
            var snapshot = _vodJobsView;
            return snapshot
                .Where(kv => { try { return !kv.Value.HasExited; } catch { return false; } })
                .Select(kv => kv.Key).ToList();
        }
    }

    public enum VodState { None, Converting, Done }

    /// <summary>
    /// Whether a full-resolution conversion of this file exists, is running,
    /// or has never been made — for the Transcode panel's file listing.
    /// </summary>
    /// Conversions already known to be finished.
    ///
    /// The folder pills ask this for every file they count, and the answer
    /// used to cost a stat, this lock, and reading the whole playlist looking
    /// for its end marker - for every file, on every listing, and the panel
    /// re-lists itself every few seconds. A finished conversion does not
    /// become unfinished, so it is worth remembering; DiscardVod forgets it
    /// when the files actually go.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _vodDone =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="height">Which conversion: 0 (the default) is full resolution.</param>
    public VodState VodStatusFor(string file, int height = 0)
    {
        var stream = VodStreamName(file, height);
        if (stream is null) return VodState.None;
        // A job in the table comes first, ahead of the cache below: a
        // conversion remembered as done, then deleted by the cache sweep and
        // started again from a television, is being made again - and the
        // cache, which only checks that a playlist exists, would have said
        // "done" through the whole of it and past its verdict.
        //
        // No lock here either, and for the same reason as ActiveVodStreams.
        //
        // Nothing converting was already free, via the counter. But while
        // something IS converting — which on a server working through a queue
        // is all the time — this used to take _lock once per file, and _lock
        // is held for the whole of starting a conversion, ffprobe included.
        // So the Transcode panel stalled exactly when there was something to
        // watch. The view is an array of the few jobs that can run at once,
        // so scanning it costs less than the lock did.
        //
        // Listed at all counts, exited or not. A job leaves the table only
        // once its exit has been judged (see StartVod's exit handler), and in
        // the moment between ffmpeg writing its end marker and that verdict,
        // "done" would be read off the marker and cached for good - which is
        // the very answer the verdict exists to correct.
        if (_vodJobCount > 0)
            foreach (var kv in _vodJobsView)
                if (string.Equals(kv.Key, stream, StringComparison.OrdinalIgnoreCase))
                    return VodState.Converting;
        // Known finished: confirm it is still on disk, which is one cheap check
        // rather than the whole-playlist read below, and stays right whichever
        // of the several delete paths removed it. StartVod and the exit
        // handler drop the entry when a conversion is made again or marked
        // as ended early, so it cannot outlive what it says.
        if (_vodDone.ContainsKey(stream))
        {
            if (File.Exists(Path.Combine(_mediaRoot, stream, "index.m3u8"))) return VodState.Done;
            _vodDone.TryRemove(stream, out _);
        }
        try
        {
            var dir = Path.Combine(_mediaRoot, stream);
            var playlist = Path.Combine(dir, "index.m3u8");
            // Finished, not merely marked finished - see IsFinished.
            if (File.Exists(playlist) && IsFinished(dir, File.ReadAllText(playlist)))
            {
                _vodDone[stream] = 0;
                return VodState.Done;
            }
        }
        catch { }
        return VodState.None;
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _vodQueue = new();

    /// <summary>How many conversions run at once from a batch; the rest wait. 1–15.</summary>
    public int MaxConcurrentVod { get; set; } = 2;

    /// <summary>
    /// Seconds to leave between starting one batch conversion and the next,
    /// 0–120. Spacing the starts keeps a mechanical disk from thrashing its
    /// heads across several files that all began reading at once; it delays
    /// only the *start* of each job, not the job itself. 0 = start as slots
    /// free up. Mirrors the media conversion tool's stagger.
    /// </summary>
    public int VodStaggerSeconds { get; set; }

    private readonly string _queueSettingsFile;
    private readonly object _pumpLock = new();
    private DateTime _lastVodStartUtc = DateTime.MinValue;
    private System.Threading.Timer? _staggerTimer;

    private sealed class QueueSettings
    {
        public int MaxParallel { get; set; } = 2;
        public int StaggerSeconds { get; set; }

        /// <summary>
        /// The batch still to convert. Absent in files written before the
        /// queue was persisted, which deserializes to an empty list — an old
        /// sidecar keeps its settings and simply restores nothing.
        /// </summary>
        public List<string> Waiting { get; set; } = new();

        /// <summary>
        /// The height to convert a waiting file at, for the few that are not
        /// full resolution. Absent means full resolution, which is every entry
        /// written before this existed.
        /// </summary>
        public Dictionary<string, int> Heights { get; set; } = new();
    }

    /// <summary>
    /// Heights for queued files that are not converted at full resolution. See
    /// QueueSettings.Heights, and RouteGpuRefusal for why the queue needs them.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _vodQueueHeights =
        new(StringComparer.OrdinalIgnoreCase);

    private int QueuedHeight(string file) => _vodQueueHeights.TryGetValue(file, out var h) ? h : 0;

    private void LoadQueueSettings()
    {
        try
        {
            if (!File.Exists(_queueSettingsFile)) return;
            var s = System.Text.Json.JsonSerializer.Deserialize<QueueSettings>(File.ReadAllText(_queueSettingsFile));
            if (s is null) return;
            MaxConcurrentVod = Math.Clamp(s.MaxParallel, 1, 15);
            VodStaggerSeconds = Math.Clamp(s.StaggerSeconds, 0, 120);

            foreach (var f in s.Waiting ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(f)) _vodQueue.Enqueue(f);
            foreach (var (f, h) in s.Heights ?? new Dictionary<string, int>())
                if (h > 0) _vodQueueHeights[f] = h;
            if (!_vodQueue.IsEmpty)
                Log.Info("ffmpeg", $"transcode queue restored: {_vodQueue.Count} file(s) still to convert");
        }
        catch (Exception ex)
        {
            // A queue file that cannot be read is not a default.
            //
            // This swallowed the exception and carried on with an empty queue,
            // and the next save wrote that emptiness over the top — so a file
            // this could not parse destroyed every entry in it, silently, with
            // nothing in the log to say a queue had ever existed. Ninety-four
            // files went that way in one restart, and the only clue was the
            // absence of the "queue restored" line, which is not something
            // anybody watches for.
            //
            // Starting empty is still the right behaviour: refusing to start
            // over a bad sidecar would be worse. But it is said out loud, and
            // the file is kept, so what was in it can be recovered rather
            // than overwritten a second later.
            Log.Error("ffmpeg", $"could not read the transcode queue ({ex.Message}) — starting with an "
                              + "empty queue. The unreadable file is kept as transcode-queue.json.unreadable; "
                              + "nothing that was queued has been converted.");
            try
            {
                var kept = _queueSettingsFile + ".unreadable";
                File.Copy(_queueSettingsFile, kept, overwrite: true);
            }
            catch { /* best effort — the log line above is the part that matters */ }
        }
    }

    /// <summary>Serialises the queue file. One writer at a time.</summary>
    private readonly object _queueFileLock = new();

    /// <summary>
    /// Writes the queue to disk, so a batch survives the server stopping.
    ///
    /// A 418-file overnight run was lost to a crash because the queue lived
    /// only in memory: the process died and took the remaining ~310 files with
    /// it, with nothing on disk to resume from.
    ///
    /// What is written is everything still owed — waiting *and* in flight. A
    /// conversion that was running when the server stopped had already been
    /// dequeued but never finished, so persisting the queue alone would
    /// quietly drop it. Its source path is in the job's own source.txt, so
    /// this needs no extra bookkeeping to find; the in-flight ones go first,
    /// since they were started first. Re-queueing something that did finish is
    /// harmless — the pump skips anything already converted.
    /// </summary>
    private void SaveQueueState()
    {
        try
        {
            var outstanding = new List<string>();
            var heights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var stream in ActiveVodStreams)
            {
                try
                {
                    // Only conversions somebody asked to keep - what the queue
                    // itself starts, and what the Transcodes window does -
                    // each at the height it was being made at. Every running
                    // job went in, plays included, and a queue entry had no
                    // height: a film somebody was watching at 720p when the
                    // server was restarted came back as a full-resolution
                    // conversion marked never to be evicted. A play is simply
                    // played again; it is not owed.
                    var dir = Path.Combine(_mediaRoot, stream);
                    if (!IsKept(dir)) continue;
                    var src = File.ReadAllText(Path.Combine(dir, "source.txt")).Trim();
                    if (src.Length == 0) continue;
                    outstanding.Add(src);
                    if (RecordedHeight(dir) is int h and > 0) heights[src] = h;
                }
                catch { /* no source.txt: nothing to resume it by */ }
            }
            foreach (var f in _vodQueue.ToArray())
            {
                if (outstanding.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
                outstanding.Add(f);
                if (QueuedHeight(f) is var h and > 0) heights[f] = h;
            }

            var json = System.Text.Json.JsonSerializer.Serialize(new QueueSettings
            {
                MaxParallel = MaxConcurrentVod,
                StaggerSeconds = VodStaggerSeconds,
                Waiting = outstanding,
                Heights = heights,
            });
            // Atomic. This file exists so a queue survives a restart, and it
            // was the one write most likely to be interrupted by one: an
            // upgrade stops the server mid-batch, and a truncating write
            // caught there loses the very list it is for.
            lock (_queueFileLock) JsonSidecar.WriteAtomic(_queueSettingsFile, json, "ffmpeg");
        }
        catch (Exception ex) { Log.Warn("ffmpeg", $"could not save the transcode queue: {ex.Message}"); }
    }

    /// <summary>
    /// Picks a restored batch back up, once, at startup. Separate from the
    /// constructor on purpose: loading reads the file, but starting encoders
    /// is work that belongs after the server is up — the same split the live
    /// channels use with RestoreRunningChannels.
    /// </summary>
    public void ResumeVodQueue()
    {
        if (_vodQueue.IsEmpty) return;
        Log.Info("ffmpeg", $"resuming batch conversion of {_vodQueue.Count} file(s)");
        PumpVodQueue();
    }

    /// <summary>
    /// Sets how many conversions run at once (1–15) and the gap between starting
    /// them (0–120 s), persists the choice, and pumps the queue so a raised cap
    /// takes effect immediately. Nulls leave a value unchanged.
    /// </summary>
    public void SetQueueSettings(int? maxParallel, int? staggerSeconds)
    {
        if (maxParallel is int mp)
        {
            var before = MaxConcurrentVod;
            MaxConcurrentVod = Math.Clamp(mp, 1, 15);
            // The owner saying how many is a fresh start for what the GPU has
            // taught. The ceiling only ever came down, so once lowered -
            // rightly or, before IsGpuSessionRefusal was narrowed, wrongly -
            // nothing short of a restart raised it again, and the setting in
            // the Transcodes panel did nothing at all. A card that really does
            // refuse will teach it again at the next refusal.
            // Only when the number actually changes: the dashboard sends it
            // with every save, stagger included, and relearning costs a round
            // of refused sessions.
            if (MaxConcurrentVod != before && _gpuSessionCeiling != int.MaxValue)
            {
                Log.Info("ffmpeg", $"conversions at a time set to {MaxConcurrentVod} — forgetting the GPU's "
                                 + $"learned limit of {_gpuSessionCeiling}");
                _gpuSessionCeiling = int.MaxValue;
            }
        }
        if (staggerSeconds is int st) VodStaggerSeconds = Math.Clamp(st, 0, 120);
        SaveQueueState();
        PumpVodQueue();
    }

    /// <summary>Number of files waiting in the batch conversion queue.</summary>
    public int VodQueueDepth => _vodQueue.Count;

    /// <summary>The files waiting in the batch queue (not yet started), in order.</summary>
    public IReadOnlyList<string> VodQueueSnapshot => _vodQueue.ToArray();

    /// <summary>
    /// Removes a file from the waiting queue. Running conversions live in the
    /// job table, not the queue, so this can never stop one that has already
    /// started. Returns true if the file was waiting and is now removed.
    /// </summary>
    public bool RemoveFromVodQueue(string file)
    {
        lock (_pumpLock)
        {
            var all = _vodQueue.ToArray();
            var kept = all.Where(f => !string.Equals(f, file, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (kept.Length == all.Length) return false;   // wasn't waiting
            while (_vodQueue.TryDequeue(out _)) { }
            foreach (var f in kept) _vodQueue.Enqueue(f);
            _vodQueueHeights.TryRemove(file, out _);
            SaveQueueState();
            return true;
        }
    }

    /// <summary>Empties the waiting queue; running conversions are unaffected.</summary>
    public int ClearVodQueue()
    {
        lock (_pumpLock)
        {
            var n = 0;
            while (_vodQueue.TryDequeue(out _)) n++;
            _vodQueueHeights.Clear();
            SaveQueueState();
            return n;
        }
    }

    /// <summary>
    /// Queues files for conversion and starts as many as the concurrency cap
    /// allows, the rest following as slots free up. Files already converted
    /// or already converting are skipped. Returns how many were newly queued.
    /// </summary>
    public int QueueVod(IEnumerable<string> files)
    {
        var n = 0;
        foreach (var f in files)
        {
            if (VodStatusFor(f) is VodState.Done or VodState.Converting)
            {
                // Asked for in the Transcodes window, so it is one to keep -
                // even though there is nothing to queue. Passed over as it
                // was, a conversion that a play had started stayed disposable:
                // the cache could evict it, and a restart mid-way would not
                // carry it over (only kept ones are).
                if (VodStreamName(f) is string done)
                {
                    var dir = Path.Combine(_mediaRoot, done);
                    if (Directory.Exists(dir)) MarkKept(dir, done);
                }
                continue;
            }
            // Both the duplicate check and the enqueue under the same lock, so
            // a concurrent RemoveFromVodQueue cannot drain this away between
            // them — see the note there.
            lock (_pumpLock)
            {
                if (_vodQueue.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
                _vodQueue.Enqueue(f);
            }
            n++;
        }
        // Persisted before anything starts: a batch is at its most valuable
        // the moment it is queued and has not been converted yet.
        if (n > 0) SaveQueueState();
        PumpVodQueue();
        return n;
    }

    /// <summary>
    /// A periodic safety net for the batch queue, called by the watchdog.
    ///
    /// The pump advances only on a one-shot event: a conversion's completion
    /// handler, or a single stagger timer. Each schedules the next and then is
    /// gone. If one is ever dropped — a thread-pool starved by a storm of
    /// channels failing on a lost network, a swallowed callback, any hiccup —
    /// nothing re-arms it, and a full queue waits for ever with nothing
    /// running. That is a real failure seen in the field: a network outage
    /// took the live channels down, and the local conversion queue, which
    /// needs no network at all, stalled silently alongside them because it
    /// shared the process the storm had jammed. This kick re-arms the pump
    /// every watchdog tick, so a stall can never outlive one interval.
    /// </summary>
    public void KickVodQueue()
    {
        if (!Available || _disposed || _vodQueue.IsEmpty) return;
        // Waiting files with nothing running is the stall itself — the pump
        // has no in-flight job whose finish would ever wake it. Worth a line,
        // unless the queue is parked for a reason it has already given (a
        // drive out of reach, ffmpeg missing): then this would say the same
        // thing again on every tick, all night.
        // A pass already under way is the same kick in progress. Waiting for
        // it here meant that when a pass ran long - an offline share taking
        // its time to say so - each tick left one more thread queued behind
        // the lock.
        if (!Monitor.TryEnter(_pumpLock)) return;
        try
        {
            if (ActiveVodStreams.Count == 0 && _pumpStalledBy is null && _unreachableNoted is null)
                Log.Info("ffmpeg", $"transcode watchdog: {_vodQueue.Count} file(s) waiting, none running — restarting the queue");
            PumpVodQueue();
        }
        finally { Monitor.Exit(_pumpLock); }
    }

    /// <summary>Why the queue last stopped short, so the watchdog's retries say it once rather than every tick.</summary>
    private string? _pumpStalledBy;

    /// <summary>Drives and shares found unreachable, and when to ask again. See PumpVodQueue.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _unreachableUntil =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan UnreachableRecheck = TimeSpan.FromMinutes(2);

    /// <summary>Starts that failed for a reason of the file's own, by file. See PumpVodQueue.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _startFailures =
        new(StringComparer.OrdinalIgnoreCase);

    private const int MaxStartFailures = 3;

    /// <summary>
    /// Whether a conversion could be set up at all: a folder can be made in
    /// the transcodes directory. What tells "every start will fail" from
    /// "this start failed" when the exception itself does not say.
    /// </summary>
    private bool MediaRootWritable()
    {
        try
        {
            var probe = Path.Combine(_mediaRoot, ".write-check-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>The drives and shares last reported unreachable, for the same reason.</summary>
    private string? _unreachableNoted;

    /// <summary>
    /// The drive or share a file lives on, when that itself cannot be reached;
    /// null when it can, or when the path is not one this can judge.
    /// </summary>
    private static string? SourceVolumeUnreachable(string file)
    {
        try
        {
            var root = Path.GetPathRoot(file);
            if (string.IsNullOrEmpty(root)) return null;
            return Directory.Exists(root) ? null : root;
        }
        catch { return null; }
    }

    /// <summary>Returns a file to the head of the queue, where it was taken from.</summary>
    private void PutBackAtFront(string file)
    {
        // Under _pumpLock, like every other rebuild of the queue - see
        // RemoveFromVodQueue for what an enqueue landing mid-rebuild costs.
        lock (_pumpLock)
        {
            var rest = _vodQueue.ToArray();
            while (_vodQueue.TryDequeue(out _)) { }
            _vodQueue.Enqueue(file);
            foreach (var f in rest) _vodQueue.Enqueue(f);
        }
    }

    private void PumpVodQueue()
    {
        if (!Available || _disposed) return;
        lock (_pumpLock)
        {
            // Nothing is taken off the queue when nothing could be started.
            // Taking files off only to fail them is how a quarantined ffmpeg
            // used to empty a batch; it also left a half-made folder behind
            // for every one, and a line in the log for each on every retry.
            if (!CanLaunch)
            {
                var why = $"ffmpeg is no longer at {FfmpegPath}";
                if (why != _pumpStalledBy)
                    Log.Warn("ffmpeg", why + $" (quarantined, or moved by an upgrade?) — {_vodQueue.Count} queued "
                                     + "file(s) wait for it, none dropped");
                _pumpStalledBy = why;
                return;
            }
            // Whether anything left the queue for good this pass — a start,
            // but also a skip. A file that was queued and has since been
            // deleted, or that some other run already converted, is dequeued
            // and passed over; saving only on a successful start would leave
            // it listed on disk for ever, restored and skipped again on every
            // restart. A file set aside and put back does not count: rewriting
            // the queue file every watchdog tick while a drive is unplugged
            // records nothing.
            var dequeued = false;
            // Each waiting file is looked at once per pass. A file set aside
            // below (its drive cannot be reached) goes to the back, so without
            // this a queue of nothing but those would go round for ever.
            var looked = 0;
            var toLook = _vodQueue.Count;
            var unreachable = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            Interlocked.Increment(ref _pumping);
            try
            {
            while (ActiveVodStreams.Count < EffectiveMaxConcurrentVod && !_vodQueue.IsEmpty && looked < toLook)
            {
                // Stagger: leave the configured gap between one start and the
                // next. It applies only while something is already converting —
                // the point is to keep several encodes from thrashing one disk,
                // so when nothing is running the next start (the first of a
                // batch) never waits. Time already elapsed counts towards the
                // gap; if it hasn't passed, wake up when it has instead of now.
                var gap = TimeSpan.FromSeconds(Math.Clamp(VodStaggerSeconds, 0, 120));
                if (gap > TimeSpan.Zero && ActiveVodStreams.Count > 0)
                {
                    var wait = gap - (DateTime.UtcNow - _lastVodStartUtc);
                    if (wait > TimeSpan.Zero)
                    {
                        _staggerTimer?.Dispose();
                        _staggerTimer = new System.Threading.Timer(_ => PumpVodQueue(), null,
                            wait, System.Threading.Timeout.InfiniteTimeSpan);
                        return;
                    }
                }

                if (!_vodQueue.TryDequeue(out var file)) break;
                looked++;
                try
                {
                    // Already found unreachable this pass: no need to ask the
                    // drive again. On an offline share every question can take
                    // seconds, and this lock is the one the Transcodes window
                    // and every finishing job wait on.
                    // Nor across passes, for a while: an offline host can take
                    // twenty seconds per question, longer than the watchdog's
                    // interval, so asking on every tick kept this lock held
                    // more or less all night. Asked again every couple of
                    // minutes instead.
                    if (Path.GetPathRoot(file) is { Length: > 0 } knownRoot
                        && (unreachable.Contains(knownRoot)
                            || (_unreachableUntil.TryGetValue(knownRoot, out var until) && DateTime.UtcNow < until)))
                    {
                        _vodQueue.Enqueue(file);
                        unreachable.Add(knownRoot);
                        continue;
                    }
                    // "Cannot tell" is not "gone". File.Exists answers false for
                    // a file on a drive that has been unplugged or a share that
                    // has dropped for a moment, exactly as for one that was
                    // deleted - so a NAS blinking mid-batch used to empty the
                    // whole queue, file by file, each one logged as "no longer
                    // there", and wrote the empty queue to disk. When the drive
                    // or share itself cannot be reached, the file waits for it
                    // at the back of the queue; the watchdog comes back round.
                    // A drive that is there with the folder gone is a file that
                    // is gone, and goes as before.
                    if (!File.Exists(file) && SourceVolumeUnreachable(file) is string root)
                    {
                        _vodQueue.Enqueue(file);
                        unreachable.Add(root);
                        _unreachableUntil[root] = DateTime.UtcNow + UnreachableRecheck;
                        continue;
                    }
                    if (Path.GetPathRoot(file) is { Length: > 0 } reached) _unreachableUntil.TryRemove(reached, out _);
                    // Say why a file left the queue without being converted.
                    //
                    // Both of these were silent, and silence here is
                    // indistinguishable from losing the work: a queue of 86
                    // emptied in under a minute with two conversions started,
                    // and nothing anywhere said the other 84 were already
                    // done. Twice today that was read as the server throwing
                    // the queue away, and checking it by hand was the only way
                    // to find out otherwise.
                    var queuedHeight = QueuedHeight(file);
                    var state = VodStatusFor(file, queuedHeight);
                    if (state is VodState.Done or VodState.Converting)
                    {
                        dequeued = true;
                        _vodQueueHeights.TryRemove(file, out _);
                        Log.Info("ffmpeg", $"skipped: {Path.GetFileName(file)} — "
                            + (state == VodState.Done ? "already converted" : "already converting"));
                        continue;
                    }
                    if (!File.Exists(file))
                    {
                        dequeued = true;
                        _vodQueueHeights.TryRemove(file, out _);
                        Log.Info("ffmpeg", $"skipped: {Path.GetFileName(file)} — the file is no longer there");
                        continue;
                    }
                    // keep: everything in this queue was put there from the
                    // Transcodes window, which is somebody asking for a file
                    // to exist - not the server making itself a copy to play.
                    // Stamped before the work, not after it.
                    //
                    // The stagger is the gap between one start and the next,
                    // and this marked the moment StartVod *finished* setting a
                    // conversion up — so everything that setting up costs was
                    // added to the wait. Clearing a partial directory is the
                    // expensive part: an interrupted 1080p episode leaves
                    // four to five hundred segment files, and deleting them
                    // recursively off an archive disk takes many seconds,
                    // inside the lock, before the clock even started. A 15
                    // second stagger was firing 26 to 35 seconds apart with
                    // the pool nowhere near its limit, which is what "the
                    // timing is not firing at 15s" was.
                    //
                    // Marking the start makes the setting say what it means:
                    // starts are 15 seconds apart. If setting one up takes
                    // longer than that the next follows immediately after,
                    // which is the honest reading of a minimum gap.
                    _lastVodStartUtc = DateTime.UtcNow;
                    StartVod(file, queuedHeight, keep: true);   // registers the job; its exit pumps the queue again
                    dequeued = true;
                    _vodQueueHeights.TryRemove(file, out _);
                    _pumpStalledBy = null;
                    _startFailures.TryRemove(file, out _);
                }
                catch (FileNotFoundException ex) when (string.Equals(ex.FileName, file, StringComparison.OrdinalIgnoreCase))
                {
                    // Gone between the check above and the start. That one is
                    // about the file - unless its drive went in that moment.
                    if (SourceVolumeUnreachable(file) is string root)
                    {
                        _vodQueue.Enqueue(file);
                        unreachable.Add(root);
                    }
                    else
                    {
                        dequeued = true;
                        _vodQueueHeights.TryRemove(file, out _);
                        Log.Info("ffmpeg", $"skipped: {Path.GetFileName(file)} — the file is no longer there");
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                           || !CanLaunch || !MediaRootWritable())
                {
                    // Not about this file: ffmpeg cannot be launched
                    // (quarantined, or blocked from running), or no conversion
                    // can be set up because the transcodes folder cannot be
                    // written - and it will fail the same way for every file
                    // behind it. This used to log and take the next, so one
                    // pass dequeued and failed the entire batch and saved the
                    // empty queue over the real one. The file goes back where
                    // it was and the pass stops; the next job to finish, or
                    // the watchdog, tries again.
                    PutBackAtFront(file);
                    var why = $"could not start queued conversion of {Path.GetFileName(file)}: {ex.Message}";
                    if (why != _pumpStalledBy)
                        Log.Warn("ffmpeg", why + $" — the queue is paused with {_vodQueue.Count} file(s) waiting, "
                                             + "none dropped; it carries on once that is fixed");
                    _pumpStalledBy = why;
                    break;
                }
                catch (Exception ex)
                {
                    // About this file, or its own folder: ffmpeg runs and the
                    // transcodes folder takes writes, so the others can go
                    // ahead. Stopping the whole queue for it - which the
                    // branch above does for a reason that is everybody's -
                    // would park a batch behind one bad entry for ever. It is
                    // given a few more tries, at the back, then let go.
                    var tries = _startFailures.AddOrUpdate(file, 1, (_, n) => n + 1);
                    if (tries < MaxStartFailures)
                    {
                        _vodQueue.Enqueue(file);
                        Log.Warn("ffmpeg", $"could not start queued conversion of {Path.GetFileName(file)} "
                                         + $"(attempt {tries} of {MaxStartFailures}): {ex.Message}");
                    }
                    else
                    {
                        _startFailures.TryRemove(file, out _);
                        _vodQueueHeights.TryRemove(file, out _);
                        dequeued = true;
                        Log.Warn("ffmpeg", $"gave up on queued conversion of {Path.GetFileName(file)} after "
                                         + $"{MaxStartFailures} attempts: {ex.Message}");
                        OnProblem?.Invoke("conversion", $"vod {Path.GetFileName(file)}",
                                          $"could not be started: {ex.Message}");
                    }
                }
            }
            }
            finally
            {
                Interlocked.Decrement(ref _pumping);
                // Once per outage rather than once per file per pass: the
                // watchdog comes round every tick, and a drive unplugged for
                // the night should say so once.
                if (unreachable.Count > 0)
                {
                    var note = string.Join(", ", unreachable);
                    if (note != _unreachableNoted)
                        Log.Warn("ffmpeg", $"queued files on {note} cannot be reached — kept in the queue until "
                                         + "that drive or share is back");
                    _unreachableNoted = note;
                }
                else if (looked >= toLook)
                    _unreachableNoted = null;   // looked at everything, and all of it was reachable
                // Once per pass, and on every way out of it — including the
                // stagger's early return — so what is on disk matches what is
                // actually still owed.
                if (dequeued) SaveQueueState();
            }
        }
    }

    /// <summary>
    /// Marks every conversion already on disk as one to keep, once.
    ///
    /// The marker did not exist before this build, so every conversion this
    /// server has ever finished is unmarked - which, under the new rule,
    /// means disposable. The one thing that must not happen on upgrade is the
    /// first sweep deleting the library the owner spent nights converting,
    /// which is exactly the fault being fixed.
    ///
    /// There is no way to tell now which of these were deliberate and which
    /// the server made for itself, so they are all treated as deliberate.
    /// Keeping something disposable costs disk; deleting something wanted
    /// costs hours of encoding and cannot be undone. Only conversions from
    /// this point on are sorted properly, and only new play-time copies are
    /// disposable.
    /// </summary>
    private void MarkExistingConversionsKeptOnce(string markerFile)
    {
        if (File.Exists(markerFile)) return;
        var marked = 0;
        try
        {
            if (Directory.Exists(_mediaRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(_mediaRoot, "vod-*"))
                {
                    var playlist = Path.Combine(dir, "index.m3u8");
                    // only finished ones: a partial is rebuilt anyway
                    if (!File.Exists(playlist)) continue;
                    if (!File.ReadAllText(playlist).Contains("#EXT-X-ENDLIST", StringComparison.Ordinal)) continue;
                    if (IsKept(dir)) continue;
                    MarkKept(dir, Path.GetFileName(dir));
                    marked++;
                }
            }
            File.WriteAllText(markerFile, DateTime.UtcNow.ToString("O"));
            if (marked > 0)
                Log.Info("ffmpeg", $"{marked} existing conversion(s) marked as ones to keep — "
                    + "the cache will not delete work that was already done");
        }
        catch (Exception ex)
        {
            Log.Warn("ffmpeg", $"could not mark existing conversions as kept: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a conversion's output directory when it did not finish — no
    /// EXT-X-ENDLIST in the playlist means ffmpeg was stopped, killed or
    /// crashed partway, leaving segments that will never play. A finished
    /// conversion (ENDLIST present) is left untouched.
    /// </summary>
    private void ReportIfIncomplete(string stream, string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            if (IsComplete(dir)) return;
            // Not deleted here. See CleanUpIncompleteVodDirs.
            Log.Info("ffmpeg", $"conversion did not finish: {stream} — kept on disk; "
                + "converting it again replaces it");
        }
        catch (Exception ex)
        {
            Log.Warn("ffmpeg", $"could not check {stream}: {ex.Message}");
        }
    }

    /// <summary>
    /// Says so when a conversion ran out of input before the film did (see
    /// JudgeEndedEarly), and puts a batch conversion back in the queue.
    ///
    /// Whether or not its drive is reachable at this moment: a share that
    /// blinked and came back before this ran would otherwise drop the file
    /// out of the batch. It cannot circle: a second attempt that stops in the
    /// same place is accepted as the file's own end (JudgeEndedEarly), and a
    /// file that keeps stopping in different places - still being copied, a
    /// failing disk - is put back twice at most.
    /// </summary>
    private void ReportEndedEarly(string source, double covered, double expected, bool batch, int height)
    {
        var name = Path.GetFileName(source);
        var detail = $"ended early: {Inv(Math.Round(covered))}s of a {Inv(Math.Round(expected))}s film";
        var outage = SourceVolumeUnreachable(source);
        var requeued = false;
        if (batch && _endedEarlyRequeues.AddOrUpdate(source, 1, (_, n) => n + 1) <= 2)
        {
            lock (_pumpLock)
                if (!_vodQueue.Contains(source, StringComparer.OrdinalIgnoreCase))
                {
                    // at the height it was being made at, not full resolution
                    if (height > 0) _vodQueueHeights[source] = height;
                    _vodQueue.Enqueue(source);
                    requeued = true;
                }
        }
        Log.Warn("ffmpeg", $"conversion of {name} {detail} — the source stopped being readable part way. "
                         + (outage is not null
                             ? $"{outage} cannot be reached right now."
                             : "It may be damaged or still being copied.")
                         + (requeued ? " It is back in the queue." : "")
                         + " It is not counted as converted; queueing or playing it converts it again.");
        OnProblem?.Invoke("conversion", $"vod {name}", detail);
    }

    /// <summary>How many times each source has been put back for ending early. See ReportEndedEarly.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _endedEarlyRequeues =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A conversion ffmpeg ran to the end writes the EXT-X-ENDLIST marker.
    ///
    /// Read from the end rather than whole. The marker is the last line of
    /// the playlist, and a feature-length conversion lists every one of its
    /// segments above it — hundreds of kilobytes that say nothing about the
    /// question being asked. Reading all of them, once per conversion, is
    /// most of what made the sweep below slow enough to be noticed.
    /// </summary>
    private static bool IsComplete(string dir)
    {
        var playlist = Path.Combine(dir, "index.m3u8");
        try
        {
            // ReadWrite | Delete: a conversion running right now is writing
            // this file, and a check must never be what makes it fail.
            using var fs = new FileStream(playlist, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            // room for the marker plus whatever trailing tags follow it
            var take = (int)Math.Min(fs.Length, 512);
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            // And not one whose input ran out (see IsFinished): that is an
            // unfinished conversion like any other, reported as one and
            // cleared by the same sweep once nothing has touched it for the
            // grace period - rather than kept for ever under a name that a
            // changed source no longer leads back to.
            return System.Text.Encoding.UTF8.GetString(buf, 0, read)
                       .Contains("#EXT-X-ENDLIST", StringComparison.Ordinal)
                   && !File.Exists(Path.Combine(dir, EndedEarlyMarker));
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    /// <summary>
    /// Names conversions that did not finish. Deletes nothing.
    ///
    /// This ran in the constructor and deleted every vod-* directory whose
    /// playlist had no EXT-X-ENDLIST - before the server had finished
    /// starting, before a line of the interface existed to say it was
    /// happening, and with no way to stop it or get any of it back.
    ///
    /// The reasoning was that an unterminated playlist means a conversion
    /// that was interrupted and is not worth keeping. The reasoning is wrong
    /// in the case that matters. This server is stopped mid-encode as a
    /// matter of routine - a restart, an upgrade, the machine sleeping, a
    /// dashboard being closed - and every conversion running at that moment
    /// becomes "incomplete". The next start then deleted it. Installing a new
    /// version does exactly that: it stops a server that may be converting,
    /// and starts one that sweeps. Hours of encoding, gone before the window
    /// opened, repeatedly, and the only trace was a log line nobody had a
    /// reason to read.
    ///
    /// That sweep was removed. This one is not it, and the difference is the
    /// grace period rather than good intentions.
    ///
    /// A partial cannot be resumed - starting the conversion again deletes the
    /// directory and encodes from the beginning - so the only thing on disk is
    /// however far it happens to play. Left alone, they accumulate: gigabytes
    /// behind a playlist listing one segment, which the owner has no reason to
    /// go looking for.
    ///
    /// What makes clearing them safe is *when*. Anything written inside the
    /// grace window is left where it is, so the conversion interrupted by the
    /// restart that led to this start survives it - which is precisely the
    /// case the old sweep destroyed, every upgrade, silently. Only work
    /// nothing has touched for a day is removed, and every removal says which
    /// stream, how big it was, and when it was last written.
    ///
    /// Set ffmpeg.partialConversionGraceHours to 0 to go back to reporting and
    /// deleting nothing.
    /// </summary>
    /// <summary>
    /// Is this conversion directory one that may be cleared: unfinished, and
    /// untouched since <paramref name="cutoff"/>?
    ///
    /// Separated out and given tests because the version of this that had no
    /// cutoff deleted the owner's work, repeatedly, and the guard is the whole
    /// of the difference.
    ///
    /// The age is the newest write *anywhere inside*, not the directory's own
    /// stamp: on Windows a directory's LastWriteTime does not move when a file
    /// inside it is appended to, so a conversion writing segments right now
    /// can look untouched for as long as it has been running — which is
    /// exactly the directory that must never be swept.
    /// </summary>
    internal static bool IsStalePartial(string dir, DateTime? cutoff,
                                        out long size, out int files, out DateTime newestWriteUtc)
    {
        size = 0;
        files = 0;
        var info = new DirectoryInfo(dir);
        // Deliberately NOT seeded with the directory's own timestamp. That
        // stamp moves whenever an entry is added or removed, which happens for
        // reasons that have nothing to do with the conversion progressing —
        // measured on the stuck Infinity War directory, whose newest segment
        // was two days old while the directory itself read as forty seconds
        // old, so seeding with it protected a conversion that was long dead.
        // The files are the work; they are what is asked.
        newestWriteUtc = DateTime.MinValue;
        if (IsComplete(dir)) { newestWriteUtc = info.LastWriteTimeUtc; return false; }

        foreach (var f in info.EnumerateFiles())
        {
            if (f.LastWriteTimeUtc > newestWriteUtc) newestWriteUtc = f.LastWriteTimeUtc;
            size += f.Length;
            files++;
        }
        // An empty directory has no work in it to date, so the only stamp
        // there is is the directory's own.
        if (files == 0) newestWriteUtc = info.LastWriteTimeUtc;
        // No cutoff means cleanup is switched off, not "everything qualifies".
        return cutoff is DateTime c && newestWriteUtc <= c;
    }

    /// <summary>
    /// Writes height.txt for conversions made before it existed, so the DLNA
    /// index can stop guessing from the directory name.
    ///
    /// Exact, and cheap: the name for a source-height conversion is computable
    /// from the source path alone, and this class is the thing that computes
    /// it. If VodStreamName(source, 0) is this directory, the conversion is
    /// full resolution — whatever the "720p" in its title suggests.
    ///
    /// Only touches directories that have no height.txt, so it does nothing on
    /// the second run.
    /// </summary>
    /// <summary>
    /// Raised when something outside the conversion COUNT has changed about
    /// them — height.txt appearing, for instance.
    ///
    /// VodIndex rebuilds when the number of directories moves, which is cheap
    /// and catches a conversion finishing or being deleted. It cannot catch a
    /// change to the contents of directories that were already there, and the
    /// height backfill is exactly that: it wrote 3,206 markers without moving
    /// the count by one, so the index went on using the answers it had read
    /// five seconds earlier and the pills did not change.
    /// </summary>
    private Action? _conversionsChanged;
    private bool _conversionsChangedPending;
    private readonly object _changedLock = new();

    public Action? ConversionsChanged
    {
        get { lock (_changedLock) return _conversionsChanged; }
        set
        {
            bool fireNow;
            lock (_changedLock)
            {
                _conversionsChanged = value;
                // Latched, because the backfill runs from this class's own
                // constructor and the listener is attached by ControlApi a
                // moment later — so the notification would otherwise be raised
                // into an empty handler and lost, which is the same stale index
                // by a different route.
                fireNow = value is not null && _conversionsChangedPending;
                if (fireNow) _conversionsChangedPending = false;
            }
            if (fireNow) { try { value!(); } catch { } }
        }
    }

    private void RaiseConversionsChanged()
    {
        Action? handler;
        lock (_changedLock)
        {
            handler = _conversionsChanged;
            if (handler is null) { _conversionsChangedPending = true; return; }
        }
        try { handler(); } catch { }
    }

    private void BackfillConversionHeights()
    {
        if (!Directory.Exists(_mediaRoot)) return;
        var written = 0;
        foreach (var dir in Directory.EnumerateDirectories(_mediaRoot, "vod-*"))
        {
            try
            {
                var marker = Path.Combine(dir, "height.txt");
                if (File.Exists(marker)) continue;
                var sourceFile = Path.Combine(dir, "source.txt");
                if (!File.Exists(sourceFile)) continue;
                var source = File.ReadAllText(sourceFile).Trim();
                if (source.Length == 0 || !File.Exists(source)) continue;
                // Only the unambiguous case is written. A directory that is not
                // the height-0 name may be a scaled copy or may have been made
                // under settings this build cannot reconstruct; the name
                // fallback still covers it, exactly as before.
                if (!string.Equals(VodStreamName(source, 0), Path.GetFileName(dir),
                                   StringComparison.OrdinalIgnoreCase)) continue;
                File.WriteAllText(marker, "0");
                written++;
            }
            catch { /* one directory failing is not a reason to stop */ }
        }
        if (written > 0)
        {
            Log.Info("ffmpeg", $"recorded the source height for {written} conversion(s) made before it was "
                             + "written down — a title ending in a resolution can no longer be mistaken "
                             + "for a scaled copy");
            // The count has not moved, so nothing else will notice. Say so.
            RaiseConversionsChanged();
        }
    }

    private void CleanUpIncompleteVodDirs()
    {
        try
        {
            if (!Directory.Exists(_mediaRoot)) return;

            var graceHours = _config.PartialConversionGraceHours;
            // Nothing running can be swept, because nothing has started yet:
            // this runs off the constructor, before the queue is restored and
            // before any request can reach StartVod. The age check below is
            // what protects the conversion that was running when this server
            // was last stopped.
            var cutoff = graceHours > 0
                ? DateTime.UtcNow - TimeSpan.FromHours(graceHours)
                : (DateTime?)null;

            var kept = 0;
            var removed = 0;
            var reclaimed = 0L;

            // Conversions already discarded (DiscardVod) whose delete could
            // not finish at the time. Nothing refers to them; they are only
            // disk space.
            foreach (var dir in Directory.EnumerateDirectories(_mediaRoot, DiscardedPrefix + "*"))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { Log.Warn("ffmpeg", $"could not delete {Path.GetFileName(dir)}: {ex.Message}"); }
            }

            foreach (var dir in Directory.EnumerateDirectories(_mediaRoot, "vod-*"))
            {
                try
                {
                    if (!IsStalePartial(dir, cutoff, out var size, out var files, out var touched))
                    {
                        if (!IsComplete(dir)) kept++;
                        continue;
                    }

                    Directory.Delete(dir, recursive: true);
                    removed++;
                    reclaimed += size;
                    Log.Info("ffmpeg", $"removed the unfinished {Path.GetFileName(dir)}: {files} file(s), {Bytes(size)}, "
                                       + $"last written {touched.ToLocalTime():yyyy-MM-dd HH:mm} "
                                       + "— it had no end marker, so there was nothing to resume");
                }
                catch (Exception ex)
                {
                    // Unreadable, or in use by something this server does not
                    // own: leave it and say so rather than retrying blindly.
                    Log.Warn("ffmpeg", $"could not clear {Path.GetFileName(dir)}: {ex.Message}");
                }
            }

            if (removed > 0)
                Log.Info("ffmpeg", $"cleaned up {removed} unfinished conversion(s), {Bytes(reclaimed)} back");
            if (kept > 0)
                Log.Info("ffmpeg", cutoff is null
                    ? $"{kept} conversion(s) did not finish and are still on disk. "
                      + "Automatic cleanup is off (ffmpeg.partialConversionGraceHours = 0); converting one again replaces it."
                    : $"{kept} conversion(s) did not finish and are still on disk, too recent to clear "
                      + $"(within {graceHours:0.#}h). Converting one again replaces it.");
        }
        catch (Exception ex) { Log.Warn("ffmpeg", $"incomplete-conversion check failed: {ex.Message}"); }
    }

    /// <summary>
    /// Whether a conversion of this stream is running now. The difference
    /// between a directory worth keeping and a part-finished one: unlinking
    /// preserves the first, and the second has nothing to preserve.
    /// </summary>
    public bool VodInProgress(string stream)
    {
        // Listed at all: an exited job stays listed only until its exit has
        // been judged, and until then it is not known to be finished.
        lock (_lock) return _vodJobs.ContainsKey(stream);
    }

    /// <summary>
    /// Throws away a finished conversion so the next play rebuilds it —
    /// what the dashboard's Retranscode offers. Used when the result is
    /// wrong rather than merely unwanted: a conversion made with codec
    /// settings since changed, or one that came out broken.
    /// </summary>
    public bool DiscardVod(string stream)
    {
        _vodDone.TryRemove(stream, out _);   // it is about to stop being done
        CancelPendingRetry(stream);          // and must not be started again behind the discard
        lock (_lock)
        {
            if (_vodJobs.TryGetValue(stream, out var p))
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                _vodJobs.Remove(stream);
                RefreshVodJobView();
            }
            StopSeekJobs(stream);
        }
        var dir = Path.Combine(_mediaRoot, stream);
        if (!Directory.Exists(dir)) return false;
        // All or nothing. A recursive delete removes every file it can before
        // it meets one that is open, so when a player was still reading a
        // segment this left half a conversion: no source.txt to rebuild from,
        // and a playlist with its end marker over missing segments that the
        // Transcodes window then called done. Moved aside first - Windows
        // refuses to rename a folder while anything inside it is open, in
        // any sharing mode (measured) - so either it all goes, or none of it.
        var aside = Path.Combine(_mediaRoot, DiscardedPrefix + stream + "-" + Guid.NewGuid().ToString("N")[..8]);
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Move(dir, aside); break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 5) { Thread.Sleep(200); }
            catch { return false; }   // still in use: left exactly as it was
        }
        try { Directory.Delete(aside, recursive: true); }
        catch (Exception ex)
        {
            // Out of use already; only the disk space is late. Swept at the next start.
            Log.Warn("ffmpeg", $"{stream} was discarded, but {Path.GetFileName(aside)} could not be deleted yet: {ex.Message}");
        }
        return true;
    }

    /// <summary>Where a discarded conversion waits to be deleted. Not "vod-", so nothing lists it as a conversion.</summary>
    private const string DiscardedPrefix = ".discarded-";

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
        // A retry waiting after a GPU refusal is this conversion too: it has
        // no job to kill yet, and left alone it would start one.
        var retry = CancelPendingRetry(stream);
        Process? p;
        lock (_lock)
        {
            StopSeekJobs(stream);
            if (!_vodJobs.Remove(stream, out p)) return retry;
            RefreshVodJobView();
        }
        // Outside the lock: KillAndRelease waits up to 2s, and PumpVodQueue takes
        // a different lock — holding _lock across either risks a stall/inversion.
        KillAndRelease(p);
        lock (_progressLock) _vodProgress.Remove(stream);
        // A cancelled conversion never reached EXT-X-ENDLIST, so its directory is
        // a partial — remove it, the same as a queued one leaves nothing behind.
        ReportIfIncomplete(stream, Path.Combine(_mediaRoot, stream));
        Log.Info("ffmpeg", $"vod job cancelled: {stream}");
        PumpVodQueue();   // a slot just freed — start the next waiting one
        return true;
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

        // Written beside the real name and moved into place, never straight
        // at it — the pattern GetStreamThumbnail below already uses.
        //
        // The cache check above is "does the file exist", with no way to tell
        // a finished thumbnail from a half-written one, and the entry is keyed
        // on the source's path, size and modification time — so it is never
        // reconsidered while the source is untouched. An ffmpeg killed
        // part-way through writing therefore left a truncated JPEG that this
        // served for good. Killing ffmpeg part-way through is not exotic: it
        // is what closing the dashboard now does, and what Task Manager or a
        // publish-and-restart always did. A move is atomic, so what lands at
        // the real name is either a whole thumbnail or nothing.
        var temp = Path.Combine(thumbDir, $"{key}.{Environment.CurrentManagedThreadId}.tmp.jpg");

        List<string> ThumbArgs(bool seek)
        {
            var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            // videos: seek before decode so a frame grab is cheap even on large files
            if (seek) a.AddRange(new[] { "-ss", "3" });
            a.AddRange(new[] { "-i", info.FullName, "-frames:v", "1", "-vf", "scale=320:-2", "-q:v", "5", temp });
            return a;
        }

        bool Landed()
        {
            if (!File.Exists(temp)) return false;
            try
            {
                File.Move(temp, thumb, overwrite: true);
                return true;
            }
            catch { try { File.Delete(temp); } catch { } return false; }
        }

        try
        {
            if (RunFfmpeg(ThumbArgs(isVideo)) && Landed()) return thumb;
            // a very short video can have nothing at 3 s — retry from the start
            if (isVideo && RunFfmpeg(ThumbArgs(false)) && Landed()) return thumb;
            try { File.Delete(temp); } catch { }
            return File.Exists(thumb) ? thumb : null;
        }
        catch
        {
            try { File.Delete(temp); } catch { }
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
        // The playlist is only safe to read once it says it is finished.
        //
        // Without EXT-X-ENDLIST an HLS playlist is a LIVE stream, and ffmpeg
        // treats it as one: asked to seek 60 seconds into a playlist that
        // lists a couple of segments, it waits for the rest to arrive. The
        // rest arrives into a file it is holding open, and the grab never
        // ends. Seen here on a conversion that had just started: the thumbnail
        // was attempted one second in.
        //
        // The segment attempts above need no such care - a .ts file is a
        // finished thing on its own - so an unfinished conversion simply gets
        // its thumbnail from a segment, or waits for the next attempt.
        var finished = false;
        try
        {
            finished = File.Exists(playlist)
                && File.ReadAllText(playlist).Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);
        }
        catch { /* unreadable: treat as unfinished */ }
        if (finished)
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
            // The timeout has to be reachable, and it was not.
            //
            // This used to read stderr to the end and then check the clock:
            //
            //     p.StandardError.ReadToEnd();
            //     if (!p.WaitForExit(timeoutMs)) ...
            //
            // ReadToEnd returns when the pipe closes, and the pipe closes when
            // the process exits - so for a process that never exits it never
            // returns, and the line below it is never reached. Measured: three
            // thumbnail grabs given a 20-second limit ran for twenty-four
            // minutes, respawning as fast as they were killed.
            //
            // An earlier pass over this file looked at exactly these lines and
            // cleared them, on the grounds that only stdout-not-being-drained
            // could deadlock. That is a different fault. This one needs no full
            // pipe at all - only a child that does not finish.
            var run = Services.ProcessJob.Run(psi, timeoutMs);
            return run is not null && run.Value.Ok;
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

    /// <summary>
    /// Tune-on-demand: starts a channel's restream when a player asks for its
    /// playlist, so picking a channel brings it up like a TV. Transient — it
    /// does not persist the channel as auto-start, only gets it running now.
    ///
    /// Returns true only when it was started just now, so the caller can wait a
    /// moment for the first segment; false if it is already running or the
    /// stream is not a known channel (an ordinary media stream, served as-is).
    /// </summary>
    public bool EnsureChannelRunning(string stream)
    {
        if (!Available) return false;
        lock (_lock)
        {
            var def = _channels.FirstOrDefault(c => ChannelStream(c.Name) == stream);
            if (def is null) return false;
            if (_liveJobs.TryGetValue(stream, out var p))
            {
                bool alive; try { alive = !p.HasExited; } catch { alive = false; }
                if (alive) return false;                    // already running
            }
            // Fresh tune: the directory still holds segments and a playlist from
            // the last run. Left there, the player loads that stale content first
            // and only reaches live once new segments push it out of the window —
            // the "starts with cached content, then switches to current" a viewer
            // sees. Clear it so playback begins at the live edge.
            ClearChannelSegments(stream);
            StartLiveJob(def.Name, def.Url);
            Log.Info("ffmpeg", $"tune-on-demand: starting channel {def.Name} (fresh)");
            return true;
        }
    }

    /// <summary>
    /// Wipes a channel's on-disk segments and playlist so a fresh restream
    /// begins at the live edge, not with whatever the last run left behind.
    /// Only for a channel that is not running — a live job owns these files.
    /// </summary>
    private void ClearChannelSegments(string stream)
    {
        try
        {
            var dir = Path.Combine(_mediaRoot, stream);
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "seg_*"))
                try { File.Delete(f); } catch { }
            foreach (var name in new[] { "index.m3u8", "index_vtt.m3u8", "init.mp4" })
                try { var f = Path.Combine(dir, name); if (File.Exists(f)) File.Delete(f); } catch { }
        }
        catch { }
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

        // One writer per channel directory, enforced here rather than by each
        // caller remembering to.
        //
        // A television left sitting on a channel retries as soon as the server
        // is back, so EnsureChannelRunning can start a job for it before
        // RestoreRunningChannels reaches the same channel — and that then
        // started a second ffmpeg into the same directory, overwriting the
        // first one's playlist and segments while the first kept writing.
        // Neither knew about the other, and the entry in _liveJobs named only
        // the newer one, so the older was never stopped: an orphan writing into
        // a live channel for as long as it lasted.
        //
        // StopJob is a no-op when nothing is listed, and it removes the entry
        // before killing, so OnLiveJobExited's ReferenceEquals check still
        // reads that death as deliberate. StartChannel and RestartChannel
        // already call it; this makes them harmlessly redundant rather than
        // load-bearing.
        StopJob(_liveJobs, stream);

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
                // Patience, tuned to match the native app. Watching these same
                // links in the provider's own player is rock-solid for hours,
                // which says the source is fine and our ingest was too quick to
                // give up: a 6-second read timeout killed the pull the moment a
                // playlist or segment was slow (the -138 deaths), and a 45s
                // reconnect budget capped in-process recovery short. Every one
                // of those exits is a process restart, and every restart resets
                // the timeline — the jumping. So: wait 20s on a read before
                // calling it dead, and reconnect in place for up to two minutes
                // rather than exiting. The watchdog window below is widened to
                // match, so it cannot kill a job that is busy recovering.
                "-rw_timeout", "20000000",              // µs — 20s
                "-reconnect", "1",
                "-reconnect_streamed", "1",
                "-reconnect_on_network_error", "1",
                "-reconnect_on_http_error", "5xx",     // never 4xx: a 404 is a rotated-out segment to skip, not retry
                "-reconnect_delay_max", "4",
                "-reconnect_max_retries", "30",
                "-reconnect_delay_total_max", "120",
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
                // Force a keyframe every segment. Without this the encoder
                // keyframes on its own schedule and the HLS muxer — which can
                // only cut a segment at a keyframe — produces wildly uneven
                // lengths (seen live: a 1.33s segment next to an 8.34s one
                // against a 4s target). Uneven segments make the live edge
                // lurch, and the browser player seeks forward to re-sync — the
                // small skips inside one programme. A keyframe exactly every
                // LiveSegmentSeconds makes every segment that length. t is the
                // post-setpts output time, so it holds whatever the source's
                // own timing does. No quality cost at a fixed CRF beyond the
                // few keyframe bits regular streaming already spends.
                args.AddRange(new[] { "-force_key_frames",
                    $"expr:gte(t,n_forced*{Inv(_config.LiveSegmentSeconds)})" });
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

                // Normalise a live channel to a standard 1080p frame.
                //
                // Some sources send an off-size raster — Pluto's server-side
                // stitcher hands back 1216x684, not any broadcast resolution —
                // and a television that scales a clean 1920x1080 to its panel
                // will show an odd size at 1:1 or overscanned instead, "too
                // big for the screen". Fitting the picture into 1920x1080 and
                // padding to it gives every set a resolution it recognises.
                //
                // force_original_aspect_ratio=decrease never enlarges past the
                // frame, so a 16:9 source lands exactly on 1920x1080 and a
                // 1080p source (a 1920x1080 channel) passes straight through
                // unchanged — nothing is downscaled, so no channel loses
                // picture. A smaller source is scaled up, which costs bits but
                // no detail, and hands the set the standard frame it wants.
                // …then flatten the timeline. An ad-stitched source resets its
                // clock at every splice — the PTS jumps from 406s back to 1.5s
                // at each break (seen directly in the output: consecutive
                // segments running 399→406 then 1.5→6.8). Over HLS that is what
                // EXT-X-DISCONTINUITY is for and the player rides it; over DLNA
                // the picture is one continuous file with no way to signal a
                // reset, so the television's clock lurches every time — the
                // "jumping around". setpts=N/FRAME_RATE/TB relabels each frame
                // by its output index instead of its source timestamp, so the
                // clock only ever climbs, whatever the input does. Unlike
                // -fps_mode cfr (tried, and it wedged: CFR waits for input time
                // to climb back past what it wrote, and an ad reset never does)
                // this drops and duplicates nothing — it renumbers frames that
                // are already flowing, so it cannot stall.
                args.AddRange(new[] { "-vf",
                    "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:-1:-1,setsar=1,setpts=N/FRAME_RATE/TB" });
            }
            args.AddRange(AudioArgs());

            // The audio half of the same flatten: relabel by sample count so
            // the audio clock climbs monotonically across the same splices, and
            // aresample=async fills or trims the gap a reset leaves so the track
            // stays level with the renumbered video. Only when both tracks are
            // being re-encoded — a copied track keeps its original timestamps
            // and cannot be relabelled on the way through.
            var flattenTimeline = !remuxAll
                && !VideoEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase)
                && !AudioEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase);
            if (flattenTimeline)
                args.AddRange(new[] { "-af", "aresample=async=1,asetpts=N/SR/TB" });

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

        var proc = Spawn(args, $"channel {name}", dir, onExited: (p, _) => OnLiveJobExited(name, url, p));
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

    /// <summary>
    /// How long a channel has to have stayed up before its death counts as a
    /// fresh problem rather than another go round the same one. The backoff
    /// below doubles per crash, so without a way back down a channel that had
    /// one bad hour in the morning would still be waiting a minute between
    /// restarts at midnight. Five minutes is well past the seconds a genuinely
    /// broken source takes to fall over, so anything that lived that long was
    /// working and deserves to start counting again from nothing.
    /// </summary>
    private static readonly TimeSpan LiveCrashForgivenAfter = TimeSpan.FromMinutes(5);

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
            var crashes = lived > LiveCrashForgivenAfter ? 0 : _liveCrashes.GetValueOrDefault(stream);
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


    /// <summary>When each conversion started, so one that has not produced
    /// anything yet is not mistaken for one that has stopped producing.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _vodStarted =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a conversion is left alone after it starts. ffmpeg opens the
    /// input, reads headers and builds its filter graph before it writes the
    /// first byte, and on a big remuxed file over a slow drive that is a long
    /// silence with nothing on disk to show for it. Two minutes is longer than
    /// that ever takes, so the watchdog never judges a job by a window it
    /// spent starting up.
    /// </summary>
    private static readonly TimeSpan VodStartGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a conversion's directory may go without growing before the job
    /// is called stuck rather than slow. Ten minutes is far longer than any gap
    /// a working encode leaves, even a slow one, so nothing that is genuinely
    /// making progress is ever killed by it; the whole point is that the slot
    /// it holds is worth more than the small chance of being wrong.
    /// </summary>
    private static readonly TimeSpan VodStuckAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The same watch the live channels get, for batch conversions - and the
    /// reason the queue could stop for good.
    ///
    /// A slot is held for as long as its ffmpeg process exists, and "exists"
    /// is not "is working". A conversion left wedged - by the machine
    /// suspending under it, by a drive that went away and came back, by
    /// ffmpeg simply hanging on a read - keeps its process, keeps its slot,
    /// and writes nothing ever again. With two slots, two of those and the
    /// queue never advances: hundreds of files still listed, nothing
    /// converting, and no error anywhere, because from the queue's point of
    /// view the machine is busy.
    ///
    /// So judge them by output, not by existence. A conversion whose
    /// directory has not grown in ten minutes is not slow, it is stuck: kill
    /// it, and the exit handler cleans up the half-finished directory and
    /// starts the next file. Ten minutes is far longer than any gap a working
    /// encode leaves - even a slow one writes a segment every few seconds -
    /// and the file it gives up on is left in the queue's own record, so
    /// nothing is silently skipped.
    /// </summary>
    public void CheckVodJobs()
    {
        if (!Available || _disposed) return;
        var stuck = new List<string>();
        // Snapshot before asking: HasExited runs the exit handler on this
        // thread, and that handler removes from _vodJobs - which, mid-foreach
        // and with a re-entrant lock, is a collection modified while walked.
        KeyValuePair<string, Process>[] jobs;
        lock (_lock) jobs = _vodJobs.ToArray();
        {
            foreach (var (stream, p) in jobs)
            {
                bool gone;
                try { gone = p.HasExited; } catch { gone = true; }
                if (gone) continue;                       // the exit handler owns it

                if (_vodStarted.TryGetValue(stream, out var started)
                    && DateTime.UtcNow - started < VodStartGrace)
                    continue;                             // still getting going

                DateTime newest;
                try
                {
                    var dir = new DirectoryInfo(Path.Combine(_mediaRoot, stream));
                    if (!dir.Exists) continue;
                    newest = dir.EnumerateFiles()
                                .Select(f => f.LastWriteTimeUtc)
                                .DefaultIfEmpty(DateTime.MinValue).Max();
                }
                catch { continue; }

                if (DateTime.UtcNow - newest > VodStuckAfter) stuck.Add(stream);
            }
        }

        foreach (var stream in stuck)
        {
            Log.Warn("ffmpeg", $"conversion {stream}: running but wrote nothing for 10 minutes - " +
                               "killing it so the queue can carry on");
            Process? p;
            lock (_lock) _vodJobs.TryGetValue(stream, out p);
            // Kill only: the Exited handler clears the slot, tidies the
            // half-written directory and starts the next file.
            try { if (p is not null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
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
    /// <summary>
    /// How long a live channel is left alone after it starts. Opening a remote
    /// source, negotiating with it and filling the first segment all happen
    /// before anything lands on disk, and an upstream that is merely slow to
    /// answer can spend most of a minute there. Ninety seconds covers that
    /// without leaving a truly dead start unnoticed for long.
    /// </summary>
    private static readonly TimeSpan LiveStartGrace = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long a running live channel may write nothing before it is judged
    /// wedged. Deliberately wider than the 120s in-process reconnect budget
    /// ffmpeg is given, because a kill here is itself a restart and a restart
    /// resets the timeline, which viewers see as a jump. This has to fire only
    /// once ffmpeg's own patient reconnect has genuinely given up, never in
    /// the middle of it. It was 90s back when a read timed out in 6s and
    /// recovery was a 45s affair; now that a read waits 20s and reconnect runs
    /// up to two minutes, 90s would shoot a job that was busy recovering.
    /// </summary>
    private static readonly TimeSpan LiveStaleAfter = TimeSpan.FromSeconds(210);

    public void CheckLiveJobs()
    {
        List<(string stream, string name)> stale = new();
        // Snapshot for the same reason as the conversion watchdog above:
        // HasExited can run the exit handler inline and mutate this table.
        KeyValuePair<string, Process>[] live;
        lock (_lock) live = _liveJobs.ToArray();
        {
            foreach (var (stream, p) in live)
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
                DateTime started;
                bool known;
                lock (_lock) known = _liveStarted.TryGetValue(stream, out started);
                if (known && DateTime.UtcNow - started < LiveStartGrace)
                    continue;                                    // still coming up

                var dir = Path.Combine(_mediaRoot, stream);
                DateTime newest;
                try
                {
                    newest = new DirectoryInfo(dir).EnumerateFiles("seg_*")
                        .Select(f => f.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                }
                catch { continue; }

                // The watchdog is for jobs that are gone, not jobs that are
                // slow; LiveStaleAfter carries the reasoning for the number.
                if (DateTime.UtcNow - newest > LiveStaleAfter)
                {
                    string label;
                    lock (_lock) label = _channels.FirstOrDefault(c => ChannelStream(c.Name) == stream)?.Name ?? stream;
                    stale.Add((stream, label));
                }
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
    /// The last eight lines a job wrote to stderr - plus what must not scroll
    /// out of them.
    ///
    /// NVENC names a refused session first and then writes nine more lines
    /// (measured; see IsGpuSessionRefusal), so an eight-line window had always
    /// lost the only line that tells a refusal from a broken file by the time
    /// anyone read it. That line is kept, and put in front of the tail when it
    /// has fallen out of it.
    ///
    /// And whether the INPUT said it ran out - see InputEnded.
    /// </summary>
    internal sealed class StderrTail
    {
        private const int Lines = 8;
        private readonly Queue<string> _lines = new(Lines);
        private string? _sessionRefusal;
        private bool _inputEnded;

        /// <summary>
        /// Whether the input said it ran out - not merely that it had a
        /// problem. Measured on cut-off sources, encoded and copied: MKV says
        /// "File ended prematurely", MP4 says "partial file"; a failed read says
        /// "I/O error". A damaged patch part way through says something else
        /// ("0x00 ... invalid as first byte of an EBML number") and the
        /// conversion carries on past it to the real end - which is why a
        /// complaint of any kind is not enough, and when in the conversion it
        /// came cannot be used either: a remux of a two-hour film takes about a
        /// second, before ffmpeg has reported its progress even once.
        /// </summary>
        public bool InputEnded { get { lock (_lines) return _inputEnded; } }

        public void Add(string line)
        {
            lock (_lines)
            {
                if (_lines.Count >= Lines) _lines.Dequeue();
                _lines.Enqueue(line);
                if (_sessionRefusal is null && line.Contains(NvencSessionRefused, StringComparison.OrdinalIgnoreCase))
                    _sessionRefusal = line;
                if (line.Contains("ended prematurely", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("partial file", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Error during demuxing", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Input/output error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("I/O error", StringComparison.OrdinalIgnoreCase))
                    _inputEnded = true;
            }
        }

        public override string ToString()
        {
            lock (_lines)
            {
                var tail = string.Join(" | ", _lines);
                return _sessionRefusal is not null && !_lines.Contains(_sessionRefusal)
                    ? _sessionRefusal + " | " + tail
                    : tail;
            }
        }
    }

    /// <summary>
    /// Starts ffmpeg. <paramref name="onExited"/> is wired before the process
    /// launches: attaching it afterwards races a job that dies immediately —
    /// a bad input fails in milliseconds — and a handler added after the event
    /// has already fired is never called, which would strand the job's entry
    /// in the tables it was meant to clean up.
    /// </summary>
    /// <param name="background">
    /// Batch work nobody is waiting on: started at below-normal priority so
    /// it fills idle cores instead of competing for busy ones. See
    /// <see cref="LowerPriority"/>.
    /// </param>
    /// <param name="onExited">
    /// Given the process and what it wrote to stderr (see StderrTail). That
    /// is passed because why a job failed decides what to do about it — an
    /// encoder that could not open a session is worth retrying, a corrupt
    /// source is not — and the caller cannot see it otherwise.
    /// </param>
    private Process Spawn(IEnumerable<string> args, string label, string? workingDir = null,
        Action<string>? onProgressLine = null, Action<Process, StderrTail>? onExited = null,
        bool background = false)
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
        var errTail = new StderrTail();
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
            errTail.Add(e.Data);
            if (trace is not null)
            {
                try
                {
                    lock (trace) trace.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {e.Data}");
                }
                catch { /* a diagnostic must never disturb the job it watches */ }
            }
        };
        // When this job began, so the line that reports it ending can say how
        // long it took. The log already timestamps both, but working a
        // duration out by subtracting two timestamps by hand — across
        // hundreds of interleaved jobs — is not the same as being told.
        var startedAt = DateTime.UtcNow;

        if (trace is not null)
            p.Exited += (_, _) => { try { lock (trace) trace.Dispose(); } catch { } };
        p.Exited += (_, _) => Task.Run(() =>
        {
            // Always on a thread-pool thread of our own, never inline.
            //
            // Process.HasExited raises this event on whatever thread noticed
            // the exit - and this server asks HasExited from inside lock(_lock).
            // Left to run inline, the handler then took _pumpLock while its
            // caller already held _lock, in the opposite order to the pump
            // path, and the two deadlocked: the whole server frozen, no
            // conversions starting, nothing in the log. Reproduced exactly
            // that way. Handing the work to the pool costs nothing and makes
            // the comment below true rather than assumed.
            //
            // An escaping exception here would terminate the server, and
            // Kill()+Dispose() elsewhere can make ExitCode throw, so
            // everything is guarded.
            // Until everything ffmpeg wrote has been read. Exited is raised
            // when the process ends, which can be before the last lines on
            // its pipes have been delivered - only the parameterless
            // WaitForExit waits for those. The lines that arrive last are
            // the ones that matter most here: a source cut off part way says
            // so moments before ffmpeg exits (see StderrTail.InputEnded), and
            // judging without that line would take the cut for a whole film.
            try { p.WaitForExit(); } catch { /* already reaped */ }
            try
            {
                if (_disposed) return;
                var tail = errTail.ToString();
                var code = p.ExitCode;
                if (code == 0)
                    Log.Info("ffmpeg", $"finished: {label} — took {Elapsed(startedAt)}");
                else
                {
                    Log.Warn("ffmpeg", $"failed: {label} after {Elapsed(startedAt)} — exited with code {code}"
                                       + (tail.Length > 0 ? " — " + tail : ""));
                    // A conversion that failed is the thing most worth seeing
                    // and the thing this log line was least good at showing:
                    // one warning in a stream of thousands, hours before
                    // anybody looks. Also listed, with ffmpeg's own last words
                    // as the detail, because they usually name the cause.
                    OnProblem?.Invoke("conversion", label,
                        $"ffmpeg exited with code {code}" + (tail.Length > 0 ? " — " + tail : ""));
                }
            }
            catch { /* process already reaped/disposed — nothing to report */ }

            // caller's cleanup, guarded for the same reason
            try { onExited?.Invoke(p, errTail); }
            catch (Exception ex) { Log.Warn("ffmpeg", $"{label}: exit handler failed: {ex.Message}"); }
        });
        p.Start();
        // Immediately, before it can do any work: a child that outlives a
        // hard kill of this server becomes a second writer in the same
        // channel directory the next time the server starts. See ProcessJob.
        Services.ProcessJob.Adopt(p);
        if (background) LowerPriority(p, label);
        RememberPid(p);
        p.BeginErrorReadLine();
        if (onProgressLine is not null) p.BeginOutputReadLine();
        Log.Info("ffmpeg", $"started: {label}");
        // "started:" and "finished:" both lead with the same word shape on
        // purpose — one grep for either finds the pair, where "started: vod X"
        // against "vod X: finished" did not.
        return p;
    }

    /// <summary>
    /// The audio half of every transcode — conversions, seeks and live
    /// channels alike, which is why it is one method and not three copies.
    ///
    /// The sample rate is the part worth explaining. ffmpeg keeps the
    /// source's rate unless told otherwise, and a library assembled over
    /// twenty years is full of odd ones: an old AVI with 24kHz MP3, a rip
    /// with 16kHz. Re-encoding those to AAC produces a file that is
    /// perfectly legal, plays correctly in a browser, and is refused by a
    /// television, whose hardware decoder handles the broadcast rates and
    /// little else. What the owner sees is a film with no sound and a set
    /// reporting the audio as unrecognisable - while the original plays
    /// fine, because the original was not AAC in an HLS stream.
    ///
    /// Measured over this library: about one conversion in seven came out at
    /// 16kHz or 24kHz. They were not damaged and nothing had failed. They
    /// were simply mute on the device most likely to play them.
    ///
    /// 48kHz because that is what HLS delivery assumes and every decoder
    /// accepts. Upsampling 16kHz audio adds nothing to the sound, which is
    /// not the point: the point is that it plays at all.
    ///
    /// "copy" is left exactly as it was. Asking for the original stream
    /// untouched is asking for the original stream untouched, rate included.
    /// </summary>
    private string[] AudioArgs() =>
        AudioEncoder.Equals("copy", StringComparison.OrdinalIgnoreCase)
            ? new[] { "-c:a", "copy" }
            : new[] { "-c:a", AudioEncoder, "-b:a", "160k", "-ac", "2", "-ar", "48000" };

    /// <summary>
    /// Drops a batch conversion to below-normal priority.
    ///
    /// A queue of conversions is work nobody is waiting on — it was queued
    /// precisely so it could happen without anybody watching it. But ffmpeg
    /// is launched with no thread limit, so each encode spreads across every
    /// core it can reach, and six of them at once (this server's own
    /// maxParallel) will take the machine from whoever is using it. The
    /// symptom is not a slow conversion, which nobody would notice; it is
    /// everything else on the box going treacly while the queue drains.
    ///
    /// Below-normal fixes that without costing throughput, because it is not
    /// a cap. The scheduler hands these threads every core nothing else
    /// wants, which on an otherwise idle machine is all of them — the queue
    /// runs exactly as fast as it did. It only yields when something at
    /// normal priority actually wants the CPU, which is the entire point.
    ///
    /// Deliberately not applied to the other two things this class spawns.
    /// A live channel is feeding a television in real time and a seek
    /// conversion has somebody sitting in front of the player waiting for it;
    /// both are somebody waiting, which is the one thing a batch job is not.
    /// </summary>
    private static void LowerPriority(Process p, string label)
    {
        try
        {
            p.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            // Never worth failing a conversion over: a job at the wrong
            // priority still converts. It can also simply have finished
            // already, which throws here and is not a fault at all.
            Log.Debug("ffmpeg", $"{label}: could not lower priority ({ex.Message})");
        }
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

    /// <summary>
    /// Stops the jobs, but never waits long to be allowed to.
    ///
    /// The wait was the problem, not the work. Everything inside the lock
    /// here is fast — StopJob kills synchronously and hands the reaping to
    /// the pool — but _lock is the same one StartVod holds for the whole of
    /// starting a conversion, and that span covers a recursive delete of a
    /// part-finished directory and an ffprobe of the source with a fifteen
    /// second timeout. PumpVodQueue starts the next queued file on its own,
    /// so a start can be in flight at the moment somebody closes the
    /// dashboard with nobody having touched anything.
    ///
    /// Blocking there put the whole teardown behind it: shutdown stalled
    /// until the five second watchdog in Program.cs called Environment.Exit,
    /// which is both far longer than closing a window should take and a path
    /// that skips the final state flush that comes after this. So the lock is
    /// asked for, briefly, and declined if a start is mid-flight.
    ///
    /// Giving up the graceful pass costs nothing on Windows: the job object
    /// takes every child with the process (see ProcessJob), which is the real
    /// guarantee here — this pass is only politeness. Elsewhere the startup
    /// sweep clears what is left, exactly as it does after a crash.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _staggerTimer?.Dispose();
        foreach (var stream in _pendingRetries.Keys.ToList()) CancelPendingRetry(stream);
        if (!Monitor.TryEnter(_lock, TimeSpan.FromMilliseconds(250)))
        {
            Log.Warn("ffmpeg", "shutdown: a job is still starting — leaving the children to the job object");
            return;
        }
        try
        {
            foreach (var key in _seekJobs.Keys.ToList()) StopSeekJobs(key);
            foreach (var key in _vodJobs.Keys.ToList()) StopJob(_vodJobs, key);
            foreach (var key in _liveJobs.Keys.ToList()) StopJob(_liveJobs, key);
        }
        finally { Monitor.Exit(_lock); }
    }
}
