using System.Security.Cryptography;
using J0kersMediaServer.Config;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Discovery;

/// <summary>
/// Makes the server findable, so a device doesn't have to be told an IP.
///
/// Three mechanisms, because no single one reaches everything:
///   * <b>mDNS</b> (RFC 6762/6763) — a <c>.local</c> name, and browsing on
///     phones, Macs and Linux. This is the one that replaces typing an IP.
///   * <b>SSDP</b> — how Windows Explorer's Network folder and smart TVs
///     find devices.
///   * <b>UDP probe</b> — a one-packet question and a JSON answer, for
///     scripts and apps.
///
/// All three point at the control port, since the dashboard is the front
/// door: from there every stream link is a click away, already carrying the
/// signed token a bare stream URL would need.
///
/// Discovery only ever advertises that the server exists and where. It
/// grants nothing — the dashboard still demands an account, and media still
/// demands a signed link — so being findable is not being open.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private readonly DiscoveryConfig _config;
    private readonly string _serverName;
    private readonly int _port;
    private readonly string _baseDirectory;

    private MdnsResponder? _mdns;
    private SsdpResponder? _ssdp;
    private UdpProbeResponder? _probe;

    /// <summary>Stable per-install identity, so a restart isn't a new device.</summary>
    public string Uuid { get; }

    public string HostName => string.IsNullOrWhiteSpace(_config.HostName) ? "j0kers" : _config.HostName.Trim();

    public DiscoveryService(DiscoveryConfig config, string serverName, int port, string baseDirectory)
    {
        _config = config;
        _serverName = string.IsNullOrWhiteSpace(serverName) ? "j0kers Media Server" : serverName;
        _port = port;
        _baseDirectory = baseDirectory;
        Uuid = LoadOrCreateUuid();
    }

    /// <summary>
    /// A UPnP device that changed identity on every restart would accumulate
    /// stale entries in clients' device lists, so the id is persisted next to
    /// the rest of the runtime state.
    /// </summary>
    private string LoadOrCreateUuid()
    {
        var file = Path.Combine(_baseDirectory, "discovery-id");
        try
        {
            if (File.Exists(file))
            {
                var existing = File.ReadAllText(file).Trim();
                if (Guid.TryParse(existing, out var parsed)) return parsed.ToString();
            }
        }
        catch (Exception ex) { Log.Debug("discovery", $"could not read discovery-id: {ex.Message}"); }

        var created = Guid.NewGuid().ToString();
        try { File.WriteAllText(file, created); }
        catch (Exception ex)
        {
            Log.Warn("discovery", $"could not save discovery-id ({ex.Message}) — " +
                                  "clients will see a new device after each restart");
        }
        return created;
    }

    private readonly object _lock = new();

    /// <summary>
    /// Applies a change to <see cref="DiscoveryConfig.Enabled"/> without a
    /// restart, so the dashboard's switch takes effect as it is flipped.
    /// Turning it off sends the goodbyes, so listeners drop us immediately
    /// rather than keeping a dead entry until the cache expires.
    /// </summary>
    public void Restart()
    {
        lock (_lock)
        {
            StopResponders();
            Start();
        }
    }

    public void Start()
    {
        if (!_config.Enabled)
        {
            Log.Info("discovery", "network announcement off — the server will not advertise itself");
            return;
        }

        if (_config.Mdns)
        {
            _mdns = new MdnsResponder(HostName, _serverName, _port);
            _mdns.Start();
        }
        if (_config.Ssdp)
        {
            _ssdp = new SsdpResponder(_serverName, Uuid, _port);
            _ssdp.Start();
        }
        if (_config.UdpProbe)
        {
            _probe = new UdpProbeResponder(_serverName, Uuid, _port, _config.UdpProbePort);
            _probe.Start();
        }

        if (_mdns is not null)
            Log.Info("discovery", $"reachable by name at http://{HostName}.local:{_port}/");
    }

    /// <summary>
    /// The UPnP description document, fetched by whatever found us via SSDP.
    /// A device that answers a search but serves no description is dropped
    /// again, so this is not optional.
    /// </summary>
    public string DescriptionXml(string host) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <URLBase>http://{host}:{_port}/</URLBase>
          <device>
            <deviceType>urn:schemas-upnp-org:device:Basic:1</deviceType>
            <friendlyName>{Escape(_serverName)}</friendlyName>
            <manufacturer>j0kers</manufacturer>
            <modelName>j0kers Media Server</modelName>
            <modelDescription>RTSP, HLS and free-TV streaming</modelDescription>
            <UDN>uuid:{Uuid}</UDN>
            <presentationURL>http://{host}:{_port}/</presentationURL>
          </device>
        </root>
        """;

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Each responder's Dispose sends its goodbye, so the server stops being
    /// offered the moment it stops announcing.
    /// </summary>
    private void StopResponders()
    {
        try { _mdns?.Dispose(); } catch { }
        try { _ssdp?.Dispose(); } catch { }
        try { _probe?.Dispose(); } catch { }
        _mdns = null;
        _ssdp = null;
        _probe = null;
    }

    public void Dispose()
    {
        lock (_lock) StopResponders();
    }
}
