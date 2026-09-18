using System.Net;
using System.Text.Json;
using J0kersMediaServer.Auth;
using J0kersMediaServer.Control;
using Xunit;
using TestServer = J0kersMediaServer.Tests.ShutdownOnCloseTests.TestServer;

namespace J0kersMediaServer.Tests;

/// <summary>
/// Who may do what, checked two ways: the table itself, and a real account
/// against the real server.
///
/// Why the table lists every write route by hand: RequiredLevel's fall-through
/// refuses any write it was never told about. That is the right default - a
/// forgotten route becomes a 403 instead of a silent grant - but it also means
/// a route that SHOULD be open fails closed without a sound. POST /api/play
/// went that way when the default changed: every Read and Edit account lost
/// the ability to play anything from the library, and nothing noticed. A new
/// write route now fails here until somebody decides, on purpose, who it is for.
/// </summary>
public class AccessLevelTests
{
    private static readonly Dictionary<(string Method, string Path), AccessLevel> Intended = new()
    {
        [("POST", "/api/play")] = AccessLevel.Read,           // watching; PlayFile confines Read to shared files
        [("POST", "/api/history/position")] = AccessLevel.Read,
        [("PUT", "/api/preferences")] = AccessLevel.Read,

        [("POST", "/api/library")] = AccessLevel.Edit,
        [("DELETE", "/api/library")] = AccessLevel.Edit,
        [("POST", "/api/favorites")] = AccessLevel.Edit,
        [("DELETE", "/api/favorites")] = AccessLevel.Edit,
        [("POST", "/api/playlists")] = AccessLevel.Edit,
        [("DELETE", "/api/playlists")] = AccessLevel.Edit,
        [("DELETE", "/api/hls")] = AccessLevel.Edit,
        [("POST", "/api/hls/retranscode")] = AccessLevel.Edit,
        [("POST", "/api/channels/import")] = AccessLevel.Edit,
        [("POST", "/api/channels/restart")] = AccessLevel.Edit,
        [("POST", "/api/channels/start")] = AccessLevel.Edit,
        [("POST", "/api/channels/stop")] = AccessLevel.Edit,
        [("POST", "/api/subtitles")] = AccessLevel.Edit,
        [("POST", "/api/tv/pin")] = AccessLevel.Edit,

        [("POST", "/api/channels")] = AccessLevel.Admin,
        [("DELETE", "/api/channels")] = AccessLevel.Admin,
        [("POST", "/api/mounts")] = AccessLevel.Admin,
        [("DELETE", "/api/mounts")] = AccessLevel.Admin,
        [("DELETE", "/api/history")] = AccessLevel.Admin,
        [("POST", "/api/dlna")] = AccessLevel.Admin,
        [("POST", "/api/settings")] = AccessLevel.Admin,
        [("POST", "/api/server/start")] = AccessLevel.Admin,
        [("POST", "/api/server/stop")] = AccessLevel.Admin,
        [("POST", "/api/server/restart")] = AccessLevel.Admin,

        [("DELETE", "/api/sessions/")] = AccessLevel.Admin,     // a prefix route: cutting off someone's stream

        [("POST", "/api/problems/clear")] = AccessLevel.ServerAdmin,
        [("POST", "/api/transcode")] = AccessLevel.ServerAdmin,
        [("POST", "/api/transcode/config")] = AccessLevel.ServerAdmin,
        [("POST", "/api/transcode/remove")] = AccessLevel.ServerAdmin,
        [("POST", "/api/transcode/delete")] = AccessLevel.ServerAdmin,
    };

    [Fact]
    public void Every_write_route_is_classified_on_purpose()
    {
        // Prefix routes too: they reach the same fall-through, so a new one
        // fails closed exactly as /api/play did. A key ending in '/' is a
        // prefix, checked with an id after it.
        var writes = ControlApi.RouteKeys
            .Where(k => k.Method is "POST" or "PUT" or "DELETE" or "PATCH")
            .ToList();

        var unlisted = writes.Where(k => !Intended.ContainsKey(k)).Select(k => $"{k.Method} {k.Path}").ToList();
        Assert.True(unlisted.Count == 0,
                    "write routes nobody has decided a level for - add them to RequiredLevel and to this table: "
                    + string.Join(", ", unlisted));

        static string Probe(string path) => path.EndsWith('/') ? path + "some-id" : path;
        var wrong = Intended
            .Select(kv => (kv.Key, want: kv.Value, got: ControlApi.RequiredLevel(kv.Key.Method, Probe(kv.Key.Path))))
            .Where(x => x.want != x.got)
            .Select(x => $"{x.Key.Method} {x.Key.Path}: meant for {x.want}, gated at {x.got}")
            .ToList();
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// A library folder can be a whole drive. The containment test appended a
    /// separator to the root before comparing, and a drive root already ends
    /// in one - so "D:\" became "D:\\", which no path starts with, and a read
    /// account was refused every file on a shared drive.
    /// </summary>
    [Theory]
    [InlineData(@"D:\", @"D:\Film.mkv", true)]
    [InlineData(@"D:\", @"d:\films\x.mkv", true)]
    [InlineData(@"D:\Films", @"D:\Films\x.mkv", true)]
    [InlineData(@"D:\Films\", @"D:\Films\x.mkv", true)]
    [InlineData(@"D:\Films", @"D:\Films", true)]
    [InlineData(@"D:\Films", @"D:\Filmsx\y.mkv", false)]
    [InlineData(@"D:\Films", @"E:\Films\x.mkv", false)]
    [InlineData("", @"D:\x.mkv", false)]
    public void Containment_holds_for_drive_roots_and_not_for_lookalike_folders(string root, string candidate, bool under)
    {
        Assert.Equal(under, ControlApi.IsUnder(root, candidate));
    }

    /// <summary>
    /// Respelling a shared path - its case, here - must not make a new
    /// conversion. The conversion's name is a hash of the path as given, so
    /// every spelling of one film was a different stream and a fresh encode,
    /// and a ten-letter path has a thousand spellings. That is only a problem
    /// once a read account can start conversions, so it is fixed for them:
    /// the path is put back into the spelling the library itself uses.
    /// </summary>
    [Fact]
    public async Task Respellings_of_a_shared_file_are_one_stream_for_a_read_account()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg);
        var shared = Directory.CreateDirectory(Path.Combine(server.Dir, "library")).FullName;
        var film = TestFfmpeg.PlayableClip(ffmpeg, Path.Combine(shared, "film.mp4"));
        using (var admin = await server.SignedInAs("admin"))
        using (var add = await admin.PostAsync("api/library", TestServer.Json(new { folder = shared })))
            Assert.True(add.IsSuccessStatusCode, "could not share the test folder");
        using var reader = await server.SignedInAs("read");

        async Task<string> StreamFor(string path)
        {
            using var r = await reader.PostAsync("api/play", TestServer.Json(new { file = path, prepare = true }));
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.IsSuccessStatusCode, $"play failed for {path}: {(int)r.StatusCode} {body}");
            return JsonDocument.Parse(body).RootElement.GetProperty("stream").GetString()!;
        }

        var asListed = await StreamFor(film);
        var shouted = await StreamFor(film.ToUpperInvariant());
        Assert.Equal(asListed, shouted);
    }

    /// <summary>
    /// How many conversions a read account can have running at once. The
    /// batch queue runs "how many at a time" and no more; interactive plays
    /// went straight to ffmpeg with no limit at all, which did not matter
    /// while only an administrator could start one. It does now: past the
    /// ceiling a read account is told to wait. Anything already running - or
    /// already finished - still plays.
    /// </summary>
    [Fact]
    public async Task A_read_account_cannot_start_unlimited_conversions()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg);
        var shared = Directory.CreateDirectory(Path.Combine(server.Dir, "library")).FullName;
        var films = new[] { "one", "two", "three" }
            .Select(n => TestFfmpeg.SlowSource(ffmpeg, Path.Combine(shared, n + ".mpg"), seconds: 60))
            .ToArray();
        using (var admin = await server.SignedInAs("admin"))
        using (var add = await admin.PostAsync("api/library", TestServer.Json(new { folder = shared })))
            Assert.True(add.IsSuccessStatusCode, "could not share the test folder");
        using var reader = await server.SignedInAs("read");

        async Task<HttpResponseMessage> Play(string f) =>
            await reader.PostAsync("api/play", TestServer.Json(new { file = f }));

        using (var a = await Play(films[0])) Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        using (var b = await Play(films[1])) Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        using (var c = await Play(films[2]))
        {
            var body = await c.Content.ReadAsStringAsync();
            Assert.True(c.StatusCode == (HttpStatusCode)429,
                        $"a read account started a third concurrent conversion (default ceiling 2): {(int)c.StatusCode} {body}");
        }
        using (var again = await Play(films[0]))
            Assert.True(again.StatusCode == HttpStatusCode.OK, "a conversion already running was refused to the account watching it");
    }

    /// <summary>
    /// A conversion stopped part way leaves a playlist on disk - any restart or
    /// closed page mid-encode does. The ceiling let such a file through as
    /// "already there", but the server treats it as new work: it clears it and
    /// encodes from the top. So replaying the partials a stop leaves behind ran
    /// encodes past the ceiling. A partial is new work, and counts.
    /// </summary>
    [Fact]
    public async Task A_half_finished_conversion_counts_against_the_viewer_ceiling()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg);
        var shared = Directory.CreateDirectory(Path.Combine(server.Dir, "library")).FullName;
        var films = new[] { "one", "two", "three" }
            .Select(n => TestFfmpeg.SlowSource(ffmpeg, Path.Combine(shared, n + ".mpg"), seconds: 60))
            .ToArray();
        using var admin = await server.SignedInAs("admin");
        using (var add = await admin.PostAsync("api/library", TestServer.Json(new { folder = shared })))
            Assert.True(add.IsSuccessStatusCode, "could not share the test folder");

        // Leave the third film exactly as a stop mid-encode would: a playlist
        // with segments listed and no end marker. The server names the
        // conversion, so it is asked to start one and then cancelled, and the
        // partial put back where that conversion lived.
        string stream;
        using (var start = await admin.PostAsync("api/play", TestServer.Json(new { file = films[2] })))
            stream = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("stream").GetString()!;
        string? dir = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (dir is null && DateTime.UtcNow < deadline)
        {
            dir = Directory.EnumerateDirectories(server.Dir, stream, SearchOption.AllDirectories).FirstOrDefault();
            if (dir is null) await Task.Delay(200);
        }
        Assert.True(dir is not null, $"the conversion folder {stream} never appeared");
        using (var cancel = await admin.PostAsync("api/transcode/remove", TestServer.Json(new { stream })))
            Assert.True(cancel.IsSuccessStatusCode, "could not cancel the conversion");
        Directory.CreateDirectory(dir!);
        File.WriteAllText(Path.Combine(dir!, "index.m3u8"),
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\nseg00000.ts\n");

        using var reader = await server.SignedInAs("read");
        async Task<HttpResponseMessage> Play(string f) =>
            await reader.PostAsync("api/play", TestServer.Json(new { file = f }));

        using (var a = await Play(films[0])) Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        using (var b = await Play(films[1])) Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        using (var c = await Play(films[2]))
        {
            var body = await c.Content.ReadAsStringAsync();
            Assert.True(c.StatusCode == (HttpStatusCode)429,
                        $"a half-finished conversion let a read account past the ceiling: {(int)c.StatusCode} {body}");
        }
    }

    /// <summary>
    /// Names that are not a file's name. A wildcard used to be read as a
    /// search and resolved to whichever sibling matched first; an alternate
    /// data stream is the same bytes under a name that hashes to a separate
    /// conversion. A read account is refused both - the library only ever
    /// hands it real paths.
    /// </summary>
    [Fact]
    public async Task A_read_account_cannot_name_a_file_by_pattern_or_alternate_stream()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg);
        var shared = Directory.CreateDirectory(Path.Combine(server.Dir, "library")).FullName;
        var film = TestFfmpeg.PlayableClip(ffmpeg, Path.Combine(shared, "film.mp4"));
        using (var admin = await server.SignedInAs("admin"))
        using (var add = await admin.PostAsync("api/library", TestServer.Json(new { folder = shared })))
            Assert.True(add.IsSuccessStatusCode, "could not share the test folder");
        using var reader = await server.SignedInAs("read");

        foreach (var spelling in new[] { Path.Combine(shared, "*.mp4"), Path.Combine(shared, "fil?.mp4"), film + "::$DATA" })
        {
            using var r = await reader.PostAsync("api/play", TestServer.Json(new { file = spelling, prepare = true }));
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest,
                        $"a read account was allowed to play '{spelling}': {(int)r.StatusCode} {body}");
        }
    }

    /// <summary>
    /// The finding, end to end: a read account, signed in with its own key,
    /// plays a file from a library folder. Before the fix this was
    /// 403 "administrator rights are required for this".
    ///
    /// And the two halves that must NOT move: the same account still cannot
    /// play a file outside the shared library, and still cannot change
    /// settings. Opening /api/play to Read is only safe because PlayFile
    /// confines it, so that confinement is asserted here too.
    /// </summary>
    [Fact]
    public async Task A_read_account_can_play_the_shared_library_and_nothing_else()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg);

        var shared = Directory.CreateDirectory(Path.Combine(server.Dir, "library")).FullName;
        var film = TestFfmpeg.PlayableClip(ffmpeg, Path.Combine(shared, "film.mp4"));
        var elsewhere = Directory.CreateDirectory(Path.Combine(server.Dir, "private")).FullName;
        var secret = TestFfmpeg.PlayableClip(ffmpeg, Path.Combine(elsewhere, "secret.mp4"));

        using (var admin = await server.SignedInAs("admin"))
        using (var add = await admin.PostAsync("api/library", TestServer.Json(new { folder = shared })))
            Assert.True(add.IsSuccessStatusCode,
                        $"could not share the test folder: {(int)add.StatusCode} {await add.Content.ReadAsStringAsync()}");

        using var reader = await server.SignedInAs("read");

        using (var play = await reader.PostAsync("api/play", TestServer.Json(new { file = film })))
        {
            var body = await play.Content.ReadAsStringAsync();
            Assert.True(play.StatusCode == HttpStatusCode.OK,
                        $"a read account could not play a shared library file: {(int)play.StatusCode} {body}");
            var doc = JsonDocument.Parse(body).RootElement;
            Assert.True(doc.TryGetProperty("direct", out _) || doc.TryGetProperty("stream", out _),
                        "the play response carried neither a direct link nor a stream: " + body);
        }

        using (var outside = await reader.PostAsync("api/play", TestServer.Json(new { file = secret })))
        {
            var body = await outside.Content.ReadAsStringAsync();
            Assert.True(outside.StatusCode == HttpStatusCode.Forbidden && body.Contains("shared library"),
                        $"a read account was allowed to play a file outside the shared library: {(int)outside.StatusCode} {body}");
        }

        using (var settings = await reader.PostAsync("api/settings", TestServer.Json(new { logLevel = "debug" })))
            Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);
    }

    /// <summary>
    /// The relay, end to end. A read account asks for a media token whose
    /// scope is "url\n&lt;target&gt;#" - and before the fix, the signature it got
    /// back was byte-for-byte a valid TV-proxy signature for
    /// "&lt;target&gt;#\n&lt;exp&gt;". The fragment keeps the tail off the wire, so an
    /// anonymous request made the server fetch the target, and proxy
    /// signatures never expire.
    ///
    /// The target is a loopback port nobody listens on, so nothing is fetched
    /// either way: the only question is whether the anonymous request was
    /// accepted as signed or sent to the auth gate, where it must be a 401.
    /// </summary>
    [Fact]
    public async Task A_stream_token_signature_does_not_open_the_proxy()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        // Claimed, so an anonymous request really is anonymous. On an unclaimed
        // server whoever arrives is its owner, and the proxy would answer them
        // regardless of any signature - this test would then prove nothing.
        (await server.SignedInAs("admin")).Dispose();

        // Exactly the signature GET /api/media/token would have handed a read
        // account for this scope: minted with the server's own signing.key. It
        // is made here directly so that this test exercises the signing fix on
        // its own, whatever the mint endpoint chooses to refuse (see below).
        const string target = "http://127.0.0.1:9/p.m3u8#";
        var token = new MediaLink(server.Dir).Sign("url\n" + target, TimeSpan.FromHours(1));
        var exp = token.Split('&')[0]["exp=".Length..];
        var sig = token.Split('&')[1]["sig=".Length..];

        using var relay = await server.Http.GetAsync(
            "api/tv/r?u=" + Uri.EscapeDataString(target + "\n" + exp) + "&s=" + Uri.EscapeDataString(sig));
        Assert.True(relay.StatusCode == HttpStatusCode.Unauthorized,
                    "an anonymous request carrying a stream token's signature was accepted by the TV proxy "
                    + $"({(int)relay.StatusCode}) - a read account could mint a permanent open relay");
    }

    /// <summary>
    /// The second layer: the mint endpoint itself refuses a scope with a
    /// control character in it. No stream name has one, and a newline is the
    /// whole shape of the forgery above, so a future change to the signing
    /// cannot quietly reopen it.
    /// </summary>
    [Fact]
    public async Task A_read_account_cannot_mint_a_token_shaped_like_a_proxy_signature()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        using var reader = await server.SignedInAs("read");

        using var mint = await reader.GetAsync(
            "api/media/token?stream=" + Uri.EscapeDataString("url\nhttp://127.0.0.1:9/p.m3u8#"));
        var body = await mint.Content.ReadAsStringAsync();
        Assert.True(mint.StatusCode == HttpStatusCode.BadRequest,
                    $"a read account was issued a token for a scope containing a newline: {(int)mint.StatusCode} {body}");

        // and an ordinary stream name still gets one
        using var ok = await reader.GetAsync("api/media/token?stream=vod-some-film-1a2b3c4d");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
