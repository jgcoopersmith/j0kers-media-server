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
}
