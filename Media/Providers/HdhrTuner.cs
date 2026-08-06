using System.Text.Json;
using System.Text.Json.Serialization;

namespace J0kersMediaServer.Media.Providers;

/// <summary>
/// Reads an HDHomeRun network tuner's channel lineup.
///
/// A tuner is an antenna with an HTTP server on it: <c>/discover.json</c>
/// names the box, <c>/lineup.json</c> lists what its last scan found, and
/// each entry already carries the URL that plays it. So importing a lineup
/// is two GETs and no protocol work — the alternative was typing forty
/// channel numbers into the add-channel form by hand.
///
/// Nothing here tunes or scans: the box does that itself, through its own
/// web UI or app. This reads the result.
/// </summary>
public static class HdhrTuner
{
    public sealed record Device(string Name, string Model, int TunerCount, string BaseUrl);
    public sealed record Channel(string Number, string Name, string Url, bool Hd, bool Drm, bool Favorite);
    public sealed record Lineup(Device Device, IReadOnlyList<Channel> Channels);

    private sealed class DiscoverJson
    {
        [JsonPropertyName("FriendlyName")] public string? FriendlyName { get; set; }
        [JsonPropertyName("ModelNumber")] public string? ModelNumber { get; set; }
        [JsonPropertyName("TunerCount")] public int TunerCount { get; set; }
        [JsonPropertyName("BaseURL")] public string? BaseUrl { get; set; }
        [JsonPropertyName("LineupURL")] public string? LineupUrl { get; set; }
    }

    private sealed class LineupJson
    {
        [JsonPropertyName("GuideNumber")] public string? GuideNumber { get; set; }
        [JsonPropertyName("GuideName")] public string? GuideName { get; set; }
        [JsonPropertyName("URL")] public string? Url { get; set; }
        [JsonPropertyName("HD")] public int Hd { get; set; }
        [JsonPropertyName("DRM")] public int Drm { get; set; }
        [JsonPropertyName("Favorite")] public int Favorite { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Normalizes whatever the user typed into a bare host[:port]. They may
    /// paste an address, a full <c>http://…/lineup.json</c>, or a name — all
    /// three mean the same box. Returns null if it isn't a usable host.
    /// </summary>
    public static string? NormalizeHost(string? input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return null;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        // http only, and no credentials smuggled in the authority
        if (u.Scheme != Uri.UriSchemeHttp) return null;
        if (!string.IsNullOrEmpty(u.UserInfo)) return null;
        if (string.IsNullOrWhiteSpace(u.Host)) return null;
        return u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}";
    }

    /// <summary>
    /// Fetches the tuner's identity and lineup. Throws with a plain message
    /// when the box isn't there or isn't a tuner — the dashboard shows it
    /// verbatim, and "no lineup.json" is the useful half of that.
    /// </summary>
    public static async Task<Lineup> ReadAsync(string host, HttpClient http, CancellationToken ct = default)
    {
        var baseUrl = $"http://{host}";

        var device = new Device("HDHomeRun", "", 0, baseUrl);
        try
        {
            var d = JsonSerializer.Deserialize<DiscoverJson>(
                await http.GetStringAsync($"{baseUrl}/discover.json", ct), Json);
            if (d is not null)
                device = new Device(
                    string.IsNullOrWhiteSpace(d.FriendlyName) ? "HDHomeRun" : d.FriendlyName,
                    d.ModelNumber ?? "", d.TunerCount,
                    string.IsNullOrWhiteSpace(d.BaseUrl) ? baseUrl : d.BaseUrl);
        }
        catch
        {
            // older firmware answers lineup.json without discover.json; the
            // lineup is what was actually asked for, so press on
        }

        List<LineupJson>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<LineupJson>>(
                await http.GetStringAsync($"{baseUrl}/lineup.json", ct), Json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"no channel lineup at {baseUrl}/lineup.json ({ex.Message}). " +
                "Check the address, and that the tuner has finished a channel scan.");
        }

        var channels = (raw ?? new List<LineupJson>())
            .Where(c => !string.IsNullOrWhiteSpace(c.Url))
            .Select(c => new Channel(
                (c.GuideNumber ?? "").Trim(),
                string.IsNullOrWhiteSpace(c.GuideName) ? (c.GuideNumber ?? "channel").Trim() : c.GuideName.Trim(),
                c.Url!.Trim(),
                c.Hd == 1, c.Drm == 1, c.Favorite == 1))
            .ToList();

        return new Lineup(device, channels);
    }

    /// <summary>
    /// The name a channel is saved under: "5.1 NBC". The number leads so the
    /// list sorts the way the remote does, and so two subchannels of one
    /// station don't collide on the same name.
    /// </summary>
    public static string ChannelName(Channel c) =>
        c.Number.Length > 0 ? $"{c.Number} {c.Name}" : c.Name;
}
