using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using J0kersMediaServer.Config;
using J0kersMediaServer.Control;
using J0kersMediaServer.Media;
using Xunit;
using TestServer = J0kersMediaServer.Tests.ShutdownOnCloseTests.TestServer;

namespace J0kersMediaServer.Tests;

/// <summary>
/// Conversions and file serving, from the 2026-09-17 audit: findings 7, 17, 22,
/// 23, 65, 66, 67, 78 and 79. Each test here was seen to fail on the code as
/// the audit found it, for the reason the audit gives, before it passed.
/// </summary>
public class AuditConversionTests
{
    /// <summary>A real manager over a folder of its own, deleted afterwards. As in ConversionTests.</summary>
    private sealed class Rig : IDisposable
    {
        public string Dir { get; }
        public string Media => Path.Combine(Dir, "media");
        public string Ffmpeg { get; }
        public FfmpegManager Manager { get; }

        public Rig()
        {
            Dir = Path.Combine(Path.GetTempPath(), "claude", "j0kers-conv-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Dir);
            Ffmpeg = TestFfmpeg.Require();
            Manager = new FfmpegManager(new FfmpegConfig { Path = Ffmpeg, VideoCodec = "libx264", Preset = "ultrafast" },
                                        Media, Dir);
        }

        public void Dispose()
        {
            Manager.Dispose();
            for (var attempt = 0; attempt < 20 && Directory.Exists(Dir); attempt++)
            {
                try { Directory.Delete(Dir, recursive: true); }
                catch { Thread.Sleep(250); }   // an ffmpeg that is still letting go of a file
            }
        }
    }

    private static async Task Until(Func<bool> condition, TimeSpan within, string failure)
    {
        var deadline = DateTime.UtcNow + within;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(100);
        }
    }

    private static async Task<JsonElement> GetJson(HttpClient http, string url)
    {
        using var r = await http.GetAsync(url);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"GET {url}: {(int)r.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>A finished conversion on disk, as the server would have made it.</summary>
    private static string FinishedConversion(string mediaRoot, string name, string source)
    {
        var dir = Path.Combine(mediaRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.m3u8"),
                          "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXTINF:2.0,\nseg_00000.ts\n#EXT-X-ENDLIST\n");
        File.WriteAllText(Path.Combine(dir, "seg_00000.ts"), "hours of encoding");
        File.WriteAllText(Path.Combine(dir, "source.txt"), source);
        return dir;
    }

    // ------------------------------------------ [07] the transcodes folder

    /// <summary>
    /// Saving a new transcodes folder moved the control API to it at once,
    /// while ffmpeg went on writing to the old one until a restart: every
    /// existing stream became "unknown stream" to delete, rebuild and
    /// subtitles. The folder this run uses stays the folder this run uses;
    /// the dialog shows what was saved.
    /// </summary>
    [Fact]
    public async Task A_saved_transcodes_folder_waits_for_the_restart()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        var running = Path.Combine(server.Dir, "media");
        var dir = FinishedConversion(running, "vod-film-1a2b3c4d", Path.Combine(server.Dir, "film.mkv"));
        var elsewhere = Path.Combine(server.Dir, "elsewhere");

        using (var save = await server.Http.PostAsync("api/settings", TestServer.Json(new { mediaRoot = elsewhere })))
        {
            var body = await save.Content.ReadAsStringAsync();
            Assert.True(save.IsSuccessStatusCode, $"saving the transcodes folder failed: {(int)save.StatusCode} {body}");
            Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("mediaRootChanged").GetBoolean(),
                        "precondition: the save was taken as a change of folder");
        }

        using (var purge = await server.Http.DeleteAsync("api/hls?stream=vod-film-1a2b3c4d&purge=1"))
        {
            var body = await purge.Content.ReadAsStringAsync();
            Assert.True(purge.IsSuccessStatusCode,
                        $"after saving a new transcodes folder, a conversion in the folder this run is still using "
                        + $"could not be deleted: {(int)purge.StatusCode} {body}");
        }
        Assert.False(Directory.Exists(dir), "the delete reported success and the conversion is still there");

        // The dialog shows what was chosen, not what is running...
        var settings = await GetJson(server.Http, "api/settings");
        Assert.Equal(Path.GetFullPath(elsewhere), settings.GetProperty("mediaRootResolved").GetString());
        // ...and a second save before the restart still says a restart is needed.
        using (var again = await server.Http.PostAsync("api/settings", TestServer.Json(new { mediaRoot = elsewhere })))
            Assert.True(JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement
                                    .GetProperty("mediaRootChanged").GetBoolean(),
                        "saved again before restarting, the new folder was reported as already in use");
    }

    /// <summary>
    /// The HLS server reads the folder when the services are rebuilt - a port
    /// or bind change, the power button - so a save that moved it live put the
    /// HLS port on the new folder while ffmpeg wrote to the old one: every
    /// stream a 404. What it reads must not move until the next start.
    /// </summary>
    [Fact]
    public void Saving_a_transcodes_folder_does_not_move_the_one_a_rebuilt_HLS_server_reads()
    {
        using var dir = new TempDir();
        var path = dir.File("server.json");
        File.WriteAllText(path, "{}");
        var config = ServerConfig.Load(path);
        var running = config.Hls.MediaRoot;
        var elsewhere = dir.File("elsewhere");

        config.UpdateSettings(new ServerConfig.SettingsOverrides { MediaRoot = elsewhere });
        Assert.True(config.Hls.MediaRoot == running,
                    $"a saved transcodes folder was applied to the running config ({config.Hls.MediaRoot}); "
                    + "a rebuilt HLS server would serve from it while ffmpeg writes to the old one");
        Assert.Equal(elsewhere, config.SavedMediaRoot);

        // and the next start uses it
        Assert.Equal(elsewhere, ServerConfig.Load(path).Hls.MediaRoot);
    }

    // ------------------------------------- [17] deleting while a player skips

    /// <summary>
    /// Deleting a conversion stopped its encoders once and then took its time
    /// over the folder, and a player still on the film asked for a segment:
    /// seek-ahead found the folder and its source.txt still there and started
    /// an encoder writing into it. That encoder's open files failed every
    /// retry of the delete, and it ran on to the end of the film. The state
    /// that race produces is a delete in progress when the request arrives -
    /// planted here directly rather than hoped for.
    /// </summary>
    [Fact]
    public void Nothing_starts_writing_into_a_conversion_while_it_is_being_deleted()
    {
        using var rig = new Rig();
        var source = TestFfmpeg.SlowSource(rig.Ffmpeg, Path.Combine(rig.Dir, "slow.mpg"));
        var stream = rig.Manager.VodStreamName(source)!;   // the name a play of this file would use
        var dir = Path.Combine(rig.Media, stream);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "source.txt"), source);
        File.WriteAllText(Path.Combine(dir, "height.txt"), "0");

        using (rig.Manager.HoldForRemoval(stream))
        {
            var started = rig.Manager.EnsureVodSegment(stream, 10);
            Assert.True(!started && rig.Manager.SeekJobCountFor(stream) == 0,
                        "a skip started a seek-ahead encoder into a conversion that was being deleted");
            var play = Record.Exception(() => rig.Manager.StartVod(source));
            Assert.True(play is IOException,
                        "a play started the conversion over in the folder that was being deleted"
                        + (play is null ? "" : $" (it failed another way: {play.GetType().Name}: {play.Message})"));
        }

        // Once the delete is over, either way, a skip is answered again.
        Assert.True(rig.Manager.EnsureVodSegment(stream, 10), "the hold outlived the delete");
        rig.Manager.CancelVod(stream);
    }

    /// <summary>
    /// Found in review of [17]. The refusal to start into a folder being
    /// deleted was a plain IOException, which the batch queue counts as the
    /// file failing to start - three of those and it is dropped for good. A
    /// file queued while its conversion is being purged could be dropped by
    /// three passes inside one delete. It now waits, uncounted.
    /// </summary>
    [Fact]
    public void A_queued_file_waiting_out_a_delete_is_not_dropped_as_failing_to_start()
    {
        using var rig = new Rig();
        var source = TestFfmpeg.SlowSource(rig.Ffmpeg, Path.Combine(rig.Dir, "slow.mpg"));
        var stream = rig.Manager.VodStreamName(source)!;
        try
        {
            using (rig.Manager.HoldForRemoval(stream))
            {
                Assert.Equal(1, rig.Manager.QueueVod(new[] { source }));   // the first pass
                for (var i = 0; i < 4; i++) rig.Manager.KickVodQueue();    // and four more
                Assert.True(rig.Manager.VodQueueSnapshot.Contains(source, StringComparer.OrdinalIgnoreCase),
                            "a queued file whose conversion was being deleted was dropped from the queue "
                            + "as failing to start");
            }
        }
        finally { rig.Manager.ClearVodQueue(); }
    }

    /// <summary>
    /// Found in review of [22]. The direct-play decision asked the codec cache
    /// before looking at the container, and the cache calls a container no
    /// browser opens "settled" without ever reading it - so every play of an
    /// MKV ran an ffprobe first, to reach the "no" its name already gave.
    /// </summary>
    [Fact]
    public void Deciding_that_an_mkv_is_not_played_as_it_stands_does_not_probe_it()
    {
        using var dir = new TempDir();
        var ffprobe = Path.Combine(Path.GetDirectoryName(TestFfmpeg.Require())!,
                                   OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        var codecs = new TvCodecs(dir.Path, ffprobe);
        var mkv = Path.Combine(dir.Path, "film.mkv");
        File.WriteAllBytes(mkv, new byte[4096]);

        Assert.False(codecs.PlaysInBrowserAsItStands(mkv));
        Assert.False(codecs.PlaysInBrowserAsItStands(mkv));
        Assert.True(codecs.ProbesRun == 0, $"deciding that an MKV needs converting ran ffprobe {codecs.ProbesRun} time(s)");
    }

    /// <summary>
    /// Found in review of [22]. An answer cached before the pixel format was
    /// recorded was read again on every request - and a browser sends one for
    /// every seek - for as long as that read kept failing (encoders have the
    /// disk): the per-seek probe [22] was fixing. Once per run now; the file
    /// keeps the answer it had. The read here fails because the file is not
    /// really a film.
    /// </summary>
    [Fact]
    public void A_file_played_as_it_stands_is_read_again_once_not_on_every_request()
    {
        using var dir = new TempDir();
        var ffprobe = Path.Combine(Path.GetDirectoryName(TestFfmpeg.Require())!,
                                   OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        var mp4 = Path.Combine(dir.Path, "film.mp4");
        File.WriteAllBytes(mp4, new byte[4096]);
        var info = new FileInfo(mp4);
        // what a build before the pixel format was recorded left in the cache
        File.WriteAllText(Path.Combine(dir.Path, "probe-cache.json"), JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [$"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"] = "h264|aac",
        }));
        var codecs = new TvCodecs(dir.Path, ffprobe);

        for (var i = 0; i < 5; i++)
            Assert.True(codecs.PlaysInBrowserAsItStands(mp4), "a file cached as H.264/AAC stopped playing as it stands");
        Assert.True(codecs.ProbesRun <= 1, $"five requests for one file ran ffprobe {codecs.ProbesRun} times");
    }

    // ------------------------------------------------- [22] /api/file probes

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
                                              SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    /// <summary>
    /// ffmpeg and ffprobe of the test's own, in a folder of its own: hard links
    /// to the real ones (copies where the volume will not link), so the test
    /// can take ffprobe away without touching the installed one.
    /// </summary>
    private static string OwnTools()
    {
        var real = TestFfmpeg.Require();
        var dir = Path.Combine(Path.GetTempPath(), "claude", "j0kers-tools-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        foreach (var exe in new[] { "ffmpeg.exe", "ffprobe.exe" })
        {
            var from = Path.Combine(Path.GetDirectoryName(real)!, exe);
            var to = Path.Combine(dir, exe);
            if (!CreateHardLink(to, from, IntPtr.Zero)) File.Copy(from, to);
        }
        return dir;
    }

    /// <summary>
    /// /api/file started an ffprobe on every request, a seek's Range request
    /// included, and a probe that came back with nothing - timed out behind a
    /// batch of encodes - turned into 415 "needs converting" for a file that
    /// was playing a moment before. Taking ffprobe away after the first
    /// request is that failure, made certain: a seek must still be answered.
    /// </summary>
    [Fact]
    public async Task A_seek_into_a_file_being_played_as_it_stands_does_not_probe_it_again()
    {
        var tools = OwnTools();
        try
        {
            using (var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true,
                                                       ffmpegPath: Path.Combine(tools, "ffmpeg.exe")))
            {
                var clip = TestFfmpeg.PlayableClip(TestFfmpeg.Require(), Path.Combine(server.Dir, "film.mp4"));
                var url = "api/file?path=" + Uri.EscapeDataString(clip);
                using (var first = await server.Http.GetAsync(url))
                    Assert.True(first.StatusCode == HttpStatusCode.OK,
                                $"precondition: the file plays as it stands ({(int)first.StatusCode})");

                TestFfmpeg.TakeAway(Path.Combine(tools, "ffprobe.exe"));

                using var seek = new HttpRequestMessage(HttpMethod.Get, url);
                seek.Headers.Range = new RangeHeaderValue(1000, null);
                using var r = await server.Http.SendAsync(seek);
                var body = r.StatusCode == HttpStatusCode.PartialContent ? "" : await r.Content.ReadAsStringAsync();
                Assert.True(r.StatusCode == HttpStatusCode.PartialContent,
                            $"a seek into a file that was already playing probed it again, and was answered "
                            + $"{(int)r.StatusCode} when that probe failed: {body}");
            }
        }
        finally
        {
            try { Directory.Delete(tools, recursive: true); } catch { /* the server may still be letting go */ }
        }
    }

    // -------------------------------------------- [23] direct play is watching

    /// <summary>
    /// A file a browser plays as it stands was never recorded as watched: not
    /// in Recently Watched, no resume point, not in Sessions, and not counted
    /// by the silence watch, which then took a server playing a film for idle.
    /// </summary>
    [Fact]
    public async Task Playing_a_file_as_it_stands_is_counted_as_watching_it()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg);
        using var owner = await server.SignedInAs("admin");
        var clip = TestFfmpeg.PlayableClip(ffmpeg, Path.Combine(server.Dir, "film.mp4"));

        using (var r = await owner.GetAsync("api/file?path=" + Uri.EscapeDataString(clip)))
        {
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"precondition: the file plays as it stands ({(int)r.StatusCode})");
            await r.Content.ReadAsByteArrayAsync();
        }

        var status = await GetJson(owner, "api/status");
        Assert.True(status.GetProperty("hls").GetProperty("viewers").GetInt32() >= 1,
                    "a film playing as it stands was not counted as a viewer - the silence watch would stop the server");

        var sessions = await GetJson(owner, "api/sessions");
        Assert.True(sessions.GetProperty("sessions").EnumerateArray()
                            .Any(s => s.GetProperty("protocol").GetString() == "file"),
                    "a film playing as it stands was missing from Sessions");

        var history = await GetJson(owner, "api/history");
        var entry = history.GetProperty("history").EnumerateArray()
                           .FirstOrDefault(e => string.Equals(e.GetProperty("path").GetString(), clip,
                                                              StringComparison.OrdinalIgnoreCase));
        Assert.True(entry.ValueKind == JsonValueKind.Object, "a film played as it stands never reached Recently Watched");
        Assert.True(entry.GetProperty("stream").GetString() == "",
                    "the file's path was recorded as a stream, which would make the entry unplayable");
        Assert.Equal("file", entry.GetProperty("kind").GetString());

        // and the position the watch tab reports under the file's path is kept
        using (var pos = await owner.PostAsync("api/history/position",
                   TestServer.Json(new { key = clip, seconds = 600, duration = 3600 })))
            Assert.True(JsonDocument.Parse(await pos.Content.ReadAsStringAsync()).RootElement
                                    .GetProperty("recorded").GetBoolean(),
                        "a position reported under the file's path found no history entry to go to");
    }

    // ----------------------------------------------- [65] folder summary cap

    /// <summary>
    /// The cap on a folder's summary counted videos only, so a tree with no
    /// videos in it was walked to the end - every file under C:\Windows for
    /// the Transcodes window opened at C:\ (measured here: 176,611 files, 23
    /// seconds cold), on the request thread, once per heartbeat.
    /// </summary>
    [Fact]
    public async Task A_folder_summary_stops_walking_a_huge_tree_with_no_videos_in_it()
    {
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true);
        var root = Path.Combine(server.Dir, "tree");
        var big = Directory.CreateDirectory(Path.Combine(root, "big")).FullName;
        for (var i = 0; i < 20_001; i++) File.Create(Path.Combine(big, $"note{i:D5}.txt")).Dispose();
        var small = Directory.CreateDirectory(Path.Combine(root, "small")).FullName;
        for (var i = 0; i < 10; i++) File.Create(Path.Combine(small, $"note{i}.txt")).Dispose();

        var listing = await GetJson(server.Http, "api/transcode/scan?path=" + Uri.EscapeDataString(root));
        JsonElement Summary(string name) => listing.GetProperty("entries").EnumerateArray()
                                                   .First(e => e.GetProperty("name").GetString() == name)
                                                   .GetProperty("summary");
        Assert.True(Summary("big").GetProperty("capped").GetBoolean(),
                    "a folder of 20,001 files and no videos was walked to its end: the cap counted videos only");
        Assert.False(Summary("small").GetProperty("capped").GetBoolean(), "a folder of ten files was reported as too large");
    }

    // ---------------------------------------------- [66] probe request order

    /// <summary>
    /// Folders the Transcodes window lists are probed in the background -
    /// first come, first served, each walked to its end before the next. Up to
    /// a drive root, then into a folder on it, and that folder's pills said
    /// "checking…" until the whole drive had been probed. The folder opened
    /// last goes first, and a long walk gives way part way through.
    ///
    /// Whether it went first is read from the order, not a clock: when the
    /// second folder is fully read, the first must still have files unread.
    /// Taken in arrival order, the first is always finished by then.
    /// </summary>
    [Fact]
    public async Task A_folder_opened_during_a_long_probe_walk_is_read_before_the_walk_finishes()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg);
        var clip = TestFfmpeg.PlayableClip(ffmpeg, Path.Combine(server.Dir, "clip.mp4"));
        var lib = Path.Combine(server.Dir, "lib");
        var big = Path.Combine(lib, "big");
        var a = Directory.CreateDirectory(Path.Combine(big, "A")).FullName;
        // Hard links: two thousand names for a couple of small files, each its
        // own entry to probe, and no disk spent on copies. NTFS allows a file
        // 1023 names, so a fresh copy is linked to every thousand.
        const int many = 2000;
        var basis = clip;
        for (var i = 0; i < many; i++)
        {
            if (i % 1000 == 0) File.Copy(clip, basis = Path.Combine(server.Dir, $"basis{i}.mp4"));
            var name = Path.Combine(a, $"c{i:D4}.mp4");
            if (!CreateHardLink(name, basis, IntPtr.Zero)) File.Copy(clip, name);
        }
        var small = Path.Combine(lib, "small");
        var b = Directory.CreateDirectory(Path.Combine(small, "B")).FullName;
        File.Copy(clip, Path.Combine(b, "film.mp4"));

        async Task<int> Unknown(string parent, string folder)
        {
            var listing = await GetJson(server.Http, "api/transcode/scan?path=" + Uri.EscapeDataString(parent));
            return listing.GetProperty("entries").EnumerateArray()
                          .First(e => e.GetProperty("name").GetString() == folder)
                          .GetProperty("summary").GetProperty("unknown").GetInt32();
        }

        // The drive root, as it were: listed, so queued for probing. The
        // background prober starts ten seconds after the server does.
        Assert.Equal(many, await Unknown(big, "A"));
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (await Unknown(big, "A") == many)
        {
            Assert.True(DateTime.UtcNow < deadline, "precondition: the probe walk never began");
            await Task.Delay(250);
        }

        // ...and now a folder on it is opened.
        Assert.Equal(1, await Unknown(small, "B"));
        deadline = DateTime.UtcNow.AddSeconds(120);
        while (await Unknown(small, "B") > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the folder opened second was never read");
            await Task.Delay(100);
        }
        var left = await Unknown(big, "A");
        Assert.True(left > 0, "the folder opened second was read only after the whole of the first had been walked");
    }

    // ------------------------------------------- [67] recycling its own folder

    /// <summary>
    /// The Transcodes panel's delete refused the server's own folder and what
    /// is inside it, and let through any folder that CONTAINS it: the server
    /// in D:\Media\j0kers and D:\Media ticked went to the Recycle Bin with
    /// the accounts, the library and the media-link key inside.
    ///
    /// Tested on the check the endpoint makes rather than through the
    /// endpoint: with the check wrong, the endpoint would really send a folder
    /// holding a running test server to this machine's Recycle Bin.
    /// </summary>
    [Theory]
    [InlineData(@"D:\Media\j0kers", @"D:\Media", true)]              // a folder the server is in: the finding
    [InlineData(@"D:\Media\j0kers", @"D:\Media\", true)]
    [InlineData(@"D:\Media\j0kers", @"D:\", true)]                   // the drive it is on
    [InlineData(@"D:\Media\j0kers", @"d:\media", true)]              // however it is spelled
    [InlineData(@"D:\Media\j0kers", @"D:\Media\j0kers", true)]       // the folder itself, as before
    [InlineData(@"D:\Media\j0kers", @"D:\Media\j0kers\users.json", true)]
    [InlineData(@"D:\Media\j0kers", @"D:\Media\Films", false)]       // a neighbour
    [InlineData(@"D:\Media\j0kers", @"D:\Media\j0kers-old", false)]  // a neighbour that shares the name's start
    [InlineData(@"D:\Media\j0kers", @"D:\Med", false)]
    [InlineData(@"D:\Media\j0kers", @"E:\", false)]                  // another drive
    public void Recycling_is_refused_for_anything_that_would_take_the_server_folder_with_it(
        string serverFolder, string ticked, bool refused)
    {
        Assert.Equal(refused, ControlApi.TouchesServerFolder(ticked, serverFolder));
    }

    // ------------------------------------------- [78] a cancel leaves nothing

    /// <summary>
    /// Cancelling from the Transcodes window left the partial conversion on
    /// disk - it only logged - and a queued conversion carries the keep
    /// marker, so the cache sweep never took it either: gigabytes of a
    /// half-done encode until a restart at least a day later.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_conversion_removes_what_it_had_written()
    {
        using var rig = new Rig();
        var film = TestFfmpeg.SlowSource(rig.Ffmpeg, Path.Combine(rig.Dir, "slow.mpg"));
        var (stream, _) = rig.Manager.StartVod(film, keep: true);   // as the queue starts one
        var dir = Path.Combine(rig.Media, stream);
        await Until(() => Directory.Exists(dir) && Directory.EnumerateFiles(dir, "seg_*").Any(),
                    TimeSpan.FromSeconds(60), "precondition: the conversion never wrote a segment");
        Assert.True(File.Exists(Path.Combine(dir, FfmpegManager.KeepMarker)), "precondition: marked to keep");

        Assert.True(rig.Manager.CancelVod(stream), "precondition: there was a conversion to cancel");
        Assert.False(Directory.Exists(dir),
                     "the cancelled conversion was left on disk - marked to keep, so the cache would never take it");
        Assert.Empty(Directory.EnumerateDirectories(rig.Media, ".discarded-*"));
    }

    // --------------------------------------- [79] the watchdog's stuck job

    /// <summary>
    /// The watchdog kills a conversion that has written nothing for ten
    /// minutes - a share that dropped out under a read, say - and its comment
    /// said the file stayed in the queue. It did not: the exit handler took
    /// the job off, nothing put the file back, and the batch lost it for good.
    /// Ten silent minutes are stood in for by setting the watchdog's limits to
    /// nothing, so the one job running counts as stuck.
    /// </summary>
    [Fact]
    public async Task A_conversion_the_watchdog_kills_goes_back_in_the_queue()
    {
        using var rig = new Rig();
        var stuck = TestFfmpeg.SlowSource(rig.Ffmpeg, Path.Combine(rig.Dir, "stuck.mpg"));
        var next = Path.Combine(rig.Dir, "next.mpg");
        File.Copy(stuck, next);
        rig.Manager.SetQueueSettings(1, 0);
        rig.Manager.QueueVod(new[] { stuck, next });
        var stream = rig.Manager.VodStreamName(stuck)!;
        Assert.True(rig.Manager.ActiveVodStreams.Contains(stream), "precondition: the first file is converting");
        var dir = Path.Combine(rig.Media, stream);
        await Until(() => Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any(),
                    TimeSpan.FromSeconds(30), "precondition: the conversion never wrote anything");

        rig.Manager.VodStartGrace = TimeSpan.Zero;
        rig.Manager.VodStuckAfter = TimeSpan.Zero;
        rig.Manager.CheckVodJobs();

        await Until(() => !rig.Manager.ActiveVodStreams.Contains(stream) && rig.Manager.VodQueueSnapshot.Contains(stuck),
                    TimeSpan.FromSeconds(20),
                    "a queued conversion the watchdog killed as stuck was dropped from the batch for good "
                    + $"(queued now: {string.Join(", ", rig.Manager.VodQueueSnapshot.Select(Path.GetFileName))})");
        // and the queue carries on with the next file in the meantime, the
        // stuck one waiting behind it rather than holding it up
        await Until(() => rig.Manager.VodStatusFor(next) == FfmpegManager.VodState.Converting, TimeSpan.FromSeconds(20),
                    "the file queued behind the stuck one was never started");
        Assert.Contains(stuck, rig.Manager.VodQueueSnapshot);
    }
}
