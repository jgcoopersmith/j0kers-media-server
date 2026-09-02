using System.Diagnostics;
using J0kersMediaServer.Services;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The deadlock these guard against, seen in the field: a batch conversion of
/// 717 files queued nothing at all and logged nothing at all, because ffprobe
/// wrote 39 KB of decode warnings about one damaged mp4, filled the stderr
/// pipe nobody was draining, and blocked forever in its own write. The caller
/// sat in ReadToEnd on stdout - which has no timeout - so the WaitForExit that
/// looked like a safety net on the next line was never reached. Three ffprobe
/// processes were found wedged on that one file, the oldest over two hours old.
/// </summary>
public class ProcessRunTests
{
    /// A child that says far more than a pipe buffer holds on the stream the
    /// caller is NOT reading first. Before ProcessJob.Run this hung for ever.
    [Fact]
    public void ChatterOnStderrDoesNotHangTheCaller()
    {
        using var dir = new TempDir();
        var big = Path.Combine(dir.Path, "chatter.txt");
        // Comfortably past the 4 KB Windows gives an anonymous pipe, and past
        // the 39 KB the real ffprobe produced.
        File.WriteAllText(big, new string('x', 200_000));

        // Raw Arguments, not ArgumentList: cmd.exe does not understand the
        // backslash-escaped quotes .NET produces when it builds the line.
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/c type " + Quoted(big) + " 1>&2 & echo h264,video",
        };

        var sw = Stopwatch.StartNew();
        var run = ProcessJob.Run(psi, 30_000);
        sw.Stop();

        Assert.NotNull(run);
        Assert.False(run!.Value.TimedOut, "the child finished; a timeout here means it was left blocked on its pipe");
        Assert.Contains("h264,video", run.Value.StdOut);
        Assert.True(run.Value.StdErr.Length > 100_000,
            $"stderr should have been drained in full, got {run.Value.StdErr.Length} bytes");
        Assert.True(sw.Elapsed.TotalSeconds < 30, "returned only because the timeout fired, not because the child finished");
    }

    /// The timeout has to be real. It never fired before, because the blocking
    /// read in front of it meant control never reached it.
    [Fact]
    public void AChildThatNeverFinishesIsKilledAtTheTimeout()
    {
        // ping, not pause: pause reads stdin, and a test host with no console
        // hands it an immediate EOF, so it would exit at once and prove nothing.
        var psi = new ProcessStartInfo("cmd.exe") { Arguments = "/c ping -n 30 127.0.0.1" };

        var sw = Stopwatch.StartNew();
        var run = ProcessJob.Run(psi, 2_000);
        sw.Stop();

        Assert.NotNull(run);
        Assert.True(run!.Value.TimedOut);
        Assert.False(run.Value.Ok);
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"took {sw.Elapsed.TotalSeconds:F1}s to honour a 2s timeout");
    }

    /// Exit code and stdout still come back the ordinary way.
    [Fact]
    public void QuietChildReportsOutputAndExitCode()
    {
        var psi = new ProcessStartInfo("cmd.exe") { Arguments = "/c echo hello" };

        var run = ProcessJob.Run(psi, 10_000);

        Assert.NotNull(run);
        Assert.True(run!.Value.Ok);
        Assert.Equal(0, run.Value.ExitCode);
        Assert.Contains("hello", run.Value.StdOut);
    }

    /// cmd.exe wants a plain quoted path, so the test builds one itself.
    private static string Quoted(string path) => (char)34 + path + (char)34;
}
