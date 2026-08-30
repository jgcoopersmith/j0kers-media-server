using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// "I close the browser and the server stops" — asserted against the real
/// executable, started the way a user starts it.
///
/// Everything else in this suite is a unit test, and that is exactly how this
/// bug survived seven fixes. It never lived in a function. It lived in the
/// wiring: which flags the shipped configuration carries, what the process
/// does with them on the way up, and what it does about a socket dropping. No
/// test that calls a method could see any of that, so every fix was verified
/// against a model of the server rather than the server, and the model was the
/// thing that was wrong.
///
/// So these start the built exe with a config file of its own, speak to it
/// over a socket, and wait for the process to die. Slow — a few seconds each,
/// because the close grace is three — and worth it: this is the only file here
/// that can fail for the reason the user's install kept failing.
///
/// The fault they are here to catch: the server opened a dashboard on its OWN
/// screen at startup, and that page held a live link exactly like anybody
/// else's. So the count of open pages never reached zero, and closing the
/// browser you were actually using was never the last page. On a server run
/// from another machine that window is invisible — which is why it reproduced
/// for the user every time and for me not once.
/// </summary>
public class ShutdownOnCloseTests
{
    // Three seconds of close grace, plus the sweep's one-second tick, plus
    // room for a loaded machine. Generous on purpose: a flaky test that gets
    // re-run until it passes is worth less than no test at all.
    private static readonly TimeSpan StopWithin = TimeSpan.FromSeconds(15);

    private const string SelfToken = "test-self-window-token";

    /// <summary>
    /// The regression test for the actual defect.
    ///
    /// The server opens a dashboard on its own machine at startup, and that
    /// window holds a live link like any other. On a server administered from
    /// somewhere else it sits on a screen nobody is at — so the count of open
    /// pages never fell to zero and closing the browser you were actually
    /// using was never the last page. That is the whole bug.
    ///
    /// So: the server's own window is open, somebody connects from elsewhere,
    /// and then that somebody closes their browser. The server must stop,
    /// even though its own window is still sitting there.
    /// </summary>
    [Fact]
    public async Task The_servers_own_window_does_not_hold_it_open_after_someone_else_leaves()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false,
                                                 selfToken: SelfToken);

        var mark = await server.ClaimSelfWindow(SelfToken);
        using var ownWindow = await server.OpenPage(cookie: mark);      // the server's own screen

        var somebodyElse = await server.OpenPage(expected: 2);          // a browser on another machine
        somebodyElse.Dispose();                                         // …which is then closed

        Assert.True(server.WaitForExit(StopWithin),
                    $"the server was still running {StopWithin.TotalSeconds:0}s after the last real page "
                    + "closed — its own startup window was holding it open, which is the original bug. Log:\n"
                    + server.ReadLog());
    }

    /// <summary>
    /// The other half of the same rule, and the reason the window cannot
    /// simply be ignored: on a machine somebody is actually sitting at, the
    /// window the server opened is the session. Nobody else has connected, so
    /// it holds the server open like any other page — and closing it stops
    /// the server, the way closing an application's window should.
    /// </summary>
    [Fact]
    public async Task The_servers_own_window_holds_it_open_while_it_is_the_only_page()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false,
                                                 selfToken: SelfToken);

        var mark = await server.ClaimSelfWindow(SelfToken);
        var ownWindow = await server.OpenPage(cookie: mark);

        // Comfortably past the close grace. Nobody else has been, so this
        // page is the session and the server belongs to whoever is looking
        // at it.
        await Task.Delay(6000);
        Assert.True(server.IsRunning,
                    "the server shut down while the only window open was the one it opened itself");

        ownWindow.Dispose();
        Assert.True(server.WaitForExit(StopWithin),
                    "closing the server's own window did not stop it");
    }

    /// <summary>
    /// The user's sentence, as an assertion: one page open, that page closes,
    /// the process is gone.
    /// </summary>
    [Fact]
    public async Task Closing_the_last_page_stops_the_server()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false);

        // Deliberately no assertion on the count here. How many pages the
        // server thinks are open is the subject of the test above; this one is
        // the sentence the owner of this server kept having to repeat, and it
        // should fail on the shutdown rather than on a number, so that what it
        // prints when it breaks is the complaint itself.
        var page = await server.OpenPage();

        page.Dispose();                       // the browser closes

        Assert.True(server.WaitForExit(StopWithin),
                    $"still running {StopWithin.TotalSeconds:0}s after the last page closed. Log:\n"
                    + server.ReadLog());
    }

    /// <summary>
    /// The opposite mistake, and the reason this was never fixed by ignoring
    /// pages that come from the server's own address: two people are looking,
    /// one of them closes their tab, and the server belongs to the other one.
    /// </summary>
    [Fact]
    public async Task Closing_one_of_two_pages_leaves_it_running()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false);

        var first = await server.OpenPage();
        var second = await server.OpenPage(expected: 2);
        Assert.Equal(2, await server.PagesOpen());

        first.Dispose();

        // Past the grace, with margin. If the grace is going to be misapplied
        // it has already happened by here.
        await Task.Delay(6000);
        Assert.True(server.IsRunning, "closing one of two open pages stopped the server");

        second.Dispose();
        Assert.True(server.WaitForExit(StopWithin), "closing the second page did not stop the server");
    }

    /// <summary>
    /// The other half of what was asked for, which had no test either: with
    /// background mode on, closing the last page must NOT stop the server.
    /// The requirement is conditional — "unless minimize to taskbar is
    /// checked" — so a fix that stopped the server harder every time would
    /// satisfy the complaint and break the feature.
    /// </summary>
    [Fact]
    public async Task Background_mode_survives_the_last_page_closing()
    {
        // The auto-open is deliberately off here, and only here. In background
        // mode the server really does open a dashboard at startup — that is
        // the one mode where doing so is harmless, because nothing is going to
        // shut down over it. Correct product behaviour, unusable in a test:
        // the first run of this opened two tabs in the machine's real browser.
        //
        // It also measured the defect, exactly, before it was written down as
        // a test. pagesOpen came back 2 when this test had opened one page.
        // The extra one was the server's own window — which in the mode above
        // is the difference between shutting down and running for ever.
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);

        var page = await server.OpenPage();
        Assert.Equal(1, await server.PagesOpen());

        page.Dispose();

        // Past both the close grace and the silence watch, which is the other
        // thing that could have taken it down.
        await Task.Delay(35000);
        Assert.True(server.IsRunning, "background mode did not survive the last page closing");
    }

    /// <summary>
    /// A server driven from its own console is a real way to use it, and
    /// closing that page has to stop it like any other. Stated as a test
    /// because the tempting one-line fix for everything above — ignore pages
    /// coming from 127.0.0.1 — passes every test before this one and breaks
    /// this one.
    /// </summary>
    [Fact]
    public async Task A_page_on_the_servers_own_machine_still_counts()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false);

        var page = await server.OpenPage();       // over loopback, like the console does
        Assert.Equal(1, await server.PagesOpen());

        page.Dispose();
        Assert.True(server.WaitForExit(StopWithin),
                    "a dashboard opened on the server's own machine no longer stops it when closed");
    }

    // ---------------------------------------------------------------- harness

    /// <summary>
    /// The built server, started as a process, with a config file of its own,
    /// on a port nothing else is using, and everything it does not need in
    /// order to answer these questions switched off.
    /// </summary>
    private sealed class TestServer : IDisposable
    {
        private readonly Process _process;
        private readonly string _dir;
        private readonly HttpClient _http;

        public int Port { get; }
        public bool IsRunning => !_process.HasExited;

        private TestServer(Process process, string dir, int port)
        {
            _process = process;
            _dir = dir;
            Port = port;
            // No proxy: a machine with one configured would otherwise send
            // loopback requests through it, and every test here would fail
            // for a reason that has nothing to do with the server.
            _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(20),
            };
        }

        public static async Task<TestServer> Start(bool openDashboardOnStart, bool backgroundMode,
                                                  string? selfToken = null)
        {
            var exe = Path.Combine(AppContext.BaseDirectory,
                                   OperatingSystem.IsWindows() ? "j0kers-media-server.exe"
                                                               : "j0kers-media-server");
            Assert.True(File.Exists(exe),
                        $"the server executable is not next to the tests ({exe}). "
                        + "These tests run the real thing; there is nothing here to run.");

            var dir = Path.Combine(Path.GetTempPath(), "claude",
                                   "j0kers-e2e-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            var port = FreePort();

            // Only the control API. RTSP, HLS, DLNA and discovery would bind
            // more ports and announce this test server on the network, which
            // is both slower and rude.
            var config = $$"""
            {
              "serverName": "shutdown test",
              "minimizeToTray": {{(backgroundMode ? "true" : "false")}},
              "rtsp":      { "enabled": false },
              "hls":       { "enabled": false },
              "discovery": { "enabled": false },
              "services":  { "dlna": false },
              "control": {
                "enabled": true,
                "bindAddress": "127.0.0.1",
                "port": {{port}},
                "openDashboardOnStart": {{(openDashboardOnStart ? "true" : "false")}}
              },
              "logging": { "level": "info", "toFile": true, "directory": "logs" }
            }
            """;
            var configPath = Path.Combine(dir, "server.json");
            File.WriteAllText(configPath, config);

            var startInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir,
            };
            startInfo.Environment["J0KERS_CONFIG"] = configPath;
            // The seam that lets a test play the window the server opens for
            // itself. Unset in every real run, where the token is random.
            if (selfToken is not null) startInfo.Environment["J0KERS_SELF_TOKEN"] = selfToken;

            var process = Process.Start(startInfo)
                          ?? throw new InvalidOperationException("could not start the server");

            var server = new TestServer(process, dir, port);
            try
            {
                await server.WaitUntilListening();
                return server;
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }

        private async Task WaitUntilListening()
        {
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited)
                    throw new InvalidOperationException(
                        $"the server exited during startup (code {_process.ExitCode}). Log:\n{ReadLog()}");
                try
                {
                    using var probe = await _http.GetAsync("api/status");
                    if (probe.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { /* not listening yet */ }
                catch (TaskCanceledException) { /* slow start */ }
                await Task.Delay(200);
            }
            throw new TimeoutException("the server never started listening. Log:\n" + ReadLog());
        }

        /// <summary>How many pages the server believes are holding it open.</summary>
        public async Task<int> PagesOpen()
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("api/status"));
            Assert.True(doc.RootElement.TryGetProperty("pagesOpen", out var pages),
                        "/api/status no longer reports pagesOpen, so this test can no longer see "
                        + "the thing it is asserting on");
            return pages.GetInt32();
        }

        /// <summary>
        /// A page, as far as the server is concerned: the live link an open
        /// dashboard holds. Disposing it is the browser closing — the socket
        /// drops, which is the signal the whole mechanism turns on.
        /// </summary>
        /// <summary>
        /// Does what the browser the server launches does: fetches the
        /// dashboard with the startup token on the URL, and comes away with
        /// the cookie that marks this page as the server's own window.
        /// </summary>
        public async Task<string> ClaimSelfWindow(string token)
        {
            using var r = await _http.GetAsync("?j=" + token);
            r.EnsureSuccessStatusCode();
            Assert.True(r.Headers.TryGetValues("Set-Cookie", out var cookies),
                        "the server did not mark this page as the window it opened for itself");
            var mark = cookies.FirstOrDefault(c => c.StartsWith("j0k-self=", StringComparison.Ordinal));
            Assert.NotNull(mark);
            return mark!.Split(';')[0];
        }

        public async Task<IDisposable> OpenPage(int expected = 1, string? cookie = null)
        {
            var page = await Page.Open(Port, cookie);
            // The link is counted when the handler runs, not when the request
            // is written, so give the server the moment in between rather than
            // racing it and then blaming it for the difference.
            await WaitFor(async () => await PagesOpen() >= expected,
                          $"the page was never counted as open (wanted {expected})");
            return page;
        }

        private static async Task WaitFor(Func<Task<bool>> condition, string failure)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (await condition()) return;
                await Task.Delay(100);
            }
            throw new TimeoutException(failure);
        }

        public bool WaitForExit(TimeSpan within) => _process.WaitForExit((int)within.TotalMilliseconds);

        public string ReadLog()
        {
            try
            {
                var logs = Path.Combine(_dir, "logs");
                if (!Directory.Exists(logs)) return "(no log)";
                // Share the handle. The server is usually still running when
                // a test wants to know why it failed, and it is holding this
                // file open — so File.ReadAllLines threw and the failure
                // message said nothing at exactly the moment it mattered.
                static IEnumerable<string> Read(string file)
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    var lines = new List<string>();
                    while (sr.ReadLine() is string line) lines.Add(line);
                    return lines;
                }
                return string.Join("\n", Directory.GetFiles(logs)
                                                  .SelectMany(Read)
                                                  .TakeLast(40));
            }
            catch (Exception ex) { return "(log unreadable: " + ex.Message + ")"; }
        }

        public void Dispose()
        {
            _http.Dispose();
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { _process.WaitForExit(5000); } catch { }
            _process.Dispose();
            // A test that leaves a config, a log and a queue file behind on
            // every run is its own kind of mess.
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    /// <summary>
    /// One held live link. Kept open with a raw socket rather than an
    /// HttpClient response, because closing it has to look to the server
    /// exactly like a browser going away: the connection drops mid-response,
    /// unannounced, with no beacon and no goodbye.
    /// </summary>
    private sealed class Page : IDisposable
    {
        private readonly TcpClient _socket;

        private Page(TcpClient socket) => _socket = socket;

        public static async Task<Page> Open(int port, string? cookie = null)
        {
            var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, port);
            var stream = socket.GetStream();
            var request = "GET /api/server/session HTTP/1.1\r\n"
                        + $"Host: 127.0.0.1:{port}\r\n"
                        + "Accept: text/event-stream\r\n"
                        + "Sec-Fetch-Site: same-origin\r\n"
                        + (cookie is null ? "" : $"Cookie: {cookie}\r\n")
                        + "Connection: keep-alive\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request));
            await stream.FlushAsync();

            // Read the response head, so the link is established rather than
            // merely requested by the time this returns.
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer);
            var head = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            if (!head.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
            {
                socket.Dispose();
                throw new InvalidOperationException("the server refused the live link: " + head.Trim());
            }
            return new Page(socket);
        }

        public void Dispose() => _socket.Dispose();
    }
}
