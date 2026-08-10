using System.Text.RegularExpressions;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media.Providers;

/// <summary>
/// Any extended-M3U playlist reachable over HTTP, treated as a lineup.
///
/// This is the general case behind the named providers. Several FAST
/// services — Tubi, The Roku Channel, Samsung TV Plus and the rest — expose
/// no stable public API of their own; what exists is a community-maintained
/// playlist that tracks whatever those services are doing this month. Aiming
/// at a playlist rather than at each service's private endpoints puts the
/// churn where somebody is already absorbing it, instead of in this server.
///
/// Configured in <c>providers.json</c>:
/// <code>
/// [ { "id": "tubi", "name": "Tubi", "url": "https://…/tubi.m3u" } ]
/// </code>
/// </summary>
public sealed class M3uProvider : IChannelProvider
{
    public string Id { get; }
    public string Name { get; }
    public bool Enabled => !string.IsNullOrWhiteSpace(_url);

    private readonly string _url;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<ProviderChannel> _lineup = Array.Empty<ProviderChannel>();
    private Dictionary<string, string> _urls = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _fetchedUtc = DateTime.MinValue;

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    public M3uProvider(string id, string name, string url, HttpClient http)
    {
        Id = id;
        Name = name;
        _url = url;
        _http = http;
    }

    public async Task<IReadOnlyList<ProviderChannel>> LineupAsync(CancellationToken ct = default)
    {
        if (_lineup.Count > 0 && DateTime.UtcNow - _fetchedUtc < Ttl) return _lineup;

        await _gate.WaitAsync(ct);
        try
        {
            if (_lineup.Count > 0 && DateTime.UtcNow - _fetchedUtc < Ttl) return _lineup;

            // providers.json is the administrator's own file, but a playlist
            // URL pointed at this network would still turn a lineup refresh
            // into a fetch of something the caller cannot reach
            if (!Services.PrivateNetwork.MayFetch(_url, out var why))
                throw new InvalidOperationException($"provider '{Id}' will not be fetched: {why}");

            var text = await _http.GetStringAsync(_url, ct);
            var (channels, urls) = Parse(text);
            _lineup = channels;
            _urls = urls;
            _fetchedUtc = DateTime.UtcNow;
            Log.Info("provider", $"{Id}: {channels.Count} channels from playlist");
            return _lineup;
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> ResolveAsync(string channelId, CancellationToken ct = default)
    {
        if (_urls.Count == 0) await LineupAsync(ct);
        return _urls.TryGetValue(channelId, out var url) ? url : null;
    }

    // ---- parsing ---------------------------------------------------------

    private static readonly Regex Attr = new("([A-Za-z0-9-]+)=\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>
    /// Reads #EXTINF attribute soup. The channel id is tvg-id when the
    /// playlist supplies one — it is the only field stable across refreshes —
    /// and otherwise a slug of the name, so a saved channel keeps resolving
    /// after the lineup is refetched.
    /// </summary>
    internal static (List<ProviderChannel>, Dictionary<string, string>) Parse(string text)
    {
        var channels = new List<ProviderChannel>();
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? name = null, group = null, logo = null, tvgId = null;
        var number = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.LastIndexOf(',');
                name = comma >= 0 ? line[(comma + 1)..].Trim() : null;
                group = logo = tvgId = null;
                foreach (Match m in Attr.Matches(line))
                {
                    var v = m.Groups[2].Value;
                    switch (m.Groups[1].Value.ToLowerInvariant())
                    {
                        case "group-title": group = v; break;
                        case "tvg-logo": logo = v; break;
                        case "tvg-id": tvgId = v; break;
                        case "tvg-name": name ??= v; break;
                    }
                }
                continue;
            }

            if (line.StartsWith('#')) continue;      // other tags, comments
            if (name is null) continue;              // stream line with no EXTINF
            if (!line.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { name = null; continue; }

            var id = !string.IsNullOrWhiteSpace(tvgId) ? tvgId! : Slug(name);
            if (id.Length == 0 || !seen.Add(id)) { name = null; continue; }

            urls[id] = line;
            channels.Add(new ProviderChannel(id, name, group ?? "", logo, ++number, null));
            name = null;
        }

        return (channels, urls);
    }

    private static string Slug(string s) =>
        string.Concat(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
}
