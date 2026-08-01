using System.Text.Json;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// The media library's root folders (the sources the dashboard browses).
/// Persisted to a library.json sidecar next to the config, same pattern as
/// mounts/channels/playlists.
/// </summary>
public sealed class LibraryStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private readonly List<string> _folders = new();

    public LibraryStore(string baseDirectory)
    {
        _file = Path.Combine(baseDirectory, "library.json");
        if (!File.Exists(_file)) return;
        try
        {
            _folders = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? new List<string>();
        }
        catch (Exception ex)
        {
            Log.Warn("library", $"could not load library.json: {ex.Message}");
        }
    }

    public IReadOnlyList<string> All
    {
        get { lock (_lock) return _folders.ToList(); }
    }

    public bool Add(string folder)
    {
        lock (_lock)
        {
            if (_folders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                return false;
            _folders.Add(folder);
            Persist();
            return true;
        }
    }

    public bool Remove(string folder)
    {
        lock (_lock)
        {
            var removed = _folders.RemoveAll(f =>
                f.Equals(folder, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Persist();
            return removed;
        }
    }

    private void Persist() =>
        File.WriteAllText(_file, JsonSerializer.Serialize(_folders,
            new JsonSerializerOptions { WriteIndented = true }));
}
