using System.Text.RegularExpressions;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// Which finished conversion belongs to which library file.
///
/// This exists because answering that question used to mean reading every
/// conversion's source.txt, one directory at a time, until a match turned up
/// — and doing it again for the next file, and the next.
///
/// Measured on the install this was written for: 2,904 conversions, 1,321 ms
/// for one full pass. DLNA calls this once per file in the folder a television
/// is opening, so a folder of fifty films cost between thirty-five and
/// sixty-six seconds before a single row appeared. The set gave up waiting and
/// dropped the connection part way through the reply — twenty HTTP 500s in one
/// day, each one a "request failed: The specified network name is no longer
/// available" — then retried, which started the whole scan again.
///
/// Folders opened instantly throughout, because a folder needs no lookup. That
/// is the shape of the complaint: dive through directories at full speed, then
/// wait a minute at the one that holds films.
///
/// So the scan happens once, in the background, and the answer becomes a
/// dictionary lookup. It is only a map from source file to directory: whether
/// that conversion is finished and complete is still decided by reading its
/// playlist at the moment it is asked for, because a conversion can be
/// half-written or have a segment missing and neither is visible from the name.
/// </summary>
public sealed class VodIndex
{
    /// A scaled copy: "vod-dune-720p-a1b2c3d4". Full-resolution conversions
    /// carry no height, and handing a 720p copy to a 4K television because it
    /// happened to be in the cache costs picture nobody asked to lose.
    private static readonly Regex Scaled = new(@"-\d+p-[0-9a-f]{8}$", RegexOptions.Compiled);

    private readonly string _root;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _bySource = new(StringComparer.OrdinalIgnoreCase);

    private int _knownCount = -1;          // conversions present when the map was built
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private volatile bool _building;

    /// How often the cheap check below is allowed to run. Counting the
    /// directories costs about 12ms on a folder of three thousand; reading
    /// them all costs a second. So the count is what gets asked repeatedly.
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(10);

    public VodIndex(string mediaRoot) => _root = mediaRoot;

    /// <summary>True once a build has finished at least once.</summary>
    public bool Ready { get; private set; }

    /// <summary>How many conversions the map currently holds. For logging and tests.</summary>
    public int Count { get { lock (_lock) return _bySource.Count; } }

    /// <summary>
    /// Builds the map off the calling thread. Called at startup: nothing waits
    /// for it, and until it finishes DirectoryFor answers "no conversion",
    /// which serves originals — exactly what happened before this existed.
    /// </summary>
    public void StartBuild()
    {
        if (_building) return;
        _building = true;
        _ = Task.Run(() =>
        {
            try { Build(); }
            catch (Exception ex) { Log.Warn("dlna", $"could not index the conversions: {ex.Message}"); }
            finally { _building = false; }
        });
    }

    /// <summary>The finished full-resolution conversion for this file, or null.</summary>
    public string? DirectoryFor(string sourceFile)
    {
        RecheckIfDue();
        lock (_lock) return _bySource.TryGetValue(sourceFile, out var dir) ? dir : null;
    }

    /// <summary>
    /// Rebuilds when the number of conversions on disk has moved.
    ///
    /// Counting is cheap - about 12ms across three thousand folders - and
    /// reading them all is not, so the count is what gets asked repeatedly.
    /// This is the only maintenance there is: a conversion finishing, one
    /// being deleted from the dashboard, and a folder removed in Explorer all
    /// move the count, and all land within one interval.
    ///
    /// A delete and a create between two checks leave the count unmoved and
    /// the map briefly wrong. Both directions are harmless: a stale hit is
    /// re-validated against the playlist and falls back to the original, and a
    /// stale miss just means the file is not offered until the next change.
    ///
    /// Being stale in the "it exists" direction is safe: the caller re-reads
    /// the playlist and checks every segment before serving, and falls back to
    /// the original file when anything is missing.
    /// </summary>
    private void RecheckIfDue()
    {
        if (_building) return;
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastCheckUtc < RecheckEvery) return;
            _lastCheckUtc = DateTime.UtcNow;
        }
        try
        {
            var n = 0;
            foreach (var _ in Directory.EnumerateDirectories(_root, "vod-*")) n++;
            if (n == _knownCount) return;
            StartBuild();
        }
        catch { /* the media root came and went; the next check will find it */ }
    }

    private void Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = 0;
        if (Directory.Exists(_root))
        {
            foreach (var dir in Directory.EnumerateDirectories(_root, "vod-*"))
            {
                seen++;
                if (Scaled.IsMatch(Path.GetFileName(dir))) continue;
                string source;
                try { source = File.ReadAllText(Path.Combine(dir, "source.txt")).Trim(); }
                catch { continue; }          // no source.txt: not one of ours to match
                if (source.Length > 0) map[source] = dir;
            }
        }
        lock (_lock)
        {
            _bySource.Clear();
            foreach (var kv in map) _bySource[kv.Key] = kv.Value;
            _knownCount = seen;
            _lastCheckUtc = DateTime.UtcNow;
        }
        Ready = true;
        Log.Info("dlna", $"conversion index: {map.Count} full-resolution conversion(s) across {seen} folder(s)");
    }
}
