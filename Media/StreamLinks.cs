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

    public IReadOnlyCollection<string> All
    {
        get { lock (_lock) return _hidden.ToList(); }
    }

    private void Persist() => JsonSidecar.Save(_file, _hidden.ToList(), "links");
}
