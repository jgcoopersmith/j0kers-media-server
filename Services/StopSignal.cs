using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Services;

/// <summary>
/// A way for another program on this machine to ask the server to stop, and
/// have it stop properly: a named event,
/// <c>Local\j0kers-media-server-stop-{process id}</c>, that runs the same
/// shutdown as the tray's Exit and Ctrl+C.
///
/// It exists for the post-commit hook, which stops the installed server so it
/// can replace the exe (tools/Stop-Server.ps1). Its only polite request was
/// CloseMainWindow, and a server minimised to the tray has no window to
/// close - so every publish ended in Stop-Process -Force, which runs no
/// shutdown at all: no goodbye to the network, no last save of what the
/// minute-by-minute saver had not written yet, no "bye" in the log. The log
/// counted 113 starts against 23 goodbyes.
///
/// Per process, so a stop meant for one server (the installed one, a test
/// server) can never reach another. In the Local namespace, so only programs in
/// the same Windows session - the same person, who could end the process
/// anyway - can set it.
/// </summary>
public static class StopSignal
{
    public static string NameFor(int processId) => $@"Local\j0kers-media-server-stop-{processId}";

    /// <summary>
    /// Starts listening for this process's stop signal; <paramref name="onStop"/>
    /// runs once, on a pool thread, when it is set. Dispose to stop listening.
    /// Null where there is no such thing (not Windows) or it could not be made.
    /// </summary>
    public static IDisposable? Listen(Action onStop)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, NameFor(Environment.ProcessId));
            var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
            {
                try { onStop(); }
                catch (Exception ex) { Log.Warn("main", "stop signal: " + ex.Message); }
            }, null, Timeout.Infinite, executeOnlyOnce: true);
            return new Listener(signal, wait);
        }
        catch (Exception ex)
        {
            // Not fatal: the tray's Exit and Ctrl+C still work, and the hook
            // falls back to what it did before.
            Log.Warn("main", $"no stop signal for other programs ({ex.Message}) - they can only force this server to stop");
            return null;
        }
    }

    private sealed class Listener(EventWaitHandle signal, RegisteredWaitHandle wait) : IDisposable
    {
        public void Dispose()
        {
            try { wait.Unregister(null); } catch { }
            signal.Dispose();
        }
    }
}
