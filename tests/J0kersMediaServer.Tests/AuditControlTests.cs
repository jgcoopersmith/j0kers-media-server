using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using J0kersMediaServer.Auth;
using J0kersMediaServer.Config;
using J0kersMediaServer.Control;
using J0kersMediaServer.Discovery;
using Xunit;
using Page = J0kersMediaServer.Tests.ShutdownOnCloseTests.Page;
using TestServer = J0kersMediaServer.Tests.ShutdownOnCloseTests.TestServer;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The control-API and startup findings from the 2026-09-17 audit: restart,
/// the HDHomeRun import, the background-mode notice, the log viewer, the
/// announcement switch, library search, channel import and pinning,
/// /api/image, /api/mounts, /player and the tray-mode toggle. Each test was
/// run against the code before its fix and seen to fail for the audit's
/// reason; the ones that could only be written against a new seam were run
/// with the fix taken out in place, which is said where it applies.
///
/// Three ways of running a server are used, each for a reason:
/// <see cref="TestServer"/> where the shared harness already fits; a launcher
/// of this file's own (<see cref="LaunchedServer"/>) where the test needs the
/// process itself - its arguments, its config file, its memory; and a
/// ControlApi built in this process (<see cref="InProcessServer"/>) where the
/// thing being asserted is a callback, which also keeps the background-mode
/// message box off the desktop of whoever runs the suite.
/// </summary>
public class AuditControlTests
{
    // ================================================================ [20]

    /// <summary>
    /// Restart from the dashboard, on a server Windows started at logon.
    ///
    /// The logon entry is <c>"exe" "…\j0kers Media Server\server.json" --autostart</c>,
    /// and the installer's folder has spaces in it. Restart handed each
    /// argument to Windows PowerShell's Start-Process as a separate element,
    /// and 5.1 joins those with a space and quotes none of them - measured on
    /// this machine (PowerShell 5.1.26100, cmd's echo standing in for the
    /// server), the arguments arrived as
    /// <c>…\j0kers Media Server\server.json --autostart</c>, unquoted. The new
    /// server saw the path as three arguments, said "Unknown option" and
    /// exited, and nothing came back.
    /// </summary>
    [Fact]
    public async Task Restart_brings_back_a_server_whose_config_path_has_spaces()
    {
        using var server = await LaunchedServer.Start(folder: "restart test with spaces",
                                                      configAsArgument: true, extraArgs: new[] { "--autostart" });

        using (var r = await server.Http.PostAsync("api/server/restart", null))
        {
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.IsSuccessStatusCode, $"the restart was refused: {(int)r.StatusCode} {body}");
        }
        Assert.True(server.Process.WaitForExit(20_000), "the old server never exited after the restart");

        // PowerShell waits for the old process, sleeps 800 ms and starts the
        // new one; the new one then has to bind. A minute is ample.
        var back = await server.WaitForAnyServer(TimeSpan.FromSeconds(60));
        Assert.True(back,
                    "no server came back on the same port within 60s of a restart, with the config path "
                    + "passed as an argument in a folder with spaces - the relaunch split it. Log:\n"
                    + server.ReadLog());
    }

    /// <summary>
    /// Pin, not proof: the quoting itself, checked against the parser Windows
    /// programs actually use (CommandLineToArgvW) for the awkward cases - a
    /// space, an embedded quote, a folder ending in a backslash, an empty
    /// argument. The test above is the one that failed before the fix.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\j0kers Media Server\server.json", "--autostart")]
    [InlineData(@"C:\folder with space\", "-c")]
    [InlineData("say \"hi\"", @"back\\slashes\")]
    [InlineData("", "x")]
    public void The_restart_command_line_splits_back_into_the_same_arguments(string a, string b)
    {
        if (!OperatingSystem.IsWindows()) return;
        var line = ControlApi.WindowsCommandLine(new[] { a, b });
        Assert.Equal(new[] { "prog.exe", a, b }, ParseLikeWindows("prog.exe " + line));
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true,
                                              CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static string[] ParseLikeWindows(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            return Enumerable.Range(0, argc)
                .Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)
                .ToArray();
        }
        finally { LocalFree(argv); }
    }

    // ================================================================ [21]

    /// <summary>
    /// Importing from an HDHomeRun could not work for any tuner there is. The
    /// endpoint insists the address is a LAN device, then read it with the
    /// providers' client - whose connect-time guard refuses every private
    /// address. Every import ended "refused to connect to 192.168.1.50: it
    /// resolves inside this network".
    ///
    /// No tuner here and no LAN traffic from a test: the client's own guard
    /// judges the address it was asked for, and the dial it approves is then
    /// made to a fake tuner on loopback.
    /// </summary>
    [Fact]
    public async Task A_tuner_on_the_lan_can_be_read()
    {
        using var tuner = new FakeTuner();
        var dials = 0;
        using var http = ControlApi.TunerClient(async (_, ct) =>
        {
            Interlocked.Increment(ref dials);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, tuner.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        });

        var lineup = await Media.Providers.HdhrTuner.ReadAsync("192.168.1.50", http);
        Assert.Equal("Fake HDHomeRun", lineup.Device.Name);
        Assert.Equal(new[] { "2.1", "5.1" }, lineup.Channels.Select(c => c.Number));
        Assert.Contains("192.168.1.50", tuner.Hosts);
        Assert.True(dials > 0, "the lineup came from somewhere other than the approved dial");
    }

    /// <summary>
    /// And the reverse: the tuner's client is not a way round the SSRF guard.
    /// This machine, the cloud metadata address and the internet are refused
    /// before any connection is made. The metadata address written as IPv6,
    /// and 0.0.0.0, were let through to the dial by the first version of the
    /// LAN rule (seen red), which is why IsLanAddress now reads an address as
    /// the one a socket would reach.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1:9090")]
    [InlineData("169.254.169.254")]
    [InlineData("8.8.8.8")]
    [InlineData("[::1]:9090")]
    [InlineData("[::ffff:169.254.169.254]")]   // the metadata address, spelled as IPv6
    [InlineData("[::ffff:127.0.0.1]:9090")]
    [InlineData("0.0.0.0:9090")]              // "this host" wherever connecting to it works
    public async Task The_tuner_client_refuses_anything_that_is_not_a_lan_device(string host)
    {
        var dials = 0;
        using var http = ControlApi.TunerClient((_, _) =>
        {
            Interlocked.Increment(ref dials);
            throw new InvalidOperationException("the guard let this through to the dial");
        });
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Media.Providers.HdhrTuner.ReadAsync(host, http));
        Assert.Contains("not a device on this network", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, dials);
    }

    // ================================================================ [52]

    /// <summary>
    /// Background mode, after the owner has had the dashboard open. Anything
    /// on the network can open the live link - it is outside the auth gate so
    /// the sign-in page can hold the server open - and dropping it used to
    /// arm the "still running in the background" notice exactly as a real
    /// dashboard closing does. A loop of that put a topmost message box on the
    /// server's desktop every time it was dismissed.
    ///
    /// The owner closing their own page must still raise it, or the notice is
    /// simply gone and this proves nothing.
    /// </summary>
    [Fact]
    public async Task An_anonymous_link_dropping_does_not_raise_the_background_notice()
    {
        using var server = await InProcessServer.Start(backgroundMode: true);
        var owner = await server.ClaimAndSignIn();

        var ownersPage = await Page.Open(server.Port, owner);      // the owner has had the dashboard open
        var stranger = await Page.Open(server.Port);               // no cookie, no key
        ownersPage.Dispose();                                      // the stranger's link still holds
        await Task.Delay(1500);
        stranger.Dispose();                                        // ...and now drops

        await Task.Delay(5000);                                    // the notice's grace, and margin
        Assert.True(server.Notices == 0,
                    $"an anonymous client opening and dropping the live link raised the background-mode notice "
                    + $"({server.Notices} time(s))");

        // the real thing still says so
        var again = await Page.Open(server.Port, owner);
        again.Dispose();
        await Task.Delay(5000);
        Assert.True(server.Notices == 1, $"the owner closing their dashboard raised {server.Notices} notice(s), not one");
    }

    /// <summary>
    /// The beacon half of the same thing. The beacon's own check was "from an
    /// address that has a page open" - which a stranger meets by opening one
    /// first. Only the owner's beacon (signed in, or the server's own window)
    /// may put a window on the server's screen.
    /// </summary>
    [Fact]
    public async Task An_anonymous_close_beacon_does_not_raise_the_background_notice()
    {
        using var server = await InProcessServer.Start(backgroundMode: true);
        var owner = await server.ClaimAndSignIn();

        var ownersPage = await Page.Open(server.Port, owner);
        var stranger = await Page.Open(server.Port);
        ownersPage.Dispose();
        await Task.Delay(1500);

        using (var beacon = new HttpRequestMessage(HttpMethod.Post, "api/server/closing")
               { Content = new StringContent(stranger.LinkId ?? "bye") })
        using (var r = await server.Http.SendAsync(beacon))
            Assert.True(r.IsSuccessStatusCode, $"the beacon was refused outright: {(int)r.StatusCode}");
        stranger.Dispose();

        await Task.Delay(5000);
        Assert.True(server.Notices == 0,
                    $"an anonymous close beacon raised the background-mode notice ({server.Notices} time(s))");
    }

    // ================================================================ [53]

    /// <summary>
    /// Background mode: the dashboard is closed, which arms the notice for
    /// three seconds later, and Exit is chosen from the tray inside those
    /// three seconds. The teardown ran on the tray thread and the tray's
    /// Dispose then waited up to two seconds to join it, while the notice's
    /// only check was a cancellation that Dispose - called last - had not
    /// made yet. So it fired: "still running in the background", on a server
    /// that was exiting.
    ///
    /// Written against the new BeginShutdown, and seen to fail with its body
    /// taken out in place (the notice fired a second time).
    /// </summary>
    [Fact]
    public async Task No_background_notice_once_shutdown_has_begun()
    {
        using var server = await InProcessServer.Start(backgroundMode: true);

        // it does fire, normally - otherwise the assertion below is empty
        (await Page.Open(server.Port)).Dispose();
        await Task.Delay(5000);
        Assert.True(server.Notices == 1, $"closing the dashboard raised {server.Notices} notice(s), not one");

        (await Page.Open(server.Port)).Dispose();   // closed again: armed for three seconds from now
        await Task.Delay(1500);                     // the link has ended; Exit is chosen
        server.Api.BeginShutdown();

        await Task.Delay(4500);
        Assert.True(server.Notices == 1,
                    "the background-mode notice fired after shutdown had begun - it told the user the server "
                    + "was still running while it was exiting");
    }

    // ================================================================ [57]

    /// <summary>
    /// The log window asks for the last few thousand lines of a file. It got
    /// them by reading the whole file into one string and splitting it - so a
    /// log left to grow (rotation size 0 is offered in the Config dialog)
    /// cost several times its size in the server's memory on every open, and
    /// above about a gigabyte ended in OutOfMemoryException.
    ///
    /// Measured on the server process itself: its peak working set across the
    /// one request, for a 128 MB file and 50 lines.
    /// </summary>
    [Fact]
    public async Task Reading_the_tail_of_a_large_log_does_not_load_the_whole_file()
    {
        using var server = await LaunchedServer.Start();
        var logs = Directory.CreateDirectory(Path.Combine(server.Dir, "logs")).FullName;
        const string name = "j0kers-20260101-000000.log";

        // 128 MiB of 128-byte lines, each "entry 0000001 xxxx…\n"
        const int lineCount = 1 << 20;
        using (var fs = new FileStream(Path.Combine(logs, name), FileMode.CreateNew, FileAccess.Write,
                                       FileShare.None, 1 << 20))
        {
            var pad = new string('x', 128 - "entry 0000000 ".Length - 1);
            for (var i = 1; i <= lineCount; i++)
                fs.Write(Encoding.ASCII.GetBytes($"entry {i:D7} {pad}\n"));
        }

        server.Process.Refresh();
        var before = server.Process.PeakWorkingSet64;

        using var r = await server.Http.GetAsync($"api/log/file?name={name}&max=50");
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"could not read the log file: {(int)r.StatusCode} {body}");

        server.Process.Refresh();
        var grew = server.Process.PeakWorkingSet64 - before;
        Assert.True(grew < 64L * 1024 * 1024,
                    $"reading 50 lines of a 128 MB log raised the server's peak working set by {grew / (1024 * 1024)} MB "
                    + "- it read the whole file to find its end");

        // and it is still the same answer: the last 50 pieces of the file
        // split on '\n', which for a file ending in a newline is the last 49
        // lines and the empty piece after them
        var doc = JsonDocument.Parse(body).RootElement;
        var expected = string.Concat(Enumerable.Range(lineCount - 48, 49)
            .Select(i => $"entry {i:D7} {new string('x', 128 - "entry 0000000 ".Length - 1)}\n"));
        Assert.True(doc.GetProperty("truncated").GetBoolean(), "a tail of a million-line file was not marked truncated");
        Assert.Equal(50, doc.GetProperty("shown").GetInt32());
        Assert.Equal(expected, doc.GetProperty("text").GetString());
    }

    /// <summary>
    /// Pin, not proof: reading from the end gives exactly the answer the old
    /// whole-file read did - the last N pieces of the file split on '\n' - for
    /// the awkward shapes: no newline at the end, CRLF, fewer lines than asked
    /// for, an empty file, a byte-order mark, and multi-byte characters across
    /// the 64 KB block boundary.
    /// </summary>
    [Fact]
    public void The_log_tail_is_the_same_answer_the_whole_file_gave()
    {
        var dir = NewDir();
        try
        {
            var bigLine = string.Concat(Enumerable.Repeat("ü€😀x", 9000));   // > 64 KB of UTF-8 per line
            var cases = new (string Name, byte[] Bytes)[]
            {
                ("empty", Array.Empty<byte>()),
                ("one", Encoding.UTF8.GetBytes("only line")),
                ("trailing", Encoding.UTF8.GetBytes("a\nb\nc\n")),
                ("no-trailing", Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(1, 120).Select(i => $"line {i}")))),
                ("crlf", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 90).Select(i => $"line {i}\r\n")))),
                ("bom-short", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("x\ny\n")).ToArray()),
                ("bom-long", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(
                    string.Concat(Enumerable.Range(1, 80).Select(i => $"é{i}\n")))).ToArray()),
                ("wide", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 60).Select(i => $"{i} {bigLine}\n")))),
            };
            foreach (var (name, bytes) in cases)
            {
                var path = Path.Combine(dir, name + ".log");
                File.WriteAllBytes(path, bytes);
                foreach (var take in new[] { 50, 51, 2000 })
                {
                    string whole;
                    using (var sr = new StreamReader(path)) whole = sr.ReadToEnd();
                    var lines = whole.Split('\n');
                    var truncated = lines.Length > take;
                    var tail = truncated ? lines[^take..] : lines;

                    var got = ControlApi.ReadLogTail(path, take);
                    Assert.True(string.Join("\n", tail) == got.Text, $"{name}, max {take}: the text differs");
                    Assert.True(tail.Length == got.Shown, $"{name}, max {take}: shown {got.Shown}, expected {tail.Length}");
                    Assert.True(truncated == got.Truncated, $"{name}, max {take}: truncated {got.Truncated}, expected {truncated}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ================================================================ [58]

    /// <summary>
    /// The Config dialog's announcement switch is applied before it is saved,
    /// "so a responder that can't take its ports is reported rather than
    /// persisted as on". It never was: every responder caught its own failure,
    /// logged a warning and returned, so Restart never threw and the dialog
    /// saved "on" with nothing announcing.
    ///
    /// Only the UDP probe is enabled here, on a port that cannot exist, so the
    /// failure is certain and nothing is bound or sent - no announcement on
    /// the network from a test.
    /// </summary>
    [Fact]
    public void Turning_announcement_on_reports_it_when_nothing_could_start()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "claude", "j0kers-audit-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var config = new DiscoveryConfig { Enabled = true, Mdns = false, Ssdp = false, UdpProbe = true, UdpProbePort = 70000 };
            using var discovery = new DiscoveryService(config, "audit test", 9, dir);
            var ex = Record.Exception(() => discovery.Restart());
            Assert.True(ex is not null,
                        "every enabled responder failed to start and Restart said nothing - the dialog would save "
                        + "announcement as on while nothing announces");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ============================================================ [59][60]

    /// <summary>
    /// Library search stops at 300 results - but only checked that between
    /// folders. One folder of 20,000 photos came back whole, each one stat'ed,
    /// and was reported as complete (truncated: false).
    /// </summary>
    [Fact]
    public async Task Library_search_keeps_its_cap_inside_one_folder()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        var photos = Directory.CreateDirectory(Path.Combine(server.Dir, "library", "photos")).FullName;
        for (var i = 1; i <= 400; i++) File.WriteAllBytes(Path.Combine(photos, $"IMG_{i:D4}.jpg"), Array.Empty<byte>());
        using (var add = await server.Http.PostAsync("api/library", TestServer.Json(new { folder = photos })))
            Assert.True(add.IsSuccessStatusCode, $"could not add the library folder: {(int)add.StatusCode}");

        using var r = await server.Http.GetAsync("api/library/search?q=img");
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"search failed: {(int)r.StatusCode} {body}");
        var doc = JsonDocument.Parse(body).RootElement;
        var count = doc.GetProperty("files").GetArrayLength() + doc.GetProperty("folders").GetArrayLength();
        Assert.True(count <= 300, $"a search capped at 300 returned {count} results from one folder");
        Assert.True(doc.GetProperty("truncated").GetBoolean(),
                    "400 matches were cut to the cap and the answer said it was complete");
    }

    /// <summary>
    /// The deadline half, which a local disk is too fast to show: the clock
    /// here moves a second every time the walk looks at it, so five looks is
    /// the five seconds a network drive would spend on a big folder. The walk
    /// used to look once per folder, and so finished a single folder of fifty
    /// matches whatever the clock said - and reported it complete.
    ///
    /// Written against the extracted walk, and seen to fail with the check
    /// inside the file loop taken out in place.
    /// </summary>
    [Fact]
    public void Library_search_keeps_its_deadline_inside_one_folder()
    {
        var root = NewDir();
        try
        {
            for (var i = 1; i <= 50; i++) File.WriteAllBytes(Path.Combine(root, $"clip {i:D2}.mp4"), Array.Empty<byte>());
            var t0 = DateTime.UtcNow;
            var looks = 0;
            DateTime Clock() => t0.AddSeconds(Interlocked.Increment(ref looks));

            var slow = ControlApi.WalkLibrary(new[] { root }, new[] { "clip" }, cap: 300, deadline: t0.AddSeconds(5), now: Clock);
            Assert.True(slow.TimedOut && slow.Files.Count < 50,
                        $"with a clock that passes the 5-second deadline after five looks, the walk looked {looks} time(s) "
                        + $"in a folder of 50 files, returned {slow.Files.Count} of them and said timedOut={slow.TimedOut}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The cap inside the loop over one folder's subfolders: ten matching
    /// folders against a cap of five. (The loop over its files is the e2e test
    /// above.) Seen to fail with the check taken out in place.
    /// </summary>
    [Fact]
    public void Library_search_keeps_its_cap_among_one_folders_subfolders()
    {
        var root = NewDir();
        try
        {
            for (var i = 1; i <= 10; i++) Directory.CreateDirectory(Path.Combine(root, $"clip folder {i}"));
            var capped = ControlApi.WalkLibrary(new[] { root }, new[] { "clip" }, cap: 5, deadline: DateTime.UtcNow.AddMinutes(1));
            Assert.True(capped.Folders.Count + capped.Files.Count <= 5 && capped.Truncated,
                        $"a cap of 5 returned {capped.Folders.Count} matching folders from one folder, truncated={capped.Truncated}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ================================================================ [61]

    /// <summary>
    /// Adding a channel is an administrator's - POST /api/channels refuses an
    /// editor. The import endpoint does the same thing with any URL at all
    /// and was left at Edit, so an editor could add rtsp://some-camera and
    /// then start it. The channels card, and so the tuner and start/stop
    /// behind it, is an administrator's too.
    ///
    /// The URL is loopback port 9: nothing is fetched whichever way this goes.
    /// </summary>
    [Fact]
    public async Task An_editor_cannot_add_or_run_channels_through_the_import()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg);
        using var admin = await server.SignedInAs("admin");
        using var editor = await server.SignedInAs("edit");

        var batch = TestServer.Json(new { channels = new[] { new { name = "cam", url = "rtsp://127.0.0.1:9/cam" } } });
        using (var r = await editor.PostAsync("api/channels/import", batch))
        {
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.Forbidden,
                        $"an edit account imported a channel of its own choosing: {(int)r.StatusCode} {body}");
        }
        foreach (var (method, path) in new[] { ("POST", "api/channels/start?name=cam"), ("POST", "api/channels/stop?name=cam"),
                                               ("POST", "api/channels/restart?name=cam"), ("GET", "api/tuner?host=127.0.0.1") })
        {
            using var r = await editor.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
            Assert.True(r.StatusCode == HttpStatusCode.Forbidden,
                        $"an edit account was let through to {method} /{path}: {(int)r.StatusCode}");
        }

        // and the administrator still can
        using (var r = await admin.PostAsync("api/channels/import",
                   TestServer.Json(new { channels = new[] { new { name = "cam2", url = "rtsp://127.0.0.1:9/cam" } } })))
            Assert.True(r.IsSuccessStatusCode, $"the administrator could not import: {(int)r.StatusCode}");
    }

    // ================================================================ [62]

    /// <summary>
    /// A pinned free-TV channel is saved as a URL through this server's own
    /// proxy, with the control port of the day written into it. Move the
    /// control port and restart, and ffmpeg dialled the old one: refused,
    /// every time, until the channel was removed and pinned again. The port is
    /// now put right when the channel starts, as the scheme already was.
    ///
    /// Written against the new OwnUrlFor, and seen to fail with the port
    /// rewrite taken out in place.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:9090/api/tv/watch?provider=pluto&id=abc&s=sig", "http", 9191,
                "http://127.0.0.1:9191/api/tv/watch?provider=pluto&id=abc&s=sig")]
    [InlineData("http://127.0.0.1:9090/api/tv/watch?provider=pluto&id=a%20b%2Fc&s=sig", "http", 9191,
                "http://127.0.0.1:9191/api/tv/watch?provider=pluto&id=a%20b%2Fc&s=sig")]   // escaping kept
    [InlineData("http://127.0.0.1:9090/api/tv/watch?provider=pluto&id=abc&s=sig", "https", 9090,
                "https://127.0.0.1:9090/api/tv/watch?provider=pluto&id=abc&s=sig")]        // TLS, as before
    [InlineData("http://localhost:8000/api/something", "http", 9191,
                "http://localhost:8000/api/something")]                                     // not ours to move
    [InlineData("http://192.168.1.50:5004/auto/v5.1", "http", 9191,
                "http://192.168.1.50:5004/auto/v5.1")]                                      // a tuner
    public void A_pinned_channel_follows_the_control_port(string stored, string scheme, int livePort, string used)
    {
        Assert.Equal(used, Media.FfmpegManager.OwnUrlFor(stored, scheme, livePort));
    }

    /// <summary>
    /// The same, end to end: a channel stored with a port nothing listens on
    /// is started, and ffmpeg's request must arrive at the port this server
    /// is actually on. The server is claimed, so the request is refused at
    /// the auth gate (the signature is made up) and nothing is fetched from
    /// any provider; the access log line is the proof it arrived.
    /// </summary>
    [Fact]
    public async Task A_pinned_channel_reaches_this_server_after_the_port_moved()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg);
        using var admin = await server.SignedInAs("admin");

        var stalePort = FreePort();   // what the pin was saved with; nothing listens there now
        var stored = $"http://127.0.0.1:{stalePort}/api/tv/watch?provider=none-such&id=x&s=made-up";
        using (var add = await admin.PostAsync("api/channels", TestServer.Json(new { name = "pinned", url = stored })))
            Assert.True(add.IsSuccessStatusCode, $"could not add the channel: {(int)add.StatusCode} {await add.Content.ReadAsStringAsync()}");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var arrived = false;
        while (!arrived && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            arrived = WholeLog(server.Dir).Contains("GET /api/tv/watch", StringComparison.Ordinal);
        }
        Assert.True(arrived,
                    $"the channel's ffmpeg never reached this server's /api/tv/watch - it was sent to the stored port "
                    + $"{stalePort}. Log:\n" + server.ReadLog());
    }

    private static string WholeLog(string serverDir)
    {
        var sb = new StringBuilder();
        foreach (var file in Directory.GetFiles(Path.Combine(serverDir, "logs"), "*.log"))
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            sb.Append(sr.ReadToEnd());
        }
        return sb.ToString();
    }

    // ================================================================ [63]

    /// <summary>
    /// /api/image serves from the dashboard's own origin. An SVG is a document
    /// that can carry script, and opened on its own ("open image in new tab")
    /// that script ran as the viewer - with their cookie, able to call the API.
    /// Anyone who can drop a file into a shared folder could put one there.
    /// </summary>
    [Fact]
    public async Task An_image_is_served_so_that_it_cannot_run_script()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        var pics = Directory.CreateDirectory(Path.Combine(server.Dir, "pics")).FullName;
        var svg = Path.Combine(pics, "evil.svg");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>fetch('/api/users')</script></svg>");

        using var r = await server.Http.GetAsync("api/image?path=" + Uri.EscapeDataString(svg));
        Assert.True(r.IsSuccessStatusCode, $"the image was not served: {(int)r.StatusCode}");

        var csp = r.Headers.TryGetValues("Content-Security-Policy", out var c) ? string.Join(";", c) : "";
        Assert.True(csp.Contains("sandbox", StringComparison.Ordinal) && csp.Contains("default-src 'none'", StringComparison.Ordinal),
                    $"an SVG was served from the dashboard's origin without a policy that stops its script (CSP: '{csp}')");
        Assert.True(r.Headers.TryGetValues("X-Content-Type-Options", out var n) && n.Contains("nosniff"),
                    "images are served without X-Content-Type-Options: nosniff");
    }

    // ================================================================ [64]

    /// <summary>
    /// A mount whose "source" is JSON null: nullable annotations are not
    /// enforced by the deserialiser, so Source was null and the switch on it
    /// threw - a 500 "internal error" where every other bad field is a 400
    /// that says what is wrong.
    /// </summary>
    [Fact]
    public async Task A_mount_with_a_null_source_is_a_bad_request()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var body = new StringContent("{\"path\":\"/x\",\"source\":null}", Encoding.UTF8, "application/json");
        using var r = await server.Http.PostAsync("api/mounts", body);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest && text.Contains("source", StringComparison.Ordinal),
                    $"a null mount source was answered {(int)r.StatusCode} {text}");
    }

    // ================================================================ [68]

    /// <summary>
    /// /player plays "a path on this server". A browser reads "/\host/x" as
    /// "//host/x" - another site - and drops tabs and newlines from a URL
    /// before reading it, so "/&lt;tab&gt;/host/x" is the same thing. Each put
    /// someone else's video on this server's player page.
    /// </summary>
    [Theory]
    [InlineData("/\\evil.example/clip.mp4")]
    [InlineData("/\t/evil.example/clip.mp4")]
    [InlineData("/\n/evil.example/clip.mp4")]
    [InlineData("/\\\\evil.example/clip.mp4")]
    public async Task The_player_refuses_a_path_a_browser_reads_as_another_site(string src)
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var r = await server.Http.GetAsync("player?src=" + Uri.EscapeDataString(src));
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest,
                    $"/player accepted src {JsonSerializer.Serialize(src)}, which a browser loads from another site: {(int)r.StatusCode}");

        // an ordinary path of ours still plays
        using var ok = await server.Http.GetAsync("player?src=" + Uri.EscapeDataString("/vod-film/index.m3u8"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    // ================================================================ [80]

    /// <summary>
    /// server.json says control.shutdownOnClose = false, and startup honours
    /// that. Unticking "Minimize to the system tray" in the Config dialog then
    /// set it to true regardless, so closing the dashboard stopped a server
    /// configured not to stop - until the next restart put it back.
    ///
    /// And the default is untouched: with nothing in server.json, leaving
    /// background mode still means closing the page stops the server.
    /// </summary>
    [Fact]
    public async Task Leaving_background_mode_keeps_a_configured_shutdown_on_close()
    {
        using (var server = await LaunchedServer.Start(backgroundMode: true, controlExtra: "\"shutdownOnClose\": false,"))
        {
            await server.LeaveBackgroundMode();
            Assert.True(await server.StopsOnClose() == false,
                        "leaving background mode overwrote control.shutdownOnClose=false from server.json");
        }

        using (var server = await LaunchedServer.Start(backgroundMode: true))
        {
            await server.LeaveBackgroundMode();
            Assert.True(await server.StopsOnClose(),
                        "with nothing configured, leaving background mode no longer makes closing the page stop the server");
        }
    }

    // ============================================================== harness

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string NewDir(string? sub = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "claude", "j0kers-audit-" + Guid.NewGuid().ToString("N")[..8]);
        if (sub is not null) dir = Path.Combine(dir, sub);
        return Directory.CreateDirectory(dir).FullName;
    }

    /// <summary>
    /// The built server as a process, with the config this test wants and the
    /// process object in hand - what the shared harness keeps to itself.
    /// Loopback only, discovery off, nothing announced.
    /// </summary>
    internal sealed class LaunchedServer : IDisposable
    {
        public Process Process { get; }
        public string Dir { get; }
        public string Root { get; }
        public int Port { get; }
        public HttpClient Http { get; }

        private LaunchedServer(Process process, string root, string dir, int port)
        {
            Process = process;
            Root = root;
            Dir = dir;
            Port = port;
            Http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(20),
            };
            Http.DefaultRequestHeaders.Add("X-J0kers-CSRF", "1");
        }

        /// <param name="folder">A folder under the test's own, for the config and state - spaces allowed.</param>
        /// <param name="configAsArgument">Name the config on the command line, as the logon entry does, rather than in J0KERS_CONFIG.</param>
        /// <param name="controlExtra">Extra members for the "control" object, each followed by a comma.</param>
        public static async Task<LaunchedServer> Start(bool backgroundMode = false, string? folder = null,
                                                       bool configAsArgument = false, string[]? extraArgs = null,
                                                       string controlExtra = "")
        {
            var exe = Path.Combine(AppContext.BaseDirectory,
                                   OperatingSystem.IsWindows() ? "j0kers-media-server.exe" : "j0kers-media-server");
            Assert.True(File.Exists(exe), $"the server executable is not next to the tests ({exe})");

            var root = NewDir();
            var dir = folder is null ? root : Directory.CreateDirectory(Path.Combine(root, folder)).FullName;
            var port = FreePort();
            var config = $$"""
            {
              "serverName": "audit test",
              "minimizeToTray": {{(backgroundMode ? "true" : "false")}},
              "rtsp":      { "enabled": false },
              "hls":       { "enabled": false },
              "discovery": { "enabled": false, "dlna": false },
              "services":  { "dlna": false },
              "control": {
                {{controlExtra}}
                "enabled": true,
                "bindAddress": "127.0.0.1",
                "port": {{port}},
                "openDashboardOnStart": false
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
            startInfo.Environment.Remove("J0KERS_CONFIG");
            startInfo.Environment.Remove("J0KERS_SELF_TOKEN");
            if (configAsArgument) startInfo.ArgumentList.Add(configPath);
            else startInfo.Environment["J0KERS_CONFIG"] = configPath;
            foreach (var a in extraArgs ?? Array.Empty<string>()) startInfo.ArgumentList.Add(a);

            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start the server");
            var server = new LaunchedServer(process, root, dir, port);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(40);
                while (true)
                {
                    if (process.HasExited)
                        throw new InvalidOperationException($"the server exited during startup (code {process.ExitCode}). Log:\n{server.ReadLog()}");
                    if (await server.Answers()) return server;
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("the server never started listening. Log:\n" + server.ReadLog());
                    await Task.Delay(200);
                }
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }

        private async Task<bool> Answers()
        {
            try
            {
                using var probe = await Http.GetAsync("api/status");
                return probe.IsSuccessStatusCode;
            }
            catch (HttpRequestException) { return false; }
            catch (TaskCanceledException) { return false; }
        }

        /// <summary>Whether any server - this process or a successor - answers on this port within the time given.</summary>
        public async Task<bool> WaitForAnyServer(TimeSpan within)
        {
            var deadline = DateTime.UtcNow + within;
            while (DateTime.UtcNow < deadline)
            {
                if (await Answers()) return true;
                await Task.Delay(250);
            }
            return false;
        }

        public async Task LeaveBackgroundMode()
        {
            Assert.True(await StopsOnClose() == false, "background mode was not on to begin with");
            using var r = await Http.PostAsync("api/settings", TestServer.Json(new { minimizeToTray = false }));
            Assert.True(r.IsSuccessStatusCode, $"could not turn background mode off: {(int)r.StatusCode}");
        }

        public async Task<bool> StopsOnClose()
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("api/status"));
            return doc.RootElement.GetProperty("stopsOnClose").GetBoolean();
        }

        public string ReadLog()
        {
            try
            {
                var logs = Path.Combine(Dir, "logs");
                if (!Directory.Exists(logs)) return "(no log)";
                var lines = new List<string>();
                foreach (var file in Directory.GetFiles(logs, "j0kers.log"))
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    while (sr.ReadLine() is string line) lines.Add(line);
                }
                return string.Join("\n", lines.TakeLast(40));
            }
            catch (Exception ex) { return "(log unreadable: " + ex.Message + ")"; }
        }

        public void Dispose()
        {
            Http.Dispose();
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
            try { Process.WaitForExit(5000); } catch { }
            Process.Dispose();
            // A restart leaves a successor that is nobody's child. It is found
            // by its command line, which names this test's own folder and no
            // other process's.
            KillServersMentioning(Root);
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// An HDHomeRun as far as HTTP can tell: /discover.json and /lineup.json
    /// on a loopback port. It records the Host each request named, which is
    /// the address the client believed it was talking to.
    /// </summary>
    private sealed class FakeTuner : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; }
        public System.Collections.Concurrent.ConcurrentBag<string> Hosts { get; } = new();

        public FakeTuner()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var head = new StringBuilder();
                        var buffer = new byte[4096];
                        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            var n = await stream.ReadAsync(buffer);
                            if (n == 0) return;
                            head.Append(Encoding.ASCII.GetString(buffer, 0, n));
                        }
                        var lines = head.ToString().Split("\r\n");
                        var path = lines[0].Split(' ')[1];
                        var host = lines.FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
                        if (host is not null) Hosts.Add(host["Host:".Length..].Trim());

                        var body = path switch
                        {
                            "/discover.json" => "{\"FriendlyName\":\"Fake HDHomeRun\",\"ModelNumber\":\"HDTC-2US\",\"TunerCount\":2}",
                            "/lineup.json" => "[{\"GuideNumber\":\"2.1\",\"GuideName\":\"KTWO\",\"URL\":\"http://192.168.1.50:5004/auto/v2.1\"},"
                                            + "{\"GuideNumber\":\"5.1\",\"GuideName\":\"KFIV\",\"URL\":\"http://192.168.1.50:5004/auto/v5.1\",\"HD\":1}]",
                            _ => null,
                        };
                        var bytes = Encoding.UTF8.GetBytes(body ?? "");
                        var status = body is null ? "404 Not Found" : "200 OK";
                        var response = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\n"
                                     + "Connection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                        await stream.WriteAsync(bytes);
                    }
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    /// <summary>
    /// Stops any j0kers-media-server whose command line contains this folder.
    /// The folder is a fresh GUID-named one under %TEMP%\claude, so the live
    /// install - whose command line names its own folder - cannot match.
    /// </summary>
    private static void KillServersMentioning(string folder)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var query = "Get-CimInstance Win32_Process -Filter \"Name='j0kers-media-server.exe'\" | "
                      + "Where-Object { $_.CommandLine -and $_.CommandLine.Contains('" + folder.Replace("'", "''") + "') } | "
                      + "ForEach-Object { $_.ProcessId }";
            var psi = new ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", query }) psi.ArgumentList.Add(a);
            using var ps = Process.Start(psi)!;
            var output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(30_000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(line, out var pid)) continue;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch { /* already gone */ }
            }
        }
        catch { /* best effort; the folder delete below says if something is still holding it */ }
    }

    /// <summary>
    /// A ControlApi in this process, listening on loopback, with no streaming
    /// services behind it. For assertions about what it calls back - the
    /// background-mode notice is a delegate here, not a message box.
    /// </summary>
    internal sealed class InProcessServer : IDisposable
    {
        public string Dir { get; }
        public int Port { get; }
        public ControlApi Api { get; }
        public HttpClient Http { get; }

        private int _notices;
        public int Notices => Volatile.Read(ref _notices);

        private InProcessServer(string dir, int port, ControlApi api)
        {
            Dir = dir;
            Port = port;
            Api = api;
            Http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(20),
            };
            Http.DefaultRequestHeaders.Add("X-J0kers-CSRF", "1");
        }

        public static Task<InProcessServer> Start(bool backgroundMode)
        {
            var dir = NewDir();
            var port = FreePort();
            var configPath = Path.Combine(dir, "server.json");
            File.WriteAllText(configPath, $$"""
            {
              "serverName": "audit in-process",
              "rtsp":      { "enabled": false },
              "hls":       { "enabled": false, "mediaRoot": "media" },
              "discovery": { "enabled": false, "dlna": false },
              "control": {
                "enabled": true, "bindAddress": "127.0.0.1", "port": {{port}},
                "openDashboardOnStart": false, "shutdownOnClose": {{(backgroundMode ? "false" : "true")}}
              },
              "logging": { "level": "info", "toFile": false }
            }
            """);
            var config = ServerConfig.Load(configPath);
            var services = new Services.ServiceController(config, dir);
            var auth = new AuthService(new UserStore(dir), () => config.Control.AuthToken, dir);
            var api = new ControlApi(config, services, dir, auth, new MediaLink(dir), ffmpeg: null, requestShutdown: () => { });
            var server = new InProcessServer(dir, port, api);
            api.OnDashboardClosed = () => Interlocked.Increment(ref server._notices);
            api.Start();
            return Task.FromResult(server);
        }

        /// <summary>Creates the administrator and signs in as it, returning the session cookie a browser would hold.</summary>
        public async Task<string> ClaimAndSignIn()
        {
            const string pass = "test-admin-passphrase-9";
            using (var setup = await Http.PostAsync("api/auth/setup", TestServer.Json(new { username = "owner", password = pass })))
                Assert.True(setup.IsSuccessStatusCode, $"could not claim the server: {(int)setup.StatusCode}");
            using var login = await Http.PostAsync("api/auth/login", TestServer.Json(new { username = "owner", password = pass }));
            Assert.True(login.IsSuccessStatusCode, $"could not sign in: {(int)login.StatusCode}");
            return login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])
                        .First(c => c.StartsWith("j0kers_session=", StringComparison.Ordinal) && !c.EndsWith('='));
        }

        public void Dispose()
        {
            Http.Dispose();
            try { Api.Dispose(); } catch { }
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }
}
