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

static void PrintUsage()
{
    Console.WriteLine("j0kers Media Server");
    Console.WriteLine("usage: j0kers-media-server [config.json] [options]");
    Console.WriteLine("  -h, --host <ip>          bind address / hostname (0.0.0.0 = all interfaces, localhost)");
    Console.WriteLine("  -r, --rtsp-port <port>   RTSP port (default 8554)");
    Console.WriteLine("  -H, --hls-port <port>    HLS port (default 8080)");
    Console.WriteLine("  -c, --control-port <port> control/dashboard port (default 9090)");
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

var mediaRoot = Path.GetFullPath(Path.IsPathRooted(config.Hls.MediaRoot)
    ? config.Hls.MediaRoot
    : Path.Combine(baseDirectory, config.Hls.MediaRoot));

try
{
    ffmpeg = new J0kersMediaServer.Media.FfmpegManager(config.Ffmpeg, mediaRoot, baseDirectory);
    J0kersMediaServer.Services.WindowsUrlAcl.EnsureFor(config);
    services = new J0kersMediaServer.Services.ServiceController(config, baseDirectory);
    services.StartServices();
    if (config.Control.Enabled)
    {
        control = new ControlApi(config, services, baseDirectory, ffmpeg);
        control.Start();

        if (config.Control.OpenDashboardOnStart)
        {
            var urls = DashboardUrls(control.BoundHost, config.Control.Port);
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
            else if (TryOpenBrowser(urls[0]))
            {
                Log.Info("main", $"dashboard opened: {urlList} (disable with control.openDashboardOnStart=false)");
            }
            else
            {
                Log.Info("main", $"no browser opener found on this system; dashboard: {urlList}");
            }
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Startup failed: {ex.Message}");
    services?.Dispose(); control?.Dispose(); ffmpeg?.Dispose();
    return 1;
}

/// <summary>
/// The URL the dashboard is reachable on, derived from the real bind
/// address. For 0.0.0.0 this is the machine's primary IP — found via a
/// routing lookup (no packets are sent), so Docker bridges and other
/// virtual interfaces don't pollute the answer.
/// </summary>
static string[] DashboardUrls(string bindAddress, int port)
{
    if (bindAddress is "127.0.0.1" or "localhost" or "::1")
        return new[] { $"http://localhost:{port}/" };
    if (bindAddress != "0.0.0.0")
        return new[] { $"http://{bindAddress}:{port}/" };
    try
    {
        using var probe = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);
        probe.Connect("8.8.8.8", 53); // routing decision only; UDP sends nothing on connect
        var ip = ((System.Net.IPEndPoint)probe.LocalEndPoint!).Address;
        return new[] { $"http://{ip}:{port}/" };
    }
    catch
    {
        return new[] { $"http://localhost:{port}/" };
    }
}

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

var shutdown = new TaskCompletionSource();
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

Log.Info("main", "ready — press Ctrl+C to stop");
await shutdown.Task;

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
