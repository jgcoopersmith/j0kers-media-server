using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Services;

/// <summary>
/// Writes the state that lives in memory to disk — on a timer, on a crash,
/// and on the way out.
///
/// The reason this exists rather than a tidy flush at the end of Main: over
/// this server's life the log records 113 starts and 23 goodbyes. Four times
/// out of five the process does not get to run its shutdown path at all — it
/// is killed to replace the executable, killed from Task Manager, or the
/// machine goes down. Anything that is only written when asked politely to
/// stop is, in practice, usually not written.
///
/// So the timer is the real mechanism and the handlers are the courtesy. A
/// hard kill loses at most one interval; a crash or a clean exit loses
/// nothing. Saves are cheap by construction — every registered store checks
/// whether it has anything new before touching the disk — so running one a
/// minute costs nothing on an idle server.
/// </summary>
public sealed class StateSaver : IDisposable
{
    private readonly List<(string name, Action save)> _stores = new();
    private readonly object _lock = new();
    private readonly Timer _timer;
    private bool _disposed;

    public StateSaver(TimeSpan interval)
    {
        _timer = new Timer(_ => SaveAll("timer"), null, interval, interval);

        // Both hooks, because they catch different deaths: ProcessExit runs
        // on a normal return from Main and on Environment.Exit, while an
        // unhandled exception on a background thread takes the process down
        // without it. Neither runs on a hard kill, which is what the timer
        // is for.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveAll("exit");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // The full exception, not just its Message. A message like
            // "Collection was modified; enumeration operation may not execute"
            // names the fault but not the collection or the thread — and this
            // is the last thing the process logs before it dies, so the stack
            // trace here is the only record of where it happened. ToString()
            // carries the trace and any inner exceptions; Message threw one of
            // these away and left a crash that could not be located.
            var ex = e.ExceptionObject as Exception;
            Log.Error("state", "crashing — saving what is in memory first: "
                + (ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "unknown"));
            SaveAll("crash");
        };
    }

    /// <summary>
    /// Registers something worth keeping. The callback must be safe to call
    /// at any moment from any thread, and cheap when nothing has changed.
    /// </summary>
    public void Register(string name, Action save)
    {
        lock (_lock) _stores.Add((name, save));
    }

    public void SaveAll(string why)
    {
        (string name, Action save)[] stores;
        lock (_lock)
        {
            if (_disposed && why == "timer") return;
            stores = _stores.ToArray();
        }

        var failed = 0;
        foreach (var (name, save) in stores)
        {
            // One store throwing must not stop the others from being saved
            // — least of all on the crash path, where this is the last thing
            // that will run.
            try { save(); }
            catch (Exception ex) { failed++; Log.Warn("state", $"could not save {name}: {ex.Message}"); }
        }
        if (failed > 0) Log.Warn("state", $"{failed} of {stores.Length} stores failed to save ({why})");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
        SaveAll("shutdown");
    }
}
