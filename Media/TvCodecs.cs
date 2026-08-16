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
/// Probing costs an ffprobe launch per file, and the shelf asks about
/// thousands, so answers are cached by path, size and modification time. A
/// file that changes is re-probed; a file that doesn't is asked once ever.
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
    private static readonly HashSet<string> UnplayableContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vob", ".ifo", ".divx", ".rm", ".rmvb", ".ogm", ".asf",
    };

    private readonly string _cacheFile;
    private readonly string _ffprobe;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _dirty;

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
        lock (_lock)
        {
            _cache[key] = $"{probed.video}|{probed.audio}";
            _dirty = true;
        }
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

    /// <summary>One-line reason, for the shelf listing. Null when nothing needs doing.</summary>
    public string? WhyConvert(string file)
    {
        if (UnplayableContainers.Contains(Path.GetExtension(file)))
            return Path.GetExtension(file).TrimStart('.').ToUpperInvariant() + " container";

        var (video, audio) = Codecs(file);
        if (video is null) return null;
        if (!PlayableVideo.Contains(video)) return Pretty(video) + " video";
        if (audio is not null && !PlayableAudio.Contains(audio)) return Pretty(audio) + " audio";
        return null;
    }

    /// ffprobe's names are not what anyone calls these formats.
    private static string Pretty(string codec) => codec.ToLowerInvariant() switch
    {
        "mpeg4" => "XviD/DivX",
        "msmpeg4v1" or "msmpeg4v2" or "msmpeg4v3" => "DivX 3",
        "wmv1" or "wmv2" or "wmv3" => "WMV",
        "vc1" => "VC-1",
        "dts" => "DTS",
        "truehd" => "TrueHD",
        "vorbis" => "Vorbis",
        "wmav1" or "wmav2" => "WMA",
        _ => codec,
    };

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
            using var p = Process.Start(psi);
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
