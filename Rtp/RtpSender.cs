using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using J0kersMediaServer.Config;
using J0kersMediaServer.Logging;
using J0kersMediaServer.Media;

namespace J0kersMediaServer.Rtp;

/// <summary>
/// Streams RTP (RFC 3550 §5.1 fixed header) over UDP, with periodic RTCP
/// Sender Reports (§6.4.1) on the paired odd port, or interleaves both over
/// the RTSP TCP connection (RFC 7826 §14) when the client asked for
/// RTP/AVP/TCP.
/// </summary>
public sealed class RtpSender : IDisposable
{
    private readonly IMediaSource _source;
    private readonly RtpConfig _config;
    private readonly uint _ssrc;
    private ushort _sequence;
    private uint _timestamp;
    private uint _packetsSent;
    private uint _octetsSent;

    private readonly UdpClient? _rtpSocket;
    private readonly UdpClient? _rtcpSocket;
    private readonly IPEndPoint? _rtpTarget;
    private readonly IPEndPoint? _rtcpTarget;

    // Interleaved (TCP) mode
    private readonly Func<byte, byte[], Task>? _interleavedWriter;
    private readonly byte _rtpChannel;
    private readonly byte _rtcpChannel;

    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private readonly object _stateLock = new();

    public bool Playing { get; private set; }
    public int LocalRtpPort { get; }
    public int LocalRtcpPort { get; }

    /// <summary>UDP (unicast) transport.</summary>
    public RtpSender(IMediaSource source, RtpConfig config, IPAddress clientAddress,
        int clientRtpPort, int clientRtcpPort, (UdpClient rtp, UdpClient rtcp, int rtpPort, int rtcpPort) sockets)
    {
        _source = source;
        _config = config;
        _ssrc = (uint)Random.Shared.Next();
        _sequence = (ushort)Random.Shared.Next(ushort.MaxValue);
        _timestamp = (uint)Random.Shared.Next();
        _rtpSocket = sockets.rtp;
        _rtcpSocket = sockets.rtcp;
        LocalRtpPort = sockets.rtpPort;
        LocalRtcpPort = sockets.rtcpPort;
        _rtpTarget = new IPEndPoint(clientAddress, clientRtpPort);
        _rtcpTarget = new IPEndPoint(clientAddress, clientRtcpPort);
    }

    /// <summary>Interleaved TCP transport (RFC 7826 §14): frames are written back on the RTSP connection.</summary>
    public RtpSender(IMediaSource source, RtpConfig config,
        Func<byte, byte[], Task> interleavedWriter, byte rtpChannel, byte rtcpChannel)
    {
        _source = source;
        _config = config;
        _ssrc = (uint)Random.Shared.Next();
        _sequence = (ushort)Random.Shared.Next(ushort.MaxValue);
        _timestamp = (uint)Random.Shared.Next();
        _interleavedWriter = interleavedWriter;
        _rtpChannel = rtpChannel;
        _rtcpChannel = rtcpChannel;
        LocalRtpPort = 0;
        LocalRtcpPort = 0;
    }

    public uint Ssrc => _ssrc;
    public ushort NextSequence => _sequence;
    public uint CurrentTimestamp => _timestamp;

    // The sequence number / timestamp of the FIRST packet of the current
    // play, captured when the pump starts. RTP-Info must report these, not
    // the live values, which the pump advances on another thread the instant
    // playback begins (RFC 7826 §18.45).
    public ushort StartSequence { get; private set; }
    public uint StartTimestamp { get; private set; }

    /// <summary>True for UDP transport (as opposed to TCP-interleaved).</summary>
    public bool IsUdp => _rtpSocket is not null;

    /// <summary>
    /// Raised when the client sends anything on the RTCP socket (receiver
    /// reports). Lets the session sweeper treat a UDP client as alive only
    /// while it's actually there — a vanished UDP peer stops sending RTCP,
    /// so its session (and RTP port pair) can be reclaimed. UDP writes never
    /// throw, so this is the only liveness signal available.
    /// </summary>
    public Action? OnReceiverActivity { get; set; }

    public void Play()
    {
        lock (_stateLock)
        {
            if (Playing) return;
            Playing = true;
            StartSequence = _sequence;
            StartTimestamp = _timestamp;
            _cts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
            if (_rtcpSocket is not null)
                _ = Task.Run(() => ReceiveRtcpAsync(_cts.Token));
        }
    }

    private async Task ReceiveRtcpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _rtcpSocket!.ReceiveAsync(ct);
                OnReceiverActivity?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { break; } // socket closed
        }
    }

    public void Pause()
    {
        Task? pump;
        CancellationTokenSource? cts;
        lock (_stateLock)
        {
            if (!Playing) return;
            Playing = false;
            cts = _cts;
            _cts = null;
            pump = _pumpTask;
            _pumpTask = null;
        }
        cts?.Cancel();
        // Wait for the old pump to wind down so a subsequent Play() can't
        // race it (stray packet with a duplicate sequence number).
        try { pump?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        cts?.Dispose();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var frame = new byte[MediaSourceFactory.FrameSamples];
        var packet = new byte[12 + frame.Length];
        var nextTick = Environment.TickCount64;
        var lastRtcp = Environment.TickCount64;
        var marker = true; // first packet after (re)start carries the marker bit

        try
        {
            while (!ct.IsCancellationRequested)
            {
                _source.NextFrame(frame);
                BuildRtpPacket(packet, frame, marker);
                marker = false;

                if (_rtpSocket is not null)
                    await _rtpSocket.SendAsync(packet, packet.Length, _rtpTarget);
                else if (_interleavedWriter is not null)
                    await _interleavedWriter(_rtpChannel, (byte[])packet.Clone());

                _sequence++;
                _timestamp += MediaSourceFactory.FrameSamples;
                _packetsSent++;
                _octetsSent += (uint)frame.Length;

                if (_config.RtcpEnabled &&
                    Environment.TickCount64 - lastRtcp >= _config.RtcpIntervalSeconds * 1000)
                {
                    lastRtcp = Environment.TickCount64;
                    await SendSenderReportAsync();
                }

                nextTick += 20;
                var delay = nextTick - Environment.TickCount64;
                if (delay > 0)
                    await Task.Delay((int)delay, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("rtp", $"stream pump stopped: {ex.Message}");
            // Transport is dead (e.g. ICMP unreachable on UDP, closed TCP
            // stream). Mark not-playing so the session sweeper can reap us.
            lock (_stateLock) Playing = false;
        }
    }

    private void BuildRtpPacket(byte[] packet, byte[] payload, bool marker)
    {
        // RFC 3550 §5.1: V=2, P=0, X=0, CC=0
        packet[0] = 0x80;
        packet[1] = (byte)((marker ? 0x80 : 0x00) | MediaSourceFactory.PayloadTypePcmu);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), _sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), _timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), _ssrc);
        payload.CopyTo(packet, 12);
    }

    private async Task SendSenderReportAsync()
    {
        // RFC 3550 §6.4.1 Sender Report: header + NTP/RTP timestamps + counts, no report blocks.
        var sr = new byte[28];
        sr[0] = 0x80;              // V=2, P=0, RC=0
        sr[1] = 200;               // PT=SR
        BinaryPrimitives.WriteUInt16BigEndian(sr.AsSpan(2), 6); // length in 32-bit words minus one
        BinaryPrimitives.WriteUInt32BigEndian(sr.AsSpan(4), _ssrc);

        var now = DateTime.UtcNow;
        var ntpSeconds = (uint)(now - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        var ntpFraction = (uint)((now.Ticks % TimeSpan.TicksPerSecond) * (4294967296.0 / TimeSpan.TicksPerSecond));
        BinaryPrimitives.WriteUInt32BigEndian(sr.AsSpan(8), ntpSeconds);
        BinaryPrimitives.WriteUInt32BigEndian(sr.AsSpan(12), ntpFraction);
        BinaryPrimitives.WriteUInt32BigEndian(sr.AsSpan(16), _timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(sr.AsSpan(20), _packetsSent);
        BinaryPrimitives.WriteUInt32BigEndian(sr.AsSpan(24), _octetsSent);

        if (_rtcpSocket is not null)
            await _rtcpSocket.SendAsync(sr, sr.Length, _rtcpTarget);
        else if (_interleavedWriter is not null)
            await _interleavedWriter(_rtcpChannel, sr);
    }

    public (uint packets, uint octets) Stats => (_packetsSent, _octetsSent);

    public void Dispose()
    {
        Pause();
        _rtpSocket?.Dispose();
        _rtcpSocket?.Dispose();
    }
}

/// <summary>Allocates even/odd UDP port pairs from the configured range (RFC 3550 §11).</summary>
public static class RtpPortAllocator
{
    private static readonly object Lock = new();

    public static (UdpClient rtp, UdpClient rtcp, int rtpPort, int rtcpPort) Allocate(RtpConfig config, IPAddress bind)
    {
        lock (Lock)
        {
            for (var port = config.PortRangeMin; port + 1 <= config.PortRangeMax; port += 2)
            {
                UdpClient? rtp = null;
                try
                {
                    rtp = new UdpClient(new IPEndPoint(bind, port));
                    var rtcp = new UdpClient(new IPEndPoint(bind, port + 1));
                    return (rtp, rtcp, port, port + 1);
                }
                catch (SocketException)
                {
                    rtp?.Dispose();
                }
            }
            throw new InvalidOperationException(
                $"No free RTP port pair in range {config.PortRangeMin}-{config.PortRangeMax}.");
        }
    }
}
