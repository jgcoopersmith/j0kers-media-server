using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Discovery;

/// <summary>
/// Answers a plain broadcast probe with a JSON description of this server —
/// the pattern Jellyfin uses on UDP 7359, and the least ceremonious way for
/// a script or an app to find the server without knowing its address.
///
/// A client broadcasts a short question to 255.255.255.255 and every server
/// listening replies. There is no registry, no multicast group to join and
/// no schema: the reply is a JSON object, and anything that can open a UDP
/// socket can use it.
///
/// The reply names the address the probe arrived on, not a configured one,
/// so a machine on several networks tells each client the address that
/// reached it — the same problem the copy buttons have, solved for free
/// because the sender's address is right there.
/// </summary>
public sealed class UdpProbeResponder : IDisposable
{
    /// <summary>
    /// Questions this answers. The Jellyfin phrasing is included so existing
    /// tooling works unchanged; the rest are what someone would guess.
    /// </summary>
    private static readonly string[] Probes =
    {
        "who is jellyfinserver?",
        "who is j0kers?",
        "who is j0kersmediaserver?",
        "j0kers?",
    };

    private readonly string _serverName;
    private readonly string _uuid;
    private readonly int _httpPort;
    private readonly int _listenPort;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _socket;
    private bool _disposed;

    public UdpProbeResponder(string serverName, string uuid, int httpPort, int listenPort)
    {
        _serverName = serverName;
        _uuid = uuid;
        _httpPort = httpPort;
        _listenPort = listenPort;
    }

    public void Start()
    {
        try
        {
            _socket = new UdpClient(AddressFamily.InterNetwork);
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.EnableBroadcast = true;
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, _listenPort));
            _ = ListenAsync(_cts.Token);
            Log.Info("probe", $"answering discovery probes on udp/{_listenPort}");
        }
        catch (Exception ex)
        {
            Log.Warn("probe", $"could not listen on udp/{_listenPort}: {ex.Message}");
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _socket is not null)
        {
            UdpReceiveResult packet;
            try { packet = await _socket.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception) { break; }

            try
            {
                // a stray large packet on a broadcast port is not a probe
                if (packet.Buffer.Length is 0 or > 256) continue;
                var text = Encoding.UTF8.GetString(packet.Buffer).Trim().ToLowerInvariant();
                if (!Probes.Any(p => text.Contains(p, StringComparison.Ordinal))) continue;

                var reply = Encoding.UTF8.GetBytes(Describe(LocalAddressFor(packet.RemoteEndPoint)));
                await _socket.SendAsync(reply, reply.Length, packet.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                Log.Debug("probe", $"reply failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Which of our addresses can reach this client. Asking the routing table
    /// rather than picking one means a client on the Wi-Fi is told the Wi-Fi
    /// address, and one on the wired network the wired address. No packets
    /// are sent; connecting a UDP socket only resolves the route.
    /// </summary>
    private static IPAddress LocalAddressFor(IPEndPoint remote)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(remote.Address, 9);
            return ((IPEndPoint)probe.LocalEndPoint!).Address;
        }
        catch
        {
            return IPAddress.Loopback;
        }
    }

    /// <summary>
    /// The field names Jellyfin clients expect, so those keep working, plus
    /// the dashboard URL under a clearer name for anything written fresh.
    /// </summary>
    private string Describe(IPAddress local) => JsonSerializer.Serialize(new
    {
        Address = $"http://{local}:{_httpPort}",
        Id = _uuid,
        Name = _serverName,
        EndpointAddress = (string?)null,
        Dashboard = $"http://{local}:{_httpPort}/",
        Product = "j0kers Media Server",
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _socket?.Close(); } catch { }
        try { _socket?.Dispose(); } catch { }
        _cts.Dispose();
    }
}
