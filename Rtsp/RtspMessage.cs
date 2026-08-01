using System.Text;

namespace J0kersMediaServer.Rtsp;

/// <summary>A parsed RTSP request (RFC 2326 §6 / RFC 7826 §20 message syntax).</summary>
public sealed class RtspRequest
{
    public required string Method { get; init; }
    public required string Uri { get; init; }
    public required string Version { get; init; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public int CSeq => int.TryParse(Header("CSeq"), out var c) ? c : 0;

    /// <summary>Path component of the request URI ("rtsp://host:port/path" → "/path").</summary>
    public string Path
    {
        get
        {
            if (Uri == "*") return "*";
            if (System.Uri.TryCreate(Uri, UriKind.Absolute, out var u))
                return u.AbsolutePath.Length == 0 ? "/" : u.AbsolutePath;
            return Uri.StartsWith('/') ? Uri : "/" + Uri;
        }
    }

    public string? QueryParameter(string name)
    {
        if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var u) || u.Query.Length <= 1)
            return null;
        foreach (var pair in u.Query.TrimStart('?').Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(pair[..eq], name, StringComparison.OrdinalIgnoreCase))
                return System.Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}

/// <summary>Builds RTSP responses with the status phrases from RFC 7826 §17.</summary>
public sealed class RtspResponse
{
    public int StatusCode { get; }
    public string ReasonPhrase { get; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public RtspResponse(int statusCode, int cseq)
    {
        StatusCode = statusCode;
        ReasonPhrase = ReasonFor(statusCode);
        Headers["CSeq"] = cseq.ToString();
        Headers["Date"] = DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'");
    }

    public RtspResponse With(string header, string value)
    {
        Headers[header] = value;
        return this;
    }

    public RtspResponse WithBody(string contentType, string body)
    {
        Body = Encoding.UTF8.GetBytes(body);
        Headers["Content-Type"] = contentType;
        return this;
    }

    public byte[] Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("RTSP/1.0 ").Append(StatusCode).Append(' ').Append(ReasonPhrase).Append("\r\n");
        Headers["Content-Length"] = Body.Length.ToString();
        foreach (var (k, v) in Headers)
            sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("\r\n");
        var head = Encoding.UTF8.GetBytes(sb.ToString());
        if (Body.Length == 0) return head;
        var result = new byte[head.Length + Body.Length];
        head.CopyTo(result, 0);
        Body.CopyTo(result, head.Length);
        return result;
    }

    public static string ReasonFor(int code) => code switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        454 => "Session Not Found",
        455 => "Method Not Valid in This State",
        457 => "Invalid Range",
        461 => "Unsupported Transport",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        _ => "Unknown",
    };
}

/// <summary>Incremental RTSP request reader over a network stream.</summary>
public static class RtspParser
{
    /// <summary>
    /// Reads one RTSP request from the stream. Returns null on clean EOF.
    /// Throws on malformed input. Interleaved binary frames sent by the
    /// client ('$'-prefixed, RFC 7826 §14 — typically RTCP receiver reports
    /// on a TCP transport) are consumed and discarded, not treated as text.
    /// </summary>
    public static async Task<RtspRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var one = new byte[1];
        int first;
        while (true)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) return null; // clean EOF
            first = one[0];
            if (first != 0x24) break;

            // '$' <channel> <2-byte length> <payload> — drain and ignore
            var header = new byte[3];
            await stream.ReadExactlyAsync(header, ct);
            var length = (header[1] << 8) | header[2];
            if (length > 0)
                await stream.ReadExactlyAsync(new byte[length], ct);
        }

        var headerBytes = await ReadUntilDoubleCrlfAsync(stream, first, ct);
        if (headerBytes is null) return null;

        var text = Encoding.UTF8.GetString(headerBytes);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || lines[0].Length == 0)
            throw new InvalidDataException("Empty RTSP request line.");

        var parts = lines[0].Split(' ', 3);
        if (parts.Length != 3)
            throw new InvalidDataException($"Malformed RTSP request line: '{lines[0]}'");

        var request = new RtspRequest { Method = parts[0], Uri = parts[1], Version = parts[2] };
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            request.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (int.TryParse(request.Header("Content-Length"), out var len) && len > 0)
        {
            var body = new byte[len];
            await stream.ReadExactlyAsync(body, ct);
            request.Body = body;
        }

        return request;
    }

    private static async Task<byte[]?> ReadUntilDoubleCrlfAsync(Stream stream, int firstByte, CancellationToken ct)
    {
        var buffer = new List<byte>(512) { (byte)firstByte };
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) return buffer.Count == 0 ? null : buffer.ToArray();
            buffer.Add(one[0]);
            var c = buffer.Count;
            if (c >= 4 && buffer[c - 4] == '\r' && buffer[c - 3] == '\n'
                       && buffer[c - 2] == '\r' && buffer[c - 1] == '\n')
                return buffer.ToArray();
            if (c > 64 * 1024)
                throw new InvalidDataException("RTSP header block exceeds 64 KiB.");
        }
    }
}

/// <summary>Parsed Transport header (RFC 7826 §18.54 / RFC 2326 §12.39).</summary>
public sealed class TransportSpec
{
    public bool IsTcpInterleaved { get; private set; }
    public bool IsUnicast { get; private set; } = true;
    public int ClientRtpPort { get; private set; }
    public int ClientRtcpPort { get; private set; }
    public byte InterleavedRtpChannel { get; private set; }
    public byte InterleavedRtcpChannel { get; private set; } = 1;

    public static TransportSpec? Parse(string header)
    {
        // Clients may offer several transports comma-separated; take the first we support.
        foreach (var offer in SplitOffers(header))
        {
            var spec = ParseSingle(offer);
            if (spec is not null) return spec;
        }
        return null;
    }

    private static IEnumerable<string> SplitOffers(string header) => header.Split(',');

    private static TransportSpec? ParseSingle(string offer)
    {
        var parts = offer.Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        var proto = parts[0].ToUpperInvariant();
        var spec = new TransportSpec();
        if (proto is "RTP/AVP" or "RTP/AVP/UDP")
            spec.IsTcpInterleaved = false;
        else if (proto == "RTP/AVP/TCP")
            spec.IsTcpInterleaved = true;
        else
            return null;

        foreach (var p in parts.Skip(1))
        {
            if (p.Equals("multicast", StringComparison.OrdinalIgnoreCase))
                spec.IsUnicast = false;
            else if (p.StartsWith("client_port=", StringComparison.OrdinalIgnoreCase))
            {
                var range = p["client_port=".Length..].Split('-');
                if (int.TryParse(range[0], out var rtp)) spec.ClientRtpPort = rtp;
                spec.ClientRtcpPort = range.Length > 1 && int.TryParse(range[1], out var rtcp)
                    ? rtcp : spec.ClientRtpPort + 1;
            }
            else if (p.StartsWith("interleaved=", StringComparison.OrdinalIgnoreCase))
            {
                var range = p["interleaved=".Length..].Split('-');
                if (byte.TryParse(range[0], out var ch)) spec.InterleavedRtpChannel = ch;
                spec.InterleavedRtcpChannel = range.Length > 1 && byte.TryParse(range[1], out var ch2)
                    ? ch2 : (byte)(spec.InterleavedRtpChannel + 1);
            }
        }

        if (!spec.IsUnicast) return null; // multicast not supported
        if (!spec.IsTcpInterleaved && spec.ClientRtpPort == 0) return null;
        return spec;
    }
}
