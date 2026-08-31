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

    /// <summary>
    /// The check that cannot be raced: refuses the connection at the moment
    /// it is dialled, on the address actually being dialled.
    ///
    /// <see cref="MayFetch"/> resolves the name to judge it and then hands
    /// the URL to HttpClient, which resolves it again to connect. Those are
    /// two separate lookups, and a name the attacker controls can answer
    /// publicly for the first and with 127.0.0.1 or 169.254.169.254 for the
    /// second - DNS rebinding, and the whole point of a check that reads DNS.
    /// Every address is already checked rather than just the first, which
    /// closes the multi-answer version of this; only the gap between the two
    /// lookups was left, and no amount of care in MayFetch can close that
    /// from where it stands.
    ///
    /// So the last word belongs here, in the socket layer, where the endpoint
    /// is no longer a name that might change but an address about to be
    /// connected to. Attach it to any HttpClient that fetches a URL this
    /// server did not choose.
    /// </summary>
    public static SocketsHttpHandler GuardPrivateAddresses(SocketsHttpHandler handler)
    {
        var inner = handler.ConnectCallback;
        handler.ConnectCallback = async (context, token) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;

            // A literal needs no lookup and cannot rebind; a name is resolved
            // once here and the connection is made to one of those very
            // addresses, so nothing can change underneath it afterwards.
            IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);

            var allowed = addresses.Where(a => !IsPrivate(a) && !IsCloudMetadata(a)).ToArray();
            if (allowed.Length == 0)
                throw new HttpRequestException(
                    $"refused to connect to {host}: it resolves inside this network");

            if (inner is not null)
                return await inner(context, token).ConfigureAwait(false);

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, port, token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
        return handler;
    }
}
