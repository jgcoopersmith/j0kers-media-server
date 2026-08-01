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

    public static ServerConfig Load(string? path)
    {
        ServerConfig cfg;
        if (path is not null && File.Exists(path))
        {
            cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), JsonOpts)
                  ?? new ServerConfig();
        }
        else
        {
            cfg = new ServerConfig();
        }

        cfg.ApplyEnvironmentOverrides();
        cfg.Validate();
        return cfg;
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
