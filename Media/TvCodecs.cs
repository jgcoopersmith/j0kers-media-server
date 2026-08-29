using System.Diagnostics;
using System.Text.Json;

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

    private readonly string _cacheFile;
    private readonly string _ffprobe;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _dirty;
    private int _sinceSave;

    public TvCodecs(string baseDirectory, string ffprobePath)
    {
        _cacheFile = Path.Combine(baseDirectory, "probe-cache.json");
        _ffprobe = ffprobePath;
        try
        {
            if (File.Exists(_cacheFile))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_cacheFile));
                if (loaded is not null) _cache = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* a corrupt cache is a cache miss, not a failure */ }
    }

    /// <summary>Codecs of a file, from the cache when its size and date are unchanged.</summary>
    public (string? video, string? audio) Codecs(string file)
    {
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
            using var p = Services.ProcessJob.Start(psi);
            if (p is null) return (null, null);
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(20_000)) { try { p.Kill(true); } catch { } return (null, null); }

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
