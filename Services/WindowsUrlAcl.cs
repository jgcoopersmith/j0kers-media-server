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

    /// <summary>
    /// Fixed per-application id for the http.sys certificate bindings, so a
    /// rebinding replaces ours rather than accumulating, and so ours can be
    /// told apart from IIS's or anyone else's on the same machine.
    /// </summary>
    private const string AppId = "{7c0a5e11-2f4b-4c8e-9c6a-1de0f0a1c001}";

    public static void EnsureFor(ServerConfig config) => EnsureFor(config, null);

    /// <summary>
    /// <paramref name="certificate"/> non-null adds the TLS half: importing
    /// the certificate into the machine store and binding it to each HTTPS
    /// port. It rides the same elevation prompt as the URL ACLs — one
    /// approval for everything Windows insists an administrator do.
    /// </summary>
    public static void EnsureFor(ServerConfig config, TlsCertificate.Loaded? certificate)
    {
        if (!OperatingSystem.IsWindows()) return;

        var commands = new List<string>();

        // Only what is actually missing. The first TLS start has work to do;
        // every start after it would otherwise raise the same elevation
        // prompt to redo bindings that are already exactly right, which is
        // both pointless and the fastest way to train someone to click
        // through UAC without reading it.
        if (certificate is not null && TlsSetupComplete(config, certificate))
        {
            Log.Debug("tls", "certificate already imported and bound — nothing to elevate for");
            certificate = null;
        }

        if (certificate is not null)
        {
            // http.sys binds a certificate to an ip:port and finds it by
            // thumbprint in the machine's own store — a PFX on disk is not
            // something it can be pointed at directly.
            //
            // Import-PfxCertificate rather than certutil: certutil asks for
            // the password on stdin even when there isn't one, and inside a
            // hidden elevated window that is not a prompt anyone can answer.
            // It sits there until the wait times out and nothing is imported.
            var pfx = certificate.Path.Replace("'", "''");
            var import = "Import-PfxCertificate -FilePath '" + pfx + "' -CertStoreLocation Cert:\\LocalMachine\\My"
                         + (certificate.Password.Length > 0
                             ? " -Password (ConvertTo-SecureString '" + certificate.Password.Replace("'", "''") + "' -AsPlainText -Force)"
                             : "");
            commands.Add($"powershell -NoProfile -NonInteractive -Command \"{import}\"");

            var tlsPorts = new List<int>();
            if (config.Control.Enabled) tlsPorts.Add(config.Control.Port);
            if (config.Hls.Enabled) tlsPorts.Add(config.Hls.Port);
            foreach (var port in tlsPorts.Distinct())
            {
                // delete first: a binding already there is not replaced, and
                // add would simply fail with "Cannot create a file when that
                // file already exists"
                commands.Add($"netsh http delete sslcert ipport=0.0.0.0:{port}");
                commands.Add($"netsh http add sslcert ipport=0.0.0.0:{port} " +
                             $"certhash={certificate.Certificate.Thumbprint} appid={AppId} certstorename=MY");
            }
        }

        // --- URL ACLs for wide HTTP binds ---
        // Wide binds need one. So does every TLS port, whatever it binds:
        // once a certificate is bound to a port, http.sys treats the port as
        // claimed, and an unprivileged https registration on it — even
        // loopback — is refused without a reservation of its own.
        // What this run will actually serve - not whether there is setup work
        // left to do. "certificate" is deliberately cleared above when TLS is
        // already imported and bound, so reading it here would call a properly
        // configured HTTPS server "http" and tear down the very reservations
        // that are making it work.
        var tls = UrlScheme.Https;
        var aclPorts = new List<int>();
        if (config.Hls.Enabled && (tls || config.Hls.BindAddress == "0.0.0.0")) aclPorts.Add(config.Hls.Port);
        if (config.Control.Enabled && (tls || config.Control.BindAddress == "0.0.0.0")) aclPorts.Add(config.Control.Port);
        aclPorts = aclPorts.Distinct().ToList();

        if (tls)
        {
            // A reservation belongs to a scheme, and these ports have just
            // changed theirs. The leftover http one is not merely useless:
            // it holds the port, so the https registration is refused with a
            // conflict — which is also why the *bind probe* cannot be trusted
            // to spot a missing https reservation.
            //
            // But the listing can. NeedsAcl is a probe: it tries to bind and
            // reads the error, and a conflict is not the error it recognises.
            // ShowUrlAcls just asks Windows what is reserved, which is exactly
            // the question, and the branch below already trusts it for the
            // mirror case. Asking it here is what turns "every start" into
            // "the first start".
            //
            // This matters more than it looks. A settled TLS server had these
            // queued unconditionally, so every single launch raised the
            // elevation prompt and spent about three seconds in netsh redoing
            // reservations that were already exactly right — measured on this
            // user's own log, 3.1 of a 3.7 second startup. Nothing else in
            // startup comes close.
            //
            // If the listing cannot be read the set comes back empty and every
            // port is treated as unsettled, which is precisely the old
            // behaviour — so the failure mode of this shortcut is the thing it
            // replaced, not a server that will not bind.
            var reserved = ShowUrlAcls();
            var settled = reserved.Length > 0
                ? aclPorts.Where(p => reserved.Contains($"https://+:{p}/", StringComparison.OrdinalIgnoreCase)).ToHashSet()
                : new HashSet<int>();

            if (settled.Count > 0)
                Log.Debug("main", $"https reservation already held for port(s) {string.Join(", ", settled)} — not asking again");

            foreach (var p in aclPorts.Where(p => !settled.Contains(p)))
                commands.Add($"netsh http delete urlacl url=http://+:{p}/");
            aclPorts = aclPorts.Where(p => !settled.Contains(p)).ToList();
        }
        else
        {
            // The mirror of the case above, and the one that was missing.
            //
            // A reservation belongs to a scheme and holds the port against the
            // other one. A machine that has ever run with TLS therefore keeps
            // an https reservation that refuses the plain-http bind outright:
            // "failed to listen on prefix http://+:8080/ because it conflicts
            // with an existing registration on the machine". Switching back to
            // http has to clear it, exactly as switching to TLS clears the
            // http one.
            //
            // Those ports then need their http reservation adding whether or
            // not the probe says so: NeedsAcl only recognises access-denied,
            // and a conflict is a different error it reads as "nothing
            // needed" - and in any case the conflicting reservation is still
            // there while the probe runs.
            var existing = ShowUrlAcls();
            var freed = aclPorts
                .Where(p => existing.Contains($"https://+:{p}/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var p in freed)
                commands.Add($"netsh http delete urlacl url=https://+:{p}/");
            aclPorts = aclPorts.Where(p => freed.Contains(p) || NeedsAcl(p)).ToList();
        }
        // sddl WD = Everyone, locale-independent (user=Everyone breaks on non-English Windows).
        // The reservation is per scheme, so switching to TLS needs its own.
        commands.AddRange(aclPorts.Select(p =>
            $"netsh http add urlacl url={UrlScheme.Prefix}+:{p}/ sddl=D:(A;;GX;;;WD)"));

        // --- the DLNA port, which stays in the clear even under TLS ---
        // Its own reservation, spelled http:// regardless of the scheme
        // everything else moved to: televisions cannot do a TLS handshake, so
        // encrypting the one service they use would remove it.
        var dlnaPort = DlnaEndpoint.PortFor(config);
        var separateDlna = config.Discovery.Dlna
                        && DlnaEndpoint.IsSeparate(config)
                        && config.Control.BindAddress == "0.0.0.0";
        if (separateDlna && NeedsAcl(dlnaPort, "http"))
            commands.Add($"netsh http add urlacl url=http://+:{dlnaPort}/ sddl=D:(A;;GX;;;WD)");

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
            if (separateDlna) tcpPorts.Add(dlnaPort);
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

            // Say whether the certificate actually bound, rather than
            // assuming it did: without a binding every TLS handshake is
            // refused by http.sys and the server looks simply dead.
            if (certificate is not null)
            {
                var unbound = new List<int>();
                if (config.Control.Enabled && !SslCertBound(config.Control.Port, certificate.Certificate.Thumbprint))
                    unbound.Add(config.Control.Port);
                if (config.Hls.Enabled && !SslCertBound(config.Hls.Port, certificate.Certificate.Thumbprint))
                    unbound.Add(config.Hls.Port);
                if (unbound.Count == 0)
                    Log.Info("tls", "certificate bound to " +
                        $"{(config.Control.Enabled ? config.Control.Port.ToString() : "-")}" +
                        $"/{(config.Hls.Enabled ? config.Hls.Port.ToString() : "-")} — HTTPS is live");
                else
                    Log.Error("tls", $"no certificate bound to port(s) {string.Join(", ", unbound)} — " +
                        "HTTPS will refuse every connection. Bind it by hand with: netsh http add sslcert " +
                        $"ipport=0.0.0.0:<port> certhash={certificate.Certificate.Thumbprint} appid={AppId} certstorename=MY");
            }
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
    /// <summary>
    /// Whether TLS is already set up exactly as this run wants it: the
    /// certificate in the machine store, bound to every port it will serve,
    /// and an https reservation for each. All three are readable without
    /// elevation, which is the point — asking is free, prompting is not.
    /// </summary>
    private static bool TlsSetupComplete(ServerConfig config, TlsCertificate.Loaded certificate)
    {
        var thumb = certificate.Certificate.Thumbprint;
        if (!CertInMachineStore(thumb)) return false;

        var reservations = ShowUrlAcls();
        foreach (var port in PortsServed(config))
        {
            if (!SslCertBound(port, thumb)) return false;
            if (!reservations.Contains($"https://+:{port}/", StringComparison.OrdinalIgnoreCase)) return false;
            // a leftover from before TLS would hold the port
            if (reservations.Contains($"http://+:{port}/", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static IEnumerable<int> PortsServed(ServerConfig config)
    {
        if (config.Control.Enabled) yield return config.Control.Port;
        if (config.Hls.Enabled) yield return config.Hls.Port;
    }

    private static bool CertInMachineStore(string thumbprint)
    {
        try
        {
            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.My,
                System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
            foreach (var c in store.Certificates)
                if (string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string ShowUrlAcls()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", "http show urlacl")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return "";
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return output;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Whether http.sys has this certificate bound to the port.</summary>
    private static bool SslCertBound(int port, string thumbprint)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh",
                $"http show sslcert ipport=0.0.0.0:{port}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return output.Contains(thumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool NeedsAcl(int port) => NeedsAcl(port, UrlScheme.Name);

    private static bool NeedsAcl(int port, string scheme)
    {
        try
        {
            using var probe = new HttpListener();
            probe.Prefixes.Add($"{scheme}://+:{port}/");
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
