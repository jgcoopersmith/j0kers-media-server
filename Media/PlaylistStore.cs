using System.Text.Json;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// Remembered media-library playlists: a named folder whose media files the
/// dashboard plays in order. Persisted to a playlists.json sidecar next to
/// the config, same pattern as mounts.json / channels.json.
/// </summary>
public sealed class PlaylistStore
{
    public sealed class PlaylistDef
    {
        public string Name { get; set; } = "";
        public string Folder { get; set; } = "";
    }

    private readonly string _file;
    private readonly object _lock = new();
    private readonly List<PlaylistDef> _playlists = new();

    public PlaylistStore(string baseDirectory)
    {
        _file = Path.Combine(baseDirectory, "playlists.json");
        _playlists = JsonSidecar.Load<List<PlaylistDef>>(_file, "playlists") ?? new List<PlaylistDef>();
    }

    public IReadOnlyList<PlaylistDef> All
    {
        get { lock (_lock) return _playlists.ToList(); }
    }

    public void Save(string name, string folder)
    {
        lock (_lock)
        {
            var existing = _playlists.FirstOrDefault(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) existing.Folder = folder; // re-saving updates
            else _playlists.Add(new PlaylistDef { Name = name, Folder = folder });
            Persist();
        }
    }

    public bool Remove(string name)
    {
        lock (_lock)
        {
            var removed = _playlists.RemoveAll(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Persist();
            return removed;
        }
    }

    private void Persist() => JsonSidecar.Save(_file, _playlists, "playlists");
}
