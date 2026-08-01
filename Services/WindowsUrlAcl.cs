using System.Diagnostics;
using System.Net;
using J0kersMediaServer.Config;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Services;

/// <summary>
/// Windows-only: http.sys requires an admin-granted URL ACL before a normal
/// process may listen on all interfaces. When the config asks for 0.0.0.0
/// and the ACL is missing, this asks for it once via an elevated netsh
/// (one UAC prompt covering every port needed). Declining is fine — the
/// binder then falls back to localhost as before.
/// </summary>
public static class WindowsUrlAcl
{
    public static void EnsureFor(ServerConfig config)
    {
        if (!OperatingSystem.IsWindows()) return;

        var ports = new List<int>();
        if (config.Hls.Enabled && config.Hls.BindAddress == "0.0.0.0") ports.Add(config.Hls.Port);
        if (config.Control.Enabled && config.Control.BindAddress == "0.0.0.0") ports.Add(config.Control.Port);
        ports = ports.Where(NeedsAcl).Distinct().ToList();
        if (ports.Count == 0) return;

        Log.Info("main", $"asking Windows for permission to listen on all interfaces (ports {string.Join(", ", ports)}) — accept the admin prompt");
        // sddl WD = Everyone, locale-independent (user=Everyone breaks on non-English Windows)
        var commands = string.Join(" & ",
            ports.Select(p => $"netsh http add urlacl url=http://+:{p}/ sddl=D:(A;;GX;;;WD)"));
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + commands)
            {
                Verb = "runas", // UAC elevation
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            proc?.WaitForExit(60_000);

            var stillMissing = ports.Where(NeedsAcl).ToList();
            if (stillMissing.Count == 0)
                Log.Info("main", "permission granted — binding to all interfaces");
            else
                Log.Warn("main", $"URL ACL still missing for port(s) {string.Join(", ", stillMissing)} — HTTP will fall back to localhost");
        }
        catch (Exception ex)
        {
            // Win32Exception 1223 = user clicked No on the UAC prompt
            Log.Warn("main", $"permission not granted ({ex.Message}) — HTTP will fall back to localhost. " +
                             "Grant manually with: netsh http add urlacl url=http://+:<port>/ sddl=D:(A;;GX;;;WD)");
        }
    }

    /// <summary>True when binding http://+:port/ is denied for lack of a URL ACL.</summary>
    private static bool NeedsAcl(int port)
    {
        try
        {
            using var probe = new HttpListener();
            probe.Prefixes.Add($"http://+:{port}/");
            probe.Start();
            probe.Stop();
            return false;
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            return true;
        }
        catch
        {
            return false; // some other problem — let the real bind report it
        }
    }
}
