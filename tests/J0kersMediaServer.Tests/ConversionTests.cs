using System.Net;
using System.Text.Json;
using J0kersMediaServer.Config;
using J0kersMediaServer.Media;
using Xunit;
using TestServer = J0kersMediaServer.Tests.ShutdownOnCloseTests.TestServer;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The ffmpeg / conversion findings from the 2026-09-17 audit. Most run a real
/// FfmpegManager against a real ffmpeg in a throwaway folder; the stderr
/// samples below are what ffmpeg 8.1.2 actually printed on this machine
/// (driver 616.56, RTX 4080 SUPER), not reconstructions.
/// </summary>
public class ConversionTests
{
    /// <summary>A real manager over a folder of its own, deleted afterwards.</summary>
    private sealed class Rig : IDisposable
    {
        public string Dir { get; }
        public string Media => Path.Combine(Dir, "media");
        public string Ffmpeg { get; }
        public FfmpegManager Manager { get; private set; }

        /// <param name="ownFfmpeg">Run on an ffmpeg of its own, which the test can take away (see TestFfmpeg.Disposable).</param>
        public Rig(string videoCodec = "libx264", bool ownFfmpeg = false)
        {
            Dir = Path.Combine(Path.GetTempPath(), "claude", "j0kers-conv-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Dir);
            Ffmpeg = ownFfmpeg ? TestFfmpeg.Disposable(Dir) : TestFfmpeg.Require();
            Manager = Open(videoCodec);
        }

        private FfmpegManager Open(string videoCodec) =>
            new(new FfmpegConfig { Path = Ffmpeg, VideoCodec = videoCodec, Preset = "ultrafast" }, Media, Dir);

        /// <summary>The same folder, as a server restarted with a different encoder would see it.</summary>
        public void Reopen(string videoCodec)
        {
            Manager.Dispose();
            Manager = Open(videoCodec);
        }

        public string Clip(string name, params string[] args)
        {
            var path = Path.Combine(Dir, name);
            TestFfmpeg.Run(Ffmpeg, args.Append(path).ToArray());
            return path;
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

    /// <summary>
    /// Until the conversion has exited AND its exit has been judged: a job is
    /// reported as converting until its exit handler has decided whether it
    /// ended early, so asking any sooner would test nothing.
    /// </summary>
    private static async Task WaitUntilJudged(FfmpegManager manager, string source, string stream)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (manager.ActiveVodStreams.Contains(stream)
               || manager.VodStatusFor(source) == FfmpegManager.VodState.Converting)
        {
            Assert.True(DateTime.UtcNow < deadline, $"the conversion of {stream} never finished");
            await Task.Delay(200);
        }
    }

    // ------------------------------------------------ a GPU that says no (F2)

    /// <summary>The 13th h264_nvenc session at once, refused. Ten lines; the first is the one that says why.</summary>
    private static readonly string[] RefusedSession =
    {
        "[h264_nvenc @ 0000014d255308c0] OpenEncodeSessionEx failed: incompatible client key (21): (no details)",
        "[h264_nvenc @ 0000014d255308c0] No capable devices found",
        "[vost#0:0/h264_nvenc @ 0000014d2552fe40] [enc:h264_nvenc @ 0000014d239fc2c0] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.",
        "[vf#0:0 @ 0000014d25b8f900] Error sending frames to consumers: Generic error in an external library",
        "[vf#0:0 @ 0000014d25b8f900] Task finished with error code: -542398533 (Generic error in an external library)",
        "[vf#0:0 @ 0000014d25b8f900] Terminating thread with return code -542398533 (Generic error in an external library)",
        "[vost#0:0/h264_nvenc @ 0000014d2552fe40] [enc:h264_nvenc @ 0000014d239fc2c0] Could not open encoder before EOF",
        "[vost#0:0/h264_nvenc @ 0000014d2552fe40] Task finished with error code: -22 (Invalid argument)",
        "[vost#0:0/h264_nvenc @ 0000014d2552fe40] Terminating thread with return code -22 (Invalid argument)",
        "[out#0/null @ 0000014d239efe40] Nothing was written into output file, because at least one of its streams received no packets.",
    };

    /// <summary>An 8192-wide source, which h264_nvenc cannot take: a fault in the file, with no session involved.</summary>
    private static readonly string[] TooWideForNvenc =
    {
        "[h264_nvenc @ 000001a19d94f140] No capable devices found",
        "[vost#0:0/h264_nvenc @ 000001a19d94e6c0] [enc:h264_nvenc @ 000001a19d8f7a80] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.",
        "[vf#0:0 @ 000001a19d96e040] Error sending frames to consumers: Generic error in an external library",
        "[vf#0:0 @ 000001a19d96e040] Task finished with error code: -542398533 (Generic error in an external library)",
        "[vf#0:0 @ 000001a19d96e040] Terminating thread with return code -542398533 (Generic error in an external library)",
        "[vost#0:0/h264_nvenc @ 000001a19d94e6c0] [enc:h264_nvenc @ 000001a19d8f7a80] Could not open encoder before EOF",
        "[vost#0:0/h264_nvenc @ 000001a19d94e6c0] Task finished with error code: -22 (Invalid argument)",
        "[vost#0:0/h264_nvenc @ 000001a19d94e6c0] Terminating thread with return code -22 (Invalid argument)",
        "[out#0/null @ 000001a19d9421c0] Nothing was written into output file, because at least one of its streams received no packets.",
    };

    private static string TailOf(IEnumerable<string> lines)
    {
        var tail = new FfmpegManager.StderrTail();
        foreach (var l in lines) tail.Add(l);
        return tail.ToString();
    }

    /// <summary>
    /// A file the encoder cannot take was read as the GPU refusing a session:
    /// put back on the queue twenty times, and the concurrency ceiling lowered
    /// a step on every attempt for the rest of the run.
    /// </summary>
    [Fact]
    public void A_file_the_encoder_cannot_take_is_not_a_refused_GPU_session()
    {
        Assert.False(FfmpegManager.IsGpuSessionRefusal(nvenc: true, TailOf(TooWideForNvenc)),
                     "an 8192-wide source was taken for a refused GPU session");
        Assert.False(FfmpegManager.IsGpuSessionRefusal(nvenc: true, string.Join(" | ", TooWideForNvenc)),
                     "\"No capable devices found\" was taken for a refused GPU session - it is printed for a bad file too");
    }

    [Fact]
    public void Only_an_NVENC_job_can_be_refused_a_GPU_session()
    {
        Assert.False(FfmpegManager.IsGpuSessionRefusal(nvenc: false, string.Join(" | ", RefusedSession)),
                     "a software encode was treated as refused a GPU session");
    }

    /// <summary>
    /// The one line that says a session was refused comes first, and nine
    /// follow it. The tail the exit handler reads was the last eight - so
    /// matching on that line alone, without keeping it, would recognise no
    /// real refusal at all and lose every conversion the GPU turned away.
    /// </summary>
    [Fact]
    public void A_real_refusal_is_still_recognised_after_its_line_scrolls_out_of_the_tail()
    {
        Assert.True(FfmpegManager.IsGpuSessionRefusal(nvenc: true, TailOf(RefusedSession)),
                    "a refused GPU session was not recognised from what the exit handler is given");
    }

    /// <summary>The learned ceiling only ever came down; the Transcodes setting could not raise it again.</summary>
    [Fact]
    public void Setting_how_many_at_a_time_forgets_the_learned_GPU_limit()
    {
        using var rig = new Rig();
        rig.Manager.GpuSessionCeiling = 2;
        rig.Manager.SetQueueSettings(6, null);
        Assert.Equal(int.MaxValue, rig.Manager.GpuSessionCeiling);

        // but a save that leaves the number as it was - the dashboard sends it
        // with the stagger too - does not throw away what the card taught
        rig.Manager.GpuSessionCeiling = 3;
        rig.Manager.SetQueueSettings(6, 10);
        Assert.True(rig.Manager.GpuSessionCeiling == 3, "changing only the stagger forgot the GPU's learned limit");
    }

    // --------------------------------------- which conversion is this one (F6)

    /// <summary>
    /// Switching from HEVC to h264 so a browser could play the library found
    /// every HEVC conversion again and called it done - the new codec never
    /// applied to a single file. And the same the other way.
    /// </summary>
    [Fact]
    public void A_conversion_in_another_codec_is_not_this_one()
    {
        using var rig = new Rig("libx265");
        var source = rig.Clip("film.mkv", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5", "-t", "1",
                              "-c:v", "libx264", "-preset", "ultrafast");

        var hevc = rig.Manager.VodStreamName(source)!;
        Directory.CreateDirectory(Path.Combine(rig.Media, hevc));   // an HEVC conversion already on disk

        rig.Reopen("libx264");
        var h264 = rig.Manager.VodStreamName(source)!;
        Assert.True(hevc != h264, "switched to h264, and the HEVC conversion was handed back as this one");

        Directory.Delete(Path.Combine(rig.Media, hevc));
        Directory.CreateDirectory(Path.Combine(rig.Media, h264));   // now only an h264 one exists
        rig.Reopen("libx265");
        Assert.True(rig.Manager.VodStreamName(source) != h264,
                    "switched to HEVC, and the h264 conversion was handed back as this one");
    }

    /// <summary>
    /// The other side of the rule above. When the card fails its check at
    /// startup, a configured hevc_nvenc runs as software h264 for the day - and
    /// a library converted in HEVC, which is still what was asked for, must not
    /// vanish and be converted again because of it. Copy mode asks for no codec
    /// at all, so every earlier conversion still stands.
    /// </summary>
    [Fact]
    public void A_conversion_in_the_codec_that_was_asked_for_is_still_found()
    {
        using var rig = new Rig("libx265");
        var source = rig.Clip("film.mkv", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5", "-t", "1",
                              "-c:v", "libx264", "-preset", "ultrafast");
        var hevc = rig.Manager.VodStreamName(source)!;
        var hevcDir = Path.Combine(rig.Media, hevc);
        Directory.CreateDirectory(hevcDir);
        File.WriteAllText(Path.Combine(hevcDir, "index.m3u8"),
                          "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:1.0,\nseg_00000.ts\n#EXT-X-ENDLIST\n");

        // HEVC configured; running as h264 because the card said no
        typeof(FfmpegManager).GetProperty("VideoEncoder")!.GetSetMethod(nonPublic: true)!
                             .Invoke(rig.Manager, new object[] { "libx264" });
        Assert.True(rig.Manager.VodStreamName(source) == hevc,
                    "a fallback to software h264 for the day hid the HEVC conversion the owner asked for");

        // ...but only a FINISHED one. An unfinished HEVC folder would be
        // cleared and converted into by today's h264 encoder, and found later
        // as the HEVC conversion.
        File.WriteAllText(Path.Combine(hevcDir, "index.m3u8"), "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:1.0,\nseg_00000.ts\n");
        Assert.True(rig.Manager.VodStreamName(source) != hevc,
                    "today's h264 encoder was pointed at an unfinished HEVC conversion's folder");

        File.WriteAllText(Path.Combine(hevcDir, "index.m3u8"),
                          "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:1.0,\nseg_00000.ts\n#EXT-X-ENDLIST\n");
        rig.Reopen("copy");
        Assert.True(rig.Manager.VodStreamName(source) == hevc, "switching to copy mode hid an existing conversion");
    }

    // ----------------------------------------- copying what cannot play (F5)

    private static string[] Source(string pixFmt, int sampleRate) => new[]
    {
        "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
        "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate={sampleRate}",
        "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", pixFmt, "-c:a", "aac",
    };

    /// <summary>
    /// 10-bit H.264 is h264 by name and plays almost nowhere; 24 kHz AAC is aac
    /// by name and mute on a television. Copying matched the name and carried
    /// both through untouched, into a conversion counted as finished.
    /// </summary>
    [Fact]
    public void Ten_bit_h264_and_24kHz_AAC_are_converted_not_copied()
    {
        using var rig = new Rig();
        var hi10 = rig.Clip("hi10p.mkv", Source("yuv420p10le", 24000));
        var (video, audio) = rig.Manager.CopyableStreams(hi10, 0);
        Assert.False(audio, "24 kHz AAC would have been copied - a television plays it mute");
        Assert.False(video, "10-bit H.264 would have been copied - neither Chrome nor a TV decodes it");

        // and what does play is still packaged rather than encoded again
        var plain = rig.Clip("plain.mkv", Source("yuv420p", 48000));
        Assert.Equal((true, true), rig.Manager.CopyableStreams(plain, 0));
        var cd = rig.Clip("cd-rate.mkv", Source("yuv420p", 44100));
        Assert.Equal((true, true), rig.Manager.CopyableStreams(cd, 0));
    }

    [Fact]
    public void Ten_bit_h264_is_not_handed_to_a_browser_as_is()
    {
        using var rig = new Rig();
        var hi10 = rig.Clip("hi10p.mp4", Source("yuv420p10le", 48000));
        Assert.False(rig.Manager.CanPlayDirectly(hi10), "10-bit H.264 was offered to the browser as it is");
        var plain = rig.Clip("plain.mp4", Source("yuv420p", 48000));
        Assert.True(rig.Manager.CanPlayDirectly(plain));
    }

    // ------------------------------------------------ an input that ends (F1)

    /// <summary>
    /// A source that stops being readable part way - a disk or share dropping
    /// out, a damaged file - is an input that ended, and ffmpeg ends the
    /// conversion normally: exit 0, end marker written, a third of the film
    /// behind it. It was then "converted" for good.
    /// </summary>
    [Fact]
    public async Task A_conversion_whose_source_ends_early_is_not_counted_as_converted()
    {
        using var rig = new Rig();
        var full = rig.Clip("full.mp4", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
                            "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "60",
                            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                            "-c:a", "aac", "-movflags", "+faststart");
        // cut off at 40%: the header still says sixty seconds
        var cut = Path.Combine(rig.Dir, "cut.mp4");
        var bytes = File.ReadAllBytes(full);
        File.WriteAllBytes(cut, bytes[..(int)(bytes.Length * 0.4)]);

        var (stream, _) = rig.Manager.StartVod(cut);
        await WaitUntilJudged(rig.Manager, cut, stream);
        Assert.True(File.ReadAllText(Path.Combine(rig.Media, stream, "index.m3u8")).Contains("#EXT-X-ENDLIST"),
                    "precondition: ffmpeg marks a conversion whose input ended early as finished");

        Assert.True(rig.Manager.VodStatusFor(cut) == FfmpegManager.VodState.None,
                    "a conversion of 40% of the film is reported as converted");
        Assert.False(rig.Manager.IsVodComplete(stream), "a conversion of 40% of the film counts as complete");
        var (again, ready) = rig.Manager.StartVod(cut);
        Assert.False(ready, "asking for the film again handed back the short conversion instead of converting it");

        // Converted again, it stops in exactly the same place: that is the
        // file, not an outage, and it is accepted rather than redone for ever -
        // by that rule, and not by the second run simply going unjudged.
        await WaitUntilJudged(rig.Manager, cut, again);
        Assert.True(rig.Manager.VodStatusFor(cut) == FfmpegManager.VodState.Done,
                    "a file that really ends there was never accepted as converted");
        Assert.Equal(1, rig.Manager.SameEndAccepted);

        // and the whole film, converted, is converted
        var (whole, _) = rig.Manager.StartVod(full);
        await WaitUntilJudged(rig.Manager, full, whole);
        Assert.Equal(FfmpegManager.VodState.Done, rig.Manager.VodStatusFor(full));
    }

    /// <summary>
    /// Queued for conversion, it ends early; put back in the queue, it ends in
    /// the same place, and is accepted as the file's own end. It neither
    /// drops out of the batch nor circles for ever.
    /// </summary>
    [Fact]
    public async Task A_batch_conversion_that_ends_early_is_put_back_and_settles()
    {
        using var rig = new Rig();
        var full = rig.Clip("full.mkv", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
                            "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "60",
                            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac");
        var cut = Path.Combine(rig.Dir, "cut.mkv");
        var bytes = File.ReadAllBytes(full);
        File.WriteAllBytes(cut, bytes[..(int)(bytes.Length * 0.4)]);

        rig.Manager.QueueVod(new[] { cut });
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (rig.Manager.VodStatusFor(cut) != FfmpegManager.VodState.Done)
        {
            Assert.True(DateTime.UtcNow < deadline,
                        $"a queued conversion that ended early never settled (accepted: {rig.Manager.SameEndAccepted}, "
                        + $"queued: {rig.Manager.VodQueueDepth}, state: {rig.Manager.VodStatusFor(cut)})");
            await Task.Delay(250);
        }
        Assert.Equal(1, rig.Manager.SameEndAccepted);
        Assert.Equal(0, rig.Manager.VodQueueDepth);
    }

    /// <summary>
    /// "Done" was remembered by stream name and checked first, against nothing
    /// but a playlist existing. A conversion deleted by the cache sweep and
    /// made again - a television asking for it - was then "done" through the
    /// whole of its remaking, and past any verdict at the end of it.
    /// </summary>
    [Fact]
    public async Task A_conversion_made_again_is_not_reported_done_while_it_is_being_made()
    {
        using var rig = new Rig();
        var film = rig.Clip("film.mkv", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-t", "90",
                            "-c:v", "mpeg2video", "-q:v", "8", "-an");
        var (stream, _) = rig.Manager.StartVod(film);
        await WaitUntilJudged(rig.Manager, film, stream);
        Assert.Equal(FfmpegManager.VodState.Done, rig.Manager.VodStatusFor(film));   // now remembered as done

        Directory.Delete(Path.Combine(rig.Media, stream), recursive: true);   // the cache sweep
        rig.Manager.StartVod(film);                                           // and a TV asking for it
        var playlist = Path.Combine(rig.Media, stream, "index.m3u8");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(playlist))
        {
            Assert.True(DateTime.UtcNow < deadline, "the second conversion never wrote a playlist");
            await Task.Delay(50);
        }
        Assert.True(rig.Manager.ActiveVodStreams.Contains(stream), "precondition: still converting");
        Assert.True(rig.Manager.VodStatusFor(film) == FfmpegManager.VodState.Converting,
                    "a conversion being made again was reported as done");
    }

    /// <summary>
    /// A conversion whose input ran out is unfinished like any other: the
    /// startup sweep clears it once nothing has touched it for the grace
    /// period, instead of keeping it for ever under a name a changed source no
    /// longer leads back to.
    /// </summary>
    [Fact]
    public void An_ended_early_conversion_is_swept_like_any_unfinished_one()
    {
        var dir = Path.Combine(Path.GetTempPath(), "claude", "j0kers-conv-" + Guid.NewGuid().ToString("N")[..8], "vod-x-1a2b3c4d");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "index.m3u8"), "#EXTM3U\n#EXTINF:6.0,\nseg_00000.ts\n#EXT-X-ENDLIST\n");
            File.WriteAllText(Path.Combine(dir, "seg_00000.ts"), "x");
            File.WriteAllText(Path.Combine(dir, FfmpegManager.EndedEarlyMarker), "ended early");
            var old = DateTime.UtcNow.AddDays(-3);
            foreach (var f in Directory.EnumerateFiles(dir)) File.SetLastWriteTimeUtc(f, old);

            Assert.True(FfmpegManager.IsStalePartial(dir, DateTime.UtcNow.AddHours(-24), out _, out _, out _),
                        "an ended-early conversion untouched for days was not treated as unfinished");
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); }
    }

    /// <summary>
    /// The length a source is probed at cannot be trusted on its own - so a
    /// whole, healthy file whose header gets its length wrong must not be
    /// taken for one that was cut off. Measured: an MP3 with cover art
    /// converts to 0 seconds of segments against a probed 180, and a VBR MP3
    /// without a Xing header probes at 441 seconds and holds 180. Judged by
    /// length alone, both were "ended early" - thrown away and converted again
    /// on every play, and never shown as converted.
    /// </summary>
    [Fact]
    public async Task A_whole_file_whose_length_is_misreported_is_still_converted()
    {
        using var rig = new Rig();
        var cover = rig.Clip("cover.png", "-f", "lavfi", "-i", "testsrc2=size=300x300", "-frames:v", "1");
        var song = rig.Clip("song.mp3", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-i", cover,
                            "-t", "180", "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame", "-b:a", "128k",
                            "-c:v", "png", "-disposition:v", "attached_pic", "-id3v2_version", "3");
        var vbr = rig.Clip("vbr.mp3", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
                           "-f", "lavfi", "-i", "anoisesrc=r=44100:a=0.5",
                           "-filter_complex", "[0]atrim=0:1[q];[1]atrim=0:179[n];[q][n]concat=v=0:a=1",
                           "-c:a", "libmp3lame", "-q:a", "0", "-write_xing", "0");

        foreach (var file in new[] { song, vbr })
        {
            var (stream, _) = rig.Manager.StartVod(file);
            await WaitUntilJudged(rig.Manager, file, stream);
            Assert.True(rig.Manager.VodStatusFor(file) == FfmpegManager.VodState.Done,
                        $"{Path.GetFileName(file)}, a whole file whose length is reported wrongly, was not counted as converted");
            var (_, ready) = rig.Manager.StartVod(file);
            Assert.True(ready, $"{Path.GetFileName(file)} was thrown away and converted again when asked for");
        }
    }

    // ------------------------------------ items left open by 309 and 310

    private static string FfprobeFor(Rig rig) =>
        Path.Combine(Path.GetDirectoryName(TestFfmpeg.Require())!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

    private static string Probe(Rig rig, string file, string entries)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(FfprobeFor(rig))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-show_entries", entries, "-of", "csv=p=0", file })
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(20_000);
        return output.Trim();
    }

    /// <summary>
    /// An MP3 with its album cover attached: ffmpeg counts the cover as a video
    /// stream of one frame, and converting it that way listed the whole song as
    /// 0 seconds of segments (measured: 0 against 180) - nothing for a player
    /// to play through or seek in. It converts as the audio it is.
    /// </summary>
    [Fact]
    public async Task A_song_with_its_cover_attached_converts_as_the_song()
    {
        using var rig = new Rig();
        var cover = rig.Clip("cover.png", "-f", "lavfi", "-i", "testsrc2=size=300x300", "-frames:v", "1");
        var song = rig.Clip("song.mp3", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-i", cover,
                            "-t", "60", "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame", "-b:a", "128k",
                            "-c:v", "png", "-disposition:v", "attached_pic", "-id3v2_version", "3");

        var (stream, _) = rig.Manager.StartVod(song);
        await WaitUntilJudged(rig.Manager, song, stream);
        var dir = Path.Combine(rig.Media, stream);
        var seconds = FfmpegManager.PlaylistSeconds(File.ReadAllText(Path.Combine(dir, "index.m3u8")));
        Assert.True(Math.Abs(seconds - 60) < 2, $"a 60-second song converted to {seconds:0.#} seconds of segments");
        var first = Directory.EnumerateFiles(dir, "seg_*").OrderBy(f => f).First();
        Assert.True(!Probe(rig, first, "stream=codec_type").Contains("video"),
                    "the cover was carried into the conversion as a video stream");
    }

    /// <summary>
    /// Healthy content, a header that overstates its length, and one damaged
    /// patch part way: the input reports an error, and the conversion comes up
    /// short against the header - which read as "ended early" and threw the
    /// conversion away. But the input carried on to its real end after the
    /// error (measured: error at about 24 s, all 60 converted), which a
    /// cut-off source never does. It is converted, first time.
    /// </summary>
    [Fact]
    public async Task A_damaged_file_that_was_read_to_its_end_is_converted()
    {
        using var rig = new Rig();
        var clean = rig.Clip("clean.mkv", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
                             "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "90",
                             "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac");
        var bytes = File.ReadAllBytes(clean);
        // Matroska Duration: 0x44 0x89 then 0x88 and an 8-byte big-endian float. Double it.
        var at = -1;
        for (var i = 0; i < Math.Min(bytes.Length, 4096) - 11; i++)
            if (bytes[i] == 0x44 && bytes[i + 1] == 0x89 && bytes[i + 2] == 0x88) { at = i + 3; break; }
        Assert.True(at > 0, "precondition: found the MKV's Duration element");
        var raw = bytes[at..(at + 8)];
        Array.Reverse(raw);
        var doubled = BitConverter.GetBytes(BitConverter.ToDouble(raw) * 2);
        Array.Reverse(doubled);
        doubled.CopyTo(bytes, at);
        // and a damaged patch 30% in
        Array.Clear(bytes, (int)(bytes.Length * 0.3), 8192);
        var damaged = Path.Combine(rig.Dir, "damaged.mkv");
        File.WriteAllBytes(damaged, bytes);

        var (stream, _) = rig.Manager.StartVod(damaged);
        await WaitUntilJudged(rig.Manager, damaged, stream);
        Assert.True(rig.Manager.VodStatusFor(damaged) == FfmpegManager.VodState.Done,
                    "a file read to its end, damaged in one place and with a header claiming double its length, "
                    + "was taken for one that was cut off");
        Assert.Equal(0, rig.Manager.SameEndAccepted);   // converted first time, not accepted on a second go
    }

    private string LongClip(Rig rig, string name, int seconds, params string[] extra) =>
        rig.Clip(name, new[] { "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25", "-f", "lavfi", "-i", "sine=sample_rate=48000",
                               "-t", seconds.ToString(), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                               "-c:a", "aac" }.Concat(extra).ToArray());

    /// <summary>
    /// A cut-off file with more than thirty seconds of it left: 84 seconds of a
    /// 120-second MKV, remuxed. The remux takes about a second, before ffmpeg
    /// has reported its progress once - which is why judging by *when* the
    /// input complained (a version of this that never shipped) took it for a
    /// file that had recovered and counted it converted. The input says it
    /// ended ("File ended prematurely"), and that is what counts.
    /// </summary>
    [Fact]
    public async Task A_long_film_cut_off_part_way_is_not_counted_as_converted()
    {
        using var rig = new Rig();
        var full = LongClip(rig, "full.mkv", 120);
        var cut = Path.Combine(rig.Dir, "cut.mkv");
        var bytes = File.ReadAllBytes(full);
        File.WriteAllBytes(cut, bytes[..(int)(bytes.Length * 0.7)]);

        var (stream, _) = rig.Manager.StartVod(cut);
        await WaitUntilJudged(rig.Manager, cut, stream);
        Assert.True(rig.Manager.VodStatusFor(cut) == FfmpegManager.VodState.None,
                    "84 seconds of a 120-second film was counted as converted");
    }

    /// <summary>
    /// An MP4 damaged 30% in and copied: ffmpeg stops at the damage, 35
    /// seconds of 120, and exits with an error while still writing the end
    /// marker - with nothing from the input to say so. ffmpeg's own failure is
    /// the evidence.
    /// </summary>
    [Fact]
    public async Task A_copy_that_stops_at_damage_and_fails_is_not_counted_as_converted()
    {
        using var rig = new Rig();
        var full = LongClip(rig, "full.mp4", 120, "-movflags", "+faststart");
        var bytes = File.ReadAllBytes(full);
        Array.Clear(bytes, (int)(bytes.Length * 0.3), 8192);
        var damaged = Path.Combine(rig.Dir, "damaged.mp4");
        File.WriteAllBytes(damaged, bytes);

        var (stream, _) = rig.Manager.StartVod(damaged);
        await WaitUntilJudged(rig.Manager, damaged, stream);
        Assert.True(File.ReadAllText(Path.Combine(rig.Media, stream, "index.m3u8")).Contains("#EXT-X-ENDLIST"),
                    "precondition: ffmpeg wrote its end marker anyway");
        Assert.True(rig.Manager.VodStatusFor(damaged) == FfmpegManager.VodState.None,
                    "a copy that failed at the damage, a third of the way in, was counted as converted");
    }

    /// <summary>A fake conversion folder for a source, as a partly made one would look to a seek.</summary>
    private static string PartConversion(Rig rig, string name, string source, int height)
    {
        var dir = Path.Combine(rig.Media, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "source.txt"), source);
        File.WriteAllText(Path.Combine(dir, "height.txt"), height.ToString());
        return name;
    }

    /// <summary>
    /// Seek-ahead encoders were capped at two per film and nothing across
    /// films - and they do not count toward the viewer's limit on conversions,
    /// because a seek is not one. Skipping around in a few partly converted
    /// films started encoders outside every limit. Now no more run at once, in
    /// all, than the owner's "how many at a time".
    /// </summary>
    [Fact]
    public void Skipping_around_in_several_films_cannot_start_encoders_without_limit()
    {
        using var rig = new Rig();
        var source = TestFfmpeg.SlowSource(rig.Ffmpeg, Path.Combine(rig.Dir, "slow.mpg"));
        for (var i = 0; i < 4; i++)
        {
            var stream = PartConversion(rig, $"vod-film{i}-0000000{i}", source, 0);
            Assert.True(rig.Manager.EnsureVodSegment(stream, 10), "precondition: a seek-ahead job was started");
        }
        var cap = Math.Max(2, rig.Manager.MaxConcurrentVod);
        Assert.True(rig.Manager.SeekJobCount <= cap,
                    $"{rig.Manager.SeekJobCount} seek-ahead encoders running across films, with \"how many at a "
                    + $"time\" set to {rig.Manager.MaxConcurrentVod}");
    }

    /// <summary>
    /// A film whose name ends in a resolution - "Film (2019) 1080p" - names its
    /// full-resolution conversion "...-2019-1080p-hash". Seek-ahead read 1080
    /// off that, so every stand-in segment was a scaled re-encode dropped
    /// between copied full-size neighbours. It reads the height the conversion
    /// recorded instead.
    /// </summary>
    [Fact]
    public async Task A_seek_in_a_film_named_after_a_resolution_matches_its_neighbours()
    {
        using var rig = new Rig();
        var source = rig.Clip("film.mp4", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
                              "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "60",
                              "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                              "-force_key_frames", "expr:gte(t,n_forced*6)", "-c:a", "aac");
        var stream = PartConversion(rig, "vod-film-2019-1080p-abcdef12", source, 0);
        Assert.True(rig.Manager.EnsureVodSegment(stream, 2));

        var dir = Path.Combine(rig.Media, stream);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string? segment = null;
        while (segment is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "the seek-ahead job never produced its first segment");
            var list = Path.Combine(dir, "seek_00002.m3u8");
            if (File.Exists(list))
            {
                string text;
                try { text = File.ReadAllText(list); } catch { text = ""; }
                var name = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("seg_00002"));
                if (name is not null) segment = Path.Combine(dir, name);
            }
            if (segment is null) await Task.Delay(200);
        }
        Assert.Equal("320,180", Probe(rig, segment, "stream=width,height").Split('\n')[0].Trim());
    }

    /// <summary>
    /// Where a conversion the GPU refused goes. The batch queue knows only
    /// file names and starts everything at full resolution, kept for good - so
    /// a refused 720p play for a television came back as a full-resolution
    /// conversion never to be evicted, and the 720p stream never appeared.
    /// </summary>
    [Theory]
    [InlineData(true, 0, true, "Batch")]         // the queue's own kind of job
    [InlineData(true, 720, true, "Batch")]       // kept and scaled: the queue carries its height now
    [InlineData(false, 0, true, "RetryHere")]    // an owner's play
    [InlineData(false, 720, true, "RetryHere")]  // a scaled play
    [InlineData(false, 720, false, "Report")]    // a viewer's play
    public void A_refused_conversion_is_retried_as_what_was_asked_for(bool keep, int height, bool requeue, string expected)
    {
        Assert.Equal(expected, FfmpegManager.RouteGpuRefusal(keep, height, requeue).ToString());
    }

    /// <summary>
    /// A refused play is tried again as itself - the same height, not kept -
    /// and cancelling it in the meantime calls the retry off. With no job and
    /// no queue entry during the wait, cancelling or deleting did nothing, and
    /// the retry brought the conversion back.
    /// </summary>
    [Fact]
    public async Task A_refused_play_is_retried_as_itself_and_can_be_called_off()
    {
        var delay = FfmpegManager.GpuRetryDelay;
        FfmpegManager.GpuRetryDelay = TimeSpan.FromMilliseconds(300);
        try
        {
            using var rig = new Rig();
            var film = TestFfmpeg.PlayableClip(rig.Ffmpeg, Path.Combine(rig.Dir, "film.mp4"));
            var stream = rig.Manager.VodStreamName(film, 120)!;

            rig.Manager.RetryAfterGpuRefusal(stream, film, 120, keep: false, "test");
            Assert.True(rig.Manager.CancelVod(stream), "cancelling reported nothing to cancel while a retry waited");
            await Task.Delay(1500);
            Assert.True(!Directory.Exists(Path.Combine(rig.Media, stream)),
                        "the conversion was cancelled while its retry waited, and the retry started it anyway");

            rig.Manager.RetryAfterGpuRefusal(stream, film, 120, keep: false, "test");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (rig.Manager.VodStatusFor(film, 120) == FfmpegManager.VodState.None)
            {
                Assert.True(DateTime.UtcNow < deadline, "the retry never started the conversion");
                await Task.Delay(100);
            }
            var dir = Path.Combine(rig.Media, stream);
            Assert.Equal("120", File.ReadAllText(Path.Combine(dir, "height.txt")).Trim());
            Assert.False(File.Exists(Path.Combine(dir, FfmpegManager.KeepMarker)), "a play was retried as one to keep");
        }
        finally { FfmpegManager.GpuRetryDelay = delay; }
    }

    /// <summary>
    /// A start that was waiting on the lock Dispose takes must not launch a
    /// job after everything was stopped - the job object would reap it in
    /// production, but it would still have been started for nothing.
    /// </summary>
    [Fact]
    public void Nothing_starts_after_the_manager_has_been_stopped()
    {
        using var rig = new Rig();
        var film = TestFfmpeg.PlayableClip(rig.Ffmpeg, Path.Combine(rig.Dir, "film.mp4"));
        rig.Manager.Dispose();
        Assert.Throws<ObjectDisposedException>(() => rig.Manager.StartVod(film));
    }

    /// <summary>
    /// The cap on seek-ahead encoders stops the requesting film's own earlier
    /// skip first - not a different film's, which someone may be watching.
    /// </summary>
    [Fact]
    public void A_skip_stops_its_own_films_earlier_skip_before_anyone_elses()
    {
        using var rig = new Rig();
        var source = TestFfmpeg.SlowSource(rig.Ffmpeg, Path.Combine(rig.Dir, "slow.mpg"));
        var theirs = PartConversion(rig, "vod-theirs-00000001", source, 0);
        var mine = PartConversion(rig, "vod-mine-00000002", source, 0);

        Assert.True(rig.Manager.EnsureVodSegment(theirs, 10));   // somebody else, first - so the oldest
        Thread.Sleep(50);
        Assert.True(rig.Manager.EnsureVodSegment(mine, 10));
        Thread.Sleep(50);
        Assert.True(rig.Manager.EnsureVodSegment(mine, 20));     // I skip again

        Assert.True(rig.Manager.SeekJobCountFor(theirs) == 1,
                    "my second skip stopped someone else's seek in another film, not my own earlier one");
        Assert.Equal(1, rig.Manager.SeekJobCountFor(mine));
    }

    /// <summary>
    /// A PIN, not a fix: the review of this work said a seek job stopped part
    /// way leaves the segment it was writing under its finished name, to be
    /// found later as "already made" and served cut short. Measured on ffmpeg
    /// 8.1.2, TS and fMP4 alike: the HLS muxer creates a segment's file only
    /// once the segment is complete, so a job killed at any point leaves
    /// nothing its own playlist does not list. No change was needed; this
    /// stays so that an ffmpeg that behaves otherwise is noticed.
    /// </summary>
    [Fact]
    public async Task A_seek_job_stopped_part_way_leaves_no_partial_segment()
    {
        using var rig = new Rig();
        var source = TestFfmpeg.SlowSource(rig.Ffmpeg, Path.Combine(rig.Dir, "slow.mpg"));
        var stream = PartConversion(rig, "vod-film-0000000a", source, 0);
        var dir = Path.Combine(rig.Media, stream);
        Assert.True(rig.Manager.EnsureVodSegment(stream, 10));

        // wait until it is writing its second segment, then stop it mid-way
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (Directory.EnumerateFiles(dir, "seg_*.seek00010.*").Count() < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "the seek job never got going");
            await Task.Delay(50);
        }
        rig.Manager.CancelVod(stream);
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (rig.Manager.SeekJobCountFor(stream) > 0) { Assert.True(DateTime.UtcNow < deadline); await Task.Delay(50); }
        await Task.Delay(1000);   // the exit handler runs on its own thread

        var listed = File.Exists(Path.Combine(dir, "seek_00010.m3u8"))
            ? File.ReadAllLines(Path.Combine(dir, "seek_00010.m3u8")).Where(l => !l.StartsWith('#')).Select(l => l.Trim()).ToHashSet()
            : new HashSet<string>();
        var unfinished = Directory.EnumerateFiles(dir, "seg_*.seek00010.*").Select(Path.GetFileName)
                                  .Where(f => !listed.Contains(f!)).ToList();
        Assert.True(unfinished.Count == 0, "a stopped seek job left a partial segment behind: " + string.Join(", ", unfinished));
    }

    /// <summary>
    /// Pressing Convert on a file a play already converted - or is converting -
    /// passed it over without marking it as one to keep, so the cache could
    /// evict it and a restart would not carry it over.
    /// </summary>
    [Fact]
    public async Task Converting_a_file_a_play_already_converted_keeps_it()
    {
        using var rig = new Rig();
        var film = TestFfmpeg.PlayableClip(rig.Ffmpeg, Path.Combine(rig.Dir, "film.mp4"));
        var (stream, _) = rig.Manager.StartVod(film);             // a play: not kept
        await WaitUntilJudged(rig.Manager, film, stream);
        Assert.False(File.Exists(Path.Combine(rig.Media, stream, FfmpegManager.KeepMarker)));

        rig.Manager.QueueVod(new[] { film });                      // then Convert
        Assert.True(File.Exists(Path.Combine(rig.Media, stream, FfmpegManager.KeepMarker)),
                    "asked to convert, a file already converted by a play was left disposable");
    }

    /// <summary>
    /// A queued conversion that is not full resolution - a kept 720p one being
    /// rebuilt, or one put back after the GPU refused it - comes back from a
    /// restart at its own height. The queue file held names only, so it came
    /// back at full resolution.
    /// </summary>
    [Fact]
    public void A_queued_conversion_keeps_its_height_across_a_restart()
    {
        using var rig = new Rig();
        var film = TestFfmpeg.PlayableClip(rig.Ffmpeg, Path.Combine(rig.Dir, "film.mp4"));
        File.WriteAllText(Path.Combine(rig.Dir, "transcode-queue.json"), JsonSerializer.Serialize(new
        {
            MaxParallel = 2, StaggerSeconds = 0, Waiting = new[] { film },
            Heights = new Dictionary<string, int> { [film] = 120 },
        }));
        rig.Reopen("libx264");
        rig.Manager.ResumeVodQueue();
        Assert.True(rig.Manager.VodStatusFor(film, 120) != FfmpegManager.VodState.None,
                    "a queued 120p conversion came back after a restart as something else");
        Assert.Equal(FfmpegManager.VodState.None, rig.Manager.VodStatusFor(film));
    }

    /// <summary>
    /// The queue file keeps what is still owed across a restart - and it took
    /// every running job, plays included, so a film somebody was watching at
    /// 120p came back after the restart as a full-resolution conversion marked
    /// never to be evicted. Only the queue's own kind of job is owed.
    /// </summary>
    [Fact]
    public void A_play_in_progress_is_not_carried_over_a_restart_as_a_batch_job()
    {
        using var rig = new Rig();
        var watched = TestFfmpeg.SlowSource(rig.Ffmpeg, Path.Combine(rig.Dir, "watched.mpg"));
        var queued = Path.Combine(rig.Dir, "queued.mpg");
        File.Copy(watched, queued);

        var keptScaled = Path.Combine(rig.Dir, "kept-scaled.mpg");
        File.Copy(watched, keptScaled);

        rig.Manager.StartVod(watched, height: 120, keep: false);   // somebody watching at 120p
        rig.Manager.StartVod(queued, keep: true);                  // a batch conversion
        rig.Manager.StartVod(keptScaled, height: 120, keep: true); // a kept 120p one being rebuilt
        rig.Manager.SetQueueSettings(null, null);                  // writes the queue file

        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Dir, "transcode-queue.json")));
        var waiting = saved.RootElement.GetProperty("Waiting").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(queued, waiting);
        Assert.True(!waiting.Contains(watched), "a film being watched at 120p was saved to be converted at full "
                                                + "resolution and kept, after the next restart");
        Assert.True(waiting.Contains(keptScaled), "a kept 120p conversion in progress was dropped at a restart");
        Assert.Equal(120, saved.RootElement.GetProperty("Heights").GetProperty(keptScaled).GetInt32());
    }

    // ----------------------------------------- the queue in an outage (F3)

    private static string UnusedDriveRoot()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var c = 'Z'; c >= 'G'; c--)
            if (!used.Contains(c)) return c + @":\";
        Assert.Fail("every drive letter from G to Z is in use; this test needs one that is not");
        return "";
    }

    /// <summary>
    /// A NAS or USB disk blinking out mid-batch: every queued file failed
    /// File.Exists, was logged as "no longer there", and the empty queue was
    /// written to disk. When the share came back, nothing was queued.
    /// </summary>
    [Fact]
    public void Files_on_a_drive_that_cannot_be_reached_stay_queued()
    {
        using var rig = new Rig();
        var root = UnusedDriveRoot();
        var files = new[] { root + @"films\a.mkv", root + @"films\b.mkv", root + @"films\c.mkv" };

        rig.Manager.QueueVod(files);
        Assert.True(rig.Manager.VodQueueDepth == 3,
                    $"the queue dropped files it could not reach: {rig.Manager.VodQueueDepth} of 3 left");
        var queueFile = Path.Combine(rig.Dir, "transcode-queue.json");
        var written = File.GetLastWriteTimeUtc(queueFile);
        Thread.Sleep(50);
        rig.Manager.KickVodQueue();   // the watchdog coming round
        rig.Manager.KickVodQueue();
        Assert.Equal(3, rig.Manager.VodQueueDepth);
        Assert.True(File.GetLastWriteTimeUtc(queueFile) == written,
                    "the queue file was rewritten on every watchdog tick with nothing in it changed");

        using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Dir, "transcode-queue.json"))))
        {
            var waiting = saved.RootElement.GetProperty("Waiting").EnumerateArray().Select(e => e.GetString()).ToList();
            foreach (var f in files) Assert.Contains(f, waiting);
        }

        // A file deleted from a drive that is there is gone, and still goes.
        var gone = Path.Combine(rig.Dir, "deleted-folder", "x.mkv");
        rig.Manager.QueueVod(new[] { gone });
        Assert.DoesNotContain(gone, rig.Manager.VodQueueSnapshot);
        Assert.Equal(3, rig.Manager.VodQueueDepth);
    }

    /// <summary>
    /// A start that fails for a reason that is not the file - ffmpeg cannot be
    /// launched, the transcodes drive is missing - failed the same way for
    /// every file, and one pass dequeued the entire batch and saved the empty
    /// queue over the real one.
    /// </summary>
    [Fact]
    public void A_start_that_fails_for_every_file_does_not_empty_the_queue()
    {
        using var rig = new Rig();
        var a = rig.Clip("a.mkv", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5", "-t", "1", "-c:v", "mpeg2video");
        var b = rig.Clip("b.mkv", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5", "-t", "1", "-c:v", "mpeg2video");
        // The transcodes folder is gone and a file sits where it was, so no
        // conversion can even be set up - nothing to do with a or b.
        Directory.Delete(rig.Media, recursive: true);
        File.WriteAllText(rig.Media, "not a folder");

        rig.Manager.QueueVod(new[] { a, b });
        Assert.True(rig.Manager.VodQueueSnapshot.SequenceEqual(new[] { a, b }),
                    $"a start failure that was not about the files emptied the queue: "
                    + $"{rig.Manager.VodQueueDepth} of 2 left");
        rig.Manager.KickVodQueue();
        Assert.Equal(new[] { a, b }, rig.Manager.VodQueueSnapshot);   // and in the order they were queued
    }

    /// <summary>
    /// ffmpeg taken away after the server started - an antivirus quarantine.
    /// Every start would fail, so nothing is taken off the queue at all, and
    /// no half-made folder is left behind for each file to be found as "an
    /// unfinished conversion" on every retry.
    /// </summary>
    [Fact]
    public void A_queue_waits_for_an_ffmpeg_that_has_been_taken_away()
    {
        using var rig = new Rig(ownFfmpeg: true);
        var a = rig.Clip("a.mkv", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5", "-t", "1", "-c:v", "mpeg2video");
        var b = rig.Clip("b.mkv", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5", "-t", "1", "-c:v", "mpeg2video");
        TestFfmpeg.TakeAway(rig.Ffmpeg);

        rig.Manager.QueueVod(new[] { a, b });
        rig.Manager.KickVodQueue();
        Assert.True(rig.Manager.VodQueueSnapshot.SequenceEqual(new[] { a, b }),
                    $"ffmpeg is gone and the queue lost files anyway: {rig.Manager.VodQueueDepth} of 2 left");
        var leftovers = Directory.EnumerateDirectories(rig.Media, "vod-*").Select(Path.GetFileName).ToList();
        Assert.True(leftovers.Count == 0, "starts that could never run left folders behind: " + string.Join(", ", leftovers));

        // And a start asked for directly - a play - that fails to launch
        // ffmpeg takes its half-made folder with it.
        Assert.Throws<System.ComponentModel.Win32Exception>(() => rig.Manager.StartVod(a));
        leftovers = Directory.EnumerateDirectories(rig.Media, "vod-*").Select(Path.GetFileName).ToList();
        Assert.True(leftovers.Count == 0, "a start that never launched left its folder behind: " + string.Join(", ", leftovers));
    }

    /// <summary>
    /// A start that fails for a reason of the file's own - here, a file sits
    /// where its conversion folder would go - must not hold up the queue behind
    /// it (stopping the whole queue is right only for a reason that is every
    /// file's), and must not be dropped on the first try either.
    /// </summary>
    [Fact]
    public void A_file_that_cannot_be_started_does_not_hold_up_the_rest()
    {
        using var rig = new Rig();
        var bad = rig.Clip("bad.mkv", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5", "-t", "1", "-c:v", "mpeg2video");
        var good = rig.Clip("good.mkv", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5", "-t", "1", "-c:v", "mpeg2video");
        File.WriteAllText(Path.Combine(rig.Media, rig.Manager.VodStreamName(bad)!), "in the way");

        rig.Manager.QueueVod(new[] { bad, good });
        Assert.True(rig.Manager.VodStatusFor(good) != FfmpegManager.VodState.None,
                    "one file that could not be started held up the file queued behind it");
        Assert.True(rig.Manager.VodQueueSnapshot.Contains(bad), "a file was dropped after a single failed start");

        rig.Manager.KickVodQueue();
        rig.Manager.KickVodQueue();
        Assert.True(!rig.Manager.VodQueueSnapshot.Contains(bad), "a file that can never start is retried for ever");
    }

    // --------------------------------------------------- retranscode (F4)

    /// <summary>A finished conversion on disk, as the server would have made it.</summary>
    private static string FinishedConversion(TestServer server, string name, string source,
                                             int? height = null, bool kept = false)
    {
        var dir = Path.Combine(server.Dir, "media", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.m3u8"),
                          "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXTINF:2.0,\nseg_00000.ts\n#EXT-X-ENDLIST\n");
        File.WriteAllText(Path.Combine(dir, "seg_00000.ts"), "hours of encoding");
        File.WriteAllText(Path.Combine(dir, "source.txt"), source);
        if (height is int h) File.WriteAllText(Path.Combine(dir, "height.txt"), h.ToString());
        if (kept) File.WriteAllText(Path.Combine(dir, FfmpegManager.KeepMarker), "keep");
        return dir;
    }

    /// <summary>
    /// With ffmpeg unavailable - quarantined, or moved by an upgrade - the
    /// conversion was deleted first and the failure found afterwards: a bare
    /// 500, and the directory gone.
    /// </summary>
    [Fact]
    public async Task Retranscode_leaves_the_conversion_alone_when_ffmpeg_cannot_rebuild_it()
    {
        // An ffmpeg that is there when the server starts and gone afterwards -
        // the quarantine case, and the one "is ffmpeg available" answered
        // wrongly, since that is decided once at startup.
        var own = Path.Combine(Path.GetTempPath(), "claude", "j0kers-ff-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(own);
        try
        {
            var ffmpeg = TestFfmpeg.Disposable(own);
            using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg);
            // precondition: it really was this ffmpeg, working, at startup -
            // or the test would be about a server that never had one
            using (var status = JsonDocument.Parse(await server.Http.GetStringAsync("api/status")))
                Assert.True(status.RootElement.GetProperty("ffmpeg").GetProperty("available").GetBoolean(),
                            "precondition: the server started with a working ffmpeg");
            TestFfmpeg.TakeAway(ffmpeg);
            var source = Path.Combine(server.Dir, "film.mkv");
            File.WriteAllBytes(source, new byte[64]);
            var dir = FinishedConversion(server, "vod-film-1a2b3c4d", source);

            using var r = await server.Http.PostAsync("api/hls/retranscode?stream=vod-film-1a2b3c4d", null);
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.ServiceUnavailable, $"expected 503, got {(int)r.StatusCode} {body}");
            Assert.True(File.Exists(Path.Combine(dir, "seg_00000.ts")),
                        "the conversion was deleted although nothing could rebuild it");
        }
        finally
        {
            try { Directory.Delete(own, recursive: true); } catch { /* the server may still be letting go of it */ }
        }
    }

    /// <summary>
    /// A conversion made at 120p came back at full resolution, and one the
    /// owner had asked to keep came back as one the cache may delete.
    /// </summary>
    [Fact]
    public async Task Retranscode_rebuilds_at_the_height_it_was_made_and_keeps_it_kept()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg);
        var source = TestFfmpeg.PlayableClip(ffmpeg, Path.Combine(server.Dir, "film.mp4"));
        FinishedConversion(server, "vod-film-120p-5e6f7a8b", source, height: 120, kept: true);

        using var r = await server.Http.PostAsync("api/hls/retranscode?stream=vod-film-120p-5e6f7a8b", null);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"retranscode failed: {(int)r.StatusCode} {body}");
        var stream = JsonDocument.Parse(body).RootElement.GetProperty("stream").GetString()!;

        Assert.True(stream.Contains("-120p-"), $"a 120p conversion was rebuilt as {stream} - full resolution");
        var rebuilt = Path.Combine(server.Dir, "media", stream);
        Assert.Equal("120", File.ReadAllText(Path.Combine(rebuilt, "height.txt")).Trim());
        Assert.True(File.Exists(Path.Combine(rebuilt, FfmpegManager.KeepMarker)),
                    "a conversion the owner kept was rebuilt as one the cache may delete");
    }

    /// <summary>
    /// When the old conversion could not all be removed - a player still has
    /// part of it open - the rebuild was reported regardless.
    /// </summary>
    [Fact]
    public async Task Retranscode_says_so_when_the_old_conversion_is_still_in_use()
    {
        var ffmpeg = TestFfmpeg.Require();
        using var server = await TestServer.Start(openDashboardOnStart: false, backgroundMode: true, ffmpegPath: ffmpeg);
        var source = TestFfmpeg.PlayableClip(ffmpeg, Path.Combine(server.Dir, "film.mp4"));
        var dir = FinishedConversion(server, "vod-film-9c8d7e6f", source);

        HttpStatusCode status;
        string body;
        using (new FileStream(Path.Combine(dir, "index.m3u8"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var r = await server.Http.PostAsync("api/hls/retranscode?stream=vod-film-9c8d7e6f", null);
            status = r.StatusCode;
            body = await r.Content.ReadAsStringAsync();
        }
        Assert.True(status == HttpStatusCode.Conflict,
                    $"a rebuild was reported while the old conversion could not be removed: {(int)status} {body}");
        // and nothing was taken from it: a refused removal is all or nothing
        foreach (var f in new[] { "seg_00000.ts", "source.txt", "index.m3u8" })
            Assert.True(File.Exists(Path.Combine(dir, f)),
                        $"the refused retranscode deleted {f} anyway - half a conversion left behind");
    }
}
