using J0kersMediaServer.Config;

namespace J0kersMediaServer.Services;

/// <summary>
/// Where DLNA is served from — which is not always where everything else is.
///
/// http.sys allows one scheme per port, so once the control port carries TLS
/// there is no way to answer plain HTTP on it. Televisions and set-top boxes
/// are the DLNA clients, and they overwhelmingly cannot do TLS at all: a
/// self-signed certificate is not something a TV will ever accept, and many
/// will not attempt https in the first place. Encrypting DLNA would simply
/// have removed the feature.
///
/// That trade costs nothing, because DLNA is unauthenticated by design — no
/// account, no cookie, no token, only the LAN-address check the server
/// already applies. TLS on that port would have protected an anonymous
/// listing that anyone on the network may fetch anyway, while the dashboard,
/// the API, sign-in and the signed media links all stay encrypted.
/// </summary>
public static class DlnaEndpoint
{
    /// <summary>
    /// The port DLNA answers on. While the server is plain HTTP, that's the
    /// control port like everything else — no second listener, no extra
    /// firewall hole. Under TLS it moves to its own port, defaulting to the
    /// control port + 1, so the encrypted port stays encrypted.
    /// </summary>
    public static int PortFor(ServerConfig config)
    {
        if (config.Discovery.DlnaPort is > 0 and <= 65535) return config.Discovery.DlnaPort;
        return UrlScheme.Https ? config.Control.Port + 1 : config.Control.Port;
    }

    /// <summary>True when DLNA has a listener of its own rather than sharing the control port.</summary>
    public static bool IsSeparate(ServerConfig config) => PortFor(config) != config.Control.Port;
}
