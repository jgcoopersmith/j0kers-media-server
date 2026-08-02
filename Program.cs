using J0kersMediaServer.Config;
using J0kersMediaServer.Control;
using J0kersMediaServer.Hls;
using J0kersMediaServer.Logging;
using J0kersMediaServer.Rtsp;

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
    Console.Error.WriteLine($"Config file not found: {Path.GetFullPath(explicitConfig)}");
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
    Console.Error.WriteLine($"Failed to load config: {ex.Message}");
    return 1;
}

if (new[] { config.Rtsp.Port, config.Hls.Port, config.Control.Port }.Distinct().Count() != 3)
{
    Console.Error.WriteLine("RTSP, HLS, and control ports must all be different " +
        $"(got rtsp={config.Rtsp.Port} hls={config.Hls.Port} control={config.Control.Port}).");
    return 1;
}

if (trayArg is bool wantTray) config.MinimizeToTray = wantTray;

Log.SetLevel(config.Logging.Level);
var baseDirectory = File.Exists(configPath)
    ? Path.GetDirectoryName(Path.GetFullPath(configPath))!
    : Directory.GetCurrentDirectory();

Log.Info("main", $"{config.ServerName} starting (config: {(File.Exists(configPath) ? configPath : "built-in defaults")})");

if (config.Mounts.Count == 0)
{
    config.Mounts.Add(new MountConfig { Path = "/test", Source = "tone", Description = "Default 440 Hz test tone" });
    Log.Info("main", "no mounts configured; added default test mount /test (440 Hz tone)");
}

J0kersMediaServer.Services.ServiceController? services = null;
ControlApi? control = null;
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
    Console.Error.WriteLine($"Failed to load accounts: {ex.Message}");
    return 1;
}
var auth = new J0kersMediaServer.Auth.AuthService(userStore, config.Control.AuthToken);

if (config.Control.Enabled && !auth.Enforcing)
{
    var exposed = config.Control.BindAddress is not ("127.0.0.1" or "localhost" or "::1");
    Log.Warn("main", exposed
        ? "no administrator account — the dashboard and its configuration are open to anyone on this network"
        : "no administrator account — open the dashboard to create one and protect the configuration");
    // needed to claim the server from anything other than this machine
    Log.Info("main", $"first-run setup code: {auth.SetupCode}");
}

var mediaRoot = Path.GetFullPath(Path.IsPathRooted(config.Hls.MediaRoot)
    ? config.Hls.MediaRoot
    : Path.Combine(baseDirectory, config.Hls.MediaRoot));

try
{
    ffmpeg = new J0kersMediaServer.Media.FfmpegManager(config.Ffmpeg, mediaRoot, baseDirectory);
    J0kersMediaServer.Services.WindowsUrlAcl.EnsureFor(config);
    services = new J0kersMediaServer.Services.ServiceController(config, baseDirectory)
    {
        Subtitles = new J0kersMediaServer.Media.SubtitleManager(ffmpeg),
        Ffmpeg = ffmpeg,
    };
    services.StartServices();
    if (config.Control.Enabled)
    {
        control = new ControlApi(config, services, baseDirectory, auth, ffmpeg,
            requestShutdown: () => shutdown.TrySetResult());
        services.OnHlsActivity = control.NoteActivity; // streaming keeps the server up
        control.Start();

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
        var shutdownOnCloseDefault = config.Control.ShutdownOnClose;

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
                icon.Notify("j0kers Media Server",
                    $"Running in the background.\nIf you don't see the icon, click the ^ arrow on the taskbar.\n{dashboardUrl}");
                return true;
            }

            tray?.Dispose();   // also restores the hidden console window
            tray = null;
            config.MinimizeToTray = false;
            config.Control.ShutdownOnClose = shutdownOnCloseDefault;
            Log.Info("main", "background mode off — console restored");
            return false;
        }

        control.SetTrayMode = ApplyTrayMode;
        if (config.MinimizeToTray) ApplyTrayMode(true);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Startup failed: {ex.Message}");
    tray?.Dispose(); services?.Dispose(); control?.Dispose(); ffmpeg?.Dispose();
    return 1;
}

/// <summary>
/// URLs the dashboard is reachable on � see Services/NetworkInfo for the
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

services?.Dispose();
control?.Dispose();
ffmpeg?.Dispose();
Log.Info("main", "bye");
return 0;
