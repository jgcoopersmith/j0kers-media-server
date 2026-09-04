using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media;

/// <summary>
/// What each account has chosen about how the dashboard looks: the theme,
/// which view each card is in, the card order, which cards are folded, and
/// the rest of it.
///
/// This exists because the browser was the wrong place to keep them. They
/// lived in localStorage, which is per browser and per profile — so the same
/// account got a different dashboard on a phone than on a desktop, a second
/// profile started from scratch, and an incognito window threw the lot away
/// the moment it closed. That last one is what made it look broken: settings
/// saved, survived a reload, and vanished with the window.
///
/// So the account holds them and the browser only caches them. Small, flat,
/// and per account: a dictionary of strings the client owns the meaning of.
/// The server deliberately knows nothing about what a key means — it is the
/// dashboard's business what "j0kers-theme" is, and a server that had to
/// learn each one would need changing every time the page grew a setting.
/// </summary>
public sealed class UserPreferences
{
    /// <summary>
    /// Caps, because this is a store a signed-in account writes into
    /// directly. Generous next to what the dashboard actually keeps —
    /// fifteen or so keys of a few characters — and small enough that no
    /// account can turn it into a disk-filling exercise.
    /// </summary>
    private const int MaxKeysPerUser = 200;
    private const int MaxKeyLength = 128;
    private const int MaxValueLength = 4096;

    private readonly string _file;
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, string>> _byUser;

    public UserPreferences(string baseDirectory)
    {
        _file = Path.Combine(baseDirectory, "preferences.json");
        _byUser = JsonSidecar.Load<Dictionary<string, Dictionary<string, string>>>(_file, "preferences")
                  ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>This account's settings, or an empty set for one that has none.</summary>
    public IReadOnlyDictionary<string, string> For(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return new Dictionary<string, string>();
        lock (_lock)
            return _byUser.TryGetValue(userId, out var mine)
                ? new Dictionary<string, string>(mine)
                : new Dictionary<string, string>();
    }

    /// <summary>
    /// Merges what the client sends into what is already stored, and returns
    /// the whole set as it now stands.
    ///
    /// A merge rather than a replace: the dashboard sends the keys it has
    /// touched, and an older build — or a page that has not learned about a
    /// setting a newer one added — must not delete what it does not know
    /// about. A key sent with an empty value is a removal, which is the only
    /// way a client has to forget one.
    /// </summary>
    public IReadOnlyDictionary<string, string> Merge(string userId, IDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(userId)) return new Dictionary<string, string>();
        lock (_lock)
        {
            if (!_byUser.TryGetValue(userId, out var mine))
                _byUser[userId] = mine = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (key, value) in values)
            {
                if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength) continue;
                if (value is null || value.Length == 0) { mine.Remove(key); continue; }
                if (value.Length > MaxValueLength) continue;
                // A new key only when there is room; an existing one is always
                // allowed through, so a full set stays editable rather than
                // frozen.
                if (!mine.ContainsKey(key) && mine.Count >= MaxKeysPerUser) continue;
                mine[key] = value;
            }

            if (mine.Count == 0) _byUser.Remove(userId);
            Persist();
            return new Dictionary<string, string>(
                _byUser.TryGetValue(userId, out var now) ? now : new Dictionary<string, string>());
        }
    }

    /// <summary>Forgets an account's settings entirely — for a deleted account.</summary>
    public void Forget(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;
        lock (_lock)
        {
            if (_byUser.Remove(userId)) Persist();
        }
    }

    private void Persist()
    {
        try { JsonSidecar.Save(_file, _byUser, "preferences"); }
        catch (Exception ex) { Log.Warn("control", $"could not save preferences: {ex.Message}"); }
    }
}
