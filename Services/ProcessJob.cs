using System.Diagnostics;
using System.Runtime.InteropServices;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Services;

/// <summary>
/// Ties every child process this server starts to the life of the server.
///
/// The reason this exists, found by measuring: ffmpeg children outlived a
/// hard kill of the server. A graceful shutdown disposes the manager and
/// kills them, but Stop-Process, Task Manager, a crash or a publish-and-
/// restart cycle never runs that path — so the children kept going, and the
/// next server start added a second ffmpeg writing into the same channel
/// directory. Both number segments from seg_00000, both run with
/// delete_segments, so they overwrite and delete each other's output. The
/// visible result is a channel that stutters, repeats, or appears to write
/// nothing at all, getting worse with every restart that leaves another
/// orphan behind.
///
/// A Windows job object with KILL_ON_JOB_CLOSE is the mechanism for this:
/// the kernel holds the handle, and when this process dies — by any means,
/// including being killed outright — every process in the job dies with it.
/// Nothing is left for the next start to collide with.
///
/// Everywhere else this is a no-op: the startup sweep in FfmpegManager is
/// the portable half, and covers orphans this cannot (ones left by a build
/// from before this existed).
/// </summary>
public static class ProcessJob
{
    private static IntPtr _job = IntPtr.Zero;
    private static readonly object Lock = new();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr security, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private const int ExtendedLimitInformation = 9;
    private const uint LimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformationStruct
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    /// <summary>
    /// Creates the job. Called once at startup, before anything is spawned.
    /// Failure is not fatal — the server runs, and the startup sweep still
    /// clears whatever a previous run left behind.
    /// </summary>
    public static void Init()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Lock)
        {
            if (_job != IntPtr.Zero) return;
            try
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    Log.Debug("main", "could not create the process job — children rely on the startup sweep");
                    return;
                }

                var info = new ExtendedLimitInformationStruct();
                info.BasicLimitInformation.LimitFlags = LimitKillOnJobClose;
                var size = Marshal.SizeOf<ExtendedLimitInformationStruct>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(job, ExtendedLimitInformation, ptr, (uint)size))
                    {
                        Log.Debug("main", $"could not configure the process job ({Marshal.GetLastWin32Error()})");
                        CloseHandle(job);   // otherwise the handle is leaked for the life of the process
                        return;
                    }
                }
                finally { Marshal.FreeHGlobal(ptr); }

                _job = job;
            }
            catch (Exception ex)
            {
                Log.Debug("main", $"process job unavailable: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Starts a process already inside the job. Use this for every ffmpeg and
    /// ffprobe the server runs.
    ///
    /// It exists rather than leaving each caller to Start and then Adopt
    /// because that is what was here before, and exactly one spawn site out
    /// of nine remembered — the long-running one. The eight that forgot were
    /// the short-lived ones: codec probes, duration probes, thumbnails,
    /// subtitle extraction. That is precisely the set that is mid-flight when
    /// somebody closes the dashboard, so the process the user just stopped
    /// left ffmpeg behind for up to another minute, still holding their file
    /// open. A rule that has to be remembered at every call site is a rule
    /// that gets kept at one of them; this one cannot be forgotten, because
    /// forgetting it means not starting the process at all.
    ///
    /// Deliberately not used for the two children that are *meant* to outlive
    /// this process: the browser being opened for the dashboard, and the
    /// successor server started by a restart.
    /// </summary>
    public static Process? Start(ProcessStartInfo psi)
    {
        var p = Process.Start(psi);
        if (p is not null) Adopt(p);
        return p;
    }

    /// <summary>
    /// Puts a freshly started process in the job, so it cannot outlive this
    /// one. Safe to call for every spawn; does nothing where jobs don't
    /// exist or the process has already exited.
    /// </summary>
    public static void Adopt(Process p)
    {
        if (!OperatingSystem.IsWindows() || _job == IntPtr.Zero) return;
        try
        {
            if (p.HasExited) return;
            if (!AssignProcessToJobObject(_job, p.Handle))
                Log.Debug("ffmpeg", $"could not adopt pid {p.Id} into the job ({Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex)
        {
            Log.Debug("ffmpeg", $"could not adopt a child process: {ex.Message}");
        }
    }

    /// <summary>
    /// What a short-lived child process left behind: both pipes, its exit
    /// code, and whether it had to be killed for outstaying its welcome.
    /// </summary>
    public readonly record struct Result(bool TimedOut, int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => !TimedOut && ExitCode == 0;
    }

    /// <summary>
    /// Runs a child to completion and hands back everything it printed.
    ///
    /// This exists because of a deadlock that stopped batch conversion dead.
    /// The pattern it replaces was, at six call sites:
    ///
    ///     var output = p.StandardOutput.ReadToEnd();
    ///     p.WaitForExit(20_000);
    ///
    /// A redirected pipe is a kernel buffer of a few kilobytes. Nothing was
    /// reading stderr, so a child with more than that to say filled it, blocked
    /// forever in its own write, and never exited or closed stdout — and
    /// ReadToEnd, which has no timeout, waited for a close that could not come.
    /// The WaitForExit on the next line was unreachable, so the timeout that
    /// looked like the safety net had never once fired.
    ///
    /// Measured on this install: ffprobe emits 39 KB of decode warnings on one
    /// damaged mp4 in the library despite "-v error". Three ffprobe processes
    /// were found wedged against that single file, the oldest more than two
    /// hours old, each holding the thread that started it.
    ///
    /// So: read both pipes at once and before waiting, and let the timeout be
    /// real. Kill the whole tree when it expires, because a child that has
    /// stopped answering is not going to be talked round.
    /// </summary>
    public static Result? Run(ProcessStartInfo psi, int timeoutMs)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        // Handed an empty stdin rather than the server's own. A child that
        // reads input - ffmpeg does, for its keyboard commands - would
        // otherwise inherit a handle nobody ever writes to and wait on it.
        psi.RedirectStandardInput = true;

        using var p = Start(psi);
        if (p is null) return null;
        try { p.StandardInput.Close(); } catch { }

        // Started before the wait, so neither pipe can back up behind it.
        var outText = p.StandardOutput.ReadToEndAsync();
        var errText = p.StandardError.ReadToEndAsync();

        var timedOut = !p.WaitForExit(timeoutMs);
        if (timedOut) { try { p.Kill(entireProcessTree: true); } catch { } }

        // WaitForExit(int) is documented not to wait for the redirected streams
        // to finish, unlike the parameterless overload. Without this the reads
        // are still in flight when the Process is disposed and the text is
        // lost - which would swap one silent wrong answer for another.
        try { Task.WaitAll(new Task[] { outText, errText }, 5_000); } catch { }

        var code = -1;
        if (!timedOut) { try { code = p.ExitCode; } catch { } }
        return new Result(timedOut, code, Done(outText), Done(errText));

        static string Done(Task<string> t) => t.IsCompletedSuccessfully ? t.Result : "";
    }
}
