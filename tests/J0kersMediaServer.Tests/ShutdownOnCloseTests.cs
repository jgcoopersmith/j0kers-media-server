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
    /// The sign-in page is not somebody else.
    ///
    /// It holds a live link like any page, but it is served before the cookie
    /// that marks the window the server opened for itself — so it arrived
    /// unmarked and was counted as a browser on another machine. That is the
    /// condition under which the server's own window stops holding it open,
    /// and it was being met one second after startup, on the server's own
    /// console, every single time anybody signed in.
    ///
    /// What followed: the sign-in link ends when the dashboard replaces it,
    /// the count falls to zero with a dashboard open and visible, and the
    /// server shuts down three seconds later. Twice in one afternoon on this
    /// machine, the second time discarding a queue of 88 files.
    /// </summary>
    [Fact]
    public async Task The_sign_in_page_does_not_disown_the_servers_own_window()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false,
                                                 selfToken: SelfToken);

        var mark = await server.ClaimSelfWindow(SelfToken);

        // Sign-in first, exactly as a browser does it, then the dashboard the
        // server opened for itself.
        var signIn = await server.OpenPage(query: "stage=login");
        using var ownWindow = await server.OpenPage(expected: 2, cookie: mark);

        // The sign-in page goes when the dashboard replaces it. The window
        // the server opened is still there, and is still somebody looking at
        // this server.
        signIn.Dispose();

        await Task.Delay(6000);   // comfortably past the close grace
        Assert.True(server.IsRunning,
                    "the server shut down after the sign-in page closed, leaving its own dashboard "
                    + "open and visible — the sign-in link had been counted as somebody on another "
                    + "machine. Log:\n" + server.ReadLog());
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

    /// <summary>
    /// The laptop lid closes mid-conversion.
    ///
    /// The server ends every live link itself after twenty seconds and counts
    /// a page as present only if it reconnects within the grace. A suspended
    /// browser cannot reconnect - a sleeping laptop, a locked phone, a dropped
    /// network - and from here that is indistinguishable from a closed tab. The
    /// sweep treated it as one, and stopped the server without asking whether
    /// it was converting: every encode in flight was killed about three
    /// seconds after the page went quiet.
    ///
    /// Nobody said stop. Going quiet is not an instruction, and work in
    /// progress keeps the server up, exactly as it does on the silence path.
    /// </summary>
    [Fact]
    public async Task A_page_that_goes_silent_mid_conversion_does_not_stop_the_server()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false, ffmpegPath: ffmpeg);

        // The page first, THEN the slow work of making a source. Starting the
        // server already counts as a dashboard having been seen, so a server
        // left with no page while the clip is made can stop underneath the
        // test - which is this very bug, just not the part being tested.
        var page = await server.OpenPage();
        var source = TestFfmpeg.SlowSource(ffmpeg, SourcePath(server));
        await StartConversion(server, source);

        page.Dispose();                       // the lid closes: the link drops, no beacon

        // Well past the grace and the sweep's tick. Before the fix the
        // server was gone by about three seconds.
        await Task.Delay(9000);
        Assert.True(server.IsRunning,
                    "a page that went quiet mid-conversion stopped the server and killed the encode. Log:\n"
                    + server.ReadLog());

        // And the premise still held when it was checked: had the conversion
        // simply finished, the server staying up would prove nothing.
        Assert.True(await IsConverting(server, source),
                    "the conversion was no longer running when the test checked, so this run proves nothing - "
                    + "the source needs to be longer on this machine");
    }

    /// <summary>
    /// The other half, which the fix must not break: when the owner really does
    /// close the page, the server stops - conversion or not. The tab says so on
    /// its way out (the pagehide beacon), and that is an instruction.
    ///
    /// Without this test the fix above could be "never stop while converting",
    /// which passes it and brings back a server that ignores being closed.
    /// </summary>
    [Fact]
    public async Task Closing_the_page_mid_conversion_still_stops_the_server()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false, ffmpegPath: ffmpeg);

        var page = await server.OpenPage();   // before the slow part; see the test above
        var source = TestFfmpeg.SlowSource(ffmpeg, SourcePath(server));
        await StartConversion(server, source);

        await server.SendCloseBeacon(page.LinkId);   // the tab is closing, and names its link
        page.Dispose();

        Assert.True(server.WaitForExit(StopWithin),
                    $"still running {StopWithin.TotalSeconds:0}s after the owner closed the page. Log:\n"
                    + server.ReadLog());
    }

    /// <summary>
    /// Two pages open; the owner closes one of them - it says so, correctly -
    /// and a moment later the laptop holding the other goes to sleep.
    ///
    /// The first close was not the last page, so it decided nothing. An
    /// earlier version of the fix recorded "a close was announced" with a
    /// ten-second window and no idea which page it came from, so the sleep
    /// that followed inside that window was read as the owner's decision and
    /// the conversion was killed. The announcement belongs to one link.
    /// </summary>
    [Fact]
    public async Task A_close_on_another_page_does_not_turn_a_later_sleep_into_a_stop()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false, ffmpegPath: ffmpeg);

        var closing = await server.OpenPage();
        var sleeping = await server.OpenPage(expected: 2);
        var source = TestFfmpeg.SlowSource(ffmpeg, SourcePath(server));
        await StartConversion(server, source);

        await server.SendCloseBeacon(closing.LinkId);   // one tab closes, and says so
        closing.Dispose();
        await Task.Delay(3000);                          // the other still holds
        sleeping.Dispose();                              // ...and then goes quiet

        await Task.Delay(9000);
        Assert.True(server.IsRunning,
                    "a close announced by one page turned a later sleep on another page into a stop. Log:\n"
                    + server.ReadLog());
        Assert.True(await IsConverting(server, source), "the conversion was not still running when checked");
    }

    /// <summary>
    /// The owner closes the page, but the beacon loses the race with its own
    /// socket and arrives just after the link has gone. It is still the same
    /// close, and it must still stop the server.
    ///
    /// Before, a beacon only counted if its page was still connected when it
    /// landed, so a late one was dropped and the owner's close was treated as
    /// a laptop going to sleep: the server kept running until the queue was
    /// empty, which could be hours.
    /// </summary>
    [Fact]
    public async Task A_close_announced_just_after_the_link_drops_still_stops_the_server()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false, ffmpegPath: ffmpeg);

        var page = await server.OpenPage();
        var source = TestFfmpeg.SlowSource(ffmpeg, SourcePath(server));
        await StartConversion(server, source);

        var link = page.LinkId;
        page.Dispose();                        // the socket goes first...
        // ...and the server has to have noticed. A fixed delay is not enough:
        // a write to a closed socket can succeed once before one fails, so an
        // earlier version of this test beaconed while the link was still
        // counted, passed against the broken code, and proved nothing.
        await server.WaitUntilPagesOpen(0);
        await server.SendCloseBeacon(link);    // and the beacon lands just after

        Assert.True(server.WaitForExit(StopWithin),
                    $"still running {StopWithin.TotalSeconds:0}s after a close whose beacon arrived late. Log:\n"
                    + server.ReadLog());
    }

    /// <summary>
    /// The close beacon is outside the auth gate - a page unloading cannot be
    /// relied on to send credentials - so anything on the network can post it.
    /// On a claimed server that must not be enough to stop it mid-conversion:
    /// an anonymous announcement is not the owner's. The owner's own, signed
    /// in, still is.
    /// </summary>
    [Fact]
    public async Task Only_a_signed_in_close_can_stop_a_claimed_server_mid_conversion()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false, ffmpegPath: ffmpeg);

        var first = await server.OpenPage();   // before claiming: see the silent test above
        using var admin = await server.SignedInAs("admin");
        var source = TestFfmpeg.SlowSource(ffmpeg, SourcePath(server));
        using (var play = await admin.PostAsync("api/play", TestServer.Json(new { file = source })))
            Assert.True(play.IsSuccessStatusCode, $"could not start the conversion: {(int)play.StatusCode}");
        await WaitConverting(server, source, admin);

        await server.SendCloseBeacon(first.LinkId);   // anonymous
        first.Dispose();
        await Task.Delay(9000);
        Assert.True(server.IsRunning,
                    "an anonymous close beacon stopped a claimed server mid-conversion. Log:\n" + server.ReadLog());

        var second = await server.OpenPage();
        await server.SendCloseBeacon(second.LinkId, from: admin);   // the owner, signed in
        second.Dispose();
        Assert.True(server.WaitForExit(StopWithin),
                    "the signed-in owner's close did not stop the server. Log:\n" + server.ReadLog());
    }

    /// <summary>
    /// The laptop sleeps mid-conversion and the server rightly waits. On
    /// waking, the page still holds the id of a link the server ended long ago
    /// - and pressing F5 sends that id on its way out. That is a refresh, not
    /// a close: the page is back a second later.
    ///
    /// A late announcement used to count at once. The grace had long since
    /// run out, so the next tick stopped the server and killed the conversion
    /// before the refreshed page could reconnect.
    /// </summary>
    [Fact]
    public async Task A_refresh_right_after_waking_does_not_stop_the_server()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false, ffmpegPath: ffmpeg);

        var page = await server.OpenPage();
        var source = TestFfmpeg.SlowSource(ffmpeg, SourcePath(server));
        await StartConversion(server, source);

        var stale = page.LinkId;
        page.Dispose();                        // the lid closes
        await server.WaitUntilPagesOpen(0);
        await Task.Delay(5000);                // well into the wait; the grace is long gone

        await server.SendCloseBeacon(stale);   // F5 on waking: the stale id goes out...
        await Task.Delay(1500);                // ...the reload takes a moment...
        using var back = await server.OpenPage();   // ...and the page is back

        await Task.Delay(6000);
        Assert.True(server.IsRunning,
                    "refreshing the page just after waking stopped the server mid-conversion. Log:\n" + server.ReadLog());
        Assert.True(await IsConverting(server, source), "the conversion was not still running when checked");
    }

    /// <summary>
    /// Administered from another machine, with the server's own window left
    /// open on its console. The owner closes their page, says so, and that is
    /// the last page that counts - so the server stops.
    ///
    /// Except the console window's link rotates every twenty seconds, and a
    /// new link used to clear the record of the close whether or not it counted.
    /// About one close in five landed in that window and was then read as a
    /// laptop going to sleep, and the server ran on until its queue was empty.
    /// </summary>
    [Fact]
    public async Task The_servers_own_window_reconnecting_does_not_erase_the_owners_close()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false,
                                                 selfToken: SelfToken, ffmpegPath: ffmpeg);
        var mark = await server.ClaimSelfWindow(SelfToken);
        using var console = await server.OpenPage(cookie: mark);         // the server's own window
        var remote = await server.OpenPage(expected: 2);                 // the owner, elsewhere
        var source = TestFfmpeg.SlowSource(ffmpeg, SourcePath(server));
        await StartConversion(server, source);

        await server.SendCloseBeacon(remote.LinkId);   // the owner closes their page, and says so
        remote.Dispose();
        await server.WaitUntilPagesOpen(1);
        using var rotated = await server.OpenPage(expected: 2, cookie: mark);   // the console's link rotates

        Assert.True(server.WaitForExit(StopWithin),
                    "the server's own window reconnecting erased the owner's close; it kept running. Log:\n"
                    + server.ReadLog());
    }

    /// <summary>
    /// The window the server opened on its own console is showing the sign-in
    /// page - a session expired, or the owner signed out - and a restored queue
    /// is converting. Closing that window is the owner closing it, signed in
    /// or not: only that window holds the cookie minted for this launch.
    ///
    /// Without that, nobody on a sign-in page can be the owner, so the close
    /// was a guess and the server ran on until the whole queue was done.
    /// </summary>
    [Fact]
    public async Task Closing_the_servers_own_sign_in_page_stops_it_mid_conversion()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false,
                                                 selfToken: SelfToken, ffmpegPath: ffmpeg);
        var mark = await server.ClaimSelfWindow(SelfToken);
        var signIn = await server.OpenPage(cookie: mark, query: "stage=login");
        using var admin = await server.SignedInAs("admin");   // the server has an owner now
        var source = TestFfmpeg.SlowSource(ffmpeg, SourcePath(server));
        using (var play = await admin.PostAsync("api/play", TestServer.Json(new { file = source })))
            Assert.True(play.IsSuccessStatusCode, $"could not start the conversion: {(int)play.StatusCode}");
        await WaitConverting(server, source, admin);

        await server.SendCloseBeacon(signIn.LinkId, cookie: mark);   // not signed in - but the console's own window
        signIn.Dispose();

        Assert.True(server.WaitForExit(StopWithin),
                    "closing the server's own sign-in window did not stop it. Log:\n" + server.ReadLog());
    }

    /// <summary>
    /// A health check, a script, or this very harness asking /api/status on a
    /// server nobody has opened a page on. That is not a dashboard, and it
    /// used to count as one: it armed close-shutdown, found no page holding
    /// the server open, and stopped it three seconds later. A headless server
    /// is left alone until a real page has been and gone.
    /// </summary>
    [Fact]
    public async Task A_status_check_does_not_stop_a_headless_server()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false);
        // Starting at all asked /api/status - the harness waits on it.
        using (var status = await server.Http.GetAsync("api/status")) status.EnsureSuccessStatusCode();

        await Task.Delay(7000);
        Assert.True(server.IsRunning,
                    "a status check armed close-shutdown on a server with no page and it stopped. Log:\n" + server.ReadLog());
    }

    /// <summary>
    /// The live link is outside the auth gate so the sign-in page can hold the
    /// server open. Anything else on the network could open it too - curl sends
    /// no Origin and no Sec-Fetch-Site, so the cross-site check waves it through
    /// - and doing so used to arm close-shutdown on a server nobody was using,
    /// then stop it when the socket closed. Only a signed-in link is a dashboard.
    /// </summary>
    [Fact]
    public async Task An_anonymous_link_cannot_stop_a_headless_claimed_server()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false);
        (await server.SignedInAs("admin")).Dispose();   // claimed: anonymous is now nobody

        var stranger = await server.OpenPage();          // no cookie, no key
        stranger.Dispose();

        await Task.Delay(7000);
        Assert.True(server.IsRunning,
                    "an anonymous client opening and dropping the live link stopped the server. Log:\n" + server.ReadLog());
    }

    /// <summary>
    /// The owner is using the window the server opened on its own console,
    /// which holds the server open for as long as nobody else has been. An
    /// anonymous client opening the live link used to count as "somebody else"
    /// - permanently - so that window stopped counting, and when the stranger
    /// dropped the socket the server stopped under the owner's hands.
    /// </summary>
    [Fact]
    public async Task An_anonymous_link_cannot_make_the_servers_own_window_stop_counting()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: false,
                                                 selfToken: SelfToken);
        var mark = await server.ClaimSelfWindow(SelfToken);
        using var console = await server.OpenPage(cookie: mark);   // the owner, at the server
        (await server.SignedInAs("admin")).Dispose();              // claimed: anonymous is now nobody

        var stranger = await server.OpenPage(expected: 2);          // no cookie, no key
        stranger.Dispose();

        await Task.Delay(7000);
        Assert.True(server.IsRunning,
                    "an anonymous link made the server's own window stop counting, and it stopped. Log:\n"
                    + server.ReadLog());
    }

    /// <summary>
    /// In background mode closing a page cannot stop the server, so a close
    /// recorded then decides nothing - and must not be kept for later. It was:
    /// the owner closed their page, the server rightly stayed up, and hours
    /// later unticking "Minimize to the system tray" put the server into the
    /// mode where that stale close counts. The next tick stopped it, killing
    /// the conversion running in the very window the owner had just saved from.
    /// </summary>
    [Fact]
    public async Task A_close_from_background_mode_does_not_stop_the_server_when_that_mode_is_turned_off()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true,
                                                 selfToken: SelfToken, ffmpegPath: ffmpeg);
        var mark = await server.ClaimSelfWindow(SelfToken);
        using var console = await server.OpenPage(cookie: mark);   // the server's own window
        var remote = await server.OpenPage(expected: 2);           // the owner, elsewhere
        var source = TestFfmpeg.SlowSource(ffmpeg, SourcePath(server));
        await StartConversion(server, source);

        await server.SendCloseBeacon(remote.LinkId);               // closed, and said so - in background mode
        remote.Dispose();
        await server.WaitUntilPagesOpen(1);
        await Task.Delay(4000);                                    // past the grace; background mode stays up

        using (var off = await server.Http.PostAsync("api/settings", TestServer.Json(new { minimizeToTray = false })))
            Assert.True(off.IsSuccessStatusCode, $"could not turn background mode off: {(int)off.StatusCode}");

        await Task.Delay(7000);
        Assert.True(server.IsRunning,
                    "turning background mode off stopped the server mid-conversion on a close recorded earlier. Log:\n"
                    + server.ReadLog());
    }

    /// <summary>
    /// Starts a full-resolution conversion and waits until the server itself
    /// reports it as converting.
    ///
    /// Two traps, both hit while writing this. "ready" in the play response
    /// means the first segments exist and playback can begin - seconds into a
    /// conversion with minutes left - not that it has finished. And the
    /// Transcode listing tracks only full-resolution conversions (a scaled
    /// copy is not what a television asked for), so a 360p job, running
    /// perfectly well, reads there as "none". Full resolution, then.
    /// </summary>
    private static async Task StartConversion(TestServer server, string source)
    {
        using var r = await server.Http.PostAsync("api/play", TestServer.Json(new { file = source }));
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"could not start the conversion: {(int)r.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("stream", out _), "the play request did not start a conversion: " + body);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await IsConverting(server, source))
        {
            Assert.True(DateTime.UtcNow < deadline,
                        "the server never reported the conversion as running. Log:\n" + server.ReadLog());
            await Task.Delay(250);
        }
    }

    /// <summary>A folder holding only the source, so the Transcode listing sees nothing else.</summary>
    private static string SourcePath(TestServer server) =>
        Path.Combine(Directory.CreateDirectory(Path.Combine(server.Dir, "source")).FullName, "source.mpg");

    private static async Task WaitConverting(TestServer server, string source, HttpClient? client = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await IsConverting(server, source, client))
        {
            Assert.True(DateTime.UtcNow < deadline,
                        "the server never reported the conversion as running. Log:\n" + server.ReadLog());
            await Task.Delay(250);
        }
    }

    /// <summary>What the server's own Transcode listing says about this file, right now.</summary>
    private static async Task<bool> IsConverting(TestServer server, string source, HttpClient? client = null)
    {
        var folder = Path.GetDirectoryName(source)!;
        using var r = await (client ?? server.Http).GetAsync("api/transcode/scan?path=" + Uri.EscapeDataString(folder));
        if (!r.IsSuccessStatusCode) return false;
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return Find(doc.RootElement);

        bool Find(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String
                    && st.GetString() == "converting")
                    return true;
                foreach (var p in e.EnumerateObject()) if (Find(p.Value)) return true;
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray()) if (Find(item)) return true;
            }
            return false;
        }
    }

    // ---------------------------------------------------------------- harness

    /// <summary>
    /// The built server, started as a process, with a config file of its own,
    /// on a port nothing else is using, and everything it does not need in
    /// order to answer these questions switched off.
    /// </summary>
    internal sealed class TestServer : IDisposable
    {
        private readonly Process _process;
        private readonly string _dir;
        private readonly HttpClient _http;
        private HttpClient? _owner;

        public int Port { get; }
        public bool IsRunning => !_process.HasExited;

        /// <summary>The server's own directory, for a test that needs to put a file where it can reach.</summary>
        public string Dir => _dir;

        /// <summary>An anonymous client for this server.</summary>
        public HttpClient Http => _http;

        private TestServer(Process process, string dir, int port)
        {
            _process = process;
            _dir = dir;
            Port = port;
            // No proxy: a machine with one configured would otherwise send
            // loopback requests through it, and every test here would fail
            // for a reason that has nothing to do with the server.
            // No cookies either, and that one is load-bearing. The default
            // handler keeps a cookie jar, so the moment a test claimed the
            // server through this client it silently became the owner's
            // session - and "anonymous" requests after that were an
            // administrator's. A test asserting that an anonymous request is
            // refused would then pass or fail for the wrong reason entirely.
            _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(20),
            };
            // What the dashboard's own requests carry; see ControlApi's CSRF gate.
            _http.DefaultRequestHeaders.Add("X-J0kers-CSRF", "1");
        }

        public static async Task<TestServer> Start(bool openDashboardOnStart, bool backgroundMode,
                                                  string? selfToken = null, string? ffmpegPath = null)
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
            if (ffmpegPath is not null)
            {
                // libx264 at veryslow: a short clip then takes long enough to
                // convert that it is still running when the test checks.
                var ffmpeg = "  \"ffmpeg\": { \"path\": " + JsonSerializer.Serialize(ffmpegPath)
                           + ", \"videoCodec\": \"libx264\", \"preset\": \"veryslow\" },\n";
                config = config.Insert(config.IndexOf('{') + 1, "\n" + ffmpeg);
            }
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
            using var doc = JsonDocument.Parse(await (_owner ?? _http).GetStringAsync("api/status"));
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

        public async Task<Page> OpenPage(int expected = 1, string? cookie = null, string? query = null)
        {
            var page = await Page.Open(Port, cookie, query);
            // The link is counted when the handler runs, not when the request
            // is written, so give the server the moment in between rather than
            // racing it and then blaming it for the difference.
            await WaitFor(async () => await PagesOpen() >= expected,
                          $"the page was never counted as open (wanted {expected})");
            return page;
        }

        public Task WaitUntilPagesOpen(int expected) =>
            WaitFor(async () => await PagesOpen() == expected, $"the server never counted {expected} page(s) open");

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

        /// <summary>
        /// What a closing browser sends on pagehide. Sent from loopback, the
        /// same address the test's pages connect from, as a real tab's would be.
        /// </summary>
        public async Task SendCloseBeacon(string? linkId = null, HttpClient? from = null, string? cookie = null)
        {
            // The body is what the dashboard sends: the id of the link it is
            // closing, or "bye" from a page that does not know it. A cookie is
            // what a browser's sendBeacon carries by itself.
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/server/closing")
            {
                Content = new StringContent(linkId ?? "bye"),
            };
            if (cookie is not null) request.Headers.Add("Cookie", cookie);
            using var r = await (from ?? _http).SendAsync(request);
            r.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Claims this server with an administrator, then signs in as a new
        /// account of the given role and returns a client carrying its device
        /// key — the credential a script or a player actually uses.
        /// </summary>
        public async Task<HttpClient> SignedInAs(string role)
        {
            const string adminPass = "test-admin-passphrase-9";
            using (var setup = await _http.PostAsync("api/auth/setup", Json(new { username = "owner", password = adminPass })))
                if (!setup.IsSuccessStatusCode && setup.StatusCode != HttpStatusCode.Conflict)
                    Assert.Fail($"could not claim the test server: {(int)setup.StatusCode} {await setup.Content.ReadAsStringAsync()}");

            var admin = await KeyClient("owner", adminPass);
            // Once claimed, /api/status is no longer open to anyone, and the
            // harness still needs it to count pages.
            _owner ??= await KeyClient("owner", adminPass);
            if (role == "admin") return admin;

            var name = "u" + Guid.NewGuid().ToString("N")[..8];
            const string pass = "test-user-passphrase-9";
            using (var create = await admin.PostAsync("api/users", Json(new { username = name, password = pass, role })))
                Assert.True(create.IsSuccessStatusCode,
                            $"could not create a {role} account: {(int)create.StatusCode} {await create.Content.ReadAsStringAsync()}");
            admin.Dispose();
            return await KeyClient(name, pass);
        }

        private async Task<HttpClient> KeyClient(string username, string password)
        {
            using var login = await _http.PostAsync("api/auth/login", Json(new { username, password, remember = true }));
            var body = await login.Content.ReadAsStringAsync();
            Assert.True(login.IsSuccessStatusCode, $"sign-in as {username} failed: {(int)login.StatusCode} {body}");
            var key = JsonDocument.Parse(body).RootElement.GetProperty("key").GetString();
            Assert.False(string.IsNullOrEmpty(key), "sign-in with remember=true returned no device key");
            var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
            {
                BaseAddress = _http.BaseAddress,
                Timeout = TimeSpan.FromSeconds(20),
            };
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
            client.DefaultRequestHeaders.Add("X-J0kers-CSRF", "1");
            return client;
        }

        public static StringContent Json(object body) =>
            new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

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
            _owner?.Dispose();
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
    internal sealed class Page : IDisposable
    {
        private readonly TcpClient _socket;

        /// <summary>
        /// The id the server gave this link, which a closing dashboard names in
        /// its beacon. Null from a server that does not send one.
        /// </summary>
        public string? LinkId { get; }

        private Page(TcpClient socket, string? linkId)
        {
            _socket = socket;
            LinkId = linkId;
        }

        public static async Task<Page> Open(int port, string? cookie = null, string? query = null)
        {
            var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, port);
            var stream = socket.GetStream();
            var request = $"GET /api/server/session{(query is null ? "" : "?" + query)} HTTP/1.1\r\n"
                        + $"Host: 127.0.0.1:{port}\r\n"
                        + "Accept: text/event-stream\r\n"
                        + "Sec-Fetch-Site: same-origin\r\n"
                        + (cookie is null ? "" : $"Cookie: {cookie}\r\n")
                        + "Connection: keep-alive\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request));
            await stream.FlushAsync();

            // Read the response head, so the link is established rather than
            // merely requested by the time this returns - and keep reading
            // briefly for the link's id, which the server sends right after.
            // Heartbeats arrive every half second, so a read never waits long;
            // a server that sends no id simply leaves LinkId null.
            var buffer = new byte[1024];
            var seen = new System.Text.StringBuilder();
            string? linkId = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(1500);
            while (true)
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
                int read;
                try { read = await stream.ReadAsync(buffer, wait.Token); }
                catch (OperationCanceledException) { read = 0; }
                seen.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                var text = seen.ToString();
                if (text.Length >= 12 && !text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
                {
                    socket.Dispose();
                    throw new InvalidOperationException("the server refused the live link: " + text.Trim());
                }
                var m = System.Text.RegularExpressions.Regex.Match(text, "event: link\\s+data: ([0-9a-f]{32})");
                if (m.Success) { linkId = m.Groups[1].Value; break; }
                if (DateTime.UtcNow > deadline) break;
            }
            return new Page(socket, linkId);
        }

        public void Dispose() => _socket.Dispose();
    }
}
