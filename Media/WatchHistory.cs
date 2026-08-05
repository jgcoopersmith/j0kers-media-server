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
        /// <summary>Display name — the file name, or the channel's name.</summary>
        public string Name { get; set; } = "";
        /// <summary>Absolute file path, or the channel id: what replaying it needs.</summary>
        public string Path { get; set; } = "";
        /// <summary>"file" or "channel" — they are replayed differently.</summary>
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
    /// The most recent entries for one account, newest first. Everyone sees
    /// their own history and no one else's — what somebody watched is theirs.
    /// </summary>
    public IReadOnlyList<Entry> Recent(string user, int count)
    {
        lock (_lock)
            return _entries
                .Where(e => string.Equals(e.User, user, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.StartedUtc)
                .Take(count)
                .ToList();
    }

    /// <summary>
    /// Records a play. Watching the same thing again moves its existing entry
    /// to the top rather than filling the list with one title — ten rows of
    /// the same film would be a worse answer to "what have I watched?".
    /// </summary>
    public void Record(string name, string path, string kind, string user)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_lock)
        {
            var existing = _entries.FirstOrDefault(e =>
                e.Path.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.User, user, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.StartedUtc = DateTime.UtcNow;
                existing.Plays++;
                existing.Name = name;
            }
            else
            {
                _entries.Add(new Entry
                {
                    Name = name,
                    Path = path,
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

    /// <summary>Forgets one entry, or the caller's whole history when path is empty.</summary>
    public bool Forget(string user, string path)
    {
        lock (_lock)
        {
            var removed = _entries.RemoveAll(e =>
                string.Equals(e.User, user, StringComparison.OrdinalIgnoreCase) &&
                (path.Length == 0 || e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) > 0;
            if (removed) Persist();
            return removed;
        }
    }

    private void Persist() => JsonSidecar.Save(_file, _entries, "history");
}
