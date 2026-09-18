using J0kersMediaServer.Services;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// Windows keeps a program's "do not sleep" request per THREAD: the request
/// SetThreadExecutionState makes belongs to the thread that called it, and
/// only that thread can release it.
///
/// Busy is called from a timer, which runs on whichever thread-pool thread is
/// free for that tick. So the request was made on one thread and "released" on
/// another - the release did nothing while the log said the machine could
/// sleep again - and when the pool retired the first thread mid-encode, the
/// request went with it and the machine slept part way through a film. Every
/// call has to come from one thread that stays alive.
/// </summary>
[Collection(nameof(KeepAwakeTests))]   // static state: never alongside itself
public class KeepAwakeTests : IDisposable
{
    private readonly Func<uint, uint> _real = KeepAwake.SetExecutionState;

    public void Dispose()
    {
        KeepAwake.SetExecutionState = _real;
        KeepAwake.ResetForTests();
    }

    [Fact]
    public void The_request_is_made_and_released_on_the_same_thread()
    {
        if (!OperatingSystem.IsWindows()) return;   // the API is Windows-only; Busy is inert elsewhere

        var calls = new List<(uint flags, int thread)>();
        var gate = new object();
        KeepAwake.ResetForTests();
        KeepAwake.SetExecutionState = f =>
        {
            lock (gate) calls.Add((f, Environment.CurrentManagedThreadId));
            return 1;   // "succeeded", without touching the real machine
        };

        void WaitForCalls(int n, string failure)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                lock (gate) if (calls.Count >= n) return;
                Assert.True(DateTime.UtcNow < deadline, failure);
                Thread.Sleep(20);
            }
        }

        // Two different threads, exactly as two timer ticks would be - and the
        // request made before it is released. (Asked for and dropped before
        // anything acts on it, correct code need not call Windows at all.)
        var set = new Thread(() => KeepAwake.Busy(true));
        set.Start(); set.Join();
        WaitForCalls(1, "Busy(true) never reached Windows");
        var release = new Thread(() => KeepAwake.Busy(false));
        release.Start(); release.Join();
        WaitForCalls(2, "Busy(false) never reached Windows");

        lock (gate)
        {
            Assert.Equal(2, calls.Count);
            Assert.True(calls[0].thread == calls[1].thread,
                        $"the sleep request was made on thread {calls[0].thread} and released on thread "
                        + $"{calls[1].thread} - Windows would not have released it");
        }
    }
}
