using System.Diagnostics;
using System.Text.Json;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// Whether a television can be expected to play a file as it stands.
///
/// A PC plays everything because VLC and the browser decode in software —
/// whatever ffmpeg knows, they play. A television decodes in hardware and
/// plays exactly the short list its silicon implements: H.264, HEVC, and on
/// newer sets AV1. Everything else is a black screen or "file not supported",
/// which is why half a library of DVD-era rips is unwatchable on the TV and
/// perfectly fine on the desk.
///
/// This is the check that tells those two groups apart, so the server can
/// hand over the original where the original is playable — no re-encode, no
/// quality lost — and reach for a conversion only where it genuinely is the
/// difference between playing and not.
///
/// Probing costs an ffprobe launch per file, so answers are cached by path,
/// size and modification time. A file that changes is re-probed; a file
/// that doesn't is asked once ever.
/// </summary>
public sealed class TvCodecs
{
    /// Decoders a television of the last decade can be relied on to have.
    /// Deliberately short: guessing wrong in this direction means the TV
    /// shows an error, and guessing wrong in the other direction only means
    /// a conversion that turns out not to have been needed.
    private static readonly HashSet<string> PlayableVideo = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "hevc", "h265", "av1", "vp9", "mpeg2video",
    };

    /// Soundtracks a television can decode. DTS and TrueHD are the usual
    /// absentees — a set without a DTS licence plays the picture and stays
    /// silent, which looks like a different fault than it is.
    private static readonly HashSet<string> PlayableAudio = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "ac3", "eac3", "mp3", "mp2", "opus", "flac",
        "pcm_s16le", "pcm_s24le", "pcm_s16be",
    };

    /// Containers a TV refuses regardless of what is inside them. A VOB is a
    /// DVD program stream: the MPEG-2 in it is decodable, but the wrapper is
    /// not something a DLNA client is built to open, and a set of them is a
    /// film cut into 1 GB pieces besides.
    ///
    /// MKV is the odd one here: the codecs inside are almost always fine —
    /// h264/AAC in Matroska is nearly as common as in MP4 — and it plays
    /// perfectly well locally or through this server's own web player. DLNA
    /// specifically is where it falls down. A wide range of real DLNA
    /// renderers, television firmware very much included, either refuse a
    /// Matroska container outright or accept it and then can't seek within
    /// it — a byte-range request lands wherever, MKV's parseable positions
    /// are governed by its own cue index rather than uniform playback time,
    /// and a renderer without that index open just guesses. That is a
    /// closer match to what was reported than anything about the codecs
    /// checked below: a film that never starts, or starts and then can't be
    /// advanced through. Treating the container itself as the reason, the
    /// same as the others here, is what gets it a real converted copy
    /// instead of the raw file the checks further down would otherwise wave
    /// through.
    private static readonly HashSet<string> UnplayableContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vob", ".ifo", ".divx", ".rm", ".rmvb", ".ogm", ".asf", ".mkv",
    };

    /// <summary>Told when a file cannot be read, so it can be listed rather than only logged.</summary>
    public Action<string, string, string>? OnProblem { get; set; }

    private readonly string _cacheFile;
    private readonly string _ffprobe;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _dirty;
    private int _sinceSave;

    /// <summary>
    /// Where this server writes its own conversions, or "" when that is not
    /// known. Nothing under it is ever probed or cached.
    ///
    /// A conversion is thousands of HLS segments, and a segment is not a
    /// library item: nothing ever asks whether a television can play
    /// seg_00417.ts, because it is never offered one. But the transcode
    /// panel can be pointed at any folder, and pointing it here queued every
    /// segment for probing like any other media file.
    ///
    /// Measured on this install: 114,643 cache entries, of which 108,084 were
    /// .ts segments under the conversions folder — sixteen segments cached for
    /// every real film. The file had reached 14 MB, and it is read whole at
    /// every start and written whole every two hundred probes.
    /// </summary>
    private readonly string _conversionsRoot;

    /// <summary>True for a path inside this server's own conversions folder.</summary>
    private bool IsConversionOutput(string file)
    {
        if (_conversionsRoot.Length == 0) return false;
        try
        {
            var full = Path.GetFullPath(file);
            return full.StartsWith(_conversionsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public TvCodecs(string baseDirectory, string ffprobePath, string? conversionsRoot = null)
    {
        _cacheFile = Path.Combine(baseDirectory, "probe-cache.json");
        _ffprobe = ffprobePath;
        _conversionsRoot = string.IsNullOrWhiteSpace(conversionsRoot)
            ? ""
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(conversionsRoot));
        try
        {
            if (File.Exists(_cacheFile))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_cacheFile));
                if (loaded is not null)
                {
                    _cache = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
                    // Drop the failures an older build recorded as answers.
                    // "|" is a probe that returned nothing, and it was being
                    // read as "no video codec", which both callers take to
                    // mean a television can play the file — so those entries
                    // are films quietly excluded from conversion for good.
                    // Forgetting them costs one ffprobe each on the next
                    // sweep; keeping them costs the film. See Codecs().
                    var failed = _cache.Where(kv => kv.Value is "|").Select(kv => kv.Key).ToList();
                    foreach (var k in failed) _cache.Remove(k);
                    if (failed.Count > 0)
                    {
                        _dirty = true;
                        Log.Info("probe", $"forgetting {failed.Count} failed probe(s) recorded as playable — they will be read again");
                    }
                    PruneStale();
                    // Written back now rather than whenever the next probe
                    // happens to flush, so the repair is done once instead
                    // of on every start for the life of the file.
                    if (_dirty) Save();
                }
            }
        }
        catch { /* a corrupt cache is a cache miss, not a failure */ }
    }

    /// <summary>
    /// Drops entries that can never be read again.
    ///
    /// The key is path|size|modified, so editing, replacing or re-encoding a
    /// file writes a new entry and strands the old one — and nothing ever
    /// removed it. Measured on this install: 114,643 entries describing 5,363
    /// files, twenty-one stale rows for every live one, in a file loaded whole
    /// into memory on every start and rewritten whole every two hundred probes.
    /// It only ever grew.
    ///
    /// A key is worth keeping when its file still exists at that exact size
    /// and date; anything else is a row no lookup can ever hit again, because
    /// Codecs() builds the key from the file as it is now.
    ///
    /// Grouped by path so this costs one stat per FILE rather than one per
    /// entry — twenty-one times fewer, which is the difference between a
    /// second and a minute on a library this size.
    /// </summary>
    private void PruneStale()
    {
        var live = new Dictionary<string, string>(_cache.Count, StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var segments = 0;
        foreach (var key in _cache.Keys)
        {
            if (SplitKey(key) is not { } parts) continue;      // unrecognisable: dropped
            // A conversion segment costs nothing to reject and there are
            // tens of thousands of them, so they go before any stat.
            if (IsConversionOutput(parts.path)) { segments++; continue; }
            if (!byPath.TryGetValue(parts.path, out var keys))
                byPath[parts.path] = keys = new List<string>();
            keys.Add(key);
        }

        foreach (var (path, keys) in byPath)
        {
            string? current = null;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) current = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }
            catch { /* unreadable path: every key for it goes */ }
            if (current is null) continue;
            foreach (var key in keys)
                if (key.Equals(current, StringComparison.OrdinalIgnoreCase) && _cache.TryGetValue(key, out var v))
                    live[key] = v;
        }

        var dropped = _cache.Count - live.Count;
        if (dropped <= 0) return;
        // Rebuilt in place: the field is readonly, and every other
        // reader holds the same reference.
        _cache.Clear();
        foreach (var kv in live) _cache[kv.Key] = kv.Value;
        _dirty = true;
        Log.Info("probe", $"probe cache: dropped {dropped} entry(s) - {segments} conversion segment(s) that "
                          + $"should never have been probed, {dropped - segments} for files that changed or went - "
                          + $"{live.Count} left");
    }

    /// <summary>The path out of a path|size|modified key, or null if it is not one.</summary>
    private static (string path, long size, long ticks)? SplitKey(string key)
    {
        var lastBar = key.LastIndexOf('|');
        if (lastBar <= 0) return null;
        var firstOfTwo = key.LastIndexOf('|', lastBar - 1);
        if (firstOfTwo <= 0) return null;
        if (!long.TryParse(key[(firstOfTwo + 1)..lastBar], out var size)) return null;
        if (!long.TryParse(key[(lastBar + 1)..], out var ticks)) return null;
        return (key[..firstOfTwo], size, ticks);
    }

    /// <summary>Codecs of a file, from the cache when its size and date are unchanged.</summary>
    public (string? video, string? audio) Codecs(string file)
    {
        // Never the server's own output: see _conversionsRoot.
        if (IsConversionOutput(file)) return (null, null);
        string key;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return (null, null);
            key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch { return (null, null); }

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                var p = hit.Split('|');
                return (Blank(p.ElementAtOrDefault(0)), Blank(p.ElementAtOrDefault(1)));
            }
        }

        var probed = Probe(file);

        // A probe that failed is not an answer, and must not be filed as one.
        //
        // Probe returns (null, null) three ways: ffprobe would not start, it
        // was still running after twenty seconds and was killed, or it threw.
        // All three used to be written into the cache as "|" — and both
        // readers below treat a null video codec as "a television can play
        // this", so the file was counted as TV-ready, dropped out of the
        // conversion list, and never looked at again, because a key that is
        // present is never re-probed. There is no expiry here to rescue it.
        //
        // Found in this install's own cache: 28 files written off that way,
        // whole seasons of one series among them. The timeout is the likely
        // culprit and bulk conversion is exactly when it bites — twenty
        // seconds is generous for reading stream headers, but not while
        // several encoders have the disk.
        //
        // So leave a failure uncached. The file goes back to "not read yet",
        // the next sweep tries again, and one that genuinely cannot be read
        // costs one ffprobe per sweep rather than a permanent wrong answer.
        if (probed.video is null && probed.audio is null)
        {
            // Listed as well as logged. This is the failure that was costing
            // whole films silently, so it belongs somewhere a person looks.
            OnProblem?.Invoke("probe", file, "could not be read — ffprobe returned nothing");
            return probed;
        }

        bool flush;
        lock (_lock)
        {
            _cache[key] = $"{probed.video}|{probed.audio}";
            _dirty = true;
            // Write it down every so often rather than only at the end of a
            // scan. Probing this library is an hour of ffprobe launches, and
            // the process is usually killed rather than asked to stop — so
            // "save when finished" means that hour is lost to any restart
            // that lands mid-scan. 200 files is a few seconds of work to
            // lose, against a file write of a few hundred KB.
            flush = ++_sinceSave >= 200;
            if (flush) _sinceSave = 0;
        }
        if (flush) Save();
        return probed;

        static string? Blank(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>
    /// True when a television would need a converted copy to play this file.
    ///
    /// A file that cannot be probed at all answers false: an unknown file is
    /// not evidence of a problem, and converting on a guess would burn hours
    /// of CPU on something that might have played perfectly well.
    /// </summary>
    public bool NeedsConversion(string file)
    {
        if (UnplayableContainers.Contains(Path.GetExtension(file))) return true;

        var (video, audio) = Codecs(file);
        if (video is null) return false;                      // unprobeable: leave it alone
        if (!PlayableVideo.Contains(video)) return true;
        if (audio is not null && !PlayableAudio.Contains(audio)) return true;
        return false;
    }

    /// <summary>
    /// Like <see cref="NeedsConversion"/> but never launches ffprobe: returns
    /// null when the file has not been probed yet. A folder summary walks a
    /// whole subtree, and probing every file it meets would turn opening a
    /// directory into an hour of ffprobe launches — so the summary asks this
    /// instead and shows "checking…" for what isn't known yet. An unplayable
    /// container is decided by extension alone, so it still answers without a
    /// probe.
    /// </summary>
    public bool? NeedsConversionCached(string file)
    {
        if (UnplayableContainers.Contains(Path.GetExtension(file))) return true;

        string key;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return null;
            key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch { return null; }

        string? video, audio;
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var hit)) return null;   // not probed yet
            var p = hit.Split('|');
            video = string.IsNullOrEmpty(p.ElementAtOrDefault(0)) ? null : p[0];
            audio = !string.IsNullOrEmpty(p.ElementAtOrDefault(1)) ? p[1] : null;
        }
        if (video is null) return false;                 // probed but unreadable: leave alone
        if (!PlayableVideo.Contains(video)) return true;
        if (audio is not null && !PlayableAudio.Contains(audio)) return true;
        return false;
    }

    public void Save()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                File.WriteAllText(_cacheFile, JsonSerializer.Serialize(_cache));
                _dirty = false;
            }
            catch { /* the cache is an optimisation; failing to keep it is not an error */ }
        }
    }

    private (string? video, string? audio) Probe(string file)
    {
        try
        {
            var psi = new ProcessStartInfo(_ffprobe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name",
                                      "-of", "csv=p=0", file })
                psi.ArgumentList.Add(a);
            // Both pipes drained together, with a timeout that can actually
            // fire - see ProcessJob.Run. ffprobe has a great deal to say on
            // stderr about a damaged file even under "-v error", and reading
            // only stdout wedged this call, and the whole batch queue behind
            // it, against the first such file in the library.
            var run = Services.ProcessJob.Run(psi, 20_000);
            if (run is null) return (null, null);
            if (run.Value.TimedOut)
            {
                Log.Warn("ffmpeg", $"ffprobe gave up on {Path.GetFileName(file)} after 20s - left unread");
                return (null, null);
            }
            var output = run.Value.StdOut;

            string? video = null, audio = null;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(',');
                if (parts.Length < 2) continue;
                var name = parts[0];       // csv order follows -show_entries: codec_name,codec_type
                var type = parts[1];
                if (type == "video" && video is null) video = name;
                else if (type == "audio" && audio is null) audio = name;
            }
            return (video, audio);
        }
        catch { return (null, null); }
    }
}
