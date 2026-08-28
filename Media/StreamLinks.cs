using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// Which converted streams are listed, as distinct from which exist.
///
/// A conversion and its entry in the HLS list used to be one thing, so
/// removing the entry deleted the work: play the film again and the whole
/// conversion ran a second time to produce a byte-identical result. The two
/// are separated here. Removing a link hides the row; the files stay, and
/// playing that media again re-links the same directory instead of
/// rebuilding it.
///
/// Nothing here deletes anything. Disk is bounded by the existing LRU cap
/// (ffmpeg.vodCacheMaxGb), which now does the whole job of reclaiming space
/// — an unlinked conversion is exactly what that cap should evict first,
/// and it evicts by least-recently-played regardless of listing.
///
/// Persisted, because a link removed before a restart should stay removed.
/// </summary>
public sealed class StreamLinks
{
    private readonly string _file;
    private readonly object _lock = new();
    private HashSet<string> _hidden;

    public StreamLinks(string baseDirectory)
    {
        _file = Path.Combine(baseDirectory, "unlinked.json");
        var saved = JsonSidecar.Load<List<string>>(_file, "links") ?? new List<string>();
        _hidden = new HashSet<string>(saved, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsHidden(string stream)
    {
        lock (_lock) return _hidden.Contains(stream);
    }

    /// <summary>Takes a stream out of the list, leaving its files alone.</summary>
    public void Hide(string stream)
    {
        lock (_lock)
        {
            if (!_hidden.Add(stream)) return;
            Persist();
        }
    }

    /// <summary>
    /// The same for a batch, written once.
    ///
    /// Queueing a folder of conversions hides every one of them, and calling
    /// Hide per file rewrote this whole file per file - seven hundred writes
    /// for a seven-hundred-file batch, each serialising the growing list.
    /// </summary>
    public void HideMany(IEnumerable<string> streams)
    {
        lock (_lock)
        {
            var added = false;
            foreach (var s in streams) added |= _hidden.Add(s);
            if (added) Persist();
        }
    }

    /// <summary>
    /// Puts it back — what playing the media again means. Called on every
    /// prepare, so re-linking needs no separate action: ask for the film and
    /// it is in the list again, with its conversion already done.
    /// </summary>
    public void Show(string stream)
    {
        lock (_lock)
        {
            if (!_hidden.Remove(stream)) return;
            Persist();
        }
    }

    /// <summary>
    /// Forgets a stream entirely — for when its directory really has gone,
    /// so the hidden list does not accumulate names of things that no longer
    /// exist and could never be re-linked.
    /// </summary>
    public void Forget(string stream) => Show(stream);


    /// <summary>
    /// Hides conversions that were made before the server started hiding them,
    /// once.
    ///
    /// Converting from the Transcode panel is not the same as publishing a
    /// stream, and since 2.0.157 a batch conversion is unlinked as it is
    /// queued. Everything converted before that was listed, and stayed listed:
    /// a library's worth of rows nobody added, which is exactly the complaint
    /// the change was meant to answer. Nothing ever went back for the backlog.
    ///
    /// So do it once. Nothing is deleted - the conversions stay on disk and
    /// keep working - and anything genuinely wanted in the list comes back by
    /// playing it, which is what publishes a stream in the first place. The
    /// marker file is what makes it once rather than every start, so a row put
    /// back deliberately is not hidden again on the next launch.
    /// </summary>
    public void HideExistingConversionsOnce(string mediaRoot)
    {
        var marker = Path.Combine(Path.GetDirectoryName(_file)!, "unlinked-migrated");
        if (File.Exists(marker)) return;

        var hidden = 0;
        try
        {
            if (Directory.Exists(mediaRoot))
            {
                var names = Directory.EnumerateDirectories(mediaRoot, "vod-*")
                                     .Select(Path.GetFileName)
                                     .Where(n => n is { Length: > 0 })
                                     .Select(n => n!)
                                     .ToList();
                lock (_lock)
                {
                    foreach (var n in names) if (_hidden.Add(n)) hidden++;
                    if (hidden > 0) Persist();
                }
            }
            File.WriteAllText(marker, "conversions made before unlinking existed were hidden once");
        }
        catch (Exception ex)
        {
            // Not being able to do this is not a reason to refuse to start; the
            // list is merely longer than it should be until next time.
            Log.Warn("links", $"could not tidy the stream list: {ex.Message}");
            return;
        }

        if (hidden > 0)
            Log.Info("links", $"{hidden} existing conversion(s) taken out of the HLS list - " +
                              "they are still on disk, and playing one puts it back");
    }
    public IReadOnlyCollection<string> All
    {
        get { lock (_lock) return _hidden.ToList(); }
    }

    private void Persist() => JsonSidecar.Save(_file, _hidden.ToList(), "links");
}
