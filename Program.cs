using J0kersMediaServer.Config;
using J0kersMediaServer.Control;
using J0kersMediaServer.Hls;
using J0kersMediaServer.Logging;
using J0kersMediaServer.Rtsp;

// Before anything is written: pick up the terminal's console if we were
// launched from one. From the desktop icon there is none, and the dashboard's
// Log card is where the server's output goes.
J0kersMediaServer.Services.ConsoleWindow.AttachToParent();

// Before any child process can be started: everything this server spawns
// joins a job that the kernel tears down when this process dies, however it
// dies. Without it, killing the server leaves its ffmpeg children running,
// and the next start puts a second writer in every channel directory.
J0kersMediaServer.Services.ProcessJob.Init();

// ---- command line ----
// j0kers-media-server [config.json] [-h host] [-r rtspPort] [-H hlsPort] [-c controlPort]
// Flags override the config file, settings.json, and env vars.
string? cfgArg = null, hostArg = null;
int? rtspPortArg = null, hlsPortArg = null, controlPortArg = null;
bool? trayArg = null;

static void PrintUsage()
{
    Console.WriteLine("j0kers Media Server");
    Console.WriteLine("usage: j0kers-media-server [config.json] [options]");
    Console.WriteLine("  -h, --host <ip>          bind address / hostname (0.0.0.0 = all interfaces, localhost)");
    Console.WriteLine("  -r, --rtsp-port <port>   RTSP port (default 8554)");
    Console.WriteLine("  -H, --hls-port <port>    HLS port (default 8080)");
    Console.WriteLine("  -c, --control-port <port> control/dashboard port (default 9090)");
    Console.WriteLine("  -t, --tray               run in the background with a tray icon (Windows)");
    Console.WriteLine("      --no-tray            keep the console even if the config asks for tray mode");
    Console.WriteLine("      --help               this help");
    Console.WriteLine("Config path defaults to $J0KERS_CONFIG, ./server.json, or ./config/server.json;");
    Console.WriteLine("missing file = built-in defaults. See config/server.json for all options.");
}

for (var i = 0; i < args.Length; i++)
{
    string Next(string flag)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"Missing value after {flag}");
            Environment.Exit(1);
        }
        return args[++i];
    }
    int NextPort(string flag)
    {
        var v = Next(flag);
        if (!int.TryParse(v, out var port) || port is < 1 or > 65535)
        {
            Console.Error.WriteLine($"{flag} needs a port number 1-65535, got '{v}'");
            Environment.Exit(1);
        }
        return port;
    }

    switch (args[i])
    {
        case "--help" or "-?" or "/?":
            PrintUsage();
            return 0;
        case "-h" or "--host":
            hostArg = Next(args[i]);
            break;
        case "-r" or "--rtsp-port":
            rtspPortArg = NextPort(args[i]);
            break;
        case "-H" or "--hls-port":
            hlsPortArg = NextPort(args[i]);
            break;
        case "-c" or "--control-port":
            controlPortArg = NextPort(args[i]);
            break;
        case "-t" or "--tray" or "--daemon" or "--background":
            trayArg = true;
            break;
        case "--no-tray":
            trayArg = false;
            break;
        default:
            if (cfgArg is null && !args[i].StartsWith('-'))
            {
                cfgArg = args[i];
            }
            else
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                PrintUsage();
                return 1;
            }
            break;
    }
}

if (hostArg is not null && !System.Net.IPAddress.TryParse(hostArg, out _)
    && !hostArg.Equals("localhost", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"-h needs an IP address (0.0.0.0 = all interfaces) or 'localhost', got '{hostArg}'");
    return 1;
}

// Config resolution: explicit argument, then $J0KERS_CONFIG, then the first
// of ./server.json, ./config/server.json, <binary dir>/server.json that
// exists — so a bare `dotnet run` from the repo root just works.
// An explicitly named config that doesn't exist is an error, not a silent
// fall-through to defaults.
var explicitConfig = cfgArg ?? Environment.GetEnvironmentVariable("J0KERS_CONFIG");
if (explicitConfig is not null && !File.Exists(explicitConfig))
{
    J0kersMediaServer.Services.ConsoleWindow.Fatal($"Config file not found: {Path.GetFullPath(explicitConfig)}");
    return 1;
}
var configPath = explicitConfig
    ?? new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "server.json"),
        Path.Combine(Directory.GetCurrentDirectory(), "config", "server.json"),
        Path.Combine(AppContext.BaseDirectory, "server.json"),
    }.FirstOrDefault(File.Exists)
    ?? "server.json";

try { Console.Title = "🃏 j0kers Media Server"; } catch { /* no console attached */ }

ServerConfig config;
try
{
    config = ServerConfig.Load(File.Exists(configPath) ? configPath : null);

    // command-line flags win over everything loaded above
    if (hostArg is not null || rtspPortArg is not null || hlsPortArg is not null || controlPortArg is not null)
    {
        config.ApplySettings(new ServerConfig.SettingsOverrides
        {
            BindAddress = hostArg,
            RtspPort = rtspPortArg,
            HlsPort = hlsPortArg,
            ControlPort = controlPortArg,
        });
        Log.Info("main", $"command-line overrides: host={hostArg ?? "-"} rtsp={rtspPortArg?.ToString() ?? "-"} hls={hlsPortArg?.ToString() ?? "-"} control={controlPortArg?.ToString() ?? "-"}");
    }
}
catch (Exception ex)
{
    J0kersMediaServer.Services.ConsoleWindow.Fatal($"Failed to load config: {ex.Message}");
    return 1;
}

if (new[] { config.Rtsp.Port, config.Hls.Port, config.Control.Port }.Distinct().Count() != 3)
{
    J0kersMediaServer.Services.ConsoleWindow.Fatal("RTSP, HLS, and control ports must all be different " +
        $"(got rtsp={config.Rtsp.Port} hls={config.Hls.Port} control={config.Control.Port}).");
    return 1;
}

if (trayArg is bool wantTray) config.MinimizeToTray = wantTray;

// ---- one server per port ----
// Double-clicking the icon when it is already running used to get as far as
// starting ffmpeg before failing to take the port, and what happened next
// depended on where it failed: an error box, or a process left alive that
// served nothing. Settle it here instead, before anything is started.
//
// A second launch is nearly always someone wanting the dashboard, so that is
// what they get — no error, no second copy.
// One claim per port this copy needs, not one for the server as a whole: a
// second copy started with a different control port would take a different
// name and sail through the guard, only to fail on the RTSP or HLS port it
// still shares — which is the half-started process the guard exists to stop.
var claims = new List<Mutex>();
string? clash = null;
bool Claim(string what, int port)
{
    var m = new Mutex(initiallyOwned: false, $"Global\\j0kers-media-server-{what}-{port}", out _);
    bool got;
    try { got = m.WaitOne(TimeSpan.Zero); }
    catch (AbandonedMutexException) { got = true; }   // the previous holder was killed
    if (got) { claims.Add(m); }
    else { clash ??= $"{what} port {port}"; m.Dispose(); }
    return got;
}

var controlFree = !config.Control.Enabled || Claim("control", config.Control.Port);
var portsFree = controlFree
                && (!config.Rtsp.Enabled || Claim("rtsp", config.Rtsp.Port))
                && (!config.Hls.Enabled || Claim("hls", config.Hls.Port));
if (!portsFree)
{
    // The dashboard is the thing to open when the *control* port is the one
    // already taken — that is the running copy's own address. If only a
    // media port clashes, the running copy is somewhere else and all that
    // can honestly be said is which port is in the way.
    if (!controlFree)
    {
        // The same address the running copy advertises, not localhost.
        // Cookies and the "remember this device" key are both per-origin, so
        // opening http://localhost:9090/ when the server opened
        // http://10.0.0.191:9090/ lands on an origin that has neither — a
        // sign-in page every time.
        // the scheme is decided further down (it needs the config directory), so
    // say what this configuration means rather than what has been set so far
    J0kersMediaServer.Services.UrlScheme.UseHttps(config.Https.Enabled);
    var running = DashboardUrls(config.Control.BindAddress, config.Control.Port)[0];
        Console.WriteLine($"j0kers Media Server is already running — opening {running}");
        if (!TryOpenBrowser(running))
            J0kersMediaServer.Services.ConsoleWindow.Fatal(
                $"j0kers Media Server is already running.\n\nIts dashboard is at {running}");
    }
    else
    {
        // Fatal already picks the right channel — the terminal when there is
        // one, a message box when there isn't; printing as well says it twice
        J0kersMediaServer.Services.ConsoleWindow.Fatal(
            $"Another copy of j0kers Media Server is already using the {clash}.\n\n" +
            "Exit that one first, or give this one different ports.");
    }
    foreach (var m in claims) { try { m.ReleaseMutex(); } catch { } m.Dispose(); }
    return 0;
}

Log.SetLevel(config.Logging.Level);
var baseDirectory = File.Exists(configPath)
    ? Path.GetDirectoryName(Path.GetFullPath(configPath))!
    : Directory.GetCurrentDirectory();

// the file sink comes up before anything else is logged, so a tray-mode
// start (no console) still leaves a record of it
Log.ConfigureFile(config.Logging.ToFile, config.Logging.ResolveDirectory(baseDirectory),
                  config.Logging.RotateSizeMb, config.Logging.RotatePeriod, config.Logging.MaxFiles);

Log.Info("main", $"{config.ServerName} starting (config: {(File.Exists(configPath) ? configPath : "built-in defaults")})");

// ---- TLS ----
// Decided before anything binds, announces, or builds a URL: the scheme is
// woven through all three, and the control and media ports move together
// because a dashboard on https loading video from http is mixed content.
J0kersMediaServer.Services.TlsCertificate.Loaded? tlsCertificate = null;
if (config.Https.Enabled)
{
    tlsCertificate = J0kersMediaServer.Services.TlsCertificate.Ensure(config.Https, baseDirectory);
    if (tlsCertificate is null)
        Log.Error("tls", "HTTPS was asked for but no certificate could be prepared — " +
                         "carrying on over plain HTTP");
    else
        J0kersMediaServer.Services.UrlScheme.UseHttps(true);
}

// Serving beyond this machine over plain HTTP means passwords and session
// cookies cross the network in the clear. That is a legitimate choice on a
// home LAN and it is what this server does by default — but it should be a
// choice, not a surprise, so it is said once at every start.
if (config.Control.Enabled
    && !J0kersMediaServer.Services.UrlScheme.Https
    && !HttpListenerBinder.IsLoopbackBind(config.Control.BindAddress))
{
    Log.Warn("main", "the dashboard is served over plain HTTP on " +
        $"{config.Control.BindAddress}:{config.Control.Port} — passwords and session cookies " +
        "cross the network unencrypted. Set https.enabled, or put it behind a TLS reverse " +
        "proxy (X-Forwarded-Proto is honoured from loopback).");
}

if (config.Mounts.Count == 0)
{
    config.Mounts.Add(new MountConfig { Path = "/test", Source = "tone", Description = "Default 440 Hz test tone" });
    Log.Info("main", "no mounts configured; added default test mount /test (440 Hz tone)");
}

J0kersMediaServer.Services.ServiceController? services = null;
ControlApi? control = null;
Timer? channelWatchdog = null;
J0kersMediaServer.Discovery.DiscoveryService? discovery = null;
J0kersMediaServer.Media.FfmpegManager? ffmpeg = null;
J0kersMediaServer.Services.TrayIcon? tray = null;
var shutdown = new TaskCompletionSource();

if (config.MinimizeToTray && !OperatingSystem.IsWindows())
{
    Log.Info("main", "minimizeToTray is Windows-only; run this as a background service " +
                     "(systemd / launchd) or with nohup instead");
    config.MinimizeToTray = false;
}

// (tray mode also turns off shutdown-on-close; see ApplyTrayMode below)

// Accounts live in users.json next to the rest of the config. Until an
// administrator exists the server behaves exactly as it did before there
// were accounts — open — and the dashboard offers to create one.
J0kersMediaServer.Auth.UserStore userStore;
try
{
    userStore = new J0kersMediaServer.Auth.UserStore(baseDirectory);
}
catch (Exception ex)
{
    J0kersMediaServer.Services.ConsoleWindow.Fatal($"Failed to load accounts: {ex.Message}");
    return 1;
}
var auth = new J0kersMediaServer.Auth.AuthService(userStore, config.Control.AuthToken, baseDirectory);

// Media URLs are authorized by signature, not by session: players can't
// carry a cookie or a header.
var mediaLinks = new J0kersMediaServer.Auth.MediaLink(baseDirectory);

if (config.Control.Enabled && !auth.Enforcing)
{
    var exposed = config.Control.BindAddress is not ("127.0.0.1" or "localhost" or "::1");
    Log.Warn("main", exposed
        ? "no administrator account — the dashboard and its configuration are open to anyone on this network"
        : "no administrator account — open the dashboard to create one and protect the configuration");
}

var mediaRoot = Path.GetFullPath(Path.IsPathRooted(config.Hls.MediaRoot)
    ? config.Hls.MediaRoot
    : Path.Combine(baseDirectory, config.Hls.MediaRoot));

try
{
    ffmpeg = new J0kersMediaServer.Media.FfmpegManager(config.Ffmpeg, mediaRoot, baseDirectory);
    J0kersMediaServer.Services.WindowsUrlAcl.EnsureFor(config, tlsCertificate);
    services = new J0kersMediaServer.Services.ServiceController(config, baseDirectory)
    {
        Subtitles = new J0kersMediaServer.Media.SubtitleManager(ffmpeg),
        Ffmpeg = ffmpeg,
        Links = mediaLinks,
        Sessions = auth,
    };
    services.StartServices();
    if (config.Control.Enabled)
    {
        control = new ControlApi(config, services, baseDirectory, auth, mediaLinks, ffmpeg,
            requestShutdown: () => shutdown.TrySetResult());
        services.OnHlsActivity = control.NoteActivity; // streaming keeps the server up
        // Unlinked conversions stay on disk but leave the listing; the HLS
        // server does the filtering and the control API owns the store.
        services.Listed = control.Listed;
        if (services.Hls is not null) services.Hls.Listed = control.Listed;
        control.Start();

        // Now that this server answers, the channels that were running when
        // it stopped can be restarted — a pinned free-TV channel pulls
        // through our own proxy, so it needs the control API to exist.
        ffmpeg.RestoreRunningChannels();

        // Likewise the batch conversion queue: whatever was still owed when
        // this server last stopped is on disk, and picks up here rather than
        // being lost with the process that was working through it.
        ffmpeg.ResumeVodQueue();

        // The channel watchdog: a live job that is running but has written
        // nothing for 90 seconds is wedged, and killing it is what revives
        // it (the exit handler restarts crashed channels).
        //
        // Assigned to a variable declared OUTSIDE this block. The first
        // version was a `using var` right here — inside a block that closes
        // a few lines down — so the timer was disposed milliseconds after
        // it was created and the watchdog never ran once. Its silence read
        // as health. It is disposed at shutdown with everything else.
        channelWatchdog = new Timer(_ =>
        {
            try { ffmpeg.CheckLiveJobs(); }
            catch (Exception ex) { Log.Warn("ffmpeg", $"watchdog: {ex.Message}"); }
            // The batch queue rides the same tick: it advances only on one-shot
            // events, so if one is ever dropped this re-arms it. Kept apart from
            // the channel check above so an exception in one cannot skip the
            // other — a channel fault must never be what stalls conversion.
            // A conversion that is running but producing nothing holds its slot
            // for ever; judged by output rather than by existence, so the queue
            // cannot be stopped for good by a wedged encode.
            try { ffmpeg.CheckVodJobs(); }
            catch (Exception ex) { Log.Warn("ffmpeg", $"conversion watchdog: {ex.Message}"); }
            try { ffmpeg.KickVodQueue(); }
            catch (Exception ex) { Log.Warn("ffmpeg", $"queue watchdog: {ex.Message}"); }
        }, null, dueTime: 30_000, period: 30_000);

        // Announce on the network. Started after the control API is listening,
        // since everything advertised points at it — a client that found us
        // first and knocked immediately would otherwise get nothing.
        discovery = new J0kersMediaServer.Discovery.DiscoveryService(
            config.Discovery, config.ServerName, config.Control.Port, baseDirectory,
            J0kersMediaServer.Services.DlnaEndpoint.PortFor(config));
        discovery.Start();
        control.Discovery = discovery;

        var urls = DashboardUrls(control.BoundHost, config.Control.Port);
        var dashboardUrl = urls[0];

        if (config.Control.OpenDashboardOnStart)
        {
            var urlList = string.Join(" · ", urls);
            if (control.BoundHost == "0.0.0.0")
                urlList += " (bound to 0.0.0.0 — reachable on any of this machine's addresses)";
            if (OperatingSystem.IsLinux()
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                // headless box — nothing to open a browser on
                Log.Info("main", $"dashboard: {urlList}");
            }
            else if (TryOpenBrowser(dashboardUrl))
            {
                Log.Info("main", $"dashboard opened: {urlList} (disable with control.openDashboardOnStart=false)");
            }
            else
            {
                Log.Info("main", $"no browser opener found on this system; dashboard: {urlList}");
            }
        }

        // Background/tray mode can be switched on and off while running —
        // the Config dialog calls this, and startup uses it too.
        var svc = services;                                   // for the menu callbacks

        bool ApplyTrayMode(bool on)
        {
            if (on)
            {
                if (tray is not null) return true;             // already in the tray
                if (!OperatingSystem.IsWindows()) return false;

                var icon = new J0kersMediaServer.Services.TrayIcon(
                    tip: $"{config.ServerName} — {dashboardUrl}",
                    openDashboard: () => TryOpenBrowser(dashboardUrl),
                    servicesRunning: () => svc.Running,
                    setServices: run => { if (run) svc.StartServices(); else svc.StopServices(); },
                    requestShutdown: () => shutdown.TrySetResult());

                if (!icon.Start(hideConsole: true))
                {
                    Log.Warn("main", "could not create the tray icon; keeping the console window");
                    icon.Dispose();
                    return false;
                }
                tray = icon;
                config.MinimizeToTray = true;
                // as a background daemon there is no "session" tab, so closing
                // the dashboard must not take the server down
                config.Control.ShutdownOnClose = false;
                Log.Info("main", $"running in the tray — double-click the joker icon for the dashboard ({dashboardUrl})");
                // No balloon here. Background mode fires this on every single
                // startup — the log shows it dozens of times — which is a
                // notification for something the user already asked to have
                // happen every time. The one balloon worth showing is on the
                // *other* end: closing the dashboard while in the background
                // is the moment someone could mistake for the app having
                // quit, and that one (OnDashboardClosed, below) already only
                // fires when background mode is the thing actually in effect.
                return true;
            }

            tray?.Dispose();   // also restores the hidden console window
            tray = null;
            config.MinimizeToTray = false;
            // Not running in the background means the dashboard is the session:
            // closing it is how someone finishes with the server, and leaving a
            // process behind that they believe they have exited is what makes
            // the next upgrade report stopping a server they thought was gone.
            // Background mode is the deliberate choice to stay running; without
            // it, closing the page shuts the server down.
            config.Control.ShutdownOnClose = true;
            Log.Info("main", "background mode off — console restored");
            return false;
        }

        // Closing the dashboard in background mode leaves the server running,
        // which is the opposite of what closing a window normally means — so
        // the tray icon says so, briefly.
        control.OnDashboardClosed = () => tray?.Notify(
            "j0kers Media Server",
            "Still running in the background — the joker icon on the taskbar reopens the dashboard.",
            autoHideMs: 3000);

        control.SetTrayMode = ApplyTrayMode;
        if (config.MinimizeToTray) ApplyTrayMode(true);
        // Started in the foreground and staying there: the same rule as turning
        // background mode off. The dashboard is the session, so closing it ends
        // the server rather than leaving one running that nobody can see.
        else config.Control.ShutdownOnClose = true;
    }
}
catch (Exception ex)
{
    // The likeliest startup failure is the server already running - somebody
    // double-clicked the icon twice - and "access is denied" is a true but
    // unhelpful way to say so. But the message must not name a port that was
    // never the problem: this used to report the control port whatever had
    // actually failed, so a clash on the media port sent people to look at
    // the dashboard port instead. Take the prefix out of the error when it
    // names one.
    var failedOn = System.Text.RegularExpressions.Regex.Match(
        ex.Message, @"prefix '([a-z]+)://[^:]*:(\d+)/'");
    var port = failedOn.Success ? failedOn.Groups[2].Value : config.Control.Port.ToString();
    var scheme = failedOn.Success ? failedOn.Groups[1].Value : "http";

    // A conflicting *registration* is not a port in use. It is a reservation
    // for the other scheme on the same port, left behind by a run that used
    // TLS, and it refuses the bind even with nothing listening - so telling
    // someone to close the running copy is advice for a different fault.
    var conflicting = ex.Message.Contains("conflicts with an existing registration",
                                          StringComparison.OrdinalIgnoreCase);
    var inUse = !conflicting
                && (ex is System.Net.HttpListenerException or System.Net.Sockets.SocketException
                    || ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase));

    J0kersMediaServer.Services.ConsoleWindow.Fatal(
        conflicting
        ? $"Port {port} is reserved on this machine for a different protocol, so this copy has stopped.\n\n" +
          $"Something - most likely this server from when it ran with HTTPS on - holds a Windows URL\n" +
          $"reservation for that port under the other scheme, and it refuses the {scheme} listener even\n" +
          $"with nothing running. Starting again should clear it, since the server now removes the\n" +
          $"leftover itself. If it persists, run as administrator:\n\n" +
          $"    netsh http delete urlacl url=https://+:{port}/\n\n" +
          $"or give the server a different port in server.json.\n\n({ex.Message})"
        : inUse
        ? $"j0kers Media Server looks like it is already running.\n\n" +
          $"Port {port} is taken, so this copy has stopped. Open the dashboard at " +
          $"http://localhost:{config.Control.Port}/ — or exit the running copy first.\n\n({ex.Message})"
        : $"Startup failed: {ex.Message}");
    tray?.Dispose(); discovery?.Dispose(); services?.Dispose(); control?.Dispose(); ffmpeg?.Dispose();
    return 1;
}

/// <summary>
/// URLs the dashboard is reachable on — see Services/NetworkInfo for the
/// interface selection rules (connected physical networks only).
/// </summary>
static string[] DashboardUrls(string bindAddress, int port) =>
    J0kersMediaServer.Services.NetworkInfo.DashboardUrls(bindAddress, port);

static bool TryOpenBrowser(string url)
{
    // each OS has its own way to hand a URL to the default browser; not
    // every Linux install ships xdg-open, so walk the common openers
    var attempts = OperatingSystem.IsWindows()
        ? new[] { new[] { url } }
        : OperatingSystem.IsMacOS()
            ? new[] { new[] { "open", url } }
            : new[]
            {
                new[] { "xdg-open", url },
                new[] { "sensible-browser", url },
                new[] { "x-www-browser", url },
            };
    foreach (var attempt in attempts)
    {
        try
        {
            if (attempt.Length == 1)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = attempt[0],
                    UseShellExecute = true,
                });
            else
                System.Diagnostics.Process.Start(attempt[0], attempt[1]);
            return true;
        }
        catch { /* opener not present — try the next */ }
    }
    return false;
}

// Keep what is in memory on disk, whatever ends this process. The timer is
// the part that matters: most of this server's exits are hard kills — to
// replace the executable, from Task Manager, or the machine going down — and
// no exit handler runs for any of them.
var stateSaver = new J0kersMediaServer.Services.StateSaver(TimeSpan.FromMinutes(1));
// Sign-in expiry slides forward all session in memory. Left unwritten, a
// restart signs everybody out against a timestamp from hours ago.
stateSaver.Register("sessions", auth.FlushSessions);
if (control is not null) stateSaver.Register("control", control.FlushState);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (!shutdown.Task.IsCompleted)
    {
        shutdown.TrySetResult();
    }
    else
    {
        // second Ctrl+C: don't make the user wait on a stuck teardown
        Log.Warn("main", "forced exit");
        Environment.Exit(130);
    }
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.TrySetResult();

Log.Info("main", tray is not null
    ? "ready — right-click the tray icon to exit"
    : "ready — press Ctrl+C to stop");
await shutdown.Task;

// take the icon down first: it also restores a hidden console so the
// shutdown log is visible
tray?.Dispose();

Log.Info("main", "shutting down (Ctrl+C again to force)");
// watchdog: if any teardown blocks, exit anyway instead of hanging the console
using var watchdog = new Timer(_ =>
{
    Log.Warn("main", "shutdown timed out — exiting");
    Environment.Exit(0);
}, null, dueTime: 5000, period: Timeout.Infinite);

// before the services it advertises, so listeners get the goodbye while
// there is still something to say goodbye about
discovery?.Dispose();
services?.Dispose();
control?.Dispose();
channelWatchdog?.Dispose();   // before ffmpeg, so no kill fires mid-teardown
ffmpeg?.Dispose();
// One last pass over everything held in memory — the idle timestamps that
// have been sliding forward all session, the codec cache — so a clean exit
// loses nothing at all rather than up to a minute.
stateSaver.Dispose();
// Releasing here also keeps these referenced for the whole run: a collected
// Mutex closes its handle, which would hand the ports to a second copy while
// this one is still serving them.
foreach (var m in claims) { try { m.ReleaseMutex(); } catch { } m.Dispose(); }
Log.Info("main", "bye");
Log.CloseFile();
return 0;
