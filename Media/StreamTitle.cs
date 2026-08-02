using System.Text;
using System.Text.RegularExpressions;

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

    /// <summary>Release/encoding tags that carry no meaning in a title.</summary>
    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase)
    {
        "brrip", "bdrip", "bluray", "blueray", "webrip", "webdl", "web", "dl", "hdrip", "dvdrip",
        "dvdscr", "dvd", "hdtv", "tvrip", "remux", "x264", "x265", "h264", "h265", "hevc", "avc",
        "xvid", "divx", "aac", "ac3", "eac3", "dts", "dd", "ddp", "dd5", "mp3", "flac", "10bit",
        "8bit", "hdr", "hdr10", "sdr", "amzn", "nf", "hulu", "atvp", "dsnp", "yify", "yts", "rarbg",
        "evo", "sparks", "visio", "jyk", "proper", "repack", "internal", "limited", "eng", "multi",
        "subs", "sub", "dubbed", "retail", "unrated",
    };

    /// <summary>Small words that stay lowercase inside a title.</summary>
    private static readonly HashSet<string> Minor = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "the", "of", "to", "in", "on", "at", "for", "with", "from", "or", "vs",
    };

    private static readonly Regex Separators = new(@"[\s._\[\]()+]+", RegexOptions.Compiled);

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

    private static string TitleCase(string word, bool first)
    {
        if (word.Length == 0) return word;
        if (!first && Minor.Contains(word)) return word.ToLowerInvariant();
        return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
    }
}
