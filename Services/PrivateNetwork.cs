using System.Net;
using System.Net.Sockets;

namespace J0kersMediaServer.Services;

/// <summary>
/// "Is this address on our side of the router?" — asked in two opposite
/// directions, which is why it lives in one place.
///
/// <b>Inbound</b>: DLNA has no authentication, so it answers private
/// addresses and refuses everything else.
///
/// <b>Outbound</b>: the free-TV proxy fetches URLs that came out of a
/// third-party playlist, and a playlist naming <c>127.0.0.1:9090</c> or a
/// cloud metadata address would have this server fetch it and hand the
/// answer back — the classic server-side request forgery. Those fetches
/// refuse private addresses for exactly the reason DLNA requires them.
/// </summary>
public static class PrivateNetwork
{
    /// <summary>Loopback, the RFC 1918 ranges, CGNAT, link-local, and IPv6's equivalents.</summary>
    public static bool IsPrivate(IPAddress? ip)
    {
        if (ip is null) return false;
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            else
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
                var v6 = ip.GetAddressBytes();
                return (v6[0] & 0xfe) == 0xfc;      // fc00::/7 unique-local
            }
        }

        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            10 => true,
            127 => true,
            0 => true,                              // "this network"
            172 => b[1] >= 16 && b[1] <= 31,
            192 => b[1] == 168,
            169 => b[1] == 254,                     // link-local, incl. cloud metadata
            100 => b[1] >= 64 && b[1] <= 127,       // CGNAT
            _ => false,
        };
    }

    /// <summary>
    /// The cloud instance-metadata address. Link-local counts as "on this
    /// network", which is right for a DHCP-less LAN and wrong for this one
    /// address in particular: on a hosted machine it hands out credentials
    /// to anything that can ask. Nothing this server fetches is ever there.
    /// </summary>
    public static bool IsCloudMetadata(IPAddress? ip) =>
        ip is not null && ip.ToString() is "169.254.169.254" or "fd00:ec2::254";

    /// <summary>
    /// A device on the local network that is not this machine and not the
    /// metadata service — what a tuner, a camera or a set-top box is.
    /// </summary>
    public static bool IsLanDevice(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) addresses = new[] { literal };
        else if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            try { addresses = Dns.GetHostAddresses(host); } catch { return true; }  // mDNS: trust the suffix
        }
        else
        {
            try { addresses = Dns.GetHostAddresses(host); } catch { return false; }
        }

        return addresses.Length > 0
               && addresses.All(a => IsPrivate(a) && !IPAddress.IsLoopback(a) && !IsCloudMetadata(a));
    }

    /// <summary>
    /// Whether a host names somewhere inside this network. Every address it
    /// resolves to is checked, not just the first: a name that answers with
    /// one public address and one loopback address is the shape of a DNS
    /// rebinding attack, and one private answer is enough to refuse.
    ///
    /// A name that will not resolve counts as not-private — the fetch that
    /// follows will fail on its own, and pretending otherwise would block
    /// public hosts during a DNS blip.
    /// </summary>
    public static bool IsPrivateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return IsPrivate(literal);

        // ".local" is mDNS: this network by definition
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            return Dns.GetHostAddresses(host).Any(IsPrivate);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this URL may be fetched by the server on a client's behalf.
    /// Anything private is refused, and so is any scheme that isn't HTTP —
    /// <c>file:</c> would be a local read dressed as a download.
    /// </summary>
    public static bool MayFetch(string? url, out string reason)
    {
        reason = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            reason = "not an absolute URL";
            return false;
        }
        if (u.Scheme is not ("http" or "https"))
        {
            reason = $"{u.Scheme} is not a fetchable scheme";
            return false;
        }
        if (IsPrivateHost(u.Host))
        {
            reason = $"{u.Host} is inside this network";
            return false;
        }
        return true;
    }
}
