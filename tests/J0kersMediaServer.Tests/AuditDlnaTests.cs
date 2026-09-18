using System.Net;
using System.Net.Sockets;
using J0kersMediaServer.Dlna;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The DLNA and live-TV findings of the 2026-09-17 audit. Each test here was
/// run against the code before its fix and seen to fail for the reason the
/// audit gives; the ones that could not fail that way say so.
/// </summary>
public class AuditDlnaTests
{
    internal sealed class Scratch : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claude", "j0kers-dlna-" + Guid.NewGuid().ToString("N")[..8]);
        public Scratch() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            for (var attempt = 0; attempt < 20 && Directory.Exists(Path); attempt++)
            {
                try { Directory.Delete(Path, recursive: true); }
                catch { Thread.Sleep(250); }
            }
        }
    }

    internal static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// A loopback listener that hands every request to one handler - for code
    /// that takes an HttpListenerContext and has to be given a real one.
    /// Bound through the server's own binder, so it is 127.0.0.1 and nothing
    /// wider.
    /// </summary>
    private sealed class Loopback : IDisposable
    {
        private readonly HttpListener _listener;
        public int Port { get; }
        public HttpClient Http { get; }

        public Loopback(Action<HttpListenerContext> handle)
        {
            Port = FreePort();
            (_listener, _) = Hls.HttpListenerBinder.StartPlain("127.0.0.1", Port, "test");
            var listener = _listener;
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); }
                    catch { break; }
                    _ = Task.Run(() => handle(ctx));
                }
            });
            Http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{Port}/"),
                Timeout = TimeSpan.FromSeconds(120),
            };
        }

        public void Dispose()
        {
            Http.Dispose();
            try { _listener.Close(); } catch { }
        }
    }

    // ------------------------------------------------------------------ [18]

    /// <summary>
    /// [18] A live channel started afresh numbers its segments from seg_00000
    /// again - tune-on-demand clears the old playlist, so there is nothing for
    /// append_list to continue from - and the television's buffer, which had
    /// recorded up to seg_00902, skipped every index at or below that for
    /// good. Nothing was ever appended again, so a set at the live edge was
    /// answered 416 on every retry, and its retries kept the buffer from ever
    /// being swept and rebuilt.
    /// </summary>
    [Fact]
    public async Task A_live_channel_restarted_from_its_first_segment_keeps_playing_over_dlna()
    {
        using var dir = new Scratch();
        var channelDir = Path.Combine(dir.Path, "ch-test");
        Directory.CreateDirectory(channelDir);
        void Segment(int index, byte fill) =>
            File.WriteAllBytes(Path.Combine(channelDir, $"seg_{index:D5}.ts"), Enumerable.Repeat(fill, 1000).ToArray());

        // A channel that has been running a while.
        for (var i = 900; i <= 903; i++) Segment(i, 0x11);

        using var live = new DlnaLive(dir.Path);
        using var tv = new Loopback(ctx => live.Serve(ctx, "ch-test", channelDir));

        (await tv.Http.GetAsync("dlna/live")).Dispose();
        // 900-902 recorded; 903 is the newest and may still be being written
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (live.CurrentSizeFor("ch-test") < 3000 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(3000, live.CurrentSizeFor("ch-test"));

        // Its ffmpeg dies and it is tuned again: the segments and the playlist
        // are cleared, and the new run starts from seg_00000.
        foreach (var f in Directory.GetFiles(channelDir, "seg_*")) File.Delete(f);
        await Task.Delay(600);
        Segment(0, 0x22);
        Segment(1, 0x22);
        Segment(2, 0x22);

        // The set asks for what follows the live edge.
        using var request = new HttpRequestMessage(HttpMethod.Get, "dlna/live");
        request.Headers.TryAddWithoutValidation("Range", "bytes=3000-");
        using var answer = await tv.Http.SendAsync(request);
        Assert.True(answer.StatusCode == HttpStatusCode.PartialContent,
                    $"the set was answered {(int)answer.StatusCode} at the live edge of a channel that is running again - "
                    + "the recording ignored the restarted channel's segments");
        // Answered as soon as the first new segment lands, as a growing file
        // is: at least seg_00000, and nothing that is not the new run.
        var body = await answer.Content.ReadAsByteArrayAsync();
        Assert.True(body.Length >= 1000, $"only {body.Length} byte(s) followed the live edge");
        Assert.True(body.All(b => b == 0x22), "what followed the live edge was not the restarted channel's picture");

        // seg_00000 and seg_00001 recorded; seg_00002 is the newest
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (live.CurrentSizeFor("ch-test") < 5000 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(5000, live.CurrentSizeFor("ch-test"));

        // and it goes on recording the new run as it grows
        Segment(3, 0x22);
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (live.CurrentSizeFor("ch-test") < 6000 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(6000, live.CurrentSizeFor("ch-test"));
    }

    // ------------------------------------------------------------- [76] limit

    /// <summary>A channel whose segment i is 100 KB of the byte i, so what a set is served says where it came from.</summary>
    private static (string Dir, Action<int> Segment) Channel(Scratch dir, string name)
    {
        var channelDir = Path.Combine(dir.Path, name);
        Directory.CreateDirectory(channelDir);
        return (channelDir, i => File.WriteAllBytes(Path.Combine(channelDir, $"seg_{i:D5}.ts"),
                                                    Enumerable.Repeat((byte)i, 100_000).ToArray()));
    }

    private static async Task RecordedTo(DlnaLive live, string stream, long size)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (live.CurrentSizeFor(stream) < size && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(size, live.CurrentSizeFor(stream));
    }

    /// <summary>
    /// [76] The live-TV recording grew for as long as a set watched - a few
    /// GB an hour - and a set left on overnight filled the drive. It is now
    /// kept to discovery.dlnaLiveMaxGb, the oldest part deleted as it grows.
    /// Twenty 100 KB segments against a limit of five.
    /// </summary>
    [Fact]
    public async Task A_live_recording_is_kept_within_its_limit()
    {
        using var dir = new Scratch();
        var (channelDir, segment) = Channel(dir, "ch-long");
        for (var i = 0; i <= 20; i++) segment(i);          // 0-19 recorded; 20 is the newest
        const long limit = 500_000;

        using var live = new DlnaLive(dir.Path, () => limit);
        using var tv = new Loopback(ctx => live.Serve(ctx, "ch-long", channelDir));
        (await tv.Http.GetAsync("dlna/live", HttpCompletionOption.ResponseHeadersRead)).Dispose();
        await RecordedTo(live, "ch-long", 2_000_000);

        var onDisk = Directory.EnumerateFiles(live.BufferFolderOf("ch-long")!).Sum(f => new FileInfo(f).Length);
        Assert.True(onDisk <= limit,
                    $"a live recording with a limit of {limit:N0} bytes kept {onDisk:N0} on disk after 2,000,000 were recorded");
        Assert.True(onDisk >= limit - 100_000, $"the recording kept only {onDisk:N0} bytes - it trimmed far past its limit");
    }

    /// <summary>
    /// [76] Why the front could not simply be trimmed: a set tuning in asks
    /// for byte 0, and with the front gone that was nothing - it would never
    /// play. It is served from the oldest part kept, and its next request, for
    /// where that ended, carries on from there: its offsets are its own.
    /// </summary>
    [Fact]
    public async Task A_set_tuning_in_after_the_front_was_trimmed_is_served_from_the_oldest_part_kept()
    {
        using var dir = new Scratch();
        var (channelDir, segment) = Channel(dir, "ch-long");
        for (var i = 0; i <= 20; i++) segment(i);
        using var live = new DlnaLive(dir.Path, () => 500_000);
        using var tv = new Loopback(ctx => live.Serve(ctx, "ch-long", channelDir));
        (await tv.Http.GetAsync("dlna/live", HttpCompletionOption.ResponseHeadersRead)).Dispose();
        await RecordedTo(live, "ch-long", 2_000_000);       // kept: segments 15-19

        async Task<(HttpStatusCode Code, string? Range, byte[] Body)> Ask(string range)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "dlna/live");
            request.Headers.TryAddWithoutValidation("Range", range);
            using var answer = await tv.Http.SendAsync(request);
            try { return (answer.StatusCode, answer.Content.Headers.ContentRange?.ToString(), await answer.Content.ReadAsByteArrayAsync()); }
            catch (HttpRequestException e)
            {
                throw new Xunit.Sdk.XunitException(
                    $"asking for {range} after the front was trimmed got a broken answer ({(int)answer.StatusCode}): {e.Message}");
            }
        }

        var first = await Ask("bytes=0-");
        Assert.True(first.Code == HttpStatusCode.PartialContent && first.Body.Length == 500_000
                    && first.Body[0] == 15 && first.Body[^1] == 19,
                    $"a set tuning in was answered {(int)first.Code} with {first.Body.Length:N0} byte(s) starting with segment "
                    + $"{(first.Body.Length > 0 ? first.Body[0] : -1)} - not the oldest part kept (15-19)");
        Assert.Equal("bytes 0-499999/500000", first.Range);

        segment(21);                                        // 20 is recorded now
        var next = await Ask("bytes=500000-");
        Assert.True(next.Code == HttpStatusCode.PartialContent && next.Body.Length == 100_000 && next.Body.All(b => b == 20),
                    $"the set's next request, for where its first ended, was answered {(int)next.Code} with "
                    + $"{next.Body.Length:N0} byte(s) - not segment 20, the one that followed");
    }

    /// <summary>A range request from a set, answered within ten seconds or failed: a wait of a minute is a set stranded.</summary>
    private static async Task<(HttpStatusCode Code, string? Range, byte[] Body)> AskSoon(
        Loopback tv, string range, string? userAgent = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, "dlna/live");
        request.Headers.TryAddWithoutValidation("Range", range);
        if (userAgent is not null) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using var soon = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var answer = await tv.Http.SendAsync(request, soon.Token);
            return (answer.StatusCode, answer.Content.Headers.ContentRange?.ToString(),
                    await answer.Content.ReadAsByteArrayAsync(soon.Token));
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException($"asking for {range}{(userAgent is null ? "" : " as " + userAgent)} "
                                               + "was left waiting - a set asking where it was playing, stranded");
        }
    }

    /// <summary>
    /// Found in review of [76]. The DLNA listing advertised the whole
    /// recorded size, and a set tuning in after trimming is served only what
    /// is kept: a set that believed the listing and seeked near its end asked
    /// past what it would be served. The listing now says the kept size, and
    /// it is exactly what a set tuning in is told.
    /// </summary>
    [Fact]
    public async Task The_size_a_set_tuning_in_is_served_is_the_size_listed()
    {
        using var dir = new Scratch();
        var (channelDir, segment) = Channel(dir, "ch-long");
        for (var i = 0; i <= 20; i++) segment(i);
        using var live = new DlnaLive(dir.Path, () => 500_000);
        using var tv = new Loopback(ctx => live.Serve(ctx, "ch-long", channelDir));
        (await tv.Http.GetAsync("dlna/live", HttpCompletionOption.ResponseHeadersRead)).Dispose();
        await RecordedTo(live, "ch-long", 2_000_000);

        var tuned = await AskSoon(tv, "bytes=0-", "tv-new");
        Assert.True(tuned.Range == $"bytes 0-{live.KeptSizeFor("ch-long") - 1}/{live.KeptSizeFor("ch-long")}",
                    $"the listing would say {live.KeptSizeFor("ch-long"):N0} bytes, and a set tuning in was served "
                    + $"{tuned.Range}");
    }

    /// <summary>
    /// Found in review of [76]. A set's own offset was moved by any request of
    /// its that asked below the kept part - a HEAD included, which takes
    /// nothing. A set playing at the live edge that probed byte 0 had its
    /// offsets moved out from under its playback: its next request, for where
    /// it was, named bytes past the end, waited a minute and was refused.
    /// </summary>
    [Fact]
    public async Task A_head_request_does_not_move_a_set_playing_at_the_live_edge()
    {
        using var dir = new Scratch();
        var (channelDir, segment) = Channel(dir, "ch-long");
        for (var i = 0; i <= 20; i++) segment(i);
        using var live = new DlnaLive(dir.Path, () => 500_000);
        using var tv = new Loopback(ctx => live.Serve(ctx, "ch-long", channelDir));
        (await tv.Http.GetAsync("dlna/live", HttpCompletionOption.ResponseHeadersRead)).Dispose();
        await RecordedTo(live, "ch-long", 2_000_000);

        var tuned = await AskSoon(tv, "bytes=0-", "tv");               // segments 15-19, its own offsets from here
        Assert.Equal(500_000, tuned.Body.Length);
        segment(21); segment(22);                                       // 20 and 21 recorded; the front moves on
        await RecordedTo(live, "ch-long", 2_200_000);

        var probe = await AskSoon(tv, "bytes=0-", "tv", HttpMethod.Head);
        Assert.Equal(HttpStatusCode.PartialContent, probe.Code);

        var next = await AskSoon(tv, "bytes=500000-", "tv");            // where it was playing
        Assert.True(next.Code == HttpStatusCode.PartialContent && next.Body.Length == 200_000 && next.Body[0] == 20,
                    $"after a HEAD, the set's next request for where it was playing was answered {(int)next.Code} with "
                    + $"{next.Body.Length:N0} byte(s) - not segments 20 and 21");
    }

    /// <summary>
    /// Found in review of [76]. Offsets were kept by address alone, so a
    /// second player on the same address tuning in moved the first one's too,
    /// out from under its playback. They are kept by address and User-Agent.
    /// </summary>
    [Fact]
    public async Task A_second_player_on_the_same_address_does_not_move_the_first()
    {
        using var dir = new Scratch();
        var (channelDir, segment) = Channel(dir, "ch-long");
        for (var i = 0; i <= 20; i++) segment(i);
        using var live = new DlnaLive(dir.Path, () => 500_000);
        using var tv = new Loopback(ctx => live.Serve(ctx, "ch-long", channelDir));
        (await tv.Http.GetAsync("dlna/live", HttpCompletionOption.ResponseHeadersRead)).Dispose();
        await RecordedTo(live, "ch-long", 2_000_000);

        var first = await AskSoon(tv, "bytes=0-", "player-one");
        Assert.Equal(500_000, first.Body.Length);
        segment(21); segment(22);
        await RecordedTo(live, "ch-long", 2_200_000);

        var second = await AskSoon(tv, "bytes=0-", "player-two");      // tunes in later, from what is kept then
        Assert.True(second.Body.Length > 0 && second.Body[0] == 17,
                    $"the second player tuning in did not start at the oldest part kept (it got segment {second.Body.FirstOrDefault()})");

        var next = await AskSoon(tv, "bytes=500000-", "player-one");
        Assert.True(next.Code == HttpStatusCode.PartialContent && next.Body.Length == 200_000 && next.Body[0] == 20,
                    $"after a second player on the same address tuned in, the first one's next request was answered "
                    + $"{(int)next.Code} with {next.Body.Length:N0} byte(s) - not segments 20 and 21");
    }

    /// <summary>
    /// Found in review of [76]. A piece deleted between being looked up and
    /// being opened - trimmed at that moment - threw on the open, which the
    /// dropped-connection path never saw: the answer was closed short and the
    /// set waited out its own timeout. Made deterministic by deleting a piece's
    /// file by hand before asking for it.
    /// </summary>
    [Fact]
    public async Task A_piece_gone_before_it_is_opened_does_not_leave_the_set_waiting()
    {
        using var dir = new Scratch();
        var (channelDir, segment) = Channel(dir, "ch-gone");
        for (var i = 0; i <= 30; i++) segment(i);                       // 3 MB recorded, in several pieces
        using var live = new DlnaLive(dir.Path, () => 10_000_000);
        using var tv = new Loopback(ctx => live.Serve(ctx, "ch-gone", channelDir));
        (await tv.Http.GetAsync("dlna/live", HttpCompletionOption.ResponseHeadersRead)).Dispose();
        await RecordedTo(live, "ch-gone", 3_000_000);

        var pieces = Directory.GetFiles(live.BufferFolderOf("ch-gone")!, "*.ts").OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.True(pieces.Count >= 2, $"precondition: the recording is in {pieces.Count} piece(s), not several");
        File.Delete(pieces[0]);                                         // the oldest, closed: gone before anyone opens it

        try { await AskSoon(tv, "bytes=0-", "tv"); }
        catch (Xunit.Sdk.XunitException) { throw; }
        catch (Exception) { /* the connection dropped: what this wants */ }
    }

    /// <summary>
    /// [76] A set part way through an answer can fall behind what the
    /// recording keeps: it stops reading, the channel moves on, and the part
    /// it was being sent is trimmed. The answer had promised every byte, and
    /// closing it short left the set waiting for the rest until its own
    /// timeout (two minutes, measured). The connection is dropped instead, so
    /// the set asks again at once.
    /// </summary>
    [Fact]
    public async Task A_set_that_falls_behind_what_is_kept_is_not_left_waiting()
    {
        using var dir = new Scratch();
        var channelDir = Path.Combine(dir.Path, "ch-slow");
        Directory.CreateDirectory(channelDir);
        const int mb4 = 4 * 1024 * 1024;
        void Segment(int i) => File.WriteAllBytes(Path.Combine(channelDir, $"seg_{i:D5}.ts"),
                                                  Enumerable.Repeat((byte)i, mb4).ToArray());
        for (var i = 0; i <= 4; i++) Segment(i);            // 0-3 recorded: 16 MB
        using var live = new DlnaLive(dir.Path, () => 4L * mb4);
        using var tv = new Loopback(ctx => live.Serve(ctx, "ch-slow", channelDir));
        (await tv.Http.GetAsync("dlna/live", HttpCompletionOption.ResponseHeadersRead)).Dispose();
        await RecordedTo(live, "ch-slow", 4L * mb4);

        // a set asks for all of it, takes the first few KB, and stops reading
        using var set = new TcpClient();
        await set.ConnectAsync(IPAddress.Loopback, tv.Port);
        var net = set.GetStream();
        await net.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
            $"GET /dlna/live HTTP/1.1\r\nHost: 127.0.0.1:{tv.Port}\r\nRange: bytes=0-\r\nConnection: keep-alive\r\n\r\n"));
        var buffer = new byte[64 * 1024];
        long got = await net.ReadAsync(buffer);

        // the channel moves on past everything the set was being sent
        for (var i = 5; i <= 9; i++) Segment(i);            // 4-8 recorded: 36 MB, of which 16 kept
        await RecordedTo(live, "ch-slow", 9L * mb4);
        Assert.True(live.KeptSizeFor("ch-slow") <= 4L * mb4, "precondition: the front was not trimmed");

        // and now it reads on
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                var n = await net.ReadAsync(buffer, patience.Token);
                if (n == 0) break;
                got += n;
            }
        }
        catch (IOException) { /* the connection dropped: what this wants */ }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException(
                $"a set that fell behind what the recording keeps was left waiting for the rest of its answer "
                + $"({got:N0} bytes of 16 MB after {clock.Elapsed.TotalSeconds:0} s)");
        }
        Assert.True(got < 4L * mb4, $"the set was sent {got:N0} bytes - all of it, from a part that had been trimmed");
    }

    // ------------------------------------------------------- shared e2e bits

    /// <summary>Adds settings to a test server's server.json before it starts.</summary>
    private static void Configure(string dir, Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        var path = Path.Combine(dir, "server.json");
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(config);
        File.WriteAllText(path, config.ToJsonString());
    }

    private static System.Text.Json.Nodes.JsonObject Section(System.Text.Json.Nodes.JsonObject config, string name)
    {
        if (config[name] is System.Text.Json.Nodes.JsonObject existing) return existing;
        var created = new System.Text.Json.Nodes.JsonObject();
        config[name] = created;
        return created;
    }

    /// <summary>Everything a test server has logged so far (ReadLog keeps only the tail).</summary>
    private static string WholeLog(ShutdownOnCloseTests.TestServer server)
    {
        var logs = Path.Combine(server.Dir, "logs");
        if (!Directory.Exists(logs)) return "";
        var text = new System.Text.StringBuilder();
        foreach (var file in Directory.GetFiles(logs))
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                text.Append(reader.ReadToEnd());
            }
            catch (IOException) { /* rotated away mid-read */ }
        }
        return text.ToString();
    }

    private static async Task Until(Func<Task<bool>> condition, TimeSpan within, string failure)
    {
        var deadline = DateTime.UtcNow + within;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(250);
        }
    }

    // ------------------------------------------------------------------ [51]

    /// <summary>
    /// [51] A television watching a live channel over DLNA went into Recently
    /// Watched as a *file* whose path was the channel's display name - "Test
    /// One.HD" - with no stream. Picking it asked /api/play for a file of that
    /// name, which does not exist, so it could never be replayed; and the name
    /// was cut at its dot, as if it were an extension.
    /// </summary>
    [Fact]
    public async Task A_live_channel_watched_over_dlna_is_remembered_as_that_channel()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await ShutdownOnCloseTests.TestServer.Start(
            openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg, dlna: true,
            beforeLaunch: dir => Configure(dir, c => Section(c, "discovery")["dlnaLiveTv"] = true));

        // Nothing answers on port 1, so the channel's ffmpeg has nothing to
        // pull; the viewing is recorded the moment the set asks, which is all
        // this needs.
        const string name = "Test One.HD";
        string stream;
        using (var add = await server.Http.PostAsync("api/channels", ShutdownOnCloseTests.TestServer.Json(
                   new { name, url = "http://127.0.0.1:1/live.m3u8" })))
        {
            var text = await add.Content.ReadAsStringAsync();
            Assert.True(add.IsSuccessStatusCode, $"could not add the channel: {(int)add.StatusCode} {text}");
            stream = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("stream").GetString()!;
        }

        // The set tunes it. The answer waits for a picture that is not coming,
        // so it is left running and abandoned once the history has what it needs.
        using var tuned = new CancellationTokenSource();
        var tuning = server.Http.GetAsync($"dlna/live?ch={Uri.EscapeDataString(stream)}",
                                          HttpCompletionOption.ResponseHeadersRead, tuned.Token);
        try
        {
            System.Text.Json.JsonElement entry = default;
            await Until(async () =>
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await server.Http.GetStringAsync("api/history"));
                foreach (var e in doc.RootElement.GetProperty("history").EnumerateArray())
                {
                    entry = e.Clone();
                    return true;
                }
                return false;
            }, TimeSpan.FromSeconds(20), "tuning a live channel over DLNA put nothing in Recently Watched");

            var kind = entry.GetProperty("kind").GetString();
            var path = entry.GetProperty("path").GetString();
            var remembered = entry.GetProperty("stream").GetString();
            Assert.True(kind == "stream" && remembered == stream,
                        $"the channel was remembered as kind '{kind}', path '{path}', stream '{remembered}' - "
                        + $"not as the stream {stream}, so it cannot be played again from the list");
            Assert.Equal("", path);
            Assert.Equal(name, entry.GetProperty("name").GetString());
        }
        finally
        {
            tuned.Cancel();
            try { (await tuning).Dispose(); } catch { /* abandoned on purpose */ }
        }
    }

    // ------------------------------------------------------------------ [24]

    /// <summary>The conversions the HLS list is hiding, as the server wrote them down.</summary>
    private static List<string> Unlinked(ShutdownOnCloseTests.TestServer server)
    {
        var file = Path.Combine(server.Dir, "unlinked.json");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.Exists(file)
                    ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? new()
                    : new();
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(100); }   // mid-rename
        }
    }

    /// <summary>The Transcodes window's row for one file.</summary>
    private static async Task<System.Text.Json.JsonElement> Row(ShutdownOnCloseTests.TestServer server, string folder, string file)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            await server.Http.GetStringAsync("api/transcode/scan?path=" + Uri.EscapeDataString(folder)));
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            if (e.GetProperty("type").GetString() == "file" && e.GetProperty("path").GetString() == file)
                return e.Clone();
        throw new Xunit.Sdk.XunitException($"the Transcodes window did not list {file}");
    }

    private static async Task<System.Text.Json.JsonElement> Convert(ShutdownOnCloseTests.TestServer server, string folder)
    {
        using var r = await server.Http.PostAsync("api/transcode",
                                                  ShutdownOnCloseTests.TestServer.Json(new { paths = new[] { folder } }));
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"Convert failed: {(int)r.StatusCode} {text}");
        return System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>
    /// [24] With DLNA off the conversion index was never built, so a film whose
    /// conversion had finished still read "not ready for TV" for ever - the
    /// pill stayed yellow and Convert kept choosing it. Choosing it queued
    /// nothing (it is converted), but the batch then hid every file it had
    /// chosen from the HLS list, not the ones it queued: a conversion somebody
    /// had published by playing it disappeared from the list again on every
    /// press. The index is also briefly behind with DLNA on - for a few
    /// seconds after a conversion finishes - and a press in that window did
    /// the same, which the first half of this test catches.
    /// </summary>
    [Fact]
    public async Task Convert_leaves_a_finished_and_published_conversion_listed_with_dlna_off()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await ShutdownOnCloseTests.TestServer.Start(openDashboardOnStart: false, backgroundMode: true,
                                                                       ffmpegPath: ffmpeg, dlna: false);
        var started = DateTime.UtcNow;
        var library = Path.Combine(server.Dir, "library");
        Directory.CreateDirectory(library);
        // An MKV: a television is given a converted copy whatever is inside,
        // and H.264/AAC inside is copied rather than encoded, so it is quick.
        var film = Path.Combine(library, "film.mkv");
        TestFfmpeg.Run(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
                       "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "2",
                       "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", film);

        // The index recounts at most every ten seconds. Starting past the
        // first ten means the press below recounts (and finds nothing yet),
        // and the next ten seconds are the window in which it has not caught
        // up with the conversion - the window a real press can land in.
        var wait = started.AddSeconds(11) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);

        var windowOpened = DateTime.UtcNow;
        var first = await Convert(server, library);
        Assert.True(first.GetProperty("queued").GetInt32() == 1, $"precondition: the film was queued ({first})");
        await Until(async () => (await Row(server, library, film)).GetProperty("state").GetString() == "done",
                    TimeSpan.FromSeconds(60), "precondition: the conversion never finished");

        // Published: playing it is what puts a conversion in the HLS list.
        string stream;
        using (var play = await server.Http.PostAsync("api/play", ShutdownOnCloseTests.TestServer.Json(new { file = film })))
        {
            var text = await play.Content.ReadAsStringAsync();
            Assert.True(play.IsSuccessStatusCode, $"precondition: could not play the film: {(int)play.StatusCode} {text}");
            stream = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("stream").GetString()!;
        }
        Assert.DoesNotContain(stream, Unlinked(server));

        // Convert again, while the index has not caught up.
        var again = await Convert(server, library);
        Assert.True(DateTime.UtcNow - windowOpened < TimeSpan.FromSeconds(10),
                    "the test took too long to reach the moment it is about; nothing was checked");
        Assert.Equal(0, again.GetProperty("queued").GetInt32());
        Assert.True(!Unlinked(server).Contains(stream),
                    "pressing Convert on a folder whose film was already converted took the published conversion "
                    + "out of the HLS list - it hid what it had chosen, not what it queued");

        // Given the index's time to catch up, the finished conversion is what
        // a television is handed, and the row says so.
        await Until(async () => (await Row(server, library, film)).GetProperty("dlnaReady").ValueKind == System.Text.Json.JsonValueKind.True,
                    TimeSpan.FromSeconds(30),
                    "a film whose conversion had finished still read 'not ready for TV' with DLNA off - "
                    + "the conversion index was never built");
        var last = await Convert(server, library);
        Assert.Equal(0, last.GetProperty("queued").GetInt32());
        Assert.Equal(0, last.GetProperty("needs").GetInt32());
        Assert.DoesNotContain(stream, Unlinked(server));
    }

    // ------------------------------------------------------------------ [19]

    /// <summary>
    /// [19] A television plays a conversion through one long GET, and the
    /// cache sweep judged it by the time that GET began: forty minutes into a
    /// film it was the oldest thing in the cache. The next conversion to start
    /// swept it - every segment but the one being read went, the rest of the
    /// film was gone, and the set's stream ended mid-film.
    /// </summary>
    [Fact]
    public async Task A_conversion_a_television_is_playing_is_not_evicted_under_it()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await ShutdownOnCloseTests.TestServer.Start(
            openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg, dlna: true,
            // a cache cap every conversion is over, so any sweep evicts all it may
            beforeLaunch: dir => Configure(dir, c => Section(c, "ffmpeg")["vodCacheMaxGb"] = 0.000001));

        var library = Path.Combine(server.Dir, "library");
        Directory.CreateDirectory(library);
        // MKV, so a television is handed the conversion; H.264/AAC inside, so
        // converting it is a copy. Big enough that a set which stops reading
        // leaves the server part way through the body rather than finished.
        var film = Path.Combine(library, "film.mkv");
        TestFfmpeg.Run(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30",
                       "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "40",
                       "-c:v", "libx264", "-preset", "ultrafast", "-b:v", "8M", "-pix_fmt", "yuv420p",
                       "-c:a", "aac", "-shortest", film);
        // A second film that has to be converted too, to set the sweep off.
        var other = Path.Combine(library, "other.mkv");
        TestFfmpeg.Run(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
                       "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "2",
                       "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", other);
        using (var add = await server.Http.PostAsync("api/library", ShutdownOnCloseTests.TestServer.Json(new { folder = library })))
            Assert.True(add.IsSuccessStatusCode, $"could not add the library folder: {(int)add.StatusCode}");

        // Played once, as anyone would: a conversion the cache may evict.
        string stream;
        var filmStarted = DateTime.UtcNow;
        using (var play = await server.Http.PostAsync("api/play", ShutdownOnCloseTests.TestServer.Json(new { file = film })))
        {
            var text = await play.Content.ReadAsStringAsync();
            Assert.True(play.IsSuccessStatusCode, $"could not play the film: {(int)play.StatusCode} {text}");
            stream = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("stream").GetString()!;
        }
        await Until(async () => (await Row(server, library, film)).GetProperty("state").GetString() == "done",
                    TimeSpan.FromSeconds(90), "precondition: the conversion never finished");
        var conversion = Path.Combine(server.Dir, "media", stream);
        var segments = Directory.GetFiles(conversion).Length;

        // Wait until DLNA hands over the conversion rather than the original
        // (the conversion index catches up within its ten-second recount).
        var id = DlnaService.Encode(film);
        await Until(async () =>
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, "dlna/file?id=" + id);
            using var r = await server.Http.SendAsync(head);
            return r.IsSuccessStatusCode && r.Content.Headers.ContentType?.MediaType is "video/mp2t" or "video/mp4";
        }, TimeSpan.FromSeconds(30), "precondition: DLNA never handed over the conversion");

        // The set starts the film, and reads a little of it.
        using var set = new TcpClient();
        await set.ConnectAsync(IPAddress.Loopback, server.Port);
        var net = set.GetStream();
        await net.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
            $"GET /dlna/file?id={id} HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nConnection: close\r\n\r\n"));
        var head0 = new List<byte>();
        var one = new byte[1];
        while (!(head0.Count >= 4 && head0[^4] == '\r' && head0[^3] == '\n' && head0[^2] == '\r' && head0[^1] == '\n'))
        {
            Assert.True(await net.ReadAsync(one) == 1, "the set's request was closed before it was answered");
            head0.Add(one[0]);
        }
        var headText = System.Text.Encoding.ASCII.GetString(head0.ToArray());
        var length = long.Parse(System.Text.RegularExpressions.Regex.Match(headText, @"Content-Length:\s*(\d+)",
                                     System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value);
        var buffer = new byte[256 * 1024];
        long got = 0;
        while (got < 512 * 1024)
        {
            var n = await net.ReadAsync(buffer);
            Assert.True(n > 0, "the film ended before the set had read half a megabyte");
            got += n;
        }

        // ...and pauses. A conversion started in the last minute is spared by
        // the sweep as one still starting up; the audit's case is forty
        // minutes into the film, so this waits until that no longer applies.
        var wait = filmStarted.AddSeconds(65) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);

        // Something else starts converting, which sweeps the cache. (Every
        // sweep ends by saying the cache is over its limit or what it trimmed;
        // a sweep with a file in use says nothing else.)
        static int Sweeps(string log) =>
            System.Text.RegularExpressions.Regex.Matches(log, "over the .* limit|cache trimmed").Count;
        var before = Sweeps(WholeLog(server));
        using (var play = await server.Http.PostAsync("api/play", ShutdownOnCloseTests.TestServer.Json(new { file = other })))
            Assert.True(play.IsSuccessStatusCode, $"precondition: could not start the other conversion: {(int)play.StatusCode}");
        await Until(() => Task.FromResult(Sweeps(WholeLog(server)) > before),
                    TimeSpan.FromSeconds(30), "precondition: the cache sweep never ran");
        await Task.Delay(1000);

        // Then carries on to the end. A film cut short either closes or simply
        // stops arriving, so a stall counts as the end too.
        using (var reading = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            while (got < length)
            {
                int n;
                try { n = await net.ReadAsync(buffer, reading.Token); }
                catch (Exception e) when (e is IOException or OperationCanceledException) { break; }
                if (n <= 0) break;
                got += n;
            }
        }
        Assert.True(got == length,
                    $"the set's film stopped at {got:N0} of {length:N0} bytes - the conversion was evicted while it was being played");
        Assert.True(Directory.Exists(conversion) && Directory.GetFiles(conversion).Length == segments,
                    "the conversion a television was playing was deleted by the cache sweep");
    }

    // ------------------------------------------------------------------ [69]

    /// <summary>
    /// [69] The live-TV M3U, and the description the plain DLNA port serves,
    /// took the host out of the Host header by cutting at its first colon. An
    /// IPv6 address is made of colons: "[2001:db8::5]:9090" became "[2001", and
    /// every channel link in the playlist - and every service URL a television
    /// was given - was "http://[2001:9091/…", which nothing can open.
    ///
    /// Asked of the real request an IPv6 client sends, through an http.sys
    /// listener on the IPv6 loopback, and handed to the helper both of those
    /// now use. The test servers cannot be reached this way: their listeners
    /// are registered for "localhost" and "127.0.0.1", and http.sys refuses
    /// any other Host before the server sees it.
    /// </summary>
    [Theory]
    [InlineData("[::1]", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("localhost", false)]
    public async Task Links_built_from_the_host_a_client_used_are_valid_for_an_ipv6_address(string asked, bool ipv6)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://{asked}:{port}/");
        listener.Start();
        var arrived = listener.GetContextAsync();

        using var client = new TcpClient(ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
        await client.ConnectAsync(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes(
            $"GET /api/tv/playlist.m3u HTTP/1.1\r\nHost: {asked}:{port}\r\nConnection: close\r\n\r\n"));
        var ctx = await arrived.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var host = Control.ControlApi.LinkHost(ctx.Request, "fallback");
            var link = $"http://{host}:9091/ch-news/index.m3u8";
            Assert.True(Uri.TryCreate(link, UriKind.Absolute, out var parsed),
                        $"a client that reached the server as {asked} was handed {link}, which is not a URL");
            Assert.Equal(9091, parsed!.Port);
            Assert.True(IPAddress.TryParse(parsed.Host.Trim('[', ']'), out var address)
                            ? address.Equals(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback)
                            : parsed.Host == asked,
                        $"a client that reached the server as {asked} was sent to {parsed.Host}");
        }
        finally
        {
            ctx.Response.Close();
        }
    }

    // ------------------------------------------------------------- [70] [71]

    private static string BufferRoot(ShutdownOnCloseTests.TestServer server) =>
        Path.Combine(server.Dir, "media", ".dlnalive");

    /// <summary>
    /// [70] [71] The live-TV buffers were never disposed on the way down: the
    /// recording a television had been watching - often gigabytes - and the
    /// folder holding it stayed in the media root after a clean shutdown, with
    /// nothing to remove them if DLNA was off at the next start.
    /// </summary>
    [Fact]
    public async Task A_clean_shutdown_removes_the_live_tv_recordings()
    {
        const string token = "audit-dlna-self-window";
        var server = await ShutdownOnCloseTests.TestServer.Start(openDashboardOnStart: false, backgroundMode: false,
                                                                selfToken: token, dlna: true);
        try
        {
            var root = BufferRoot(server);
            Assert.True(Directory.Exists(root), $"precondition: DLNA on makes its buffer folder ({root})");
            // what a set watching a channel leaves in it
            Directory.CreateDirectory(Path.Combine(root, "ch-left"));
            File.WriteAllBytes(Path.Combine(root, "ch-left", "dlna.ts"), new byte[64 * 1024]);

            // The server's own window closes, which stops it the ordinary way.
            var mark = await server.ClaimSelfWindow(token);
            var page = await server.OpenPage(cookie: mark);
            page.Dispose();
            Assert.True(server.WaitForExit(TimeSpan.FromSeconds(30)),
                        "precondition: closing the server's own window stops it. Log:\n" + server.ReadLog());

            Assert.False(Directory.Exists(root),
                         "the live-TV recordings were left in the media root after a clean shutdown");
        }
        finally { server.Dispose(); }
    }

    /// <summary>
    /// [71] The shutdown the audit describes happens while a set is watching,
    /// and a set part way through a response has the recording open. Taking
    /// the buffers down has to remove the recording even then, not leave it
    /// behind because a reader was holding it at that moment.
    /// </summary>
    [Fact]
    public async Task Taking_the_live_tv_buffers_down_removes_a_recording_a_set_is_still_reading()
    {
        using var dir = new Scratch();
        var channelDir = Path.Combine(dir.Path, "ch-test");
        Directory.CreateDirectory(channelDir);
        // big enough that the response cannot all sit in socket buffers
        for (var i = 0; i < 4; i++)
            File.WriteAllBytes(Path.Combine(channelDir, $"seg_{i:D5}.ts"), new byte[8 * 1024 * 1024]);

        var live = new DlnaLive(dir.Path);
        using var tv = new Loopback(ctx => live.Serve(ctx, "ch-test", channelDir));

        (await tv.Http.GetAsync("dlna/live", HttpCompletionOption.ResponseHeadersRead)).Dispose();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (live.CurrentSizeFor("ch-test") < 24L * 1024 * 1024 && DateTime.UtcNow < deadline) await Task.Delay(50);
        var recording = live.BufferFolderOf("ch-test")!;   // the recording's folder, pieces and all

        // A set that asks for the recording and then stops reading: the
        // server is part way through the body, with the file open.
        using var set = new TcpClient();
        await set.ConnectAsync(IPAddress.Loopback, tv.Port);
        var ask = System.Text.Encoding.ASCII.GetBytes(
            $"GET /dlna/live HTTP/1.1\r\nHost: 127.0.0.1:{tv.Port}\r\nConnection: keep-alive\r\n\r\n");
        await set.GetStream().WriteAsync(ask);
        await set.GetStream().ReadExactlyAsync(new byte[4096]);          // the head and the first of the body
        await Task.Delay(1000);                                          // and the server blocks, mid-file

        live.Dispose();

        Assert.False(Directory.Exists(recording) || Directory.Exists(Path.Combine(dir.Path, ".dlnalive")),
                     "the recording a set was part way through was left behind when the buffers were taken down");
    }

    /// <summary>
    /// Found in review of [71]. A swept buffer deletes its folder after waiting
    /// up to two seconds for its recorder, and a set tuning the same channel in
    /// that time starts the next buffer - in the same folder, named after the
    /// channel. Once the recording's files shared delete, that late delete took
    /// the new recording with it. The late delete is done here by hand, exactly
    /// as Buffer.Dispose does it, after the new buffer is recording.
    /// </summary>
    [Fact]
    public async Task A_buffer_taken_down_late_does_not_delete_the_recording_that_replaced_it()
    {
        using var dir = new Scratch();
        var channelDir = Path.Combine(dir.Path, "ch-late");
        Directory.CreateDirectory(channelDir);
        for (var i = 0; i < 3; i++)
            File.WriteAllBytes(Path.Combine(channelDir, $"seg_{i:D5}.ts"), new byte[1024 * 1024]);

        var live = new DlnaLive(dir.Path);
        try
        {
            using var tv = new Loopback(ctx => live.Serve(ctx, "ch-late", channelDir));
            async Task Tune()
            {
                (await tv.Http.GetAsync("dlna/live", HttpCompletionOption.ResponseHeadersRead)).Dispose();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (live.CurrentSizeFor("ch-late") == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
                Assert.True(live.CurrentSizeFor("ch-late") > 0, "precondition: the channel was not recorded");
            }

            await Tune();
            var first = live.BufferFolderOf("ch-late")!;
            // the set has gone: sweep the buffer once nothing holds it
            var until = DateTime.UtcNow.AddSeconds(10);
            while (live.BufferFolderOf("ch-late") is not null && DateTime.UtcNow < until)
            {
                live.SweepIdle(TimeSpan.Zero);
                await Task.Delay(50);
            }
            Assert.True(live.BufferFolderOf("ch-late") is null, "precondition: the idle buffer was not swept");

            await Tune();   // a set tunes in again
            var second = live.BufferFolderOf("ch-late")!;
            bool Recorded() => Directory.Exists(second) && Directory.EnumerateFiles(second, "*.ts").Any();
            Assert.True(Recorded(), "precondition: the new buffer has no recording");

            // the first buffer's delete, arriving late
            try { Directory.Delete(first, recursive: true); } catch (DirectoryNotFoundException) { }

            Assert.True(Recorded(),
                        "the first buffer's late clean-up deleted the recording of the buffer that replaced it");
        }
        finally { live.Dispose(); }
    }

    /// <summary>
    /// [70] A recording left by a server that did not get to shut down cleanly
    /// was only ever cleared by a later start with DLNA on. With it off, the
    /// file stayed in the media root for good.
    /// </summary>
    [Fact]
    public async Task A_live_tv_recording_left_behind_is_removed_at_start_with_dlna_off()
    {
        using var server = await ShutdownOnCloseTests.TestServer.Start(
            openDashboardOnStart: false, backgroundMode: true, dlna: false,
            beforeLaunch: dir =>
            {
                var left = Path.Combine(dir, "media", ".dlnalive", "ch-left");
                Directory.CreateDirectory(left);
                File.WriteAllBytes(Path.Combine(left, "dlna.ts"), new byte[64 * 1024]);
            });

        Assert.False(Directory.Exists(BufferRoot(server)),
                     "a live-TV recording from an earlier run was left in the media root by a start with DLNA off");
    }

    /// <summary>
    /// [50] Switching DLNA on under HTTPS with a network bind opened its plain
    /// port on localhost only - Windows refuses the wide bind until startup
    /// has asked for the port's URL ACL - and the save said nothing. The save
    /// now reports it when the listener's bind fell back. Only the decision is
    /// tested: reaching the fallback takes a wide bind, which tests do not
    /// make. The wiring (StartDlnaListener, SaveSettings, the Config dialog)
    /// is checked by reading.
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0", "localhost", true)]         // asked for the network, got this machine
    [InlineData("192.168.1.20", "localhost", true)]
    [InlineData("0.0.0.0", "0.0.0.0", false)]          // the network, as asked
    [InlineData("192.168.1.20", "192.168.1.20", false)]
    [InlineData("127.0.0.1", "localhost", false)]      // this machine, as asked
    [InlineData("localhost", "localhost", false)]
    [InlineData("::1", "localhost", false)]
    public void A_dlna_port_that_fell_back_to_this_machine_is_noticed(string bind, string bound, bool fellBack)
    {
        Assert.True(Hls.HttpListenerBinder.FellBackToLoopback(bind, bound) == fellBack,
                    $"bind {bind}, bound {bound}: expected fell back = {fellBack}");
    }
}

/// <summary>
/// Tests that switch the server's URL scheme, which is one fact for the whole
/// process (Services.UrlScheme). Run on their own, after everything else, so no
/// other test in this process is reading it while it says https.
/// </summary>
[CollectionDefinition(nameof(ProcessWideUrlScheme), DisableParallelization = true)]
public sealed class ProcessWideUrlScheme { }

[Collection(nameof(ProcessWideUrlScheme))]
public class AuditDlnaSchemeTests
{
    /// <summary>A control port whose next port up is free too - where DLNA goes under TLS.</summary>
    private static int ControlPortWithFreeNeighbour()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = AuditDlnaTests.FreePort();
            try
            {
                var next = new TcpListener(IPAddress.Loopback, port + 1);
                next.Start();
                next.Stop();
                return port;
            }
            catch (SocketException) when (attempt < 20) { }
        }
    }

    /// <summary>
    /// [54] Under TLS, DLNA has a plain port of its own: the control port + 1.
    /// That was worked out afresh on every use, from a control port the Config
    /// dialog changes at once while the listeners stay where they are until a
    /// restart. So after the port was changed, the description a television
    /// fetched from the DLNA port sent its every request to a port nothing
    /// listened on; and switching DLNA off and on again opened that other port,
    /// while the network announcement still pointed at the first.
    ///
    /// In-process, because TLS cannot be switched on for a test server: binding
    /// a certificate needs an administrator. Nothing here is served beyond
    /// loopback, and nothing is announced - the discovery service is never
    /// started.
    /// </summary>
    [Fact]
    public async Task The_plain_dlna_port_stays_where_it_was_opened_when_the_control_port_is_changed()
    {
        var wasHttps = Services.UrlScheme.Https;
        Services.UrlScheme.UseHttps(true);
        try
        {
            using var dir = new AuditDlnaTests.Scratch();
            var control = ControlPortWithFreeNeighbour();
            var dlnaPort = control + 1;
            var config = new Config.ServerConfig();
            config.Control.BindAddress = "127.0.0.1";
            config.Control.Port = control;
            config.Discovery.Enabled = false;
            config.Discovery.Dlna = true;

            using var api = new Control.ControlApi(config, new Services.ServiceController(config, dir.Path), dir.Path,
                                                   new Auth.AuthService(new Auth.UserStore(dir.Path), "", dir.Path),
                                                   new Auth.MediaLink(dir.Path));
            using var discovery = new Discovery.DiscoveryService(config.Discovery, "audit", control, dir.Path,
                                                                 Services.DlnaEndpoint.PortFor(config));
            api.Discovery = discovery;
            Assert.True(api.SetDlna(true));

            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            async Task<string> UrlBase()
            {
                var xml = await http.GetStringAsync($"http://127.0.0.1:{dlnaPort}/description.xml");
                return System.Text.RegularExpressions.Regex.Match(xml, "<URLBase>(.*?)</URLBase>").Groups[1].Value;
            }
            Assert.Equal($"http://127.0.0.1:{dlnaPort}/", await UrlBase());          // precondition

            // Saved from the Config dialog - "takes effect at restart".
            config.ApplySettings(new Config.ServerConfig.SettingsOverrides { ControlPort = control + 100 });

            var told = await UrlBase();
            Assert.True(told == $"http://127.0.0.1:{dlnaPort}/",
                        $"after the control port was changed, the DLNA port's description sent a television to {told}, "
                        + $"where nothing listens - the DLNA listener is still on {dlnaPort}");
            Assert.Equal(dlnaPort, api.DlnaPort);

            // Off and on again before the restart: the same port as before,
            // which is the one the network announcement names.
            api.SetDlna(false);
            api.SetDlna(true);
            string again;
            try { again = await UrlBase(); }
            catch (HttpRequestException e)
            {
                throw new Xunit.Sdk.XunitException(
                    $"switched off and on again, DLNA no longer answered on {dlnaPort}, the port it is announced on ({e.Message})");
            }
            Assert.Equal($"http://127.0.0.1:{dlnaPort}/", again);
        }
        finally
        {
            Services.UrlScheme.UseHttps(wasHttps);
        }
    }
}
