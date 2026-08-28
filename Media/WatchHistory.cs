using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// What has been watched lately, newest first. Playback is otherwise
/// invisible once a session ends — the sessions table only ever shows what
/// is live — so this is the record of what came before it. Persisted to a
/// history.json sidecar next to the config, per account.
/// </summary>
public sealed class WatchHistory
{
    /// <summary>How many entries are kept on disk. The dashboard shows ten.</summary>
    private const int Capacity = 50;

    public sealed class Entry
    {
        /// <summary>The readable title — StreamTitle's work, not the raw slug.</summary>
        public string Name { get; set; } = "";
        /// <summary>Absolute file path when one is known; otherwise empty.</summary>
        public string Path { get; set; } = "";
        /// <summary>
        /// The HLS stream this played as. Preparing a file and then watching
        /// it are two events about one viewing, and this is what ties them
        /// together — without it the same film lands in the list twice.
        /// </summary>
        public string Stream { get; set; } = "";
        /// <summary>"file" (replayable from disk) or "stream" (replayable while the stream lasts).</summary>
        public string Kind { get; set; } = "file";
        /// <summary>Account that watched it; empty before accounts exist.</summary>
        public string User { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        /// <summary>How many times it has been played, counting this one.</summary>
        public int Plays { get; set; } = 1;
    }

    private readonly string _file;
    private readonly object _lock = new();
    private readonly List<Entry> _entries;

    public WatchHistory(string baseDirectory)
    {
        _file = System.IO.Path.Combine(baseDirectory, "history.json");
        _entries = JsonSidecar.Load<List<Entry>>(_file, "history") ?? new List<Entry>();
    }

    /// <summary>
    /// The most recent entries for one account, newest first, plus
    /// everything watched without an account. Everyone sees their own
    /// history and no one else's — what somebody watched is theirs.
    ///
    /// DLNA is the reason for the second half: a television browsing the
    /// library presents no credential — the protocol has none — so those
    /// plays belong to nobody and would otherwise be recorded and never
    /// shown. They are already visible to any signed-in account in the
    /// sessions table while they are playing, so listing them afterwards
    /// reveals nothing new; leaving them out just made the list wrong.
    /// </summary>
    public IReadOnlyList<Entry> Recent(string user, int count)
    {
        lock (_lock)
            return _entries
                .Where(e => e.User.Length == 0
                            || string.Equals(e.User, user, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.StartedUtc)
                .Take(count)
                .ToList();
    }

    /// <summary>
    /// Records a play. Watching the same thing again moves its existing entry
    /// to the top rather than filling the list with one title — ten rows of
    /// the same film would be a worse answer to "what have I watched?".
    ///
    /// An entry is matched on its file path or its stream, whichever the
    /// caller knows: preparing a file gives the path, and the viewing that
    /// follows a few seconds later gives the stream. They are one viewing and
    /// have to land on one row.
    /// </summary>
    public void Record(string name, string path, string stream, string kind, string user)
    {
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(stream)) return;
        lock (_lock)
        {
            var existing = _entries.FirstOrDefault(e =>
                string.Equals(e.User, user, StringComparison.OrdinalIgnoreCase) &&
                ((path.Length > 0 && e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ||
                 (stream.Length > 0 && e.Stream.Equals(stream, StringComparison.OrdinalIgnoreCase))));
            if (existing is not null)
            {
                // the same viewing arriving twice (prepare, then watch) is not
                // a second play — only a genuinely later one is
                var again = DateTime.UtcNow - existing.StartedUtc > TimeSpan.FromMinutes(2);
                existing.StartedUtc = DateTime.UtcNow;
                if (again) existing.Plays++;
                if (path.Length > 0) existing.Path = path;      // fill in what the other half knew
                if (stream.Length > 0) existing.Stream = stream;
                if (existing.Path.Length > 0) existing.Kind = "file";
                if (name.Length > 0) existing.Name = name;
            }
            else
            {
                _entries.Add(new Entry
                {
                    Name = name,
                    Path = path,
                    Stream = stream,
                    Kind = kind,
                    User = user,
                    StartedUtc = DateTime.UtcNow,
                });
            }

            // trim oldest-first across all accounts
            if (_entries.Count > Capacity)
            {
                _entries.Sort((a, b) => b.StartedUtc.CompareTo(a.StartedUtc));
                _entries.RemoveRange(Capacity, _entries.Count - Capacity);
            }
            Persist();
        }
    }

    /// <summary>
    /// Forgets one entry — named by its file path or its stream, since the
    /// caller may only have one of them — or the caller's whole history when
    /// the key is empty.
    ///
    /// Reaches the no-account entries as well, matching what
    /// <see cref="Recent"/> shows: a DLNA row the caller can see is a row the
    /// caller must be able to clear.
    /// </summary>
    public bool Forget(string user, string key)
    {
        lock (_lock)
        {
            var removed = _entries.RemoveAll(e =>
                (e.User.Length == 0 || string.Equals(e.User, user, StringComparison.OrdinalIgnoreCase)) &&
                (key.Length == 0
                 || e.Path.Equals(key, StringComparison.OrdinalIgnoreCase)
                 || e.Stream.Equals(key, StringComparison.OrdinalIgnoreCase))) > 0;
            if (removed) Persist();
            return removed;
        }
    }

    private void Persist() => JsonSidecar.Save(_file, _entries, "history");
}
