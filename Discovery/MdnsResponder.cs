using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Discovery;

/// <summary>
/// Answers multicast DNS queries so the server can be reached by name
/// (RFC 6762) and found by browsing (DNS-SD, RFC 6763) — the same mechanism
/// behind every <c>something.local</c> address.
///
/// This is what turns "type the IP" into "type j0kers.local". The address a
/// device needs depends on which network it is on, and a machine on both
/// Ethernet and Wi-Fi has more than one; a name resolves to whichever is
/// right for the asker, and survives a DHCP lease changing underneath it.
///
/// Two things are published:
///   * <c>&lt;host&gt;.local</c> → an A record, so the name resolves at all.
///   * <c>_http._tcp.local</c> → a PTR/SRV/TXT triple, so the dashboard shows
///     up in anything that browses for web services on the network.
///
/// Nothing is claimed exclusively: a real responder probes for conflicts
/// first (§8.1) and defends its name. This one asserts, which is fine on a
/// home network where the name is unlikely to be contested, and is why the
/// hostname is configurable rather than fixed.
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    /// <summary>
    /// Two minutes, the DNS-SD default for records that can change. Long
    /// enough to spare the network constant re-querying, short enough that a
    /// server which vanishes without saying goodbye stops being offered.
    /// </summary>
    private const uint Ttl = 120;

    private readonly string _hostName;      // "j0kers" → j0kers.local
    private readonly string _instanceName;  // shown when browsing
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Local> _locals = new();
    private UdpClient? _socket;
    private bool _disposed;

    private string HostFqdn => _hostName + ".local";
    private const string ServiceType = "_http._tcp.local";
    private string InstanceFqdn => _instanceName + "." + ServiceType;

    public MdnsResponder(string hostName, string instanceName, int port)
    {
        _hostName = Sanitize(hostName, "j0kers");
        _instanceName = string.IsNullOrWhiteSpace(instanceName) ? _hostName : instanceName.Trim();
        _port = port;
    }

    /// <summary>A hostname label: letters, digits and hyphens (RFC 1123).</summary>
    private static string Sanitize(string raw, string fallback)
    {
        var cleaned = new string((raw ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    public void Start()
    {
        _locals.AddRange(LocalAddresses());
        if (_locals.Count == 0)
        {
            Log.Warn("mdns", "no usable network interface — name discovery is off");
            return;
        }

        // One socket, bound to the wildcard, joined to the group once per
        // interface. Binding to a specific interface address instead looks
        // tidier and does not work: Windows delivers multicast to sockets
        // bound to the wildcard port, so a per-interface bind receives
        // nothing — the responder appears to start and then answers nobody.
        // Sharing is required rather than optional, since other mDNS stacks
        // (Bonjour, and anything bundling it) hold this port too.
        try
        {
            _socket = new UdpClient(AddressFamily.InterNetwork);
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
        }
        catch (Exception ex)
        {
            Log.Warn("mdns", $"could not bind udp/{MdnsPort} ({ex.Message}) — name discovery is off");
            _socket = null;
            return;
        }

        var joined = 0;
        foreach (var local in _locals)
        {
            try
            {
                _socket.JoinMulticastGroup(MulticastGroup, local.Address);
                joined++;
            }
            catch (Exception ex)
            {
                Log.Debug("mdns", $"cannot join the group on {local.Address}: {ex.Message}");
            }
        }

        if (joined == 0)
        {
            Log.Warn("mdns", "no interface joined the mDNS group — name discovery is off");
            _socket.Dispose();
            _socket = null;
            return;
        }

        _ = ListenAsync(_socket, _cts.Token);
        Log.Info("mdns", $"announcing {HostFqdn} on {joined} interface(s) → port {_port}");
        _ = AnnounceAsync(_cts.Token);
    }

    /// <summary>
    /// Which of our addresses shares a network with this client, so the name
    /// resolves to something it can actually reach. A machine on both
    /// Ethernet and Wi-Fi has to answer a phone with the Wi-Fi address; the
    /// wired one is correct and useless.
    /// </summary>
    private Local BestLocalFor(IPAddress remote)
    {
        foreach (var local in _locals)
        {
            if (SameSubnet(local.Address, remote, local.Mask)) return local;
        }
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

    private sealed record Local(IPAddress Address, IPAddress Mask);

    /// <summary>Addresses worth answering on, with their masks so replies can be matched to a subnet.</summary>
    private static List<Local> LocalAddresses()
    {
        var found = new List<Local>();
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
                    found.Add(new Local(addr.Address, addr.IPv4Mask ?? IPAddress.Parse("255.255.255.0")));
                }
            }
        }
        catch (Exception ex) { Log.Debug("mdns", $"interface scan failed: {ex.Message}"); }
        return found;
    }

    // ---- serving ---------------------------------------------------------

    private async Task ListenAsync(UdpClient socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await socket.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception) { break; }   // socket closed

            try
            {
                var questions = DnsMessage.ReadQuestions(packet.Buffer);
                if (questions.Count == 0) continue;

                // answer with the address that reaches whoever asked
                var local = BestLocalFor(packet.RemoteEndPoint.Address);
                var reply = BuildReply(questions, local);
                if (reply is null) continue;

                // §5.4: a question can ask to be answered directly. Otherwise
                // the answer goes to the group, so everyone's cache benefits.
                var wantsUnicast = questions.Any(q => q.WantsUnicast);
                var target = wantsUnicast
                    ? packet.RemoteEndPoint
                    : new IPEndPoint(MulticastGroup, MdnsPort);
                await socket.SendAsync(reply, reply.Length, target);
            }
            catch (Exception ex)
            {
                Log.Debug("mdns", $"reply failed: {ex.Message}");
            }
        }
    }

    /// <summary>Answers only what was asked, or null when nothing here matches.</summary>
    private byte[]? BuildReply(List<DnsMessage.Question> questions, Local local)
    {
        var b = new DnsMessage.Builder();

        foreach (var q in questions)
        {
            if (q.BareClass != DnsMessage.ClassIn) continue;
            var name = q.Name;
            var any = q.Type == DnsMessage.TypeAny;

            // "what services exist here?" — the DNS-SD meta-query
            if (Is(name, "_services._dns-sd._udp.local") && (any || q.Type == DnsMessage.TypePtr))
                b.Ptr(name, ServiceType, Ttl);

            // "who offers HTTP?" — answered with our instance, plus the
            // records needed to act on it, so one exchange is enough
            if (Is(name, ServiceType) && (any || q.Type == DnsMessage.TypePtr))
            {
                b.Ptr(ServiceType, InstanceFqdn, Ttl);
                b.Srv(InstanceFqdn, HostFqdn, (ushort)_port, Ttl);
                b.Txt(InstanceFqdn, TxtRecords(), Ttl);
                b.A(HostFqdn, local.Address, Ttl);
            }

            if (Is(name, InstanceFqdn))
            {
                if (any || q.Type == DnsMessage.TypeSrv)
                {
                    b.Srv(InstanceFqdn, HostFqdn, (ushort)_port, Ttl);
                    b.A(HostFqdn, local.Address, Ttl);
                }
                if (any || q.Type == DnsMessage.TypeTxt) b.Txt(InstanceFqdn, TxtRecords(), Ttl);
            }

            // "where is j0kers.local?" — the question that replaces the IP
            if (Is(name, HostFqdn) && (any || q.Type == DnsMessage.TypeA))
                b.A(HostFqdn, local.Address, Ttl);
        }

        return b.IsEmpty ? null : b.Build();
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<string> TxtRecords()
    {
        yield return "path=/";
        yield return "product=j0kers Media Server";
    }

    // ---- announcing ------------------------------------------------------

    /// <summary>
    /// Announces unprompted at startup (RFC 6762 §8.3) so listeners learn of
    /// the server without having to ask. Sent more than once, spaced out,
    /// because multicast is lossy and a dropped announcement is invisible.
    /// </summary>
    private async Task AnnounceAsync(CancellationToken ct)
    {
        var delays = new[] { 0, 1000, 3000 };
        foreach (var delay in delays)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                if (delay > 0) await Task.Delay(delay, ct);
                await SendUnsolicitedAsync(Ttl);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Debug("mdns", $"announce failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Announces once per interface, each carrying that interface's own
    /// address. One send would go out whichever interface the routing table
    /// prefers, leaving every other network unaware — so the outgoing
    /// interface is selected explicitly before each.
    /// </summary>
    private async Task SendUnsolicitedAsync(uint ttl)
    {
        var socket = _socket;
        if (socket is null) return;

        foreach (var local in _locals)
        {
            var b = new DnsMessage.Builder();
            b.Ptr(ServiceType, InstanceFqdn, ttl);
            b.Srv(InstanceFqdn, HostFqdn, (ushort)_port, ttl);
            b.Txt(InstanceFqdn, TxtRecords(), ttl);
            b.A(HostFqdn, local.Address, ttl);
            var msg = b.Build();

            try
            {
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    local.Address.GetAddressBytes());
                await socket.SendAsync(msg, msg.Length, new IPEndPoint(MulticastGroup, MdnsPort));
            }
            catch (Exception ex) { Log.Debug("mdns", $"send on {local.Address} failed: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // TTL 0 is a goodbye (§10.1): listeners drop the records now rather
        // than offering a server that has gone until the TTL runs out.
        try { SendUnsolicitedAsync(0).Wait(TimeSpan.FromMilliseconds(600)); } catch { }

        _cts.Cancel();
        try { _socket?.Close(); } catch { }
        try { _socket?.Dispose(); } catch { }
        _socket = null;
        // Deliberately not _locals.Clear(): the receive loop may still be
        // inside BestLocalFor enumerating this list, and closing the socket
        // ends it but is not awaited here. Clearing a list on an object that
        // is being discarded (Restart makes a fresh responder) buys nothing
        // and is the one structural mutation that could race a reader mid-walk
        // — "Collection was modified". Let it fall away with the object.
        _cts.Dispose();
    }
}
