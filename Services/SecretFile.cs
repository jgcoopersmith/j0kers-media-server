using System.Diagnostics;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Services;

/// <summary>
/// Keeps the files that must not be read by other accounts on this machine
/// readable only by the one running the server: <c>users.json</c> (password
/// hashes and key digests), <c>signing.key</c> (mints any media link), and
/// <c>sessions.json</c> (live session digests).
///
/// They used to inherit whatever the config directory allowed, which on a
/// shared machine is usually "any local user". Nothing here helps against
/// an administrator — nothing can — but it closes the ordinary case of
/// another account simply opening the file.
/// </summary>
public static class SecretFile
{
    private static readonly HashSet<string> Done = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    /// <summary>
    /// Restricts a file to its owner. Done once per path per run — the ACL
    /// survives rewrites, and shelling out to icacls on every session save
    /// would be absurd. Failure is logged and tolerated: a server that
    /// refuses to run because it could not tighten a permission is worse
    /// than one that says so and carries on.
    /// </summary>
    public static void Protect(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (Lock)
        {
            if (!Done.Add(path)) return;
        }

        try
        {
            if (!File.Exists(path)) return;

            if (!OperatingSystem.IsWindows())
            {
                // owner read/write, nothing for anyone else
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return;
            }

            // Windows has no chmod. icacls is the supported way to do this
            // without dragging in the ACL API, and it is the same tool the
            // URL-ACL setup already uses:
            //   /inheritance:r  stop inheriting the folder's permissive ACL
            //   /grant:r <me>:F replace any entry for us with full control
            var me = Environment.UserDomainName.Length > 0
                ? $"{Environment.UserDomainName}\\{Environment.UserName}"
                : Environment.UserName;
            var psi = new ProcessStartInfo("icacls")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { path, "/inheritance:r", "/grant:r", $"{me}:F" })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return;
            p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            if (p.ExitCode != 0)
                Log.Warn("secrets", $"could not restrict {Path.GetFileName(path)} to this account: " +
                                    (err.Trim().Length > 0 ? err.Trim() : $"icacls exit {p.ExitCode}"));
            else
                Log.Debug("secrets", $"{Path.GetFileName(path)} restricted to {me}");
        }
        catch (Exception ex)
        {
            Log.Warn("secrets", $"could not restrict {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
