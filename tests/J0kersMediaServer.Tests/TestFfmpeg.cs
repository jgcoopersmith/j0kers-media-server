using System.Diagnostics;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// A real ffmpeg for the tests that need a conversion actually running.
///
/// It fails rather than skipping when there is none. xunit 2 has no way to
/// report a test as skipped at run time, so the alternative would be a test
/// that returns early and reports a pass it never earned - coverage that
/// exists only on paper. A machine without ffmpeg cannot run this server's
/// conversions either, so saying so is the honest result.
/// </summary>
internal static class TestFfmpeg
{
    public static string Require()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new List<string>();

        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(local))
        {
            // the installed server carries its own, which is what it runs with
            candidates.Add(Path.Combine(local, "Programs", "j0kers Media Server", exe));
            var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            try
            {
                if (Directory.Exists(winget))
                    candidates.AddRange(Directory.EnumerateFiles(winget, exe, SearchOption.AllDirectories));
            }
            catch { /* an unreadable package folder is not a reason to fail the lookup */ }
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0) candidates.Add(Path.Combine(dir, exe));

        var found = candidates.FirstOrDefault(File.Exists);
        Assert.True(found is not null,
                    "this test needs a real ffmpeg to have a conversion running, and none was found "
                    + "beside the installed server, under WinGet, or on PATH");
        return found!;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
                                              SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    /// <summary>
    /// An ffmpeg of the test's own, in <paramref name="dir"/>, that it can
    /// take away later - what an antivirus quarantine does to a running
    /// server. A hard link to the real one where the volume allows (it is
    /// over 200 MB), a copy otherwise. Deleting it leaves the real one alone.
    /// </summary>
    public static string Disposable(string dir)
    {
        var real = Require();
        var mine = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (!OperatingSystem.IsWindows() || !CreateHardLink(mine, real, IntPtr.Zero)) File.Copy(real, mine);
        return mine;
    }

    /// <summary>Deletes a Disposable ffmpeg, waiting out a scanner that is still looking at it.</summary>
    public static void TakeAway(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Delete(path); return; }
            catch when (attempt < 40) { Thread.Sleep(250); }
        }
    }

    /// <summary>Runs ffmpeg to completion and fails the test if it does not succeed.</summary>
    public static void Run(string ffmpeg, params string[] args)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-hide_banner", "-v", "error", "-y" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start ffmpeg");
        var err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } Assert.Fail("ffmpeg took over two minutes to make a test clip"); }
        Assert.True(p.ExitCode == 0, $"ffmpeg could not make a test clip (exit {p.ExitCode}): {err}");
    }

    /// <summary>A clip a browser plays as-is: H.264 and AAC in MP4.</summary>
    public static string PlayableClip(string ffmpeg, string path)
    {
        Run(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                    "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-shortest", path);
        return path;
    }

    /// <summary>
    /// Cheap to make and slow to convert: three minutes of 720p MPEG-2, which
    /// no browser plays, so the server must re-encode all of it at the
    /// veryslow preset the test server is configured with. Measured here at
    /// about 9% two seconds in, so this runs for over a minute - comfortably
    /// longer than any test that needs it running.
    /// </summary>
    public static string SlowSource(string ffmpeg, string path, int seconds = 180)
    {
        Run(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30",
                    "-t", seconds.ToString(), "-c:v", "mpeg2video", "-q:v", "8", "-an", path);
        return path;
    }
}
