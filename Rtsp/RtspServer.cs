using System.Net;
using System.Net.Sockets;
using J0kersMediaServer.Config;
using J0kersMediaServer.Logging;
using J0kersMediaServer.Media;
using J0kersMediaServer.Rtp;

namespace J0kersMediaServer.Rtsp;

/// <summary>
/// RTSP server implementing the core method set of RFC 2326 / RFC 7826:
/// OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER.
/// Speaks RTSP/1.0 on the wire (what real-world clients use); the state
/// machine and status codes follow RFC 7826 §13.
/// </summary>
public sealed class RtspServer : IDisposable
{
    private const string PublicMethods = "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER";

    private readonly ServerConfig _config;
    private readonly string _baseDirectory;
    private readonly SessionManager _sessions;
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();

    public SessionManager Sessions => _sessions;

    public RtspServer(ServerConfig config, string baseDirectory)
    {
        _config = config;
        _baseDirectory = baseDirectory;
        _sessions = new SessionManager(config.Rtsp.SessionTimeoutSeconds, config.Rtsp.MaxSessions);
    }

    public void Start()
    {
        var bind = ServerConfig.ResolveBindAddress(_config.Rtsp.BindAddress);
        _listener = new TcpListener(bind, _config.Rtsp.Port);
        _listener.Start();
        Log.Info("rtsp", $"listening on rtsp://{_config.Rtsp.BindAddress}:{_config.Rtsp.Port}");
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleConnectionAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn("rtsp", $"accept failed: {ex.Message}");
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var sessionsOnConnection = new List<string>();
        using var writeLock = new SemaphoreSlim(1, 1);
        EndPoint? remote = null;

        try
        {
            using var _ = client;
            // read the endpoint AFTER 'using' so a client that RSTs before we
            // get here still disposes the socket instead of leaking it
            remote = client.Client.RemoteEndPoint;
            Log.Debug("rtsp", $"connection from {remote}");
            var stream = client.GetStream();

            // Serializes RTSP responses and interleaved RTP frames onto the one TCP stream.
            async Task WriteAsync(byte[] data)
            {
                await writeLock.WaitAsync(ct);
                try { await stream.WriteAsync(data, ct); }
                finally { writeLock.Release(); }
            }

            async Task WriteInterleavedAsync(byte channel, byte[] payload)
            {
                // RFC 7826 §14: '$' <channel> <2-byte big-endian length> <payload>
                var frame = new byte[4 + payload.Length];
                frame[0] = 0x24;
                frame[1] = channel;
                frame[2] = (byte)(payload.Length >> 8);
                frame[3] = (byte)(payload.Length & 0xFF);
                payload.CopyTo(frame, 4);
                await WriteAsync(frame);
            }

            while (!ct.IsCancellationRequested)
            {
                RtspRequest? request;
                try
                {
                    request = await RtspParser.ReadRequestAsync(stream, ct);
                }
                catch (InvalidDataException ex)
                {
                    Log.Warn("rtsp", $"{remote}: malformed request: {ex.Message}");
                    await WriteAsync(new RtspResponse(400, 0).Serialize());
                    break;
                }

                if (request is null) break; // client closed

                if (_config.Logging.LogRtspMessages)
                    Log.Debug("rtsp", $"{remote} → {request.Method} {request.Uri}");

                var response = Dispatch(request, (IPEndPoint)remote!, WriteInterleavedAsync, sessionsOnConnection);
                await WriteAsync(response.Serialize());

                if (_config.Logging.LogRtspMessages)
                    Log.Debug("rtsp", $"{remote} ← {response.StatusCode} {response.ReasonPhrase}");
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Log.Warn("rtsp", $"{remote}: connection error: {ex.Message}");
        }
        finally
        {
            // TCP-interleaved sessions die with the connection; UDP sessions
            // survive until TEARDOWN or timeout (RFC 7826 §13.1).
            foreach (var id in sessionsOnConnection)
            {
                var s = _sessions.Get(id);
                if (s is not null && s.Sender.LocalRtpPort == 0)
                    _sessions.Remove(id);
            }
            Log.Debug("rtsp", $"connection closed: {remote}");
        }
    }

    private RtspResponse Dispatch(RtspRequest request, IPEndPoint remote,
        Func<byte, byte[], Task> interleavedWriter, List<string> sessionsOnConnection)
    {
        try
        {
            // OPTIONS stays open so a client can discover the server (and
            // learn it needs credentials) before being challenged; every
            // method that reveals or delivers media does not.
            var method = request.Method.ToUpperInvariant();
            if (method != "OPTIONS" && RequiresCredentials(request) is RtspResponse challenge)
                return challenge;

            return method switch
            {
                "OPTIONS" => HandleOptions(request),
                "DESCRIBE" => HandleDescribe(request),
                "SETUP" => HandleSetup(request, remote, interleavedWriter, sessionsOnConnection),
                "PLAY" => HandlePlay(request),
                "PAUSE" => HandlePause(request),
                "TEARDOWN" => HandleTeardown(request),
                "GET_PARAMETER" => HandleGetParameter(request),
                "SET_PARAMETER" => new RtspResponse(200, request.CSeq), // accepted, no parameters defined
                _ => new RtspResponse(501, request.CSeq).With("Public", PublicMethods),
            };
        }
        catch (Exception ex)
        {
            Log.Error("rtsp", $"{request.Method} {request.Uri} failed: {ex.Message}");
            return new RtspResponse(500, request.CSeq);
        }
    }

    /// <summary>Account lookup for RTSP credentials; null leaves RTSP open.</summary>
    public Auth.AuthService? Accounts { get; set; }

    /// <summary>
    /// Returns a 401 challenge when this request needs credentials and
    /// hasn't got valid ones, or null to let it through.
    ///
    /// This is HTTP Basic (RFC 7826 §19.1 defers to the HTTP schemes), not
    /// Digest — deliberately. Digest requires the server to hold
    /// MD5(user:realm:password), which means keeping a second, weak copy of
    /// every password next to the PBKDF2 hashes and undoing the point of
    /// hashing them. Basic hands over the password, so it verifies against
    /// the real hash. It also puts the password on the wire, which on a LAN
    /// already carrying unencrypted HTTP and RTP is the same exposure the
    /// rest of the server has. A key works here too, as the username with
    /// any password, for clients you'd rather not give an account password.
    /// </summary>
    private RtspResponse? RequiresCredentials(RtspRequest request)
    {
        if (Accounts is null || !_config.Rtsp.RequireAuth || !Accounts.Enforcing) return null;

        var header = request.Headers.TryGetValue("Authorization", out var value) ? value : null;
        if (Accounts.VerifyRtspCredentials(header)) return null;

        Log.Warn("rtsp", $"{request.Method} {request.Uri} refused: no valid credentials");
        return new RtspResponse(401, request.CSeq)
            .With("WWW-Authenticate", $"Basic realm=\"{_config.Rtsp.Realm}\"");
    }

    private RtspResponse HandleOptions(RtspRequest request) =>
        new RtspResponse(200, request.CSeq).With("Public", PublicMethods);

    /// <summary>
    /// The base URI a relative control attribute resolves against: scheme +
    /// authority + path with a trailing slash, and no query string.
    /// </summary>
    private static string BaseUriForControl(RtspRequest request)
    {
        if (Uri.TryCreate(request.Uri, UriKind.Absolute, out var u))
        {
            var path = u.AbsolutePath.TrimEnd('/');
            return $"{u.Scheme}://{u.Authority}{path}/";
        }
        return request.Uri.Split('?')[0].TrimEnd('/') + "/";
    }

    private RtspResponse HandleDescribe(RtspRequest request)
    {
        var mount = ResolveMount(request);
        if (mount is null)
            return new RtspResponse(404, request.CSeq);

        var serverAddress = "0.0.0.0"; // per RFC 8866, address in c= is informative for RTSP; ports come from SETUP
        // Use the ABSOLUTE request URI as the media control attribute. A
        // relative control ("streamid=0") is resolved against Content-Base,
        // which drops the query string — so the announcement service's
        // ?play=<clip> was lost and SETUP 404'd. An absolute control URI
        // carries the query intact and works for ordinary mounts too.
        var sdp = Sdp.Build(_config.ServerName, mount.Value.name, serverAddress, request.Uri);
        return new RtspResponse(200, request.CSeq)
            .With("Content-Base", BaseUriForControl(request))
            .WithBody("application/sdp", sdp);
    }

    private RtspResponse HandleSetup(RtspRequest request, IPEndPoint remote,
        Func<byte, byte[], Task> interleavedWriter, List<string> sessionsOnConnection)
    {
        var mount = ResolveMount(request);
        if (mount is null)
            return new RtspResponse(404, request.CSeq);

        var transportHeader = request.Header("Transport");
        if (transportHeader is null)
            return new RtspResponse(400, request.CSeq);

        var transport = TransportSpec.Parse(transportHeader, _config.Rtsp.AllowInterleavedTcp);
        if (transport is null)
            return new RtspResponse(461, request.CSeq); // Unsupported Transport

        IMediaSource source;
        try
        {
            source = mount.Value.factory();
        }
        catch (Exception ex)
        {
            Log.Error("rtsp", $"cannot open source for {mount.Value.name}: {ex.Message}");
            return new RtspResponse(404, request.CSeq);
        }

        RtpSender sender;
        string transportResponse;
        if (transport.IsTcpInterleaved)
        {
            sender = new RtpSender(source, _config.Rtp, interleavedWriter,
                transport.InterleavedRtpChannel, transport.InterleavedRtcpChannel);
            transportResponse =
                $"RTP/AVP/TCP;unicast;interleaved={transport.InterleavedRtpChannel}-{transport.InterleavedRtcpChannel};ssrc={sender.Ssrc:X8}";
        }
        else
        {
            var sockets = RtpPortAllocator.Allocate(_config.Rtp, ServerConfig.ResolveBindAddress(_config.Rtsp.BindAddress));
            sender = new RtpSender(source, _config.Rtp, remote.Address,
                transport.ClientRtpPort, transport.ClientRtcpPort, sockets);
            transportResponse =
                $"RTP/AVP;unicast;client_port={transport.ClientRtpPort}-{transport.ClientRtcpPort};" +
                $"server_port={sender.LocalRtpPort}-{sender.LocalRtcpPort};ssrc={sender.Ssrc:X8}";
        }

        var session = new RtspSession
        {
            MountPath = mount.Value.name,
            Sender = sender,
            ClientAddress = remote.Address.ToString(),
        };
        // incoming RTCP from the client counts as liveness (UDP writes never fail)
        sender.OnReceiverActivity = session.Touch;

        if (!_sessions.TryAdd(session))
        {
            sender.Dispose();
            return new RtspResponse(503, request.CSeq)
                .With("Retry-After", "30"); // at session cap
        }

        sessionsOnConnection.Add(session.Id);
        Log.Info("rtsp", $"session {session.Id}: SETUP {mount.Value.name} for {remote} ({(transport.IsTcpInterleaved ? "TCP interleaved" : "UDP")})");

        return new RtspResponse(200, request.CSeq)
            .With("Transport", transportResponse)
            .With("Session", $"{session.Id};timeout={_config.Rtsp.SessionTimeoutSeconds}");
    }

    private RtspResponse HandlePlay(RtspRequest request)
    {
        var session = _sessions.Get(request.Header("Session")?.Split(';')[0]);
        if (session is null)
            return new RtspResponse(454, request.CSeq);

        session.Touch();
        session.Sender.Play();
        session.State = SessionState.Playing;
        Log.Info("rtsp", $"session {session.Id}: PLAY {session.MountPath}");

        // RTP-Info per RFC 7826 §18.45: the seq/rtptime of the FIRST packet,
        // captured inside Play() before the pump advances them
        var rtpInfo = $"url={request.Uri};seq={session.Sender.StartSequence};rtptime={session.Sender.StartTimestamp}";
        return new RtspResponse(200, request.CSeq)
            .With("Session", session.Id)
            .With("Range", "npt=0-")
            .With("RTP-Info", rtpInfo);
    }

    private RtspResponse HandlePause(RtspRequest request)
    {
        var session = _sessions.Get(request.Header("Session")?.Split(';')[0]);
        if (session is null)
            return new RtspResponse(454, request.CSeq);

        session.Touch();
        session.Sender.Pause();
        session.State = SessionState.Ready;
        Log.Info("rtsp", $"session {session.Id}: PAUSE {session.MountPath}");
        return new RtspResponse(200, request.CSeq).With("Session", session.Id);
    }

    private RtspResponse HandleTeardown(RtspRequest request)
    {
        var sessionId = request.Header("Session")?.Split(';')[0];
        var session = _sessions.Get(sessionId);
        if (session is null)
            return new RtspResponse(454, request.CSeq);

        Log.Info("rtsp", $"session {session.Id}: TEARDOWN {session.MountPath}");
        _sessions.Remove(session.Id);
        return new RtspResponse(200, request.CSeq).With("Connection", "close");
    }

    private RtspResponse HandleGetParameter(RtspRequest request)
    {
        // Empty-body GET_PARAMETER doubles as a session keep-alive (RFC 7826 §18.19 usage note).
        var session = _sessions.Get(request.Header("Session")?.Split(';')[0]);
        session?.Touch();
        var response = new RtspResponse(200, request.CSeq);
        if (session is not null) response.With("Session", session.Id);
        return response;
    }

    /// <summary>
    /// Maps a request URI to a media source. Configured mounts are matched by
    /// path prefix; "/annc" implements the RFC 4240 announcement service
    /// convention (annc, play= parameter) adapted to RTSP URIs.
    /// </summary>
    private (string name, Func<IMediaSource> factory)? ResolveMount(RtspRequest request)
    {
        var path = request.Path;

        if (_config.Services.AnnouncementEnabled &&
            (path == "/annc" || path.StartsWith("/annc/", StringComparison.Ordinal)))
        {
            var play = request.QueryParameter("play");
            if (string.IsNullOrWhiteSpace(play)) return null;

            // Confine clip lookup to the configured directory.
            var clipDir = Path.GetFullPath(Path.Combine(_baseDirectory, _config.Services.AnnouncementClipDirectory));
            var clip = Path.GetFullPath(Path.Combine(clipDir, play));
            if (!clip.StartsWith(clipDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(clip, clipDir, StringComparison.OrdinalIgnoreCase))
                return null;
            if (!File.Exists(clip)) return null;

            return ($"annc:{play}", () => new UlawFileSource(clip));
        }

        foreach (var mount in _config.MountsSnapshot())
        {
            if (path.Equals(mount.Path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(mount.Path + "/", StringComparison.OrdinalIgnoreCase))
            {
                var m = mount;
                return (m.Path, () => MediaSourceFactory.Create(m, _baseDirectory));
            }
        }

        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        _sessions.Dispose();
    }
}
