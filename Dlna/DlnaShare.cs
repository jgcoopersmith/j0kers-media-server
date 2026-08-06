using J0kersMediaServer.Media;

namespace J0kersMediaServer.Dlna;

/// <summary>
/// Which library folders DLNA is allowed to show.
///
/// The library is what the dashboard browses behind a sign-in; DLNA has no
/// sign-in at all, so "everything in the library" is not always the right
/// answer for it. This is the narrowing: an explicit list of the folders
/// that go out over the network, persisted to a dlna.json sidecar.
///
/// Until it is edited, every library folder is shared — nothing about
/// turning DLNA on should quietly hide half the library. Once a choice has
/// been made it is kept literally, including the empty one: sharing nothing
/// is a legitimate thing to have chosen, and must not read as "unset" and
/// spring back to everything.
/// </summary>
public sealed class DlnaShare
{
    private sealed class State
    {
        public bool Chosen { get; set; }
        public List<string> Folders { get; set; } = new();
    }

    private readonly string _file;
    private readonly object _lock = new();
    private State _state;

    public DlnaShare(string baseDirectory)
    {
        _file = System.IO.Path.Combine(baseDirectory, "dlna.json");
        _state = JsonSidecar.Load<State>(_file, "dlna") ?? new State();
    }

    /// <summary>True while every library folder is shared, edits included.</summary>
    public bool SharingAll(IReadOnlyList<string> libraryRoots)
    {
        lock (_lock)
        {
            if (!_state.Chosen) return true;
            return libraryRoots.All(IsSharedLocked);
        }
    }

    /// <summary>The library folders DLNA may show, in library order.</summary>
    public IReadOnlyList<string> Shared(IReadOnlyList<string> libraryRoots)
    {
        lock (_lock)
        {
            if (!_state.Chosen) return libraryRoots;
            return libraryRoots.Where(IsSharedLocked).ToList();
        }
    }

    private bool IsSharedLocked(string folder) =>
        _state.Folders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Replaces the choice. Only folders that are actually in the library
    /// are stored — a share pointing at a folder the library no longer has
    /// would come back to life if that folder were ever re-added.
    /// </summary>
    public void Set(IEnumerable<string> folders, IReadOnlyList<string> libraryRoots)
    {
        lock (_lock)
        {
            _state = new State
            {
                Chosen = true,
                Folders = folders
                    .Where(f => libraryRoots.Any(r => r.Equals(f, StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
            JsonSidecar.Save(_file, _state, "dlna");
        }
    }
}
