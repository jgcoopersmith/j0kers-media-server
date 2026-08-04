using System.Net;
using System.Text;

namespace J0kersMediaServer.Discovery;

/// <summary>
/// The slice of the DNS wire format (RFC 1035 §4) that multicast DNS needs:
/// parse a query, write a response. Nothing here is a general DNS
/// implementation — there is no recursion, no zone handling, and only the
/// four record types DNS-SD advertises a service with.
/// </summary>
internal static class DnsMessage
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeSrv = 33;
    public const ushort TypeAny = 255;

    public const ushort ClassIn = 1;

    /// <summary>
    /// Top bit of the class field. On a question it asks for a unicast reply
    /// (RFC 6762 §5.4); on a record it tells listeners to replace what they
    /// had cached rather than add to it (§10.2).
    /// </summary>
    public const ushort FlagUnicastResponse = 0x8000;
    public const ushort FlagCacheFlush = 0x8000;

    public readonly record struct Question(string Name, ushort Type, ushort Class)
    {
        public bool WantsUnicast => (Class & FlagUnicastResponse) != 0;
        public ushort BareClass => (ushort)(Class & ~FlagUnicastResponse);
    }

    /// <summary>
    /// Reads the questions out of a message. Returns an empty list for
    /// anything that isn't a well-formed query — a malformed packet on a
    /// multicast port is background noise, not an error worth raising.
    /// </summary>
    public static List<Question> ReadQuestions(byte[] data)
    {
        var questions = new List<Question>();
        try
        {
            if (data.Length < 12) return questions;
            var flags = ReadUInt16(data, 2);
            if ((flags & 0x8000) != 0) return questions;   // a response, not a query
            var count = ReadUInt16(data, 4);
            if (count == 0 || count > 32) return questions;

            var pos = 12;
            for (var i = 0; i < count; i++)
            {
                var name = ReadName(data, ref pos);
                if (name is null || pos + 4 > data.Length) break;
                var type = ReadUInt16(data, pos);
                var cls = ReadUInt16(data, pos + 2);
                pos += 4;
                questions.Add(new Question(name, type, cls));
            }
        }
        catch { /* truncated or hostile packet; whatever parsed is enough */ }
        return questions;
    }

    /// <summary>
    /// Reads a domain name, following compression pointers (RFC 1035 §4.1.4).
    /// Returns null if the encoding is invalid or loops.
    /// </summary>
    private static string? ReadName(byte[] data, ref int pos)
    {
        var labels = new List<string>();
        var jumped = false;
        var hops = 0;
        var cursor = pos;

        while (true)
        {
            if (cursor >= data.Length) return null;
            var len = data[cursor];

            if ((len & 0xC0) == 0xC0)                    // pointer
            {
                if (cursor + 1 >= data.Length) return null;
                if (++hops > 16) return null;            // pointer loop
                var target = ((len & 0x3F) << 8) | data[cursor + 1];
                if (!jumped) { pos = cursor + 2; jumped = true; }
                cursor = target;
                continue;
            }

            cursor++;
            if (len == 0) break;                          // end of name
            if (cursor + len > data.Length) return null;
            labels.Add(Encoding.UTF8.GetString(data, cursor, len));
            cursor += len;
        }

        if (!jumped) pos = cursor;
        return string.Join('.', labels);
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    // ---- writing ---------------------------------------------------------

    /// <summary>Builds a response message. Names are written in full; mDNS does not require compression.</summary>
    public sealed class Builder
    {
        private readonly List<byte> _answers = new();
        private int _count;

        /// <summary>A PTR record: one name pointing at another.</summary>
        public Builder Ptr(string name, string target, uint ttl)
        {
            WriteHeaderFor(name, TypePtr, ttl, cacheFlush: false);
            var rdata = EncodeName(target);
            WriteRdata(rdata);
            return this;
        }

        /// <summary>An SRV record: which host and port actually serve an instance.</summary>
        public Builder Srv(string name, string target, ushort port, uint ttl)
        {
            WriteHeaderFor(name, TypeSrv, ttl, cacheFlush: true);
            var rdata = new List<byte>();
            AddUInt16(rdata, 0);      // priority
            AddUInt16(rdata, 0);      // weight
            AddUInt16(rdata, port);
            rdata.AddRange(EncodeName(target));
            WriteRdata(rdata);
            return this;
        }

        /// <summary>A TXT record: length-prefixed key=value strings.</summary>
        public Builder Txt(string name, IEnumerable<string> entries, uint ttl)
        {
            WriteHeaderFor(name, TypeTxt, ttl, cacheFlush: true);
            var rdata = new List<byte>();
            var any = false;
            foreach (var e in entries)
            {
                var bytes = Encoding.UTF8.GetBytes(e);
                if (bytes.Length > 255) continue;
                rdata.Add((byte)bytes.Length);
                rdata.AddRange(bytes);
                any = true;
            }
            if (!any) rdata.Add(0);   // an empty TXT is a single zero-length string
            WriteRdata(rdata);
            return this;
        }

        /// <summary>An A record: the IPv4 address of a host.</summary>
        public Builder A(string name, IPAddress address, uint ttl)
        {
            WriteHeaderFor(name, TypeA, ttl, cacheFlush: true);
            WriteRdata(address.GetAddressBytes());
            return this;
        }

        public bool IsEmpty => _count == 0;

        /// <summary>Serializes as an authoritative response (RFC 6762 §18).</summary>
        public byte[] Build()
        {
            var msg = new List<byte>(12 + _answers.Count);
            AddUInt16(msg, 0);            // ID: zero in multicast responses
            AddUInt16(msg, 0x8400);       // QR=1, AA=1
            AddUInt16(msg, 0);            // no questions echoed back
            AddUInt16(msg, (ushort)_count);
            AddUInt16(msg, 0);            // authority
            AddUInt16(msg, 0);            // additional
            msg.AddRange(_answers);
            return msg.ToArray();
        }

        private void WriteHeaderFor(string name, ushort type, uint ttl, bool cacheFlush)
        {
            _answers.AddRange(EncodeName(name));
            AddUInt16(_answers, type);
            AddUInt16(_answers, (ushort)(ClassIn | (cacheFlush ? FlagCacheFlush : 0)));
            AddUInt32(_answers, ttl);
            _count++;
        }

        private void WriteRdata(IReadOnlyCollection<byte> rdata)
        {
            AddUInt16(_answers, (ushort)rdata.Count);
            _answers.AddRange(rdata);
        }

        private static void AddUInt16(List<byte> to, ushort v)
        {
            to.Add((byte)(v >> 8));
            to.Add((byte)(v & 0xFF));
        }

        private static void AddUInt32(List<byte> to, uint v)
        {
            to.Add((byte)(v >> 24));
            to.Add((byte)((v >> 16) & 0xFF));
            to.Add((byte)((v >> 8) & 0xFF));
            to.Add((byte)(v & 0xFF));
        }
    }

    /// <summary>Encodes a dotted name as length-prefixed labels ending in a zero byte.</summary>
    private static List<byte> EncodeName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var raw = Encoding.UTF8.GetBytes(label);
            if (raw.Length > 63) continue;   // over the label limit; skip rather than corrupt
            bytes.Add((byte)raw.Length);
            bytes.AddRange(raw);
        }
        bytes.Add(0);
        return bytes;
    }
}
