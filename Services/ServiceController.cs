using J0kersMediaServer.Config;
using J0kersMediaServer.Hls;
using J0kersMediaServer.Logging;
using J0kersMediaServer.Rtsp;

namespace J0kersMediaServer.Services;

/// <summary>
/// Owns the streaming services (RTSP + HLS) so the control API can stop,
/// start, and restart them at runtime — e.g. from the dashboard's power
/// button or after a settings change. The control API itself stays up so
/// the dashboard keeps working while services are stopped.
/// </summary>
public sealed class ServiceController : IDisposable
{
    private readonly ServerConfig _config;
    private readonly string _baseDirectory;
    private readonly object _lock = new();

    public RtspServer? Rtsp { get; private set; }
    public HlsServer? Hls { get; private set; }
    public bool Running { get; private set; }

    /// <summary>Attached to each HLS server so streams can serve subtitles.</summary>
    public Media.SubtitleManager? Subtitles { get; set; }

    /// <summary>Attached to each HLS server so streams can render poster frames.</summary>
    public Media.FfmpegManager? Ffmpeg { get; set; }

    /// <summary>Signed-URL verifier and session lookup, handed to each media server it starts.</summary>
    public Auth.MediaLink? Links { get; set; }
    public Auth.AuthService? Sessions { get; set; }

    /// <summary>
    /// Live HLS viewers. Owned here rather than by the HLS server so that
    /// restarting the services (a port change, the power button) doesn't
    /// forget who is watching.
    /// </summary>
    public HlsViewers Viewers { get; } = new();

    /// <summary>
    /// Every byte of media the server has sent, across RTP and HLS. Owned
    /// here so it survives a services restart — a counter that resets when
    /// the power button is used would show a huge negative rate.
    /// </summary>
    public Throughput Served { get; } = new();

    private Action? _onHlsActivity;

    /// <summary>Invoked on each HLS request; also applied to a running server.</summary>
    public Action? OnHlsActivity
    {
        get => _onHlsActivity;
        set
        {
            _onHlsActivity = value;
            if (Hls is not null) Hls.OnActivity = value;
        }
    }

    public ServiceController(ServerConfig config, string baseDirectory)
    {
        _config = config;
        _baseDirectory = baseDirectory;
    }

    public void StartServices()
    {
        lock (_lock)
        {
            if (Running) return;
            if (_config.Rtsp.Enabled)
            {
                Rtsp = new RtspServer(_config, _baseDirectory) { Accounts = Sessions, Served = Served };
                Rtsp.Start();
            }
            if (_config.Hls.Enabled)
            {
                Hls = new HlsServer(_config.Hls, _baseDirectory)
                {
                    Subtitles = Subtitles,
                    Ffmpeg = Ffmpeg,
                    OnActivity = _onHlsActivity,
                    Links = Links,
                    Sessions = Sessions,
                    Viewers = Viewers,
                    Served = Served,
                };
                Hls.Start();
            }
            Running = true;
            Log.Info("services", "streaming services started");
        }
    }

    public void StopServices()
    {
        lock (_lock)
        {
            if (!Running) return;
            Rtsp?.Dispose();
            Rtsp = null;
            Hls?.Dispose();
            Hls = null;
            Running = false;
            Log.Info("services", "streaming services stopped (control API stays up)");
        }
    }

    public void RestartServices()
    {
        StopServices();
        StartServices();
    }

    public void Dispose() => StopServices();
}
