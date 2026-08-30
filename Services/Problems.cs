using System.Collections.Concurrent;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Services;

/// <summary>
/// What has gone wrong lately, in a form somebody can look at.
///
/// Everything here was already being noticed — a conversion exiting non-zero,
/// an ffprobe that would not read a file, a source that has vanished — and
/// every one of them was written to the log and nowhere else. The log is
/// Server Admin only, it is thousands of lines long, and the interesting line
/// scrolled past hours ago, so in practice these were invisible. On a library
/// of any size that means files quietly not working with nothing to say so:
/// this install had 28 films written off by failed probes and no way to find
/// out short of reading a day of log.
///
/// So the same events are also collected here, newest first, capped, and
/// shown as a card. Nothing is a "problem" that the server merely retried
/// successfully — this is for things a person may want to do something about.
/// </summary>
public sealed class Problems
{
    /// <summary>One thing that went wrong.</summary>
    /// <param name="Kind">conversion | probe | source | other — groups the list.</param>
    /// <param name="Path">The file it concerns, when it concerns one.</param>
    /// <param name="Detail">What happened, in words.</param>
    public sealed record Problem(string Kind, string Path, string Detail, DateTime WhenUtc, int Count);

    /// <summary>
    /// How many are kept. Enough to cover a long conversion run without the
    /// list becoming its own scrolling haystack.
    /// </summary>
    private const int Cap = 200;

    // Keyed so the same file failing the same way twenty times is one row with
    // a count, not twenty rows. A queue that fails on every file of a broken
    // drive would otherwise push everything else out of the list.
    private readonly ConcurrentDictionary<string, Problem> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string kind, string path, string detail)
    {
        var key = kind + "|" + path + "|" + detail;
        _byKey.AddOrUpdate(
            key,
            _ => new Problem(kind, path, detail, DateTime.UtcNow, 1),
            (_, prior) => prior with { WhenUtc = DateTime.UtcNow, Count = prior.Count + 1 });

        // Oldest out when it grows past the cap. Cheap because it only runs
        // when the dictionary is actually over, which on a healthy server is
        // never.
        if (_byKey.Count <= Cap) return;
        foreach (var stale in _byKey.OrderBy(p => p.Value.WhenUtc).Take(_byKey.Count - Cap).ToList())
            _byKey.TryRemove(stale.Key, out _);
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Problem> All =>
        _byKey.Values.OrderByDescending(p => p.WhenUtc).ToList();

    public int Count => _byKey.Count;

    /// <summary>Forgets one row, or everything. What the card's ✕ and Clear do.</summary>
    public void Clear(string? kind = null, string? path = null)
    {
        if (kind is null && path is null) { _byKey.Clear(); Log.Info("problems", "problem list cleared"); return; }
        foreach (var p in _byKey.Where(p => (kind is null || p.Value.Kind == kind)
                                         && (path is null || p.Value.Path == path)).ToList())
            _byKey.TryRemove(p.Key, out _);
    }
}
