using System.Runtime.InteropServices;
using System.Text;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Services;

/// <summary>
/// "Start with Windows": an entry under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, which Windows
/// runs at every interactive logon — after a boot, after a sign-out and back
/// in, and after a switch-user relogin alike.
///
/// Per-user (HKCU) rather than machine-wide (HKLM) deliberately. HKLM needs an
/// administrator to write, and a service that starts before anybody logs in
/// has no desktop to put a tray icon or a dashboard window on — which is the
/// half of this setting that the owner actually asked for. HKCU starts it when
/// there is a session to start it into.
///
/// Raw Win32 rather than <c>Microsoft.Win32.Registry</c>, whose types are not
/// in the reference set for a platform-neutral <c>net10.0</c> target; adding a
/// package for four calls would trade the project's single cross-platform
/// target framework for very little. Everything here is inert on non-Windows
/// systems, same as <see cref="TrayIcon"/>.
/// </summary>
public static class WindowsAutostart
{
    /// <summary>
    /// The name the entry appears under in Task Manager's Startup tab, so
    /// somebody who finds it there knows what it is and can turn it off from
    /// the place Windows tells them to.
    /// </summary>
    public const string EntryName = "j0kers Media Server";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Task Manager's Startup tab does not delete a Run entry when you turn it
    /// off — it leaves the value exactly where it is and records the decision
    /// here instead, under the same value name, as a binary blob whose first
    /// byte is 2 for enabled and 3 for disabled.
    ///
    /// So the presence of a Run value is not the same as "starts at logon",
    /// and a server that only looked at the Run key would report the box
    /// ticked while nothing started. Verified on this machine: OneDrive,
    /// Spotify and Docker Desktop all sit in the Run key with a 3 here.
    ///
    /// It also means an entry cannot be re-enabled by rewriting the Run value:
    /// the approval is keyed by name and survives a delete-and-rewrite. It has
    /// to be cleared, which is what <see cref="ClearApproval"/> is for.
    /// </summary>
    private const string ApprovalKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));

    private const int KEY_QUERY_VALUE = 0x0001, KEY_SET_VALUE = 0x0002;
    private const int REG_SZ = 1;
    private const int RRF_RT_REG_SZ = 0x0002, RRF_RT_REG_BINARY = 0x0008;
    private const int ERROR_SUCCESS = 0, ERROR_FILE_NOT_FOUND = 2;

    /// <summary>Windows only. Everywhere else the setting is inert and reported unsupported.</summary>
    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>
    /// The command Windows should run at logon: the executable and, when one
    /// was loaded, the absolute path of its config file.
    ///
    /// The config path is spelled out rather than left implicit because a Run
    /// entry inherits Explorer's working directory — usually
    /// <c>C:\Windows\system32</c> — not the install directory. The server does
    /// fall back to the <c>server.json</c> beside its own binary, but only
    /// after looking in the working directory first, so a stray
    /// <c>server.json</c> wherever Windows happened to start it would win.
    /// An absolute path removes the question.
    ///
    /// Callers must not reach the no-config form for a real registration:
    /// a bare executable starts a server that finds none of its own state.
    /// <c>ServerConfig.EnsureConfigFile</c> exists to make sure there is always
    /// a path to pass; the argument stays optional only so the composition
    /// itself can be tested and so <see cref="Refresh"/> has something to
    /// compare against.
    ///
    /// Pure, so the quoting can be tested without touching the registry.
    /// </summary>
    public static string ComposeCommand(string exePath, string? configPath)
        => string.IsNullOrWhiteSpace(configPath)
            ? $"\"{exePath}\""
            : $"\"{exePath}\" \"{configPath}\"";

    /// <summary>
    /// Where this server was started from, or null if the runtime will not say
    /// (it does not, for instance, under some single-file hosts).
    /// </summary>
    public static string? CurrentExecutable()
    {
        try { return Environment.ProcessPath; }
        catch { return null; }
    }

    /// <summary>The command currently registered, or null if there is no entry.</summary>
    public static string? Registered()
    {
        var raw = ReadValue(RunKey, EntryName, RRF_RT_REG_SZ);
        if (raw is null || raw.Length == 0) return null;
        // The size that comes back counts the terminator, which would otherwise
        // arrive as a trailing NUL inside the string.
        return Encoding.Unicode.GetString(raw).TrimEnd('\0');
    }

    /// <summary>
    /// True when the entry exists but Windows has been told not to run it —
    /// the state Task Manager's Startup tab leaves behind. See
    /// <see cref="ApprovalKey"/>.
    /// </summary>
    public static bool DisabledByWindows()
    {
        var approval = ReadValue(ApprovalKey, EntryName, RRF_RT_REG_BINARY);
        // No approval record at all is the normal enabled case: Windows only
        // writes one once somebody has touched the switch.
        return approval is { Length: > 0 } && (approval[0] & 1) != 0;
    }

    /// <summary>
    /// Will the server actually start at the next logon? Both halves have to
    /// agree — an entry that is present but switched off in Task Manager does
    /// not start, and reporting it as on is how the dashboard would end up
    /// lying about it.
    /// </summary>
    public static bool IsEnabled() => Registered() is { Length: > 0 } && !DisabledByWindows();

    /// <summary>
    /// Adds or removes the logon entry. Returns false with a reason in
    /// <paramref name="error"/> if it could not be done, so the caller can
    /// refuse to persist a setting that did not actually take effect.
    /// </summary>
    public static bool Apply(bool enable, string? configPath, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            // Turning it off where it was never on is not a failure — there is
            // nothing to remove and nothing to warn about.
            if (!enable) return true;
            error = "starting with the system is Windows-only; on macOS/Linux use launchd, systemd, or a login item";
            return false;
        }

        if (!enable) return Remove(out error);

        var exe = CurrentExecutable();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            error = "could not work out where this server was started from, so there is nothing to register";
            return false;
        }

        return Write(ComposeCommand(exe, configPath), out error);
    }

    /// <summary>
    /// Rewrites the entry if it is present but points somewhere else — which
    /// is what an upgrade that replaced or moved the binary leaves behind. A
    /// stale entry fails silently at the next logon, which is the worst way
    /// for this feature to break: the box stays ticked and nothing starts.
    ///
    /// Does nothing when the setting is off, so it can be called unconditionally
    /// at startup.
    /// </summary>
    public static void Refresh(bool enabled, string? configPath)
    {
        if (!OperatingSystem.IsWindows() || !enabled) return;
        // No config file to name means no way to write a command better than
        // whatever is already there, and rewriting it to a bare executable
        // would be strictly worse — that server would start into Explorer's
        // working directory and find none of its own state. Leave it.
        if (string.IsNullOrWhiteSpace(configPath)) return;

        var current = Registered();
        if (current is null) return;                    // off, or removed by hand — leave it alone

        var exe = CurrentExecutable();
        if (string.IsNullOrWhiteSpace(exe)) return;
        var want = ComposeCommand(exe, configPath);
        if (string.Equals(current, want, StringComparison.OrdinalIgnoreCase)) return;

        if (Write(want, out var error))
            Log.Info("startup", $"start-with-Windows entry updated: {want}");
        else
            Log.Warn("startup", $"start-with-Windows entry is stale and could not be updated: {error}");
    }

    private static bool Write(string command, out string? error)
    {
        error = null;
        var key = IntPtr.Zero;
        try
        {
            var rc = RegCreateKeyEx(HKEY_CURRENT_USER, RunKey, 0, null, 0, KEY_SET_VALUE,
                                    IntPtr.Zero, out key, out _);
            if (rc != ERROR_SUCCESS)
            {
                error = $"could not open the Windows startup list (error {rc})";
                return false;
            }
            // REG_SZ wants its terminator counted; a value written without one
            // reads back with whatever follows it in the hive.
            var bytes = Encoding.Unicode.GetBytes(command + "\0");
            rc = RegSetValueEx(key, EntryName, 0, REG_SZ, bytes, bytes.Length);
            if (rc != ERROR_SUCCESS)
            {
                error = $"could not add the startup entry (error {rc})";
                return false;
            }
            // Writing the Run value is not enough on its own: if this entry was
            // ever switched off in Task Manager, the "disabled" record outlives
            // a delete-and-rewrite and the new value would never run.
            ClearApproval();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally { if (key != IntPtr.Zero) RegCloseKey(key); }
    }

    private static bool Remove(out string? error)
    {
        error = null;
        var key = IntPtr.Zero;
        try
        {
            var rc = RegCreateKeyEx(HKEY_CURRENT_USER, RunKey, 0, null, 0, KEY_SET_VALUE | KEY_QUERY_VALUE,
                                    IntPtr.Zero, out key, out _);
            if (rc != ERROR_SUCCESS)
            {
                error = $"could not open the Windows startup list (error {rc})";
                return false;
            }
            rc = RegDeleteValue(key, EntryName);
            // Already gone is the state that was asked for, not a failure.
            if (rc != ERROR_SUCCESS && rc != ERROR_FILE_NOT_FOUND)
            {
                error = $"could not remove the startup entry (error {rc})";
                return false;
            }
            // Leave no approval record behind for a value that no longer
            // exists, so a later re-tick starts from a clean slate.
            ClearApproval();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally { if (key != IntPtr.Zero) RegCloseKey(key); }
    }

    /// <summary>
    /// The two-call RegGetValue dance: ask for the size, then for the bytes.
    /// Sizes here are in BYTES, not characters, which is why the string reader
    /// hands the whole buffer to <see cref="Encoding.Unicode"/> rather than
    /// halving anything.
    /// </summary>
    private static byte[]? ReadValue(string subKey, string name, int typeFlag)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var size = 0;
            var rc = RegGetValue(HKEY_CURRENT_USER, subKey, name, typeFlag, out _, null, ref size);
            if (rc != ERROR_SUCCESS || size <= 0) return null;
            var buffer = new byte[size];
            rc = RegGetValue(HKEY_CURRENT_USER, subKey, name, typeFlag, out _, buffer, ref size);
            if (rc != ERROR_SUCCESS) return null;
            // The second call can report fewer bytes than the first reserved.
            return size == buffer.Length ? buffer : buffer[..size];
        }
        catch { return null; }
    }

    /// <summary>
    /// Removes any "disabled in Task Manager" record for this entry. Opens the
    /// key rather than creating it: a machine where nobody has ever touched the
    /// Startup tab has no such key, and there is nothing there to clear.
    /// Best-effort — failing to tidy this up must not fail the save.
    /// </summary>
    private static void ClearApproval()
    {
        if (!OperatingSystem.IsWindows()) return;
        var key = IntPtr.Zero;
        try
        {
            if (RegOpenKeyEx(HKEY_CURRENT_USER, ApprovalKey, 0, KEY_SET_VALUE, out key) != ERROR_SUCCESS)
                return;
            RegDeleteValue(key, EntryName);
        }
        catch { /* tidying, not the operation */ }
        finally { if (key != IntPtr.Zero) RegCloseKey(key); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyEx(IntPtr hKey, string subKey, int options, int samDesired,
                                           out IntPtr result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegCreateKeyEx(IntPtr hKey, string subKey, int reserved, string? cls,
                                             int options, int samDesired, IntPtr security,
                                             out IntPtr result, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueEx(IntPtr hKey, string name, int reserved, int type,
                                            byte[] data, int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteValue(IntPtr hKey, string name);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegGetValue(IntPtr hKey, string subKey, string value, int flags,
                                          out int type, byte[]? data, ref int cbData);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}
