using System.Diagnostics;
using System.Net;
using J0kersMediaServer.Config;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Services;

/// <summary>
/// Windows-only network self-setup, gathered into a single UAC prompt:
///
/// 1. URL ACLs — http.sys requires an admin-granted reservation before a
///    normal process may listen on all interfaces.
/// 2. Firewall — inbound rules must be PORT-based: HTTP traffic terminates
///    in the http.sys kernel driver (netstat shows PID 4/System owning the
///    socket), so the program-based allow rule Windows offers on first run
///    never matches it. Local browsing works (loopback skips the firewall)
///    while phones and other machines are silently dropped.
///
/// Declining the prompt is fine — HTTP falls back to localhost-only.
/// </summary>
public static class WindowsUrlAcl
{
    private const string TcpRuleName = "j0kers Media Server (TCP)";
    private const string UdpRuleName = "j0kers Media Server (UDP)";

    public static void EnsureFor(ServerConfig config)
    {
        if (!OperatingSystem.IsWindows()) return;

        var commands = new List<string>();

        // --- URL ACLs for wide HTTP binds ---
        var aclPorts = new List<int>();
        if (config.Hls.Enabled && config.Hls.BindAddress == "0.0.0.0") aclPorts.Add(config.Hls.Port);
        if (config.Control.Enabled && config.Control.BindAddress == "0.0.0.0") aclPorts.Add(config.Control.Port);
        aclPorts = aclPorts.Where(NeedsAcl).Distinct().ToList();
        // sddl WD = Everyone, locale-independent (user=Everyone breaks on non-English Windows)
        commands.AddRange(aclPorts.Select(p => $"netsh http add urlacl url=http://+:{p}/ sddl=D:(A;;GX;;;WD)"));

        // --- port-based firewall rules when anything binds wide ---
        var anyWide = (config.Rtsp.Enabled && config.Rtsp.BindAddress == "0.0.0.0")
                   || (config.Hls.Enabled && config.Hls.BindAddress == "0.0.0.0")
                   || (config.Control.Enabled && config.Control.BindAddress == "0.0.0.0");
        if (anyWide)
        {
            var tcpPorts = new List<int>();
            if (config.Rtsp.Enabled) tcpPorts.Add(config.Rtsp.Port);
            if (config.Hls.Enabled) tcpPorts.Add(config.Hls.Port);
            if (config.Control.Enabled) tcpPorts.Add(config.Control.Port);
            var tcpList = string.Join(",", tcpPorts.Distinct());
            var udpRange = $"{config.Rtp.PortRangeMin}-{config.Rtp.PortRangeMax}";

            if (!FirewallRuleMatches(TcpRuleName, tcpList))
            {
                commands.Add($"netsh advfirewall firewall delete rule name=\"{TcpRuleName}\"");
                commands.Add($"netsh advfirewall firewall add rule name=\"{TcpRuleName}\" dir=in action=allow protocol=TCP localport={tcpList}");
            }
            if (config.Rtsp.Enabled && !FirewallRuleMatches(UdpRuleName, udpRange))
            {
                commands.Add($"netsh advfirewall firewall delete rule name=\"{UdpRuleName}\"");
                commands.Add($"netsh advfirewall firewall add rule name=\"{UdpRuleName}\" dir=in action=allow protocol=UDP localport={udpRange}");
            }
        }

        if (commands.Count == 0) return;

        Log.Info("main", "asking Windows for network permissions (URL ACLs / firewall ports) — accept the admin prompt");
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", commands))
            {
                Verb = "runas", // UAC elevation
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            proc?.WaitForExit(60_000);

            var aclMissing = aclPorts.Where(NeedsAcl).ToList();
            if (aclMissing.Count == 0)
                Log.Info("main", "network permissions in place — server reachable from other devices");
            else
                Log.Warn("main", $"URL ACL still missing for port(s) {string.Join(", ", aclMissing)} — HTTP will fall back to localhost");
        }
        catch (Exception ex)
        {
            // Win32Exception 1223 = user clicked No on the UAC prompt
            Log.Warn("main", $"permission not granted ({ex.Message}) — remote devices may be blocked. " +
                             "Grant manually: netsh http add urlacl url=http://+:<port>/ sddl=D:(A;;GX;;;WD) and " +
                             "netsh advfirewall firewall add rule name=\"j0kers\" dir=in action=allow protocol=TCP localport=<ports>");
        }
    }

    /// <summary>
    /// True when the named inbound allow rule exists and covers the wanted
    /// ports. Parses netsh output leniently; on any doubt returns false so
    /// the rule gets recreated (delete+add is idempotent).
    /// </summary>
    private static bool FirewallRuleMatches(string name, string wantedPorts)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh",
                $"advfirewall firewall show rule name=\"{name}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (p.ExitCode != 0) return false;
            return output.Replace(" ", "").Contains(wantedPorts.Replace(" ", ""));
        }
        catch
        {
            return false;
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
