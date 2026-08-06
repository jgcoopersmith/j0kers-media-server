using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Discovery;

/// <summary>
/// Answers SSDP searches so the server appears where Windows and smart TVs
/// look for devices — the "Network" folder in Explorer, and the device lists
/// on set-top boxes. This is the discovery half of UPnP: HTTP-shaped
/// messages over UDP multicast rather than a protocol of its own.
///
/// Two message kinds matter. A client multicasts <c>M-SEARCH</c> and every
/// matching device answers directly; a device multicasts <c>NOTIFY</c> when
/// it arrives or leaves, so clients already listening learn without asking.
/// Both are implemented, since a client that started first will never send
/// a search that we would otherwise be waiting for.
///
/// Being found is only half of it: whatever finds us then fetches the
/// description document named in the LOCATION header, which the control API
/// serves at <c>/description.xml</c>. Without that, a device appears briefly
/// and is then discarded as unreachable.
/// </summary>
public sealed class SsdpResponder : IDisposable
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.255.250");
    private const int SsdpPort = 1900;

    /// <summary>How long a client may cache us. UPnP requires at least 1800.</summary>
    private const int MaxAge = 1800;

    private readonly string _serverName;
    private readonly string _uuid;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IPAddress> _locals = new();
    private readonly List<(IPAddress addr, IPAddress mask)> _localMasks = new();
    private UdpClient? _socket;
    private bool _disposed;

    /// <summary>
    /// What this device claims to be. A generic Basic device unless DLNA is
    /// switched on: claiming MediaServer:1 has clients ask for a
    /// ContentDirectory service, and a device that answers a browse request
    /// with nothing is worse than one that never claimed to. With DLNA on,
    /// that service exists, so the claim is honest — and necessary, because
    /// a TV looks for MediaServer:1 specifically.
    /// </summary>
    private readonly string _deviceType;
    private const string BasicDevice = "urn:schemas-upnp-org:device:Basic:1";
    private const string MediaServerDevice = "urn:schemas-upnp-org:device:MediaServer:1";
    private const string ContentDirectory = "urn:schemas-upnp-org:service:ContentDirectory:1";
    private const string ConnectionManager = "urn:schemas-upnp-org:service:ConnectionManager:1";

    private readonly bool _mediaServer;

    public SsdpResponder(string serverName, string uuid, int port, bool mediaServer = false)
    {
        _serverName = serverName;
        _uuid = uuid;
        _port = port;
        _mediaServer = mediaServer;
        _deviceType = mediaServer ? MediaServerDevice : BasicDevice;
    }

    public void Start()
    {
        _locals.AddRange(LocalAddresses());
        if (_locals.Count == 0)
        {
            Log.Warn("ssdp", "no usable network interface — UPnP discovery is off");
            return;
        }

        // Wildcard bind, then join per interface — the same reason as mDNS:
        // multicast arrives at sockets bound to the port, not to an address.
        // Windows runs its own SSDP service on this port, so sharing it is a
        // requirement rather than a courtesy.
        try
        {
            _socket = new UdpClient(AddressFamily.InterNetwork);
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
        }
        catch (Exception ex)
        {
            Log.Warn("ssdp", $"could not bind udp/{SsdpPort} ({ex.Message}) — UPnP discovery is off");
            _socket = null;
            return;
        }

        var joined = 0;
        foreach (var local in _locals)
        {
            try { _socket.JoinMulticastGroup(MulticastGroup, local); joined++; }
            catch (Exception ex) { Log.Debug("ssdp", $"cannot join the group on {local}: {ex.Message}"); }
        }

        if (joined == 0)
        {
            Log.Warn("ssdp", "no interface joined the SSDP group — UPnP discovery is off");
            _socket.Dispose();
            _socket = null;
            return;
        }

        _ = ListenAsync(_socket, _cts.Token);
        Log.Info("ssdp", $"announcing on {joined} interface(s) → port {_port}");
        _ = AliveLoopAsync(_cts.Token);
    }

    /// <summary>The address on the client's own network, so LOCATION is fetchable.</summary>
    private IPAddress BestLocalFor(IPAddress remote)
    {
        foreach (var (addr, mask) in _localMasks)
            if (SameSubnet(addr, remote, mask)) return addr;
        return _locals[0];
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (x.Length != y.Length || x.Length != m.Length) return false;
        for (var i = 0; i < x.Length; i++)
            if ((x[i] & m[i]) != (y[i] & m[i])) return false;
        return true;
    }

    private List<IPAddress> LocalAddresses()
    {
        var found = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (!nic.SupportsMulticast) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (addr.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    found.Add(addr.Address);
                    _localMasks.Add((addr.Address, addr.IPv4Mask ?? IPAddress.Parse("255.255.255.0")));
                }
            }
        }
        catch (Exception ex) { Log.Debug("ssdp", $"interface scan failed: {ex.Message}"); }
        return found;
    }

    // ---- answering searches ---------------------------------------------

    private async Task ListenAsync(UdpClient socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await socket.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception) { break; }

            try
            {
                var text = Encoding.UTF8.GetString(packet.Buffer);
                if (!text.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase)) continue;

                var st = Header(text, "ST");
                if (!Matches(st)) continue;

                // MX is how many seconds the client will wait; answering
                // instantly from every device on a network is what MX exists
                // to prevent, so spread replies across that window.
                var mx = Math.Clamp(ParseInt(Header(text, "MX"), 1), 0, 5);
                if (mx > 0) await Task.Delay(Random.Shared.Next(0, mx * 500), ct);

                var local = BestLocalFor(packet.RemoteEndPoint.Address);
                var reply = SearchResponse(local, string.IsNullOrEmpty(st) ? _deviceType : st);
                var bytes = Encoding.UTF8.GetBytes(reply);
                await socket.SendAsync(bytes, bytes.Length, packet.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Debug("ssdp", $"reply failed: {ex.Message}"); }
        }
    }

    /// <summary>Whether a search target asks for something we are.</summary>
    private bool Matches(string? st) =>
        string.IsNullOrEmpty(st)
        || st == "ssdp:all"
        || st == "upnp:rootdevice"
        || st == _deviceType
        || st == "uuid:" + _uuid
        // a TV searching for a media server asks for the device or either
        // service by name, never for ssdp:all
        || (_mediaServer && st is ContentDirectory or ConnectionManager);

    private string Location(IPAddress local) => $"http://{local}:{_port}/description.xml";

    private string SearchResponse(IPAddress local, string st) =>
        "HTTP/1.1 200 OK\r\n" +
        $"CACHE-CONTROL: max-age={MaxAge}\r\n" +
        "EXT:\r\n" +
        $"LOCATION: {Location(local)}\r\n" +
        $"SERVER: {ServerHeader()}\r\n" +
        $"ST: {st}\r\n" +
        $"USN: uuid:{_uuid}::{(st == "uuid:" + _uuid ? "" : st)}\r\n".Replace("::\r\n", "\r\n") +
        "\r\n";

    private static string ServerHeader() =>
        $"{Environment.OSVersion.VersionString.Split(' ')[0]}/1.0 UPnP/1.0 j0kers/1.0";

    // ---- announcing ------------------------------------------------------

    /// <summary>
    /// Re-announces well inside the cache window. A client that missed the
    /// startup burst — or joined the network later — picks us up here rather
    /// than only when it next searches.
    /// </summary>
    private async Task AliveLoopAsync(CancellationToken ct)
    {
        try
        {
            await NotifyAsync("ssdp:alive", ct);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(MaxAge / 2.0), ct);
                await NotifyAsync("ssdp:alive", ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug("ssdp", $"announce loop stopped: {ex.Message}"); }
    }

    private async Task NotifyAsync(string nts, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null) return;

        foreach (var local in _locals.ToList())
        {
            // pick the outgoing interface explicitly, or every announcement
            // leaves by the default route and the other networks never hear it
            try
            {
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    local.GetAddressBytes());
            }
            catch (Exception ex) { Log.Debug("ssdp", $"cannot select {local}: {ex.Message}"); continue; }

            var targets = _mediaServer
                ? new[] { "upnp:rootdevice", _deviceType, ContentDirectory, ConnectionManager, "uuid:" + _uuid }
                : new[] { "upnp:rootdevice", _deviceType, "uuid:" + _uuid };
            foreach (var nt in targets)
            {
                var usn = nt == "uuid:" + _uuid ? $"uuid:{_uuid}" : $"uuid:{_uuid}::{nt}";
                var msg =
                    "NOTIFY * HTTP/1.1\r\n" +
                    $"HOST: {MulticastGroup}:{SsdpPort}\r\n" +
                    $"CACHE-CONTROL: max-age={MaxAge}\r\n" +
                    $"LOCATION: {Location(local)}\r\n" +
                    $"SERVER: {ServerHeader()}\r\n" +
                    $"NT: {nt}\r\n" +
                    $"NTS: {nts}\r\n" +
                    $"USN: {usn}\r\n" +
                    "\r\n";
                var bytes = Encoding.UTF8.GetBytes(msg);
                try { await socket.SendAsync(bytes, bytes.Length, new IPEndPoint(MulticastGroup, SsdpPort)); }
                catch (Exception ex) { Log.Debug("ssdp", $"notify failed: {ex.Message}"); }
                if (ct.IsCancellationRequested) return;
            }
        }
    }

    private static string? Header(string message, string name)
    {
        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            if (!trimmed[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return trimmed[(colon + 1)..].Trim().Trim('"');
        }
        return null;
    }

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, out var v) ? v : fallback;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // tell listeners we're going, so we don't linger in their lists
        try { NotifyAsync("ssdp:byebye", CancellationToken.None).Wait(TimeSpan.FromMilliseconds(600)); } catch { }

        _cts.Cancel();
        try { _socket?.Close(); } catch { }
        try { _socket?.Dispose(); } catch { }
        _socket = null;
        _locals.Clear();
        _localMasks.Clear();
        _cts.Dispose();
    }
}
