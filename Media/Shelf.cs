using System.Text.Json;
using System.Text.Json.Serialization;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// The shelf: every film in the library, ready for the television.
///
/// The library is 3 TB of thirty years of rips, and most of it predates
/// H.264. A television plays the H.264 files and refuses the rest, which is
/// not something the server can fix at the moment somebody presses play —
/// converting a two-hour film takes longer than the patience of anyone
/// standing in front of a TV holding a remote. So it is done beforehand:
/// walk the library, find what the TV cannot decode, and convert those and
/// only those, at full resolution, one at a time, in the background.
///
/// Three rules keep this from being destructive:
///
///   Nothing is replaced. Conversions live in the media cache beside the
///   library; the originals are never touched, moved, or rewritten.
///
///   Nothing is downscaled. Every conversion is height 0 — same picture
///   size as the source. The point is a decoder the TV has, not a smaller
///   file.
///
///   Nothing fills the disk. The worker stops before free space falls under
///   a floor, and says so, rather than converting until Windows starts
///   failing writes.
///
/// It is resumable by construction: StartVod's cache key is the source path,
/// size and mtime, so a conversion that already exists is recognised and
/// skipped. Stopping and starting the server, or the shelf, loses only the
/// file that was in flight.
/// </summary>
public sealed class Shelf
{
    public sealed class State
    {
        /// Whether the worker should be running. Persisted, so a shelf left
        /// running survives a restart and picks up where it stopped.
        [JsonPropertyName("running")] public bool Running { get; set; }

        /// Files that failed, and why. Kept so a second run doesn't spend
        /// another hour rediscovering the same broken download.
        [JsonPropertyName("failed")] public Dictionary<string, string> Failed { get; set; } = new();

        /// Free space to leave alone, in GB.
        [JsonPropertyName("freeSpaceFloorGb")] public double FreeSpaceFloorGb { get; set; } = 100;
    }

    private readonly string _file;
    private readonly FfmpegManager _ffmpeg;
    private readonly TvCodecs _codecs;
    private readonly Func<IReadOnlyList<string>> _folders;
    private readonly string _mediaRoot;
    private readonly object _lock = new();

    private State _state = new();
    private Thread? _worker;
    private CancellationTokenSource? _cancel;

    // What the last scan found, so the dashboard can report without walking
    // 4,400 files on every poll.
    private volatile int _total, _done, _skipped;
    private volatile string _current = "";
    private volatile string _note = "";

    public Shelf(string baseDirectory, string mediaRoot, FfmpegManager ffmpeg, TvCodecs codecs,
                 Func<IReadOnlyList<string>> folders)
    {
        _file = Path.Combine(baseDirectory, "shelf.json");
        _mediaRoot = mediaRoot;
        _ffmpeg = ffmpeg;
        _codecs = codecs;
        _folders = folders;
        Load();
    }

    /// Extensions worth looking at. Everything else in a film folder is a
    /// subtitle, a poster or a readme.
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".vob",
        ".ts", ".m2ts", ".webm", ".flv", ".divx", ".asf", ".rm", ".rmvb", ".ogm",
    };

    public bool Running => _state.Running;
    public string Current => _current;
    public string Note => _note;
    public int Total => _total;
    public int Done => _done;
    public int Skipped => _skipped;
    public IReadOnlyDictionary<string, string> Failed => _state.Failed;
    public double FreeSpaceFloorGb => _state.FreeSpaceFloorGb;

    public void Start()
    {
        lock (_lock)
        {
            _state.Running = true;
            Save();
            if (_worker is { IsAlive: true }) return;
            // Getting here proves the previous worker has finished, because a
            // live one returns on the line above, and that worker was the only
            // thing holding the old token. So the source it ran on is now
            // nobody's, and this is the one moment it can be released without
            // pulling a token out from under a thread that is still reading
            // it. Started and stopped through an evening it would otherwise
            // leave a handle behind per run.
            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
            _worker = new Thread(() => Work(_cancel.Token))
            {
                IsBackground = true,
                Name = "shelf",
                // Below everything else on the machine. This runs for days;
                // it must never be the reason a film stutters.
                Priority = ThreadPriority.Lowest,
            };
            _worker.Start();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _state.Running = false;
            Save();
            _cancel?.Cancel();
            // The conversion in flight is left to finish. Killing it would
            // leave a part-written directory that the next run would have to
            // detect and clean, and a film that is 90% converted is worth
            // more than the minute saved by stopping now.
            //
            // Which is why the source is usually not disposed here: that
            // worker keeps reading the token for as long as its conversion
            // takes, and pulling the source out from under a live reader is
            // the kind of unsafe that only shows itself under load. The next
            // Start releases it instead, and Start can only reach that point
            // once the worker is gone. A shelf stopped with no worker left is
            // the one case that can be tidied here, and the field is cleared
            // with it so a second Stop cannot cancel a disposed source.
            if (_worker is not { IsAlive: true })
            {
                _cancel?.Dispose();
                _cancel = null;
            }
            _note = "stopping after the current conversion";
        }
    }

    /// <summary>Resume a shelf that was running when the server was last shut down.</summary>
    public void ResumeIfWasRunning()
    {
        if (_state.Running) Start();
    }

    /// <summary>
    /// What the shelf would do, without doing it: how many files the TV
    /// cannot play, and how much of them is already converted. Walks the
    /// library, so it is not something to call on a timer.
    /// </summary>
    public (int need, int have, long sourceBytes) Survey(CancellationToken ct = default)
    {
        int need = 0, have = 0;
        long bytes = 0;
        foreach (var file in Library(ct))
        {
            if (ct.IsCancellationRequested) break;
            if (!_codecs.NeedsConversion(file)) continue;
            need++;
            if (AlreadyConverted(file)) have++;
            else { try { bytes += new FileInfo(file).Length; } catch { } }
        }
        _codecs.Save();
        return (need, have, bytes);
    }

    private IEnumerable<string> Library(CancellationToken ct)
    {
        foreach (var root in _folders())
        {
            if (ct.IsCancellationRequested) yield break;
            if (!Directory.Exists(root)) continue;

            IEnumerator<string> walk;
            try
            {
                walk = Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                }).GetEnumerator();
            }
            catch { continue; }

            using (walk)
            {
                while (true)
                {
                    // MoveNext is what touches the disk, and a folder that
                    // disappears mid-walk throws from there rather than from
                    // the call that opened it.
                    string file;
                    try { if (!walk.MoveNext()) break; file = walk.Current; }
                    catch { break; }
                    if (VideoExtensions.Contains(Path.GetExtension(file))) yield return file;
                }
            }
        }
    }

    /// <summary>A finished, full-resolution conversion of this file already on disk.</summary>
    private bool AlreadyConverted(string file)
    {
        var dir = ConversionDirFor(file);
        if (dir is null) return false;
        try
        {
            var text = File.ReadAllText(Path.Combine(dir, "index.m3u8"));
            return text.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>
    /// Where StartVod would put this file's full-resolution conversion.
    /// Asked for rather than recomputed, so the two can never drift apart.
    /// </summary>
    private string? ConversionDirFor(string file)
    {
        var name = _ffmpeg.VodStreamName(file, 0);
        return name is null ? null : Path.Combine(_mediaRoot, name);
    }

    private void Work(CancellationToken ct)
    {
        Log.Info("shelf", "started — looking for media the TV cannot play");
        try
        {
            var files = new List<string>();
            _note = "scanning the library";
            foreach (var f in Library(ct)) files.Add(f);
            _codecs.Save();

            var queue = new List<string>();
            var have = 0;
            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) break;
                if (!_codecs.NeedsConversion(f)) continue;
                // Conversions that already exist count as shelved, and are
                // marked as such. They were made incidentally — somebody
                // played the film once — which means the cache cap still
                // owns them, and the cap is 10 GB against a shelf measured
                // in hundreds. Left unmarked they would be evicted as the
                // shelf grew, and the shelf would convert them again.
                if (AlreadyConverted(f))
                {
                    have++;
                    var existing = _ffmpeg.VodStreamName(f, 0);
                    if (existing is not null) MarkAsShelf(existing);
                    continue;
                }
                if (_state.Failed.ContainsKey(f)) continue;
                queue.Add(f);
            }
            _codecs.Save();

            // Smallest first. A shelf that produces fifty watchable films on
            // the first evening is more use than one that spends that evening
            // on a single 4K remux, and if it is stopped early the work that
            // did happen covers more of the library.
            queue.Sort((a, b) => Size(a).CompareTo(Size(b)));

            _total = queue.Count + have;
            _done = have;
            _skipped = _state.Failed.Count;
            Log.Info("shelf", $"{queue.Count} to convert, {have} already done, {_state.Failed.Count} skipped earlier");

            foreach (var file in queue)
            {
                if (ct.IsCancellationRequested || !_state.Running) break;

                var free = FreeBytes();
                if (free > 0 && free < (long)(_state.FreeSpaceFloorGb * 1024 * 1024 * 1024))
                {
                    _note = $"paused — under {_state.FreeSpaceFloorGb:0} GB free";
                    Log.Warn("shelf", _note + "; stopping so the disk does not fill");
                    lock (_lock) { _state.Running = false; Save(); }
                    break;
                }

                _current = Path.GetFileName(file);
                _note = "converting";
                try
                {
                    var (stream, _) = _ffmpeg.StartVod(file, 0);
                    MarkAsShelf(stream);

                    // Wait it out. The conversion runs in ffmpeg; this thread
                    // only needs to know when to start the next one.
                    while (_ffmpeg.VodInProgress(stream))
                    {
                        if (ct.IsCancellationRequested) { _note = "stopping after this conversion"; }
                        Thread.Sleep(2000);
                    }

                    if (AlreadyConverted(file)) { _done++; }
                    else
                    {
                        Remember(file, "conversion did not finish");
                        _skipped++;
                    }
                }
                catch (Exception ex)
                {
                    Remember(file, ex.Message);
                    _skipped++;
                    Log.Warn("shelf", $"skipped {Path.GetFileName(file)}: {ex.Message}");
                }
                _current = "";
            }

            if (!ct.IsCancellationRequested && _state.Running)
            {
                _note = "done";
                lock (_lock) { _state.Running = false; Save(); }
                Log.Info("shelf", $"finished — {_done} converted, {_skipped} skipped");
            }
            else if (_note != "done") _note = "stopped";
        }
        catch (Exception ex)
        {
            _note = "stopped: " + ex.Message;
            Log.Error("shelf", "worker failed: " + ex);
            lock (_lock) { _state.Running = false; Save(); }
        }
        finally
        {
            _current = "";
            _codecs.Save();
        }
    }

    private static long Size(string f)
    {
        try { return new FileInfo(f).Length; } catch { return long.MaxValue; }
    }

    private long FreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(_mediaRoot)!).AvailableFreeSpace; }
        catch { return 0; }
    }

    /// <summary>
    /// Marks a conversion as belonging to the shelf, which exempts it from
    /// the cache's size cap. Without this the LRU would evict the shelf as
    /// fast as it was built — the cap exists to stop incidental conversions
    /// accumulating, and these are the opposite of incidental.
    /// </summary>
    private void MarkAsShelf(string stream)
    {
        try { File.WriteAllText(Path.Combine(_mediaRoot, stream, "shelf.txt"), "kept for the TV"); }
        catch { }
    }

    private void Remember(string file, string reason)
    {
        lock (_lock) { _state.Failed[file] = reason; Save(); }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_file)) ?? new State();
        }
        catch { _state = new State(); }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(_state,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Forget the skip list, so a second run retries what failed.</summary>
    public void ClearFailures()
    {
        lock (_lock) { _state.Failed.Clear(); Save(); }
        _skipped = 0;
    }
}
