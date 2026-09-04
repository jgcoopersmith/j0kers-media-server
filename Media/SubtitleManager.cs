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
    private static readonly object AttachLock = new();

    public Track? AttachFile(string subtitleFile, string subsDir, string? label = null)
    {
        if (!_ffmpeg.Available || !File.Exists(subtitleFile)) return null;
        Directory.CreateDirectory(subsDir);

        // serialize the read-modify-write of user.json: two concurrent
        // attaches used to pick the same id and clobber each other
        lock (AttachLock)
        {
            var existing = LoadUserTracks(subsDir);
            // never reuse an id whose file is already on disk, even if
            // user.json was unreadable a moment ago
            var n = existing.Count;
            while (File.Exists(Path.Combine(subsDir, $"usr{n}.vtt"))) n++;
            var id = "usr" + n;
            var outPath = Path.Combine(subsDir, id + ".vtt");

            var args = new List<string> { "-v", "error", "-y" };
            var charset = CharsetArg(subtitleFile);
            if (charset is not null) args.AddRange(new[] { "-sub_charenc", charset });
            args.AddRange(new[] { "-i", subtitleFile, "-c:s", "webvtt", outPath });

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
            try
            {
                File.WriteAllText(UserFile(subsDir),
                    JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log.Warn("subs", $"attached but could not record {id}: {ex.Message}");
            }
            Log.Info("subs", $"attached subtitle {Path.GetFileName(subtitleFile)} as {id}");
            return new Track(track.Id, track.Label + " (added)", track.Language, "user", true, "vtt");
        }
    }

    private string FfprobePath => _ffmpeg.FfprobePath;

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
            var psi = new ProcessStartInfo(FfprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[]
                     {
                         "-v", "error", "-select_streams", "s",
                         "-show_entries", "stream=index,codec_name:stream_tags=language,title",
                         "-of", "json", mediaFile,
                     })
                psi.ArgumentList.Add(a);
            var run = Services.ProcessJob.Run(psi, 15_000);   // both pipes, real timeout
            if (run is null) return results;
            var json = run.Value.StdOut;
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

    /// <summary>
    /// Sidecar subtitle files belonging to a media file, in a stable order.
    /// The name must match exactly or be followed by a separator, otherwise
    /// "Episode 1.mkv" would also claim "Episode 10.en.srt".
    /// </summary>
    private static IEnumerable<string> SidecarFiles(string mediaFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(mediaFile));
        if (dir is null || !Directory.Exists(dir)) return Enumerable.Empty<string>();
        var baseName = Path.GetFileNameWithoutExtension(mediaFile);

        return Directory.EnumerateFiles(dir)
            .Where(f => SidecarExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f =>
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                if (!stem.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) return false;
                if (stem.Length == baseName.Length) return true;
                return stem[baseName.Length] is '.' or '_' or '-' or ' ';
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<Track> Sidecars(string mediaFile)
    {
        var results = new List<Track>();
        try
        {
            var baseName = Path.GetFileNameWithoutExtension(mediaFile);
            var index = 0;
            foreach (var file in SidecarFiles(mediaFile))
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

        // convert to a temp file and move into place, so two concurrent
        // requests can't serve (and permanently cache) a half-written VTT
        var temp = Path.Combine(cacheDir, $"{safeId}.{Environment.CurrentManagedThreadId}.tmp.vtt");
        var args = new List<string> { "-v", "error", "-y" };

        if (safeId.StartsWith("emb", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(safeId[3..], out var ordinal)) return null;
            var track = List(mediaFile).FirstOrDefault(t => t.Id == safeId);
            if (track is { Supported: false }) return null;
            args.AddRange(new[] { "-i", mediaFile, "-map", $"0:s:{ordinal}", "-c:s", "webvtt", temp });
        }
        else if (safeId.StartsWith("ext", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(safeId[3..], out var index)) return null;
            var sidecar = SidecarFiles(mediaFile).Skip(index).FirstOrDefault();
            if (sidecar is null) return null;
            var charset = CharsetArg(sidecar);
            if (charset is not null) args.AddRange(new[] { "-sub_charenc", charset });
            args.AddRange(new[] { "-i", sidecar, "-c:s", "webvtt", temp });
        }
        else return null;

        if (RunFfmpeg(args) && File.Exists(temp))
        {
            try
            {
                File.Move(temp, outPath, overwrite: true);
                return outPath;
            }
            catch
            {
                // another request won the race and already published it
                try { File.Delete(temp); } catch { }
                if (File.Exists(outPath)) return outPath;
            }
        }
        try { File.Delete(temp); } catch { }

        Log.Warn("subs", $"could not convert track {safeId} of {Path.GetFileName(mediaFile)}");
        return null;
    }

    /// <summary>
    /// Downloaded subtitles are frequently Windows-1252/Latin-1 rather than
    /// UTF-8. ffmpeg does NOT fail on those — it silently drops the cues
    /// containing the offending bytes — so the encoding has to be detected
    /// up front and declared with -sub_charenc.
    /// </summary>
    private static string? CharsetArg(string file)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return null;   // UTF-8 BOM
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) return null; // UTF-16
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return null; // valid UTF-8
        }
        catch (DecoderFallbackException)
        {
            return "CP1252";
        }
        catch
        {
            return null;
        }
    }

    private bool RunFfmpeg(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(_ffmpeg.FfmpegPath)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // Same shape, same fault as FfmpegManager.RunFfmpeg: reading
            // stderr to the end waits for the process to exit, so the
            // timeout below it could never fire on one that does not.
            var run = Services.ProcessJob.Run(psi, 60_000);
            return run is not null && run.Value.Ok;
        }
        catch
        {
            return false;
        }
    }

    private static string ShortLanguage(string lang) =>
        lang.Length == 0 ? "" : LanguageCodes.TryGetValue(lang, out var s) ? s : lang.ToLowerInvariant();

    /// <summary>
    /// Display names for the common languages. A lookup table rather than
    /// CultureInfo because the build runs with InvariantGlobalization, where
    /// constructing a specific culture throws.
    /// </summary>
    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["es"] = "Spanish", ["fr"] = "French", ["de"] = "German",
        ["it"] = "Italian", ["pt"] = "Portuguese", ["ru"] = "Russian", ["ja"] = "Japanese",
        ["ko"] = "Korean", ["zh"] = "Chinese", ["nl"] = "Dutch", ["sv"] = "Swedish",
        ["no"] = "Norwegian", ["da"] = "Danish", ["fi"] = "Finnish", ["pl"] = "Polish",
        ["tr"] = "Turkish", ["ar"] = "Arabic", ["he"] = "Hebrew", ["hi"] = "Hindi",
        ["cs"] = "Czech", ["el"] = "Greek", ["hu"] = "Hungarian", ["ro"] = "Romanian",
        ["uk"] = "Ukrainian", ["vi"] = "Vietnamese", ["th"] = "Thai", ["id"] = "Indonesian",
    };

    private static string LanguageName(string lang)
    {
        var code = ShortLanguage(lang);
        return LanguageNames.TryGetValue(code, out var name) ? name : lang.ToUpperInvariant();
    }
}
