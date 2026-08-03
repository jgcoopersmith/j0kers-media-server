namespace J0kersMediaServer.Media.Providers;

/// <summary>One channel in a provider's lineup, as the dashboard shows it.</summary>
public sealed record ProviderChannel(
    string Id,
    string Name,
    string Group,
    string? LogoUrl,
    int Number,
    string? Summary);

/// <summary>
/// A source of free ad-supported linear channels (FAST services).
///
/// A provider does two things: list what's on offer, and turn one entry
/// into a playable HLS URL. Resolution is deliberately separate from
/// listing because several of these services hand out short-lived session
/// tokens — the URL has to be minted at play time, not at browse time, or
/// it is dead before anyone presses play.
/// </summary>
public interface IChannelProvider
{
    /// <summary>Stable slug used in URLs and channel ids (e.g. "pluto").</summary>
    string Id { get; }

    /// <summary>Name shown in the dashboard.</summary>
    string Name { get; }

    /// <summary>Whether this provider can be used right now (config present, reachable).</summary>
    bool Enabled { get; }

    /// <summary>The current lineup. Implementations cache; callers may call this freely.</summary>
    Task<IReadOnlyList<ProviderChannel>> LineupAsync(CancellationToken ct = default);

    /// <summary>
    /// A playable HLS master-playlist URL for one channel, freshly
    /// authorized. Returns null when the channel is unknown.
    /// </summary>
    Task<string?> ResolveAsync(string channelId, CancellationToken ct = default);

    /// <summary>
    /// Re-authorizes an upstream URL that was minted earlier.
    ///
    /// A player holds the variant-playlist URL it found in the master and
    /// refetches it every few seconds for as long as the channel is on. Any
    /// session token baked into that URL will lapse long before the viewer
    /// stops watching, so the proxy hands each URL back here immediately
    /// before fetching it. Providers with no expiring credential need do
    /// nothing.
    /// </summary>
    Task<string> RefreshAsync(string url, CancellationToken ct = default) => Task.FromResult(url);
}

/// <summary>
/// The providers this server knows about, looked up by slug.
/// </summary>
public sealed class ProviderRegistry : IDisposable
{
    private readonly Dictionary<string, IChannelProvider> _byId;

    public ProviderRegistry(IEnumerable<IChannelProvider> providers)
    {
        _byId = providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IChannelProvider> All => _byId.Values;

    public IChannelProvider? Get(string id) =>
        _byId.TryGetValue(id, out var p) ? p : null;

    public void Dispose()
    {
        foreach (var p in _byId.Values) (p as IDisposable)?.Dispose();
    }
}
