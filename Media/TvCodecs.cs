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

    /// <summary>
    /// The background prune, so a test can wait for it.
    ///
    /// Production never does: the whole point is that the server comes up
    /// without it. But "it happens eventually" is not something a test can
    /// assert, and the alternative — pruning synchronously so the tests stay
    /// simple — is the 59 seconds this moved off the startup path. Exposing
    /// the task keeps both honest.
    /// </summary>
    internal Task? Pruning { get; private set; }

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
                    // Pruning is housekeeping, and it was holding up the
                    // dashboard.
                    //
                    // It stats every cached library file to see whether it is
                    // still there at that size and date. On this install that
                    // is thousands of stats against a large archive drive, and
                    // the startup instrumentation caught it costing **59.2
                    // seconds** — during which nothing had bound the control
                    // port, so the dashboard simply did not answer and the
                    // desktop icon looked dead.
                    //
                    // Nothing needs it done first. A stale entry cannot give a
                    // wrong answer: the key carries the file's size and
                    // modification time, so a changed or missing file misses
                    // the cache and is re-probed. The only cost of a stale
                    // entry is the bytes it occupies. So it runs behind the
                    // server coming up.
                    Pruning = Task.Run(() =>
                    {
                        try
                        {
                            PruneStale();
                            // Written back rather than waiting for the next
                            // probe to flush, so the repair is done once
                            // instead of on every start for the life of the
                            // file.
                            if (_dirty) Save();
                        }
                        catch (Exception ex) { Log.Warn("probe", $"pruning the probe cache failed: {ex.Message}"); }
                    });
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
    /// <summary>
    /// Drops cache entries whose file has changed or gone.
    ///
    /// Two things here are load-bearing now that this runs on a background
    /// thread instead of during construction.
    ///
    /// The stat pass holds no lock. It is the slow half — thousands of stats
    /// against an archive drive, measured at 59 seconds — and holding
    /// <c>_lock</c> across it would block every probe and every lookup for
    /// the duration, which is the startup stall this moved off the critical
    /// path, reintroduced somewhere worse.
    ///
    /// The write pass removes only the keys it decided were dead, under the
    /// lock. It does NOT clear and repopulate, which is what it used to do:
    /// clearing threw away every probe recorded during those 59 seconds, and
    /// did it while other threads were reading the same dictionary.
    /// </summary>
    private void PruneStale()
    {
        List<string> keys;
        lock (_lock) keys = new List<string>(_cache.Keys);

        var byPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var doomed = new List<string>();
        var segments = 0;
        foreach (var key in keys)
        {
            if (SplitKey(key) is not { } parts) { doomed.Add(key); continue; }   // unrecognisable
            // A conversion segment costs nothing to reject and there are tens
            // of thousands of them, so they go before any stat.
            if (IsConversionOutput(parts.path)) { doomed.Add(key); segments++; continue; }
            if (!byPath.TryGetValue(parts.path, out var forPath))
                byPath[parts.path] = forPath = new List<string>();
            forPath.Add(key);
        }

        foreach (var (path, forPath) in byPath)
        {
            string? current = null;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) current = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }
            catch { /* unreadable path: every key for it goes */ }

            foreach (var key in forPath)
                if (current is null || !key.Equals(current, StringComparison.OrdinalIgnoreCase))
                    doomed.Add(key);
        }

        int dropped, left;
        lock (_lock)
        {
            dropped = 0;
            foreach (var key in doomed)
                if (_cache.Remove(key)) dropped++;
            if (dropped <= 0) return;
            _dirty = true;
            left = _cache.Count;
        }

        Log.Info("probe", $"probe cache: dropped {dropped} entry(s) - {segments} conversion segment(s) that "
                          + $"should never have been probed, {dropped - segments} for files that changed or went - "
                          + $"{left} left");
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

    /// <summary>
    /// One cached answer: "video|audio|pixfmt". The pixel format is the
    /// newer third field.
    ///
    /// The codec name alone does not say whether a picture plays: 10-bit
    /// H.264 ("Hi10P", common in anime releases) and 4:2:2 or 4:4:4 H.264 are
    /// all "h264", and no browser and hardly a television decodes them. With
    /// names only, the Transcodes window showed such an .mp4 as ready to play
    /// as it stands, left it out of Convert, and a television was handed it
    /// directly.
    ///
    /// Entries written before the field existed go on answering exactly as they
    /// always did - the pixel format unknown, which is judged as before - and
    /// the H.264 ones, where it matters, are refreshed by the sweep that already
    /// walks the library (see IsSettled). They are not treated as unread in the
    /// meantime: that hid every H.264 file from a television, and made opening
    /// a folder in the Transcodes window probe them one by one inside the
    /// request, until the sweep got round to them.
    /// </summary>
    private static (string? video, string? audio, string? pixFmt) Parse(string hit)
    {
        var p = hit.Split('|');
        var video = string.IsNullOrEmpty(p.ElementAtOrDefault(0)) ? null : p[0];
        var audio = string.IsNullOrEmpty(p.ElementAtOrDefault(1)) ? null : p[1];
        var pixFmt = string.IsNullOrEmpty(p.ElementAtOrDefault(2)) ? null : p[2];
        return (video, audio, pixFmt);
    }

    /// <summary>An answer recorded before the pixel format was, for a codec where it matters.</summary>
    private static bool NeedsPixelFormat(string hit) => hit.Split('|').Length < 3 && hit.StartsWith("h264|", StringComparison.Ordinal);

    /// <summary>
    /// Whether an H.264 picture is one a player can decode: 8-bit 4:2:0,
    /// which is what every encoder here writes (-pix_fmt yuv420p). yuvj420p
    /// is the same with full-range levels - what phones record - and plays.
    /// A pixel format nobody knows is judged as it always was, as playable;
    /// any other codec is not judged by this.
    /// </summary>
    public static bool PlayablePicture(string? video, string? pixFmt) =>
        video is not "h264" || pixFmt is null or "yuv420p" or "yuvj420p";

    /// <summary>
    /// Whether this file's answer is complete, so the library sweep can pass
    /// it by: a container a television never plays (decided without a probe),
    /// or a cached answer that has its pixel format where it needs one.
    /// </summary>
    public bool IsSettled(string file)
    {
        if (UnplayableContainers.Contains(Path.GetExtension(file))) return true;
        string key;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return true;   // nothing to read
            key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch { return true; }
        lock (_lock) return _cache.TryGetValue(key, out var hit) && !NeedsPixelFormat(hit);
    }

    /// <summary>
    /// Whether a television plays this file's sound as it stands: null when
    /// the file has not been read yet. For music, where the picture question
    /// (NeedsConversion) does not apply - and answered "no picture, so leave it
    /// alone" for every song, whatever its sound was.
    /// </summary>
    public bool? TvPlaysSoundOf(string file)
    {
        var known = CodecsCached(file);
        if (known is null) return null;
        return known.Value.audio is string audio && PlayableAudio.Contains(audio);
    }

    /// <summary>Reads a file again whatever the cache says - the sweep's answer to IsSettled.</summary>
    public (string? video, string? audio, string? pixFmt) Refresh(string file) => Details(file, refresh: true);

    /// <summary>Codecs of a file, from the cache when its size and date are unchanged.</summary>
    public (string? video, string? audio) Codecs(string file)
    {
        var (video, audio, _) = Details(file);
        return (video, audio);
    }

    /// <summary>Codecs and pixel format of a file, from the cache when its size and date are unchanged.</summary>
    /// <param name="refresh">Read it again even when cached (see Refresh). A failed read keeps what was known.</param>
    public (string? video, string? audio, string? pixFmt) Details(string file, bool refresh = false)
    {
        // Never the server's own output: see _conversionsRoot.
        if (IsConversionOutput(file)) return (null, null, null);
        string key;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return (null, null, null);
            key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch { return (null, null, null); }

        (string? video, string? audio, string? pixFmt)? known = null;
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                known = Parse(hit);
                if (!refresh) return known.Value;
            }
        }

        var probed = Probe(file);
        // A refresh that fails - a timeout while encoders have the disk - is
        // not a reason to lose the answer the file already had.
        if (probed.video is null && probed.audio is null && known is { } kept) return kept;

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
            _cache[key] = $"{probed.video}|{probed.audio}|{probed.pixFmt}";
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

        var (video, audio, pixFmt) = Details(file);
        return Needs(video, audio, pixFmt);
    }

    /// <summary>The television's answer from what is known of a file. See NeedsConversion.</summary>
    private static bool Needs(string? video, string? audio, string? pixFmt)
    {
        if (video is null) return false;                      // unprobeable: leave it alone
        if (!PlayableVideo.Contains(video)) return true;
        // h264 by name, and not a picture a set decodes: 10-bit or 4:2:2. An
        // h264 whose pixel format ffprobe could not name is left as it was.
        if (pixFmt is not null && !PlayablePicture(video, pixFmt)) return true;
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

        (string? video, string? audio, string? pixFmt) known;
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var hit)) return null;   // not probed yet
            known = Parse(hit);
        }
        return Needs(known.video, known.audio, known.pixFmt);
    }

    /// <summary>
    /// The codecs already known for this file, or null when it has not been
    /// probed. Never launches ffprobe.
    ///
    /// Exists because a listing must not probe. A directory listing asks about
    /// every file in it, and every file in every sub-folder for the summary
    /// pills; anything on that path that can start an ffprobe turns opening a
    /// folder into thousands of process launches. That is exactly what a live
    /// codec check did when it was put there — the Transcode window's Up and
    /// Refresh stopped responding, because the request behind them had become
    /// minutes of probing.
    /// </summary>
    public (string? video, string? audio, string? pixFmt)? CodecsCached(string file)
    {
        if (IsConversionOutput(file)) return null;
        string key;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return null;
            key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch { return null; }

        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var hit)) return null;
            return Parse(hit);
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                // Atomic: this is written every 200 probes, and truncating
                // it on a kill costs the whole cache rather than the last few
                // entries. Compact on purpose, so it goes through the text
                // form rather than JsonSidecar.Save's indented one.
                JsonSidecar.WriteAtomic(_cacheFile, JsonSerializer.Serialize(_cache), "probe");
                _dirty = false;
            }
            catch { /* the cache is an optimisation; failing to keep it is not an error */ }
        }
    }

    private (string? video, string? audio, string? pixFmt) Probe(string file)
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
            // JSON, not csv: the pixel format is a video field only, and csv
            // drops the names that would say which value on a line is which.
            foreach (var a in new[] { "-v", "error",
                                      "-show_entries", "stream=codec_type,codec_name,pix_fmt:stream_disposition=attached_pic",
                                      "-of", "json", file })
                psi.ArgumentList.Add(a);
            // Both pipes drained together, with a timeout that can actually
            // fire - see ProcessJob.Run. ffprobe has a great deal to say on
            // stderr about a damaged file even under "-v error", and reading
            // only stdout wedged this call, and the whole batch queue behind
            // it, against the first such file in the library.
            var run = Services.ProcessJob.Run(psi, 20_000);
            if (run is null) return (null, null, null);
            if (run.Value.TimedOut)
            {
                Log.Warn("ffmpeg", $"ffprobe gave up on {Path.GetFileName(file)} after 20s - left unread");
                return (null, null, null);
            }

            string? video = null, audio = null, pixFmt = null;
            using var doc = JsonDocument.Parse(run.Value.StdOut);
            if (doc.RootElement.TryGetProperty("streams", out var streams))
                foreach (var st in streams.EnumerateArray())
                {
                    var type = st.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    var name = st.TryGetProperty("codec_name", out var n) ? n.GetString() : null;
                    // An album cover attached to a song is not a picture to
                    // decode: recorded as the song's "video", it made every MP3
                    // with art a PNG that no television plays.
                    if (type == "video" && st.TryGetProperty("disposition", out var disp)
                        && disp.TryGetProperty("attached_pic", out var ap)
                        && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1)
                        continue;
                    if (type == "video" && video is null)
                    {
                        video = name;
                        pixFmt = st.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() : null;
                    }
                    else if (type == "audio" && audio is null) audio = name;
                }
            // "|" separates the fields in the cache, so a value carrying one
            // (none does; ffprobe names are identifiers) could not be stored.
            return (Clean(video), Clean(audio), Clean(pixFmt));

            static string? Clean(string? s) => string.IsNullOrEmpty(s) || s.Contains('|') ? null : s;
        }
        catch { return (null, null, null); }
    }
}
