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
    /// Restricts a file to its owner. Remembered per path per run, so a
    /// session save does not shell out to icacls every time.
    ///
    /// Two things were wrong with that memo, and together they meant the
    /// accounts file was usually not protected at all.
    ///
    /// The path was marked done *before* checking the file exists. On a first
    /// run nothing is there yet — UserStore protects on construction, before
    /// any account has been written — so the path was recorded as handled, the
    /// method returned without doing anything, and the write that followed a
    /// moment later found the job already ticked off. The file was never
    /// restricted for the life of that install.
    ///
    /// And the header claimed "the ACL survives rewrites", which is true of a
    /// write in place and false of the temp-file-and-rename these files use:
    /// the rename puts a NEW file at that name, and a new file inherits the
    /// folder's permissions. Every save after the first therefore undid this,
    /// silently — see <see cref="WriteAllText"/>, which is the fix for that
    /// half.
    ///
    /// Failure is logged and tolerated: a server that refuses to run because
    /// it could not tighten a permission is worse than one that says so and
    /// carries on. <paramref name="force"/> ignores the memo, for a caller
    /// that knows the file has just been replaced.
    /// </summary>
    public static void Protect(string path, bool force = false)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!force)
        {
            lock (Lock)
            {
                if (Done.Contains(path)) return;
            }
        }

        try
        {
            // Checked before the memo is written, not after: a file that is
            // not there yet has not been protected, and saying it has is how
            // this came to be a no-op forever.
            if (!File.Exists(path)) return;

            if (!OperatingSystem.IsWindows())
            {
                // owner read/write, nothing for anyone else
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                MarkDone(path);
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

            // Both pipes at once - reading one to the end while the other
            // fills is the deadlock ProcessJob.Run exists to remove.
            var run = ProcessJob.Run(psi, 10_000);
            if (run is null) return;
            var err = run.Value.StdErr.Trim();
            if (!run.Value.Ok)
                Log.Warn("secrets", $"could not restrict {Path.GetFileName(path)} to this account: " +
                                    (err.Length > 0 ? err
                                     : run.Value.TimedOut ? "icacls did not finish"
                                     : $"icacls exit {run.Value.ExitCode}"));
            else
            {
                MarkDone(path);
                Log.Debug("secrets", $"{Path.GetFileName(path)} restricted to {me}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("secrets", $"could not restrict {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static void MarkDone(string path)
    {
        lock (Lock) Done.Add(path);
    }

    /// <summary>
    /// Writes a secret file atomically WITHOUT throwing away the restrictive
    /// ACL it already has.
    ///
    /// The usual temp-file-and-rename does throw it away. File.Move leaves a
    /// new file at that name, and a new file takes the folder's permissions —
    /// so accounts and sessions were readable by anyone with access to the
    /// directory from the first save onward, whatever the first Protect had
    /// done.
    ///
    /// File.Replace is the API for this: it swaps the contents and keeps the
    /// destination's own attributes and ACL, atomically, with no icacls call
    /// to pay for. Only the create path — when there is no destination to
    /// inherit from — needs restricting afterwards.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        var tmp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllText(tmp, contents);
        try
        {
            if (File.Exists(path))
            {
                // Keeps the ACL that is already on it. No backup file: the
                // callers that want one take their own copy first.
                File.Replace(tmp, path, null);
                return;
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException)
        {
            // Replace is fussier than Move — a different volume, a file held
            // open by a scanner. Fall back rather than lose the write, and
            // restrict the result since it is a new file.
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        Protect(path, force: true);
    }
}
