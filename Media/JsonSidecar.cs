using System.Text.Json;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// Reading and writing the small JSON files that sit next to the config —
/// library folders, pinned media, playlists, channels.
///
/// Both halves exist because the obvious versions lose data.
///
/// <b>Writing</b> straight over the live file leaves it truncated if the
/// process dies partway through, and this server is routinely closed by
/// force-quitting a tray app. The write therefore lands in a sibling
/// temp file and is moved over the original, which is atomic on every
/// platform we run on: a reader sees the old file or the new one.
///
/// <b>Reading</b> a file that exists but won't parse used to log a warning
/// and carry on with an empty list — and the next edit then persisted that
/// empty list over the top, so a truncated file quietly became no file at
/// all. Now the unreadable file is moved aside with a .corrupt suffix and
/// the loss is stated plainly, so there is something to put back.
/// </summary>
internal static class JsonSidecar
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// Loads <paramref name="file"/>, or returns null if it isn't there.
    ///
    /// A file that exists but cannot be read is renamed to
    /// <c>&lt;name&gt;.corrupt</c> and null is returned, so the caller starts
    /// empty without the original being overwritten by the next save.
    /// </summary>
    public static T? Load<T>(string file, string label) where T : class
    {
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file), ReadOpts);
        }
        catch (Exception ex)
        {
            Log.Error(label, $"could not read {Path.GetFileName(file)}: {ex.Message}");
            Quarantine(file, label);
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> so that a crash mid-write cannot
    /// destroy what was already there.
    /// </summary>
    public static void Save<T>(string file, T value, string label)
        => WriteAtomic(file, JsonSerializer.Serialize(value, WriteOpts), label);

    /// <summary>
    /// The same atomic write, for a caller that has already serialised — a
    /// compact cache that must not be indented into several megabytes, or a
    /// type with its own options.
    ///
    /// Worth having as its own method because three of them were not doing
    /// this at all: the probe cache, the transcode queue and the subtitle
    /// sidecar each called File.WriteAllText straight at the real file. That
    /// truncates first and writes after, so a kill in between — which for this
    /// server is routine, since an upgrade stops it mid-flight — leaves an
    /// empty or half-written file rather than the previous one. The transcode
    /// queue is the sharpest case: it exists precisely to survive a restart.
    /// </summary>
    public static void WriteAtomic(string file, string json, string label)
    {
        // the writer's own temp name: every caller holds a lock around its
        // own file today, but a shared "x.json.tmp" is one refactor away from
        // two writers interleaving into it and moving the mess into place
        var tmp = $"{file}.{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            // the caller's in-memory state is still correct; only the record
            // of it failed, and saying so beats a silent no-op
            Log.Error(label, $"could not save {Path.GetFileName(file)}: {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// Moves an unreadable file aside. Keeps one generation: a second
    /// failure overwrites the first .corrupt rather than piling up, and the
    /// first is the one that matters — it holds the last good content.
    /// </summary>
    private static void Quarantine(string file, string label)
    {
        var aside = file + ".corrupt";
        try
        {
            // Do not overwrite an existing .corrupt. The summary above says
            // the first one is the one that matters — it holds the last good
            // content — and `overwrite: true` did exactly the opposite,
            // replacing it with whatever the newest damaged version happens to
            // be. A second failure after the file has already been rewritten
            // empty would have destroyed the only copy worth having.
            if (File.Exists(aside))
            {
                Log.Warn(label, $"{Path.GetFileName(file)} is damaged again — leaving the earlier " +
                                $"{Path.GetFileName(aside)} in place, since that is the copy with " +
                                "the last good content, and starting empty");
                try { File.Delete(file); } catch { /* it will be rewritten anyway */ }
                return;
            }
            File.Move(file, aside);
            Log.Warn(label, $"moved it to {Path.GetFileName(aside)} and started empty — " +
                            "its contents are recoverable from there");
        }
        catch (Exception ex)
        {
            Log.Error(label, $"could not set the damaged file aside ({ex.Message}) — " +
                             "it will be overwritten by the next change");
        }
    }
}
