using System.Collections.Concurrent;
using System.Security.Cryptography;
using J0kersMediaServer.Rtp;

namespace J0kersMediaServer.Rtsp;

public enum SessionState { Ready, Playing }

/// <summary>One RTSP session (RFC 7826 §13.1): identifier, transport, and stream state.</summary>
public sealed class RtspSession : IDisposable
{
    public string Id { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    public required string MountPath { get; init; }
    public required RtpSender Sender { get; init; }
    public SessionState State { get; set; } = SessionState.Ready;
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    public string ClientAddress { get; init; } = "";

    public void Touch() => LastActivity = DateTime.UtcNow;

    public void Dispose() => Sender.Dispose();
}

/// <summary>Tracks live sessions and expires idle ones per the negotiated timeout.</summary>
public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, RtspSession> _sessions = new();
    private readonly int _timeoutSeconds;
    private readonly int _maxSessions;
    private readonly Timer _sweeper;

    public SessionManager(int timeoutSeconds, int maxSessions)
    {
        _timeoutSeconds = timeoutSeconds;
        _maxSessions = maxSessions;
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public int Count => _sessions.Count;
    public IReadOnlyCollection<RtspSession> All => _sessions.Values.ToArray();

    public bool TryAdd(RtspSession session)
    {
        if (_sessions.Count >= _maxSessions) return false;
        return _sessions.TryAdd(session.Id, session);
    }

    public RtspSession? Get(string? id) =>
        id is not null && _sessions.TryGetValue(id, out var s) ? s : null;

    public void Remove(string id)
    {
        if (_sessions.TryRemove(id, out var session))
            session.Dispose();
    }

    private void Sweep()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-_timeoutSeconds);
        foreach (var (id, session) in _sessions)
        {
            // An actively-pumping stream counts as liveness even without
            // RTSP keepalives; the pump clears Playing when the transport
            // dies, at which point the idle timeout takes over.
            if (session.Sender.Playing)
            {
                session.Touch();
                continue;
            }
            if (session.LastActivity < cutoff)
            {
                Logging.Log.Info("rtsp", $"session {id} timed out (idle > {_timeoutSeconds}s)");
                Remove(id);
            }
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        foreach (var id in _sessions.Keys) Remove(id);
    }
}
