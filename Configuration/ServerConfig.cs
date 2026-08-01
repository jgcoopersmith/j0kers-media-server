using System.Text.Json;
using System.Text.Json.Serialization;

namespace J0kersMediaServer.Config;

/// <summary>
/// Root configuration for j0kers Media Server.
/// Loaded from JSON, with every value overridable via environment
/// variables of the form J0KERS_SECTION_KEY (e.g. J0KERS_RTSP_PORT).
/// </summary>
public sealed class ServerConfig
{
    [JsonPropertyName("serverName")]
    public string ServerName { get; set; } = "j0kers Media Server";

    [JsonPropertyName("rtsp")]
    public RtspConfig Rtsp { get; set; } = new();

    [JsonPropertyName("rtp")]
    public RtpConfig Rtp { get; set; } = new();

    [JsonPropertyName("hls")]
    public HlsConfig Hls { get; set; } = new();

    [JsonPropertyName("control")]
    public ControlConfig Control { get; set; } = new();

    [JsonPropertyName("services")]
    public ServicesConfig Services { get; set; } = new();

    [JsonPropertyName("ffmpeg")]
    public FfmpegConfig Ffmpeg { get; set; } = new();

    [JsonPropertyName("mounts")]
    public List<MountConfig> Mounts { get; set; } = new();

    [JsonPropertyName("logging")]
    public LoggingConfig Logging { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Mounts added at runtime (dashboard / control API) live in a
    /// mounts.json sidecar next to the main config, so the hand-edited,
    /// commented server.json is never rewritten by the server.
    /// </summary>
    [JsonIgnore] public string DynamicMountsFile { get; private set; } = "mounts.json";

    /// <summary>Dashboard-edited settings (hostname/ports) live in this sidecar.</summary>
    [JsonIgnore] public string SettingsFile { get; private set; } = "settings.json";

    private readonly HashSet<string> _dynamicMountPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removedMountPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _mountLock = new();

    public bool IsDynamicMount(string path) => _dynamicMountPaths.Contains(path);

    public static ServerConfig Load(string? path)
    {
        ServerConfig cfg;
        if (path is not null && File.Exists(path))
        {
            cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), JsonOpts)
                  ?? new ServerConfig();
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            cfg.DynamicMountsFile = Path.Combine(dir, "mounts.json");
            cfg.SettingsFile = Path.Combine(dir, "settings.json");
        }
        else
        {
            cfg = new ServerConfig();
            cfg.DynamicMountsFile = Path.Combine(Directory.GetCurrentDirectory(), "mounts.json");
            cfg.SettingsFile = Path.Combine(Directory.GetCurrentDirectory(), "settings.json");
        }

        cfg.LoadSettingsOverrides();
        cfg.ApplyEnvironmentOverrides();
        cfg.LoadDynamicMounts();
        cfg.Validate();
        return cfg;
    }

    public sealed class SettingsOverrides
    {
        [JsonPropertyName("serverName")] public string? ServerName { get; set; }
        [JsonPropertyName("bindAddress")] public string? BindAddress { get; set; }
        [JsonPropertyName("rtspPort")] public int? RtspPort { get; set; }
        [JsonPropertyName("hlsPort")] public int? HlsPort { get; set; }
        [JsonPropertyName("controlPort")] public int? ControlPort { get; set; }
    }

    private SettingsOverrides _persistedSettings = new();

    private void LoadSettingsOverrides()
    {
        if (!File.Exists(SettingsFile)) return;
        try
        {
            _persistedSettings = JsonSerializer.Deserialize<SettingsOverrides>(
                File.ReadAllText(SettingsFile), JsonOpts) ?? new SettingsOverrides();
            ApplySettings(_persistedSettings);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"settings.json is invalid: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies and persists dashboard settings. Only the fields actually
    /// provided are stored in the sidecar, so anything the user never
    /// touched keeps following server.json.
    /// </summary>
    public void UpdateSettings(SettingsOverrides s)
    {
        ApplySettings(s);
        if (s.ServerName is not null) _persistedSettings.ServerName = s.ServerName;
        if (s.BindAddress is not null) _persistedSettings.BindAddress = s.BindAddress;
        if (s.RtspPort is not null) _persistedSettings.RtspPort = s.RtspPort;
        if (s.HlsPort is not null) _persistedSettings.HlsPort = s.HlsPort;
        if (s.ControlPort is not null) _persistedSettings.ControlPort = s.ControlPort;
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_persistedSettings, JsonOpts));
    }

    /// <summary>Applies dashboard settings on top of the loaded config.</summary>
    public void ApplySettings(SettingsOverrides s)
    {
        if (!string.IsNullOrWhiteSpace(s.ServerName)) ServerName = s.ServerName;
        if (!string.IsNullOrWhiteSpace(s.BindAddress))
        {
            Rtsp.BindAddress = s.BindAddress;
            Hls.BindAddress = s.BindAddress;
            Control.BindAddress = s.BindAddress;
        }
        if (s.RtspPort is int rp) Rtsp.Port = rp;
        if (s.HlsPort is int hp) Hls.Port = hp;
        if (s.ControlPort is int cp) Control.Port = cp;
    }

    private sealed class MountSidecar
    {
        [JsonPropertyName("added")] public List<MountConfig> Added { get; set; } = new();
        [JsonPropertyName("removed")] public List<string> Removed { get; set; } = new();
    }

    private void LoadDynamicMounts()
    {
        if (!File.Exists(DynamicMountsFile)) return;
        var text = File.ReadAllText(DynamicMountsFile);

        // legacy format was a bare array of added mounts
        var sidecar = text.TrimStart().StartsWith('[')
            ? new MountSidecar { Added = JsonSerializer.Deserialize<List<MountConfig>>(text, JsonOpts) ?? new() }
            : JsonSerializer.Deserialize<MountSidecar>(text, JsonOpts) ?? new MountSidecar();

        foreach (var m in sidecar.Added)
        {
            if (Mounts.Any(x => string.Equals(x.Path, m.Path, StringComparison.OrdinalIgnoreCase)))
                continue; // server.json wins on conflict
            Mounts.Add(m);
            _dynamicMountPaths.Add(m.Path);
        }

        // tombstones: server.json mounts the user removed from the dashboard
        foreach (var path in sidecar.Removed)
        {
            _removedMountPaths.Add(path);
            Mounts.RemoveAll(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Adds a mount at runtime and persists it to the sidecar file.</summary>
    public void AddDynamicMount(MountConfig mount)
    {
        lock (_mountLock)
        {
            if (Mounts.Any(x => string.Equals(x.Path, mount.Path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"a mount at '{mount.Path}' already exists");
            Mounts.Add(mount);
            _dynamicMountPaths.Add(mount.Path);
            _removedMountPaths.Remove(mount.Path); // re-adding clears a tombstone
            SaveDynamicMounts();
        }
    }

    /// <summary>
    /// Removes any mount at runtime. Dashboard-added mounts are dropped from
    /// the sidecar; server.json mounts get a persisted tombstone instead, so
    /// the hand-edited config file itself is never rewritten.
    /// </summary>
    public bool RemoveMount(string path)
    {
        lock (_mountLock)
        {
            var existed = Mounts.RemoveAll(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!existed) return false;
            if (!_dynamicMountPaths.Remove(path))
                _removedMountPaths.Add(path); // came from server.json → tombstone it
            SaveDynamicMounts();
            return true;
        }
    }

    private void SaveDynamicMounts()
    {
        var sidecar = new MountSidecar
        {
            Added = Mounts.Where(m => _dynamicMountPaths.Contains(m.Path)).ToList(),
            Removed = _removedMountPaths.ToList(),
        };
        File.WriteAllText(DynamicMountsFile, JsonSerializer.Serialize(sidecar, JsonOpts));
    }

    private void ApplyEnvironmentOverrides()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name);

        if (int.TryParse(Env("J0KERS_RTSP_PORT"), out var rtspPort)) Rtsp.Port = rtspPort;
        if (int.TryParse(Env("J0KERS_HLS_PORT"), out var hlsPort)) Hls.Port = hlsPort;
        if (int.TryParse(Env("J0KERS_CONTROL_PORT"), out var ctlPort)) Control.Port = ctlPort;
        if (Env("J0KERS_BIND_ADDRESS") is { Length: > 0 } bind)
        {
            Rtsp.BindAddress = bind;
            Hls.BindAddress = bind;
            Control.BindAddress = bind;
        }
        if (Env("J0KERS_LOG_LEVEL") is { Length: > 0 } lvl) Logging.Level = lvl;
    }

    private void Validate()
    {
        foreach (var (port, name) in new[]
                 {
                     (Rtsp.Port, "rtsp.port"),
                     (Hls.Port, "hls.port"),
                     (Control.Port, "control.port"),
                 })
        {
            if (port is < 1 or > 65535)
                throw new InvalidOperationException($"Config error: {name}={port} is not a valid TCP port.");
        }

        if (Rtp.PortRangeMin >= Rtp.PortRangeMax)
            throw new InvalidOperationException("Config error: rtp.portRangeMin must be < rtp.portRangeMax.");
        if (Rtp.PortRangeMin % 2 != 0)
            throw new InvalidOperationException("Config error: rtp.portRangeMin must be even (RTP uses even/odd port pairs, RFC 3550 §11).");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Mounts)
        {
            if (string.IsNullOrWhiteSpace(m.Path) || !m.Path.StartsWith('/'))
                throw new InvalidOperationException($"Config error: mount path '{m.Path}' must start with '/'.");
            if (!seen.Add(m.Path))
                throw new InvalidOperationException($"Config error: duplicate mount path '{m.Path}'.");
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>
    /// Bind addresses accept "localhost" as well as literal IPs; socket
    /// binds need an IPAddress, so map the name here instead of letting
    /// IPAddress.Parse throw.
    /// </summary>
    public static System.Net.IPAddress ResolveBindAddress(string bindAddress) =>
        bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? System.Net.IPAddress.Loopback
            : System.Net.IPAddress.Parse(bindAddress);
}

public sealed class RtspConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("bindAddress")] public string BindAddress { get; set; } = "0.0.0.0";
    [JsonPropertyName("port")] public int Port { get; set; } = 8554; // 554 needs elevation
    [JsonPropertyName("sessionTimeoutSeconds")] public int SessionTimeoutSeconds { get; set; } = 60;
    [JsonPropertyName("allowInterleavedTcp")] public bool AllowInterleavedTcp { get; set; } = true;
    [JsonPropertyName("maxSessions")] public int MaxSessions { get; set; } = 64;
    [JsonPropertyName("realm")] public string Realm { get; set; } = "j0kers";
}

public sealed class RtpConfig
{
    [JsonPropertyName("portRangeMin")] public int PortRangeMin { get; set; } = 20000;
    [JsonPropertyName("portRangeMax")] public int PortRangeMax { get; set; } = 20999;
    [JsonPropertyName("rtcpEnabled")] public bool RtcpEnabled { get; set; } = true;
    /// <summary>RTCP sender-report interval; RFC 3550 §6.2 recommends ~5s minimum for small sessions.</summary>
    [JsonPropertyName("rtcpIntervalSeconds")] public double RtcpIntervalSeconds { get; set; } = 5.0;
    [JsonPropertyName("dscp")] public int Dscp { get; set; } = 0;
}

public sealed class HlsConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("bindAddress")] public string BindAddress { get; set; } = "0.0.0.0";
    [JsonPropertyName("port")] public int Port { get; set; } = 8080;
    /// <summary>Directory whose subdirectories are exposed as HLS variant streams.</summary>
    [JsonPropertyName("mediaRoot")] public string MediaRoot { get; set; } = "media";
    [JsonPropertyName("targetDurationSeconds")] public int TargetDurationSeconds { get; set; } = 6;
    /// <summary>Sliding-window size for live playlists; 0 = VOD (full playlist + EXT-X-ENDLIST).</summary>
    [JsonPropertyName("liveWindowSegments")] public int LiveWindowSegments { get; set; } = 0;
    [JsonPropertyName("corsAllowOrigin")] public string CorsAllowOrigin { get; set; } = "*";
}

public sealed class ControlConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("bindAddress")] public string BindAddress { get; set; } = "127.0.0.1";
    [JsonPropertyName("port")] public int Port { get; set; } = 9090;
    /// <summary>Optional bearer token; empty = no auth (loopback only recommended).</summary>
    [JsonPropertyName("authToken")] public string AuthToken { get; set; } = "";

    /// <summary>Open the dashboard in the default browser on startup.</summary>
    [JsonPropertyName("openDashboardOnStart")] public bool OpenDashboardOnStart { get; set; } = true;
}

public sealed class FfmpegConfig
{
    /// <summary>
    /// Path to the ffmpeg executable. "ffmpeg" uses PATH; the server also
    /// probes the winget alias directory as a fallback.
    /// </summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "ffmpeg";

    /// <summary>x264 preset for transcodes (ultrafast…veryslow).</summary>
    [JsonPropertyName("preset")] public string Preset { get; set; } = "veryfast";

    /// <summary>CRF quality for VOD transcodes (lower = better, 18–28 sane).</summary>
    [JsonPropertyName("crf")] public int Crf { get; set; } = 23;

    /// <summary>Live channel segment length in seconds.</summary>
    [JsonPropertyName("liveSegmentSeconds")] public int LiveSegmentSeconds { get; set; } = 4;

    /// <summary>Sliding window size for live channels.</summary>
    [JsonPropertyName("liveWindowSegments")] public int LiveWindowSegments { get; set; } = 6;

    /// <summary>
    /// "transcode" (default, works for MPEG-2 tuners etc.) or "copy"
    /// (remux only — cheap, but the source codecs must be HLS-compatible).
    /// </summary>
    [JsonPropertyName("liveVideoMode")] public string LiveVideoMode { get; set; } = "transcode";

    /// <summary>
    /// Cap on the vod-* transcode cache under the HLS media root, in GB.
    /// Least-recently-played conversions are evicted when exceeded. 0
    /// disables eviction.
    /// </summary>
    [JsonPropertyName("vodCacheMaxGb")] public double VodCacheMaxGb { get; set; } = 10;
}

public sealed class ServicesConfig
{
    /// <summary>RFC 4240-style announcement service: /annc?play=&lt;clip&gt; resolves clips from this directory.</summary>
    [JsonPropertyName("announcementEnabled")] public bool AnnouncementEnabled { get; set; } = true;
    [JsonPropertyName("announcementClipDirectory")] public string AnnouncementClipDirectory { get; set; } = "clips";
}

public sealed class MountConfig
{
    /// <summary>RTSP presentation path, e.g. "/test".</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "/test";

    /// <summary>"tone" (built-in PCMU test tone) or "file" (raw G.711 µ-law file).</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "tone";

    /// <summary>For source=file: path to a raw 8 kHz G.711 µ-law file.</summary>
    [JsonPropertyName("file")] public string? File { get; set; }

    /// <summary>For source=tone: frequency in Hz.</summary>
    [JsonPropertyName("toneFrequencyHz")] public double ToneFrequencyHz { get; set; } = 440.0;

    [JsonPropertyName("description")] public string Description { get; set; } = "";
}

public sealed class LoggingConfig
{
    /// <summary>trace | debug | info | warn | error</summary>
    [JsonPropertyName("level")] public string Level { get; set; } = "info";
    [JsonPropertyName("logRtspMessages")] public bool LogRtspMessages { get; set; } = false;
}
