using J0kersMediaServer.Config;
using J0kersMediaServer.Control;
using J0kersMediaServer.Hls;
using J0kersMediaServer.Logging;
using J0kersMediaServer.Rtsp;

// Config resolution: explicit argument, then $J0KERS_CONFIG, then the first
// of ./server.json, ./config/server.json, <binary dir>/server.json that
// exists — so a bare `dotnet run` from the repo root just works.
var configPath = args.Length > 0 ? args[0]
    : Environment.GetEnvironmentVariable("J0KERS_CONFIG")
    ?? new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "server.json"),
        Path.Combine(Directory.GetCurrentDirectory(), "config", "server.json"),
        Path.Combine(AppContext.BaseDirectory, "server.json"),
    }.FirstOrDefault(File.Exists)
    ?? "server.json";

if (args.Length > 0 && args[0] is "--help" or "-h")
{
    Console.WriteLine("j0kers Media Server");
    Console.WriteLine("usage: j0kers-media-server [config.json]");
    Console.WriteLine("       config path defaults to $J0KERS_CONFIG, ./server.json, or ./config/server.json;");
    Console.WriteLine("       missing file = built-in defaults. See config/server.json for all options.");
    return 0;
}

ServerConfig config;
try
{
    config = ServerConfig.Load(File.Exists(configPath) ? configPath : null);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to load config: {ex.Message}");
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

RtspServer? rtsp = null;
HlsServer? hls = null;
ControlApi? control = null;

try
{
    if (config.Rtsp.Enabled)
    {
        rtsp = new RtspServer(config, baseDirectory);
        rtsp.Start();
    }
    if (config.Hls.Enabled)
    {
        hls = new HlsServer(config.Hls, baseDirectory);
        hls.Start();
    }
    if (config.Control.Enabled)
    {
        control = new ControlApi(config, rtsp, baseDirectory);
        control.Start();

        if (config.Control.OpenDashboardOnStart)
        {
            var url = $"http://localhost:{config.Control.Port}/";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true, // hands the URL to the OS default browser
                });
                Log.Info("main", $"dashboard opened at {url} (disable with control.openDashboardOnStart=false)");
            }
            catch (Exception ex)
            {
                Log.Warn("main", $"could not open browser ({ex.Message}); dashboard is at {url}");
            }
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Startup failed: {ex.Message}");
    rtsp?.Dispose(); hls?.Dispose(); control?.Dispose();
    return 1;
}

var shutdown = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.TrySetResult();

Log.Info("main", "ready — press Ctrl+C to stop");
await shutdown.Task;

Log.Info("main", "shutting down");
rtsp?.Dispose();
hls?.Dispose();
control?.Dispose();
return 0;
