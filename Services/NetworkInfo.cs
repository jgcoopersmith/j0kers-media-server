using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace J0kersMediaServer.Services;

/// <summary>
/// Which addresses this machine is actually reachable on. Bound to 0.0.0.0
/// the server listens on every interface, but only the currently connected
/// physical networks are useful to a phone or another PC — so disconnected
/// adapters, self-assigned 169.254.x addresses, and virtual/VM/VPN/container
/// adapters are filtered out.
/// </summary>
public static class NetworkInfo
{
    public sealed record Iface(string Name, string Address, string Kind, bool Primary);

    /// <summary>Connected physical interfaces, default-route one first.</summary>
    public static IReadOnlyList<Iface> Active()
    {
        var found = new List<Iface>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.Wireless80211)) continue;
                if (IsVirtual(nic.Description ?? "") || IsVirtual(nic.Name ?? "")) continue;

                var kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wi-fi" : "ethernet";
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = addr.Address.ToString();
                    if (s.StartsWith("169.254.", StringComparison.Ordinal)) continue; // no DHCP lease
                    if (found.Any(f => f.Address == s)) continue;
                    found.Add(new Iface(nic.Name ?? kind, s, kind, false));
                }
            }
        }
        catch { /* enumeration is best-effort */ }

        // the interface carrying the default route is the one other devices
        // are most likely to share a network with
        var primary = PrimaryAddress();
        if (primary is not null)
        {
            var idx = found.FindIndex(f => f.Address == primary);
            if (idx > 0)
            {
                var p = found[idx];
                found.RemoveAt(idx);
                found.Insert(0, p);
            }
            if (found.Count > 0 && found[0].Address == primary)
                found[0] = found[0] with { Primary = true };
        }
        return found;
    }

    /// <summary>The address of the default-route interface (no packets sent).</summary>
    public static string? PrimaryAddress()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect("8.8.8.8", 53); // routing decision only
            return ((IPEndPoint)probe.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Dashboard URLs for a bind address: every reachable address when wildcard.</summary>
    public static string[] DashboardUrls(string bindAddress, int port)
    {
        if (bindAddress is "127.0.0.1" or "localhost" or "::1")
            return new[] { $"http://localhost:{port}/" };
        if (bindAddress != "0.0.0.0")
            return new[] { $"http://{bindAddress}:{port}/" };

        var active = Active();
        return active.Count == 0
            ? new[] { $"http://localhost:{port}/" }
            : active.Select(i => $"http://{i.Address}:{port}/").ToArray();
    }

    private static bool IsVirtual(string s) =>
        s.Contains("virtual", StringComparison.OrdinalIgnoreCase)
        || s.Contains("vmware", StringComparison.OrdinalIgnoreCase)
        || s.Contains("hyper-v", StringComparison.OrdinalIgnoreCase)
        || s.Contains("virtualbox", StringComparison.OrdinalIgnoreCase)
        || s.Contains("vethernet", StringComparison.OrdinalIgnoreCase)
        || s.Contains("docker", StringComparison.OrdinalIgnoreCase)
        || s.Contains("loopback", StringComparison.OrdinalIgnoreCase)
        || s.Contains("tap-", StringComparison.OrdinalIgnoreCase)
        || s.Contains("tun", StringComparison.OrdinalIgnoreCase)
        || s.Contains("vpn", StringComparison.OrdinalIgnoreCase)
        || s.Contains("nordlynx", StringComparison.OrdinalIgnoreCase)
        || s.Contains("wireguard", StringComparison.OrdinalIgnoreCase)
        || s.Contains("wintun", StringComparison.OrdinalIgnoreCase);
}
