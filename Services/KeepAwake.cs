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

    private static bool _held;
    private static readonly object Lock = new();

    /// <summary>
    /// Call whenever the answer may have changed - true while conversions are
    /// running or queued, false once they are not. Cheap and idempotent: the
    /// call into Windows only happens when the state actually changes.
    /// </summary>
    public static void Busy(bool busy)
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Lock)
        {
            if (busy == _held) return;
            try
            {
                // ES_CONTINUOUS on its own is the release: it replaces the
                // standing request rather than adding to it.
                var flags = busy
                    ? ExecutionState.Continuous | ExecutionState.SystemRequired
                    : ExecutionState.Continuous;
                if (SetThreadExecutionState(flags) == 0)
                {
                    Log.Debug("power", "could not change the sleep request");
                    return;
                }
                _held = busy;
                Log.Info("power", busy
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
