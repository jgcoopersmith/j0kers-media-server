using System.Runtime.InteropServices;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Services;

/// <summary>
/// Keeps Windows from going to sleep while there is work in progress.
///
/// Locking the screen does not stop a program, but it does start the idle
/// timer that puts the machine to sleep - and a sleeping machine stops
/// ffmpeg part way through a film, every time, on a server that is meant to
/// spend the night converting. Nothing in the process says "I am busy", so
/// Windows has no reason to think otherwise: a long encode looks exactly
/// like an idle machine to the power manager.
///
/// SetThreadExecutionState is how a program says it. ES_SYSTEM_REQUIRED
/// deliberately, and not ES_DISPLAY_REQUIRED: the screen should still turn
/// off and lock as usual - it is the machine staying awake that matters, not
/// the monitor staying lit.
///
/// The flag is dropped as soon as the work finishes, so an idle server sleeps
/// normally and this cannot become a machine that never rests.
/// </summary>
public static class KeepAwake
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(ExecutionState flags);

    // Only ever touched by the owner thread.
    private static bool _held;
    // What callers last asked for; guarded by Lock.
    private static bool _wanted;
    private static Thread? _owner;
    private static readonly AutoResetEvent Changed = new(false);
    private static readonly object Lock = new();

    /// <summary>The call into Windows, replaceable so a test can see which thread makes it.</summary>
    internal static Func<uint, uint> SetExecutionState = f => SetThreadExecutionState((ExecutionState)f);

    /// <summary>For tests: forget what is held, as a fresh process would.</summary>
    internal static void ResetForTests()
    {
        lock (Lock) _wanted = false;
        _held = false;
    }

    /// <summary>
    /// Call whenever the answer may have changed - true while conversions are
    /// running or queued, false once they are not. Cheap and idempotent: the
    /// call into Windows only happens when the state actually changes.
    ///
    /// It does not make that call itself. SetThreadExecutionState records the
    /// request against the CALLING THREAD, and only that thread can release
    /// it. This is called from a timer, which runs on whichever thread-pool
    /// thread is free for that tick - so the request was made on one thread and
    /// "released" on another, which does nothing, while the log said the
    /// machine could sleep again. The other way round was worse: when the pool
    /// retired the thread that had made the request, the request went with it,
    /// and Windows slept part way through an overnight encode - exactly what
    /// this class exists to stop. So one thread, started on first need and kept
    /// for the life of the process, makes every call; this only tells it what
    /// is wanted.
    /// </summary>
    public static void Busy(bool busy)
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Lock)
        {
            _wanted = busy;
            if (_owner is null)
            {
                if (!busy) return;   // nothing was ever asked for, so nothing to release
                _owner = new Thread(Own) { IsBackground = true, Name = "keep-awake" };
                _owner.Start();
            }
        }
        Changed.Set();
    }

    private static void Own()
    {
        while (true)
        {
            Changed.WaitOne();
            bool wanted;
            lock (Lock) wanted = _wanted;
            if (wanted == _held) continue;
            try
            {
                // ES_CONTINUOUS on its own is the release: it replaces the
                // standing request rather than adding to it.
                var flags = wanted
                    ? ExecutionState.Continuous | ExecutionState.SystemRequired
                    : ExecutionState.Continuous;
                if (SetExecutionState((uint)flags) == 0)
                {
                    Log.Debug("power", "could not change the sleep request");
                    continue;
                }
                _held = wanted;
                Log.Info("power", wanted
                    ? "conversions running - asking Windows not to sleep (the screen may still turn off)"
                    : "conversions finished - the machine may sleep again");
            }
            catch (Exception ex)
            {
                Log.Debug("power", $"sleep request unavailable: {ex.Message}");
            }
        }
    }
}
