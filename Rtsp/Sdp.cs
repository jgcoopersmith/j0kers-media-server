using System.Text;
using J0kersMediaServer.Media;

namespace J0kersMediaServer.Rtsp;

/// <summary>
/// Builds the SDP session description returned by DESCRIBE
/// (RFC 7826 §20.3 references SDP per RFC 4566/8866; RFC 2326 Appendix C).
/// </summary>
public static class Sdp
{
    public static string Build(string serverName, string sessionName, string serverAddress, string controlUri)
    {
        var sessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sb = new StringBuilder();
        sb.Append("v=0\r\n");
        sb.Append($"o=- {sessionId} {sessionId} IN IP4 {serverAddress}\r\n");
        sb.Append($"s={sessionName}\r\n");
        sb.Append($"i=Served by {serverName}\r\n");
        sb.Append($"c=IN IP4 {serverAddress}\r\n");
        sb.Append("t=0 0\r\n");
        sb.Append("a=control:*\r\n");
        // Audio: PCMU/8000, static payload type 0 (RFC 3551 §6)
        sb.Append("m=audio 0 RTP/AVP 0\r\n");
        sb.Append($"a=rtpmap:{MediaSourceFactory.PayloadTypePcmu} PCMU/{MediaSourceFactory.SampleRate}\r\n");
        sb.Append($"a=control:{controlUri}\r\n");
        return sb.ToString();
    }
}
