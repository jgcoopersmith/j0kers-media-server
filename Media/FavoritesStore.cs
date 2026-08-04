using System.Text.Json;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// Pinned media items — the quick buttons at the top of the dashboard's
/// media library. Persisted to a favorites.json sidecar next to the config.
/// </summary>
public sealed class FavoritesStore
{
    public sealed class Favorite
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
    }

    private readonly string _file;
    private readonly object _lock = new();
    private readonly List<Favorite> _favorites = new();

    public FavoritesStore(string baseDirectory)
    {
        _file = System.IO.Path.Combine(baseDirectory, "favorites.json");
        _favorites = JsonSidecar.Load<List<Favorite>>(_file, "favorites") ?? new List<Favorite>();
    }

    public IReadOnlyList<Favorite> All
    {
        get { lock (_lock) return _favorites.ToList(); }
    }

    public bool Add(string name, string path)
    {
        lock (_lock)
        {
            if (_favorites.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                return false;
            _favorites.Add(new Favorite { Name = name, Path = path });
            Persist();
            return true;
        }
    }

    public bool Remove(string path)
    {
        lock (_lock)
        {
            var removed = _favorites.RemoveAll(f =>
                f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Persist();
            return removed;
        }
    }

    private void Persist() => JsonSidecar.Save(_file, _favorites, "favorites");
}
