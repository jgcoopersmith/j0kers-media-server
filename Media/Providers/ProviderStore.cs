using System.Text.Json;
using System.Text.Json.Serialization;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Media.Providers;

/// <summary>
/// Builds the provider registry: Pluto TV always, plus any playlist-backed
/// providers named in a <c>providers.json</c> sidecar next to the config
/// (same pattern as channels.json / playlists.json).
///
/// The file is written with a commented starting set the first time the
/// server runs, so the shape is discoverable without reading the docs:
/// <code>
/// [
///   { "id": "tubi", "name": "Tubi", "url": "https://…/tubi.m3u", "enabled": false }
/// ]
/// </code>
/// </summary>
public static class ProviderStore
{
    public sealed class ProviderDef
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

        /// <summary>
        /// Relay segments through this server rather than letting the player
        /// fetch them from the source CDN. Needed only when the source does
        /// not send permissive CORS on its media; costs the bandwidth.
        /// </summary>
        [JsonPropertyName("relaySegments")] public bool RelaySegments { get; set; }
    }

    public static ProviderRegistry Load(string baseDirectory, HttpClient http)
    {
        var providers = new List<IChannelProvider> { new PlutoTvProvider(http) };

        var file = Path.Combine(baseDirectory, "providers.json");
        if (!File.Exists(file))
        {
            TryWriteTemplate(file);
            return new ProviderRegistry(providers);
        }

        try
        {
            var defs = JsonSerializer.Deserialize<List<ProviderDef>>(File.ReadAllText(file),
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                ?? new List<ProviderDef>();

            foreach (var d in defs)
            {
                if (!d.Enabled) continue;
                if (string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Url)) continue;
                if (d.Id.Equals("pluto", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn("provider", "'pluto' is built in — ignoring the providers.json entry of that name");
                    continue;
                }
                providers.Add(new M3uProvider(d.Id, string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name, d.Url, http));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("provider", $"could not load providers.json: {ex.Message}");
        }

        return new ProviderRegistry(providers);
    }

    /// <summary>Which providers relay segments, by id.</summary>
    public static HashSet<string> RelaySet(string baseDirectory)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(baseDirectory, "providers.json");
        if (!File.Exists(file)) return set;
        try
        {
            var defs = JsonSerializer.Deserialize<List<ProviderDef>>(File.ReadAllText(file),
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var d in defs ?? new List<ProviderDef>())
                if (d.RelaySegments && !string.IsNullOrWhiteSpace(d.Id)) set.Add(d.Id);
        }
        catch { /* already reported by Load */ }
        return set;
    }

    private const string Template = """
    // Playlist-backed channel providers, listed alongside the built-in Pluto TV.
    //
    // Each entry is an extended-M3U playlist URL whose channels become
    // browsable here. This is how the services that publish no usable API of
    // their own — Tubi, The Roku Channel, Samsung TV Plus and the rest — are
    // reached: point at a playlist that someone keeps current, and the churn
    // stays with whoever maintains it.
    //
    // Nothing is enabled by default: uncomment a line and set "enabled": true.
    // "relaySegments" is only needed if playback fails with a CORS error in the
    // browser console — it routes the video through this server instead of
    // straight from the source. None of the three below need it.
    //
    // These point at iptv-org, which is community-maintained and refreshed
    // continuously; the URLs are stable but what is behind them is not this
    // project's to promise. Individual channels come and go, so an occasional
    // dead one is normal rather than a fault here.
    //
    // Note: Sling Freestream is deliberately absent. Its streams are DRM
    // (Widevine), so no playlist can make them play here.
    [
      // { "id": "tubi",    "name": "Tubi",
      //   "url": "https://raw.githubusercontent.com/iptv-org/iptv/master/streams/us_tubi.m3u",
      //   "enabled": true },
      // { "id": "roku",    "name": "The Roku Channel",
      //   "url": "https://raw.githubusercontent.com/iptv-org/iptv/master/streams/us_roku.m3u",
      //   "enabled": true },
      // { "id": "samsung", "name": "Samsung TV Plus",
      //   "url": "https://raw.githubusercontent.com/iptv-org/iptv/master/streams/us_samsung.m3u",
      //   "enabled": true }
    ]
    """;

    private static void TryWriteTemplate(string file)
    {
        try { File.WriteAllText(file, Template); }
        catch (Exception ex) { Log.Warn("provider", $"could not write providers.json: {ex.Message}"); }
    }
}
