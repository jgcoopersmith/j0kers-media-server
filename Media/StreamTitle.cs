using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// Turns a stream directory name into something readable for display.
/// Purely cosmetic: the directory name, its URLs, and every existing link
/// stay exactly as they are — only the label shown to the user changes.
///
///   vod-skyfall-2012-1080p-brrip-x264-yify-df019bf7  ->  Skyfall (2012) · 1080p
///   vod-batman-begins-2005-eng-dvdrip-cd1-72ef4f5c   ->  Batman Begins (2005) CD1
///   ch-nbc-5-1                                       ->  Nbc 5 1
/// </summary>
public static class StreamTitle
{
    private static readonly Regex CacheKey = new(@"-[0-9a-f]{8}$", RegexOptions.Compiled);
    private static readonly Regex Year = new(@"^(19|20)\d{2}$", RegexOptions.Compiled);
    private static readonly Regex Quality = new(@"^\d{3,4}p$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Part = new(@"^(cd|disc|part)\d$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Size = new(@"^\d+(mb|gb)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Release/encoding tags that carry no meaning in a title. The built-in
    /// list, and what the prettifier uses unless a stream-titles.json says
    /// otherwise.
    /// </summary>
    private static readonly HashSet<string> DefaultJunk = new(StringComparer.OrdinalIgnoreCase)
    {
        "brrip", "bdrip", "bluray", "blueray", "webrip", "webdl", "web", "dl", "hdrip", "dvdrip",
        "dvdscr", "dvd", "hdtv", "tvrip", "remux", "x264", "x265", "h264", "h265", "hevc", "avc",
        "xvid", "divx", "aac", "ac3", "eac3", "dts", "dd", "ddp", "dd5", "mp3", "flac", "10bit",
        "8bit", "hdr", "hdr10", "sdr", "amzn", "nf", "hulu", "atvp", "dsnp", "yify", "yts", "rarbg",
        "evo", "sparks", "visio", "jyk", "proper", "repack", "internal", "limited", "eng", "multi",
        "subs", "sub", "dubbed", "retail", "unrated",
    };

    /// <summary>Small words that stay lowercase inside a title. Built in, as above.</summary>
    private static readonly HashSet<string> DefaultMinor = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "the", "of", "to", "in", "on", "at", "for", "with", "from", "or", "vs",
    };

    // What the prettifier actually consults. They begin as the built-in sets
    // and are only ever swapped once, at startup, by Initialise. Volatile
    // because the swap has to be seen by whatever thread prettifies next, and
    // a whole set is replaced rather than added to so that no reader can ever
    // observe a half-filled list.
    private static volatile HashSet<string> Junk = DefaultJunk;
    private static volatile HashSet<string> Minor = DefaultMinor;

    private static readonly Regex Separators = new(@"[\s._\[\]()+]+", RegexOptions.Compiled);

    /// <summary>
    /// The shape of stream-titles.json: two arrays, either of which may be
    /// left out. An array that is present replaces the matching built-in list
    /// outright; one that is absent leaves it alone.
    /// </summary>
    private sealed class Words
    {
        [JsonPropertyName("junk")] public string[]? Junk { get; set; }
        [JsonPropertyName("minor")] public string[]? Minor { get; set; }
    }

    // Written by hand, so it forgives the things hand-written JSON has in it.
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static bool _initialised;

    /// <summary>
    /// Takes the word lists from stream-titles.json in the server's base
    /// directory, if there is one there.
    ///
    /// The lists are the only part of this that is a matter of taste: a
    /// release group nobody has heard of yet leaves its name in every title
    /// until someone can add it, and that should not mean a rebuild. So they
    /// can be replaced from a file next to the config.
    ///
    /// Every way of failing ends in the built-in lists being kept: no file,
    /// an unreadable file, JSON that will not parse, JSON of the wrong shape,
    /// an array left out. Nothing an administrator can do to that file, or
    /// fail to do, changes what titles look like today unless they meant to.
    /// Being static and shared, this class has no base directory of its own
    /// until it is told one, which is why the call is explicit and made once
    /// at startup; anything prettified before that uses the built-ins.
    /// </summary>
    public static void Initialise(string baseDirectory)
    {
        if (_initialised) return;
        _initialised = true;
        try
        {
            var file = Path.Combine(baseDirectory, "stream-titles.json");
            if (!File.Exists(file)) return;

            var words = JsonSerializer.Deserialize<Words>(File.ReadAllText(file), ReadOpts);
            var junk = Set(words?.Junk);
            var minor = Set(words?.Minor);
            if (junk is null && minor is null) return;
            if (junk is not null) Junk = junk;
            if (minor is not null) Minor = minor;
            Log.Info("titles", "stream-titles.json: " +
                               (junk is null ? "built-in release tags" : $"{junk.Count} release tags") + ", " +
                               (minor is null ? "built-in small words" : $"{minor.Count} small words"));
        }
        catch (Exception ex)
        {
            // Deliberately not quarantined the way the sidecar stores are:
            // this file is nobody's data but the administrator's own text,
            // and moving it aside would hide the typo they need to see.
            Log.Warn("titles", $"could not read stream-titles.json ({ex.Message}); " +
                               "keeping the built-in word lists");
        }
    }

    /// <summary>
    /// The set a supplied array becomes, or null when there was no array at
    /// all. Blank entries are dropped and the rest trimmed, because a list
    /// typed by hand has stray spaces in it.
    /// </summary>
    private static HashSet<string>? Set(string[]? words)
    {
        if (words is null) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in words)
            if (!string.IsNullOrWhiteSpace(w)) set.Add(w.Trim());
        return set;
    }

    /// <summary>
    /// Readable label for a media FILE name — same cleanup, but keyed off
    /// the usual filename separators (spaces, dots, underscores, brackets)
    /// instead of slug dashes. "The.Legend.of.Drunken.Master.dvd.avi" →
    /// "The Legend of Drunken Master". Display only; the real file name is
    /// what every path still uses.
    /// </summary>
    public static string PrettifyFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem)) return fileName;
        var normalized = Separators.Replace(stem, "-").Trim('-');
        var pretty = Core(normalized);
        return string.IsNullOrWhiteSpace(pretty) ? fileName : pretty;
    }

    public static string Prettify(string streamName)
    {
        if (string.IsNullOrWhiteSpace(streamName)) return streamName;

        var s = streamName;
        if (s.StartsWith("vod-", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        else if (s.StartsWith("ch-", StringComparison.OrdinalIgnoreCase)) s = s[3..];
        s = CacheKey.Replace(s, "");   // drop the cache hash suffix

        var pretty = Core(s);
        return string.IsNullOrWhiteSpace(pretty) ? streamName : pretty;
    }

    /// <summary>Shared cleanup over a dash-separated name.</summary>
    private static string Core(string s)
    {
        var tokens = s.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return "";

        string? year = null, quality = null, part = null;
        var titleTokens = new List<string>();

        // A year marks where the title ends and release metadata begins —
        // far more reliable than trying to blacklist every release tag.
        var yearIndex = Array.FindIndex(tokens, t => Year.IsMatch(t));
        if (yearIndex > 0)
        {
            year = tokens[yearIndex];
            titleTokens.AddRange(tokens.Take(yearIndex));
            foreach (var t in tokens.Skip(yearIndex + 1))
            {
                if (quality is null && Quality.IsMatch(t)) quality = t.ToLowerInvariant();
                else if (part is null && Part.IsMatch(t)) part = t.ToUpperInvariant();
            }
        }
        else
        {
            foreach (var t in tokens)
            {
                if (quality is null && Quality.IsMatch(t)) { quality = t.ToLowerInvariant(); continue; }
                if (part is null && Part.IsMatch(t)) { part = t.ToUpperInvariant(); continue; }
                if (Quality.IsMatch(t) || Part.IsMatch(t) || Size.IsMatch(t) || Junk.Contains(t)) continue;
                titleTokens.Add(t);
            }
        }

        if (titleTokens.Count == 0) return "";

        var sb = new StringBuilder();
        for (var i = 0; i < titleTokens.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(TitleCase(titleTokens[i], first: i == 0));
        }
        if (year is not null) sb.Append(" (").Append(year).Append(')');
        if (part is not null) sb.Append(' ').Append(part);
        if (quality is not null) sb.Append(" · ").Append(quality);
        return sb.ToString();
    }

    /// <summary>
    /// Season and episode codes. Not words, and title-casing them produces
    /// "S01e03", which is how the episode of a series ends up looking wrong
    /// in the transcode list. Every naming convention that produces one
    /// writes it as a single token, so recognising it is enough.
    /// </summary>
    private static readonly Regex Episode =
        new(@"^s\d{1,3}e\d{1,3}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Tokens that are an abbreviation rather than a word, and read wrongly
    /// in sentence case. Deliberately short: "us" and "uk" are left out
    /// because they are also ordinary words and a film really can be called
    /// Us, which matters more than a resolution tag reading tidily.
    /// </summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "4k", "3d", "uhd",
    };

    private static string TitleCase(string word, bool first)
    {
        if (word.Length == 0) return word;
        if (Episode.IsMatch(word) || Abbreviations.Contains(word)) return word.ToUpperInvariant();
        if (!first && Minor.Contains(word)) return word.ToLowerInvariant();
        return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
    }
}
