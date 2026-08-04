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

    /// <summary>
    /// Live count, reserved and admitted alike. Kept separately from
    /// _sessions.Count because a reservation exists before its session does.
    /// </summary>
    private int _reserved;

    /// <summary>
    /// Claims a slot under the cap, or returns false if there is none.
    ///
    /// Testing _sessions.Count and then adding is two steps, and concurrent
    /// SETUPs can all pass the test before any of them adds — so the cap was
    /// a limit only when requests arrived one at a time. The count is instead
    /// moved up front, atomically, and given back if the caller doesn't use
    /// it. Callers must Release() or TryAdd() exactly once per successful
    /// reservation.
    /// </summary>
    public bool TryReserve()
    {
        // optimistic CAS: re-read and retry rather than lock, since this is
        // on the SETUP path and contention is brief
        while (true)
        {
            var current = Volatile.Read(ref _reserved);
            if (current >= _maxSessions) return false;
            if (Interlocked.CompareExchange(ref _reserved, current + 1, current) == current) return true;
        }
    }

    /// <summary>Gives back a reservation that never became a session.</summary>
    public void Release() => Interlocked.Decrement(ref _reserved);

    /// <summary>
    /// Adds a session against a slot already claimed by <see cref="TryReserve"/>.
    /// The reservation is consumed either way — a duplicate id is a failure to
    /// use the slot, not a reason to hold it.
    /// </summary>
    public bool TryAdd(RtspSession session)
    {
        if (_sessions.TryAdd(session.Id, session)) return true;
        Release();
        return false;
    }

    public RtspSession? Get(string? id) =>
        id is not null && _sessions.TryGetValue(id, out var s) ? s : null;

    public void Remove(string id)
    {
        // TryRemove is the arbiter: two callers racing the same id (sweeper
        // and TEARDOWN) means only one disposes, and only one gives the slot
        // back, so the cap can neither leak nor drift below zero
        if (!_sessions.TryRemove(id, out var session)) return;
        Release();
        session.Dispose();
    }

    private void Sweep()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-_timeoutSeconds);
        foreach (var (id, session) in _sessions)
        {
            // A TCP-interleaved stream is alive as long as its connection is
            // open (the pump clears Playing when a write fails), so streaming
            // counts as liveness for it. A UDP stream cannot detect a vanished
            // client through writes, so it relies on the idle timeout, which
            // is refreshed by RTSP keepalives and by incoming RTCP — a client
            // that disappears stops both and is reaped, freeing its ports.
            if (session.Sender.Playing && !session.Sender.IsUdp)
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
