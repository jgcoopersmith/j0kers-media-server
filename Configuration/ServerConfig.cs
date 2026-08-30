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

    [JsonPropertyName("https")]
    public HttpsConfig Https { get; set; } = new();

    [JsonPropertyName("discovery")]
    public DiscoveryConfig Discovery { get; set; } = new();

    /// <summary>
    /// Run in the background with a notification-area (tray) icon and the
    /// console hidden — double-click the icon for the dashboard, right-click
    /// for a menu. Windows only; ignored elsewhere.
    /// </summary>
    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; }

    /// <summary>
    /// What removing an HLS stream link should do with a conversion that
    /// already exists: "ask" (the default - the dashboard offers both),
    /// "keep" to always leave the files, or "delete" to always free the disk.
    /// Set from the Config dialog, so the choice is the server's rather than
    /// one browser's.
    /// </summary>
    [JsonPropertyName("streamRemoveAction")]
    public string StreamRemoveAction { get; set; } = "ask";

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

    public bool IsDynamicMount(string path)
    {
        lock (_mountLock) return _dynamicMountPaths.Contains(path);
    }

    /// <summary>
    /// A thread-safe copy of the mounts. Readers (RTSP SETUP/DESCRIBE, the
    /// dashboard) must never enumerate the live list while another request
    /// adds or removes a mount under the lock.
    /// </summary>
    public IReadOnlyList<MountConfig> MountsSnapshot()
    {
        lock (_mountLock) return Mounts.ToArray();
    }

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
        [JsonPropertyName("authToken")] public string? AuthToken { get; set; }
        [JsonPropertyName("minimizeToTray")] public bool? MinimizeToTray { get; set; }
        [JsonPropertyName("openDashboardOnStart")] public bool? OpenDashboardOnStart { get; set; }
        [JsonPropertyName("linkLifetimeHours")] public int? LinkLifetimeHours { get; set; }
        [JsonPropertyName("discoveryEnabled")] public bool? DiscoveryEnabled { get; set; }
        [JsonPropertyName("dlnaEnabled")] public bool? DlnaEnabled { get; set; }
        [JsonPropertyName("httpsEnabled")] public bool? HttpsEnabled { get; set; }
        [JsonPropertyName("logLevel")] public string? LogLevel { get; set; }
        [JsonPropertyName("logToFile")] public bool? LogToFile { get; set; }
        [JsonPropertyName("logDirectory")] public string? LogDirectory { get; set; }
        [JsonPropertyName("logRotateSizeMb")] public int? LogRotateSizeMb { get; set; }
        [JsonPropertyName("logRotatePeriod")] public string? LogRotatePeriod { get; set; }
        [JsonPropertyName("logMaxFiles")] public int? LogMaxFiles { get; set; }
        [JsonPropertyName("mediaRoot")] public string? MediaRoot { get; set; }
        [JsonPropertyName("streamRemoveAction")] public string? StreamRemoveAction { get; set; }
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
        if (s.AuthToken is not null) _persistedSettings.AuthToken = s.AuthToken;
        if (s.MinimizeToTray is not null) _persistedSettings.MinimizeToTray = s.MinimizeToTray;
        if (s.LinkLifetimeHours is not null) _persistedSettings.LinkLifetimeHours = s.LinkLifetimeHours;
        if (s.DiscoveryEnabled is not null) _persistedSettings.DiscoveryEnabled = s.DiscoveryEnabled;
        if (s.DlnaEnabled is not null) _persistedSettings.DlnaEnabled = s.DlnaEnabled;
        if (s.HttpsEnabled is not null) _persistedSettings.HttpsEnabled = s.HttpsEnabled;
        if (s.LogLevel is not null) _persistedSettings.LogLevel = s.LogLevel;
        if (s.LogToFile is not null) _persistedSettings.LogToFile = s.LogToFile;
        if (s.LogDirectory is not null) _persistedSettings.LogDirectory = s.LogDirectory;
        if (s.LogRotateSizeMb is not null) _persistedSettings.LogRotateSizeMb = s.LogRotateSizeMb;
        if (s.LogRotatePeriod is not null) _persistedSettings.LogRotatePeriod = s.LogRotatePeriod;
        if (s.LogMaxFiles is not null) _persistedSettings.LogMaxFiles = s.LogMaxFiles;
        if (s.MediaRoot is not null) _persistedSettings.MediaRoot = s.MediaRoot;
        if (s.StreamRemoveAction is not null) _persistedSettings.StreamRemoveAction = s.StreamRemoveAction;
        WriteAtomic(SettingsFile, JsonSerializer.Serialize(_persistedSettings, JsonOpts), "settings");
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
        if (!string.IsNullOrWhiteSpace(s.AuthToken)) Control.AuthToken = s.AuthToken;
        // Whether the server opens a browser on ITS OWN machine at startup.
        //
        // It had no way in at all — not the Config dialog, not /api/settings —
        // and on a server administered from another machine it is the setting
        // that quietly breaks shutdown-on-close. The window it opens sits on
        // the server's own screen holding a live link, so closing the browser
        // on the machine you are actually sitting at can never bring the count
        // to zero, and the server never stops. Nobody could see it, and nobody
        // could turn it off without hand-editing server.json on the box.
        if (s.OpenDashboardOnStart is bool openDash) Control.OpenDashboardOnStart = openDash;

        if (s.MinimizeToTray is bool tray)
        {
            // Worth a line, because this quietly beats server.json and the two
            // disagreeing is exactly the state nobody could see: server.json
            // says false, settings.json says true, and the server runs in the
            // tray while the file somebody edited says it should not.
            if (tray != MinimizeToTray)
                J0kersMediaServer.Logging.Log.Info("config", $"settings.json sets minimizeToTray={tray}, "
                                         + $"overriding server.json's {MinimizeToTray}");
            MinimizeToTray = tray;
        }
        if (s.LinkLifetimeHours is int hours) Hls.LinkLifetimeHours = hours;
        if (s.DiscoveryEnabled is bool announce) Discovery.Enabled = announce;
        if (s.DlnaEnabled is bool dlna) Discovery.Dlna = dlna;
        // takes effect at the next start: the listeners are already bound, and
        // the certificate binding needs the elevation prompt startup does
        if (s.HttpsEnabled is bool tls) Https.Enabled = tls;
        if (!string.IsNullOrWhiteSpace(s.LogLevel)) Logging.Level = s.LogLevel;
        if (s.LogToFile is bool toFile) Logging.ToFile = toFile;
        if (!string.IsNullOrWhiteSpace(s.LogDirectory)) Logging.Directory = s.LogDirectory;
        if (s.LogRotateSizeMb is int mb) Logging.RotateSizeMb = mb;
        if (!string.IsNullOrWhiteSpace(s.LogRotatePeriod)) Logging.RotatePeriod = s.LogRotatePeriod;
        if (s.LogMaxFiles is int keep) Logging.MaxFiles = keep;
        // takes effect at the next start: the media root is read once, when the
        // transcoder and HLS server are constructed
        if (!string.IsNullOrWhiteSpace(s.MediaRoot)) Hls.MediaRoot = s.MediaRoot;
        if (!string.IsNullOrWhiteSpace(s.StreamRemoveAction)) StreamRemoveAction = s.StreamRemoveAction;
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
        WriteAtomic(DynamicMountsFile, JsonSerializer.Serialize(sidecar, JsonOpts), "mounts");
    }

    /// <summary>
    /// Writes through a temp file so a crash partway cannot truncate what was
    /// already saved. Config lives in Configuration/ rather than Media/, so it
    /// keeps its own copy of the two lines rather than depending on the media
    /// sidecar helper.
    /// </summary>
    private static void WriteAtomic(string file, string json, string label)
    {
        var tmp = $"{file}.{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            // global:: — this class has a Logging property that shadows the namespace
            global::J0kersMediaServer.Logging.Log.Error(label, $"could not save {Path.GetFileName(file)}: {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
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

        if (Rtp.PortRangeMin is < 1 or > 65534 || Rtp.PortRangeMax is < 2 or > 65535)
            throw new InvalidOperationException("Config error: rtp port range must be within 1–65535.");
        if (Rtp.PortRangeMin >= Rtp.PortRangeMax)
            throw new InvalidOperationException("Config error: rtp.portRangeMin must be < rtp.portRangeMax.");
        if (Rtp.PortRangeMin % 2 != 0)
            throw new InvalidOperationException("Config error: rtp.portRangeMin must be even (RTP uses even/odd port pairs, RFC 3550 §11).");
        if (Rtp.RtcpIntervalSeconds <= 0)
            throw new InvalidOperationException("Config error: rtp.rtcpIntervalSeconds must be > 0.");
        if (Hls.TargetDurationSeconds < 1)
            throw new InvalidOperationException("Config error: hls.targetDurationSeconds must be ≥ 1.");

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

    /// <summary>
    /// Require an account on RTSP once one exists — play with
    /// <c>rtsp://user:password@host:8554/mount</c>, which VLC, ffplay and
    /// every other client understand. Set false to leave RTSP open.
    /// </summary>
    [JsonPropertyName("requireAuth")] public bool RequireAuth { get; set; } = true;
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
    /// <summary>
    /// Origin allowed to read playlists cross-origin. The default reflects
    /// this machine's own pages (the dashboard sits on another port, which
    /// is a separate origin) and refuses everyone else. Set "*" to go back
    /// to letting any website read them.
    /// </summary>
    [JsonPropertyName("corsAllowOrigin")] public string CorsAllowOrigin { get; set; } = "";

    /// <summary>
    /// How long a signed media link stays valid. A week by default: long
    /// enough that a link you sent someone still works next weekend, short
    /// enough that a leaked one isn't permanent. Editable in the dashboard's
    /// Config dialog.
    /// </summary>
    [JsonPropertyName("linkLifetimeHours")] public int LinkLifetimeHours { get; set; } = 168;
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

    /// <summary>
    /// Shut the whole server down cleanly when the dashboard page closes.
    /// A short grace period ignores page refreshes and tab switches: if any
    /// dashboard reconnects within 5 seconds, the shutdown is cancelled.
    /// </summary>
    [JsonPropertyName("shutdownOnClose")] public bool ShutdownOnClose { get; set; } = true;
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

    /// <summary>
    /// Sliding window size for live channels — how many recent segments stay
    /// live and listed. At 4s each, 15 is a ~60s window.
    ///
    /// It was 6 (~24s), which is too short to survive a channel restart. A
    /// Pluto restream's upstream ffmpeg dies and restarts every few minutes
    /// (upstream EOF at an ad splice), leaving a 3–6s gap with no new
    /// segments; meanwhile a television buffers seconds ahead. With only 24s
    /// live, a device that stalls through the gap finds the segments it was
    /// mid-playback of already deleted, so it snaps to the live edge — the
    /// "long pause, then jumps minutes ahead, rejoins mid-advert" a viewer
    /// sees. A 60s window gives the device runway to ride the gap out and
    /// keep playing across it. Cost is ~35s more latency behind live and a
    /// few more segments on disk per channel — both cheap for a channel
    /// nobody is steering.
    /// </summary>
    [JsonPropertyName("liveWindowSegments")] public int LiveWindowSegments { get; set; } = 15;

    /// <summary>
    /// "transcode" (default, works for MPEG-2 tuners etc.) or "copy"
    /// (remux only — cheap, but the source codecs must be HLS-compatible).
    /// </summary>
    [JsonPropertyName("liveVideoMode")] public string LiveVideoMode { get; set; } = "transcode";

    /// <summary>
    /// Output video codec for transcodes: h264 (default), h265/hevc, vp9,
    /// av1, copy, or any raw ffmpeg encoder name (e.g. libx264). Validated
    /// against the installed ffmpeg's encoder list at startup; falls back
    /// to h264 with a warning if unavailable.
    /// </summary>
    [JsonPropertyName("videoCodec")] public string VideoCodec { get; set; } = "h264";

    /// <summary>
    /// Output audio codec for transcodes: aac (default), mp3, opus, ac3,
    /// flac, copy, or any raw ffmpeg encoder name. Same validation rules.
    /// </summary>
    [JsonPropertyName("audioCodec")] public string AudioCodec { get; set; } = "aac";

    /// <summary>
    /// Cap on the vod-* transcode cache under the HLS media root, in GB.
    /// Least-recently-played conversions are evicted when exceeded. 0
    /// disables eviction.
    /// </summary>
    /// <summary>
    /// How much disk converted copies may occupy before the least recently
    /// played are deleted. 0 - the default - means no limit: nothing is ever
    /// deleted to reclaim space.
    ///
    /// It defaulted to 10 GB, and the shipped server.json never named the
    /// setting, so every install ran on that number without it appearing
    /// anywhere the owner could see. Ten gigabytes is about twenty films. An
    /// overnight library conversion therefore spent hours producing copies
    /// and deleting the older ones as fast as it made the new ones - the
    /// count going down over an hour of work, with nothing in the interface
    /// to say why. Nothing was broken; the cache did exactly what it was
    /// told, and what it was told was a number nobody had chosen.
    ///
    /// A conversion is expensive to make and cheap to keep, and this server
    /// converts a library on purpose rather than only caching what it plays.
    /// Deleting that work to save disk is the wrong trade by default. Set a
    /// number here to put a limit back; conversions requested from the
    /// Transcodes window are still never deleted, whatever it is set to.
    /// </summary>
    [JsonPropertyName("vodCacheMaxGb")] public double VodCacheMaxGb { get; set; } = 0;

    /// <summary>
    /// Carry a channel's subtitles through the restream.
    ///
    /// On by default: dropping them was a workaround for a provider whose
    /// subtitle endpoint stalls, and a workaround that deletes a feature is
    /// the user's decision, not the server's. Turn it off if a particular
    /// source's subtitle track is unreliable enough to disturb the video.
    /// </summary>
    [JsonPropertyName("liveSubtitles")] public bool LiveSubtitles { get; set; } = true;
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

    /// <summary>
    /// One line per request served, on every port — the media, the API, DLNA.
    /// On by default; set false to keep only the event log. See AccessLog.
    /// </summary>
    [JsonPropertyName("accessLog")] public bool AccessLog { get; set; } = true;

    /// <summary>
    /// Keep a copy of the log on disk. On by default: in tray mode there is
    /// no console at all, so without this nothing survives the session.
    /// </summary>
    [JsonPropertyName("toFile")] public bool ToFile { get; set; } = true;

    /// <summary>Where the log files live; relative paths are from the working directory.</summary>
    [JsonPropertyName("directory")] public string Directory { get; set; } = "logs";

    /// <summary>Start a new file once the current one passes this size. 0 = no size limit.</summary>
    [JsonPropertyName("rotateSizeMb")] public int RotateSizeMb { get; set; } = 10;

    /// <summary>none | hourly | daily | weekly | monthly. Combines with the size limit — whichever hits first.</summary>
    [JsonPropertyName("rotatePeriod")] public string RotatePeriod { get; set; } = "daily";

    /// <summary>How many rotated files to keep before the oldest are deleted.</summary>
    [JsonPropertyName("maxFiles")] public int MaxFiles { get; set; } = 7;

    /// <summary>
    /// The log directory as an absolute path. A relative setting hangs off
    /// the config directory, not the working directory — the desktop
    /// shortcut and `dotnet run` start from different places.
    /// </summary>
    public string ResolveDirectory(string baseDirectory)
    {
        var dir = string.IsNullOrWhiteSpace(Directory) ? "logs" : Directory;
        return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(dir)
            ? dir
            : System.IO.Path.Combine(baseDirectory, dir));
    }
}

/// <summary>
/// TLS for the dashboard and the media port.
///
/// Both ports or neither: a dashboard on https that loads its video from
/// http is mixed content, which browsers block outright — so turning this
/// on moves the control port and the HLS port together, keeping the same
/// port numbers and changing the scheme. RTSP is unaffected; it has its own
/// transport and its own authentication.
/// </summary>
public sealed class HttpsConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    /// <summary>
    /// A PKCS#12 (.pfx) file holding the certificate and its private key.
    /// Empty means "make one": a self-signed certificate is generated into
    /// the config directory, valid for this machine's names and addresses.
    /// </summary>
    [JsonPropertyName("certificate")] public string Certificate { get; set; } = "";

    [JsonPropertyName("password")] public string Password { get; set; } = "";
}

/// <summary>
/// Announcing the server on the local network, so devices can find it
/// without being told an IP. Off would mean typing an address on every
/// device; on means it appears by name.
/// </summary>
public sealed class DiscoveryConfig
{
    /// <summary>Master switch — false silences all three mechanisms.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>The .local name: "j0kers" publishes j0kers.local.</summary>
    [JsonPropertyName("hostName")] public string HostName { get; set; } = "j0kers";

    /// <summary>Bonjour/Avahi-style naming and browsing (RFC 6762/6763).</summary>
    [JsonPropertyName("mdns")] public bool Mdns { get; set; } = true;

    /// <summary>UPnP discovery — Windows Explorer's Network folder, smart TVs.</summary>
    [JsonPropertyName("ssdp")] public bool Ssdp { get; set; } = true;

    /// <summary>One-packet JSON answer for scripts and apps.</summary>
    [JsonPropertyName("udpProbe")] public bool UdpProbe { get; set; } = true;

    /// <summary>Jellyfin uses 7359; sharing it means their clients find this too.</summary>
    [JsonPropertyName("udpProbePort")] public int UdpProbePort { get; set; } = 7359;

    /// <summary>
    /// Serve the library over DLNA, so TVs and players with no browser can
    /// browse it from their own "Media Server" input.
    ///
    /// Off by default, and the default matters: DLNA has no authentication
    /// of any kind — no account, no cookie, no token — so switching it on
    /// shares every library folder with every device on the network. The
    /// server refuses DLNA requests from anything that isn't a private LAN
    /// address, which is the only boundary the protocol allows.
    /// </summary>
    [JsonPropertyName("dlna")] public bool Dlna { get; set; }

    /// <summary>
    /// A port of its own for DLNA, in the clear.
    ///
    /// TVs and set-top boxes overwhelmingly cannot do TLS, so switching the
    /// server to HTTPS would otherwise take DLNA away with it. Since DLNA
    /// has no authentication in the first place — that is the protocol, not
    /// a choice made here — serving it over plain HTTP alongside a TLS
    /// dashboard gives up nothing that DLNA had.
    ///
    /// 0 means "decide for me": the control port while the server is plain
    /// HTTP (no extra port at all), and the control port + 1 once TLS is on.
    /// </summary>
    [JsonPropertyName("dlnaPort")] public int DlnaPort { get; set; }

    /// <summary>
    /// Hand a television the converted copy of a file when one already
    /// exists, instead of the original.
    ///
    /// The point is a device that cannot decode what is on disk — HEVC, an
    /// unfamiliar container — being given H.264/AAC instead of failing.
    /// Only conversions that were never downscaled are used, so the picture
    /// size is whatever the file had; a 720p copy made for a phone is never
    /// substituted for the original. Nothing is converted on demand: if
    /// there is no finished full-resolution copy, the original is served
    /// exactly as before.
    ///
    /// Off by default, and the default is the point: a conversion is a
    /// re-encode, and a re-encode is lossier than the file it came from.
    /// Substituting one for a television that could have decoded the
    /// original is a downgrade nobody asked for. Switch it on when a device
    /// genuinely cannot play what is on disk — an HEVC file, an unfamiliar
    /// container — and would otherwise get nothing at all.
    /// </summary>
    [JsonPropertyName("dlnaUseTranscode")] public bool DlnaUseTranscode { get; set; }

    /// <summary>
    /// Show running live channels to DLNA under a "Live TV" folder, each one
    /// playable as a continuous stream.
    ///
    /// A live channel has no end and no fixed size, and DLNA is built around
    /// files that have both — so this presents each channel as one large,
    /// byte-range-seekable file whose bytes are delivered at real time once a
    /// player catches the live edge (a DVR/timeshift shape). It is the same
    /// fixed-size, seekable response a television already plays for a finished
    /// conversion, which is the point: an earlier attempt served a chunked
    /// live stream instead, and at least one television rejected it outright,
    /// auto-advancing within a second. This keeps a real Content-Length so
    /// that set has a file to play, not a stream to refuse.
    ///
    /// Off by default: it retains segments on disk while a television watches
    /// (a few GB per hour per channel, swept when nobody is), and whether a
    /// given set accepts the timeshift shape can only be found by trying it.
    /// </summary>
    [JsonPropertyName("dlnaLiveTv")] public bool DlnaLiveTv { get; set; }
}
