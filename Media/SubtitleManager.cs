using System.Diagnostics;
using System.Text;
using System.Text.Json;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// Subtitle discovery and conversion. Browsers only render WebVTT, while
/// media ships subtitles either embedded in the container (SRT/ASS/mov_text)
/// or as sidecar files next to the video (movie.en.srt). Both are found
/// here and converted to .vtt on demand with ffmpeg, cached inside the
/// stream's own directory.
///
/// Bitmap subtitle formats (PGS/VobSub, common on Blu-ray and DVD rips)
/// cannot become WebVTT without OCR — they are listed but flagged
/// unsupported rather than failing silently.
/// </summary>
public sealed class SubtitleManager
{
    public sealed record Track(
        string Id,
        string Label,
        string Language,
        string Kind,
        bool Supported,
        string Codec);

    private static readonly string[] SidecarExtensions =
        { ".srt", ".vtt", ".ass", ".ssa", ".sub", ".smi" };

    /// <summary>Bitmap subtitle codecs — no text to extract without OCR.</summary>
    private static readonly HashSet<string> BitmapCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub" };

    /// <summary>ISO 639-2 → BCP 47 for the common cases (srclang wants short codes).</summary>
    private static readonly Dictionary<string, string> LanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "en", ["spa"] = "es", ["fra"] = "fr", ["fre"] = "fr", ["deu"] = "de", ["ger"] = "de",
        ["ita"] = "it", ["por"] = "pt", ["rus"] = "ru", ["jpn"] = "ja", ["kor"] = "ko",
        ["zho"] = "zh", ["chi"] = "zh", ["nld"] = "nl", ["dut"] = "nl", ["swe"] = "sv",
        ["nor"] = "no", ["dan"] = "da", ["fin"] = "fi", ["pol"] = "pl", ["tur"] = "tr",
        ["ara"] = "ar", ["heb"] = "he", ["hin"] = "hi", ["ces"] = "cs", ["cze"] = "cs",
        ["ell"] = "el", ["gre"] = "el", ["hun"] = "hu", ["ron"] = "ro", ["rum"] = "ro",
        ["ukr"] = "uk", ["vie"] = "vi", ["tha"] = "th", ["ind"] = "id",
    };

    private readonly FfmpegManager _ffmpeg;

    public SubtitleManager(FfmpegManager ffmpeg) => _ffmpeg = ffmpeg;

    /// <summary>The media file a stream directory was produced from, if known.</summary>
    public static string? SourceFile(string streamDir)
    {
        try
        {
            var marker = Path.Combine(streamDir, "source.txt");
            if (!File.Exists(marker)) return null;
            var path = File.ReadAllText(marker).Trim();
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private sealed class UserTrack
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Language { get; set; } = "";
    }

    private static string UserFile(string subsDir) => Path.Combine(subsDir, "user.json");

    private static List<UserTrack> LoadUserTracks(string subsDir)
    {
        try
        {
            var f = UserFile(subsDir);
            if (!File.Exists(f)) return new List<UserTrack>();
            return JsonSerializer.Deserialize<List<UserTrack>>(File.ReadAllText(f)) ?? new List<UserTrack>();
        }
        catch { return new List<UserTrack>(); }
    }

    /// <summary>Subtitle files the user attached by hand to this stream.</summary>
    public IEnumerable<Track> UserTracks(string subsDir) =>
        LoadUserTracks(subsDir).Select(u =>
            new Track(u.Id, u.Label + " (added)", u.Language, "user", true, "vtt"));

    /// <summary>
    /// Attaches an arbitrary subtitle file to a stream: converts it to
    /// WebVTT in the stream's subs directory and records it. Returns the
    /// new track, or null when the file can't be converted.
    /// </summary>
    public Track? AttachFile(string subtitleFile, string subsDir, string? label = null)
    {
        if (!_ffmpeg.Available || !File.Exists(subtitleFile)) return null;
        Directory.CreateDirectory(subsDir);

        var existing = LoadUserTracks(subsDir);
        var id = "usr" + existing.Count;
        var outPath = Path.Combine(subsDir, id + ".vtt");

        var args = $"-v error -y {CharsetArg(subtitleFile)}-i \"{subtitleFile}\" -c:s webvtt \"{outPath}\"";
        if (!(RunFfmpeg(args) && File.Exists(outPath)))
        {
            Log.Warn("subs", $"could not convert {Path.GetFileName(subtitleFile)} to WebVTT");
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(subtitleFile);
        var lang = stem.Split('.', '_', '-').LastOrDefault(s => s.Length is 2 or 3) ?? "";
        var track = new UserTrack
        {
            Id = id,
            Label = string.IsNullOrWhiteSpace(label) ? stem : label!,
            Language = ShortLanguage(lang),
        };
        existing.Add(track);
        File.WriteAllText(UserFile(subsDir),
            JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true }));
        Log.Info("subs", $"attached subtitle {Path.GetFileName(subtitleFile)} as {id}");
        return new Track(track.Id, track.Label + " (added)", track.Language, "user", true, "vtt");
    }

    private string FfprobePath
    {
        get
        {
            var dir = Path.GetDirectoryName(_ffmpeg.FfmpegPath);
            var name = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
            if (!string.IsNullOrEmpty(dir))
            {
                var beside = Path.Combine(dir, name);
                if (File.Exists(beside)) return beside;
            }
            return "ffprobe"; // PATH
        }
    }

    /// <summary>All subtitle tracks available for a media file.</summary>
    public IReadOnlyList<Track> List(string mediaFile)
    {
        var tracks = new List<Track>();
        if (!_ffmpeg.Available || !File.Exists(mediaFile)) return tracks;
        tracks.AddRange(Embedded(mediaFile));
        tracks.AddRange(Sidecars(mediaFile));
        return tracks;
    }

    private IEnumerable<Track> Embedded(string mediaFile)
    {
        var results = new List<Track>();
        try
        {
            var args = $"-v error -select_streams s -show_entries stream=index,codec_name:stream_tags=language,title " +
                       $"-of json \"{mediaFile}\"";
            using var p = Process.Start(new ProcessStartInfo(FfprobePath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return results;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15_000);
            if (string.IsNullOrWhiteSpace(json)) return results;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("streams", out var streams)) return results;

            var ordinal = 0; // position among subtitle streams == ffmpeg's 0:s:N
            foreach (var s in streams.EnumerateArray())
            {
                var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                string lang = "", title = "";
                if (s.TryGetProperty("tags", out var tags))
                {
                    if (tags.TryGetProperty("language", out var l)) lang = l.GetString() ?? "";
                    if (tags.TryGetProperty("title", out var t)) title = t.GetString() ?? "";
                }

                var supported = !BitmapCodecs.Contains(codec);
                var label = title.Length > 0 ? title
                    : lang.Length > 0 ? LanguageName(lang)
                    : $"Track {ordinal + 1}";
                if (!supported) label += " (image-based)";

                results.Add(new Track($"emb{ordinal}", label, ShortLanguage(lang), "embedded", supported, codec));
                ordinal++;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("subs", $"probe failed for {Path.GetFileName(mediaFile)}: {ex.Message}");
        }
        return results;
    }

    private static IEnumerable<Track> Sidecars(string mediaFile)
    {
        var results = new List<Track>();
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(mediaFile));
            if (dir is null || !Directory.Exists(dir)) return results;
            var baseName = Path.GetFileNameWithoutExtension(mediaFile);

            var index = 0;
            foreach (var file in Directory.EnumerateFiles(dir)
                         .Where(f => SidecarExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                         .Where(f => Path.GetFileNameWithoutExtension(f)
                             .StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                // "movie.en.srt" / "movie.English.forced.srt" → the bit after the base name
                var stem = Path.GetFileNameWithoutExtension(file);
                var suffix = stem.Length > baseName.Length ? stem[baseName.Length..].Trim('.', '_', '-', ' ') : "";
                var lang = suffix.Split('.', '_', '-').FirstOrDefault(s => s.Length is 2 or 3) ?? "";
                var label = suffix.Length > 0 ? suffix : Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                results.Add(new Track($"ext{index}", label + " (file)", ShortLanguage(lang), "external", true,
                    Path.GetExtension(file).TrimStart('.').ToLowerInvariant()));
                index++;
            }
        }
        catch { /* unreadable directory — no sidecars */ }
        return results;
    }

    /// <summary>
    /// Returns a cached WebVTT for the given track, converting on first use.
    /// Null when the track is unknown, unsupported, or conversion fails.
    /// </summary>
    public string? GetVtt(string mediaFile, string trackId, string cacheDir)
    {
        if (!_ffmpeg.Available || !File.Exists(mediaFile)) return null;

        var safeId = new string(trackId.Where(char.IsLetterOrDigit).ToArray());
        if (safeId.Length == 0) return null;
        var outPath = Path.Combine(cacheDir, safeId + ".vtt");
        if (File.Exists(outPath)) return outPath;
        Directory.CreateDirectory(cacheDir);

        string args;
        if (safeId.StartsWith("emb", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(safeId[3..], out var ordinal)) return null;
            var track = List(mediaFile).FirstOrDefault(t => t.Id == safeId);
            if (track is { Supported: false }) return null;
            args = $"-v error -y -i \"{mediaFile}\" -map 0:s:{ordinal} -c:s webvtt \"{outPath}\"";
        }
        else if (safeId.StartsWith("ext", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(safeId[3..], out var index)) return null;
            var dir = Path.GetDirectoryName(Path.GetFullPath(mediaFile));
            var baseName = Path.GetFileNameWithoutExtension(mediaFile);
            var sidecar = Directory.EnumerateFiles(dir!)
                .Where(f => SidecarExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Where(f => Path.GetFileNameWithoutExtension(f)
                    .StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Skip(index).FirstOrDefault();
            if (sidecar is null) return null;
            args = $"-v error -y {CharsetArg(sidecar)}-i \"{sidecar}\" -c:s webvtt \"{outPath}\"";
        }
        else return null;

        if (RunFfmpeg(args) && File.Exists(outPath)) return outPath;

        Log.Warn("subs", $"could not convert track {safeId} of {Path.GetFileName(mediaFile)}");
        return null;
    }

    /// <summary>
    /// Downloaded subtitles are frequently Windows-1252/Latin-1 rather than
    /// UTF-8. ffmpeg does NOT fail on those — it silently drops the cues
    /// containing the offending bytes — so the encoding has to be detected
    /// up front and declared with -sub_charenc.
    /// </summary>
    private static string CharsetArg(string file)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return "";   // UTF-8 BOM
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) return ""; // UTF-16
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return ""; // valid UTF-8
        }
        catch (DecoderFallbackException)
        {
            return "-sub_charenc CP1252 ";
        }
        catch
        {
            return "";
        }
    }

    private bool RunFfmpeg(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(_ffmpeg.FfmpegPath, args)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            p.StandardError.ReadToEnd();
            return p.WaitForExit(60_000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ShortLanguage(string lang) =>
        lang.Length == 0 ? "" : LanguageCodes.TryGetValue(lang, out var s) ? s : lang.ToLowerInvariant();

    private static string LanguageName(string lang)
    {
        try
        {
            var code = ShortLanguage(lang);
            return new System.Globalization.CultureInfo(code).EnglishName;
        }
        catch
        {
            return lang.ToUpperInvariant();
        }
    }
}
