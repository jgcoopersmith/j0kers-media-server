using System.Text.Json;
using J0kersMediaServer.Dlna;
using J0kersMediaServer.Media;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// What a television and the Transcodes window are told about a library file:
/// whether it can be reached at all, and whether it plays as it stands.
/// Items left open by v2.0.309 and v2.0.310, each seen to fail before its fix.
/// </summary>
public class LibraryReadinessTests
{
    private sealed class Scratch : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claude", "j0kers-ready-" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>
    /// A library folder that is a whole drive (E:\). DLNA kept its own copy of
    /// the old containment test, which built "E:\\" - a separator doubled onto
    /// a root that already ends in one - so a television could see the drive
    /// and open nothing inside it.
    /// </summary>
    [Fact]
    public void A_television_can_open_what_is_inside_a_whole_drive_library_folder()
    {
        using var dir = new Scratch();
        var drive = System.IO.Path.GetPathRoot(dir.Path)!;          // e.g. C:\
        var library = new LibraryStore(dir.Path);
        library.Add(drive);
        var dlna = new DlnaService(library, new DlnaShare(dir.Path), () => "test", "uuid");

        var inside = System.IO.Path.Combine(drive, "Movies", "film.mkv");
        Assert.True(dlna.ResolvePath(DlnaService.Encode(inside)) == inside,
                    $"{inside} is inside the library folder {drive}, and DLNA would not open it");
        Assert.Equal(drive, dlna.ResolvePath(DlnaService.Encode(drive)));

        // and a drive that is not in the library stays out of reach
        var elsewhere = (drive.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? "Y" : "Z") + @":\Movies\film.mkv";
        Assert.Null(dlna.ResolvePath(DlnaService.Encode(elsewhere)));
    }

    private static string Clip(string dir, string name, string pixFmt)
    {
        var path = System.IO.Path.Combine(dir, name);
        TestFfmpeg.Run(TestFfmpeg.Require(), "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
                       "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "1",
                       "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", pixFmt, "-c:a", "aac", path);
        return path;
    }

    private static string Ffprobe() =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(TestFfmpeg.Require())!,
                               OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

    /// <summary>
    /// 10-bit H.264 is "h264" by name, and the probe cache kept names only - so
    /// the Transcodes window showed a 10-bit .mp4 as ready to play as it stands
    /// and left it out of Convert, and a television was handed it directly.
    /// Neither a browser nor a set decodes it.
    /// </summary>
    [Fact]
    public void A_ten_bit_h264_file_is_not_called_ready_to_play()
    {
        using var dir = new Scratch();
        var hi10 = Clip(dir.Path, "hi10p.mp4", "yuv420p10le");
        var plain = Clip(dir.Path, "plain.mp4", "yuv420p");
        var tv = new TvCodecs(dir.Path, Ffprobe());

        Assert.True(tv.NeedsConversion(hi10), "a television would be handed 10-bit H.264 as it stands");
        Assert.False(tv.NeedsConversion(plain));

        var known = tv.CodecsCached(hi10);
        Assert.NotNull(known);
        Assert.False(FfmpegManager.PlayableAsIs(hi10, known!.Value.video, known.Value.audio, known.Value.pixFmt),
                     "the Transcodes window would call a 10-bit H.264 .mp4 ready to play");
        var plainKnown = tv.CodecsCached(plain)!.Value;
        Assert.True(FfmpegManager.PlayableAsIs(plain, plainKnown.video, plainKnown.audio, plainKnown.pixFmt));
    }

    /// <summary>
    /// A cache written before the pixel format was recorded says "h264|aac"
    /// for a 10-bit file, which reads as playable. Such an entry goes on
    /// answering as it did - treating it as unread hid every H.264 file from a
    /// television and made folder listings probe - but the library sweep sees
    /// it is not settled and reads it again, and then the truth is known.
    /// </summary>
    [Fact]
    public void An_h264_answer_from_before_the_pixel_format_was_recorded_is_read_again()
    {
        using var dir = new Scratch();
        var hi10 = Clip(dir.Path, "hi10p.mp4", "yuv420p10le");
        var other = System.IO.Path.Combine(dir.Path, "other.mkv");
        File.WriteAllText(other, "not really a film");

        string Key(string f)
        {
            var info = new FileInfo(f);
            return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "probe-cache.json"),
                          JsonSerializer.Serialize(new Dictionary<string, string>
                          {
                              [Key(hi10)] = "h264|aac",     // the old two-field form
                              [Key(other)] = "hevc|aac",
                          }));
        var tv = new TvCodecs(dir.Path, Ffprobe());
        tv.Pruning?.Wait();

        // Meanwhile: answered as it always was - nothing hidden, nothing probed.
        Assert.Equal(false, tv.NeedsConversionCached(hi10));
        Assert.NotNull(tv.CodecsCached(hi10));
        // But not settled, so the sweep reads it again.
        Assert.False(tv.IsSettled(hi10), "an H.264 answer with no pixel format was taken as final");
        Assert.True(tv.IsSettled(other), "an HEVC answer loses nothing without the field, and needs no second read");

        tv.Refresh(hi10);   // what the sweep does
        Assert.True(tv.IsSettled(hi10));
        Assert.True(tv.NeedsConversionCached(hi10) == true, "read again, the 10-bit file still was not found out");
    }

    /// <summary>
    /// A read again that fails - ffprobe timing out while encoders have the
    /// disk - keeps the answer the file had. A PIN: a failed probe is never
    /// written to the cache, so this held before the change too; it guards the
    /// refresh path against ever making it otherwise.
    /// </summary>
    [Fact]
    public void A_failed_second_read_keeps_the_answer_the_file_had()
    {
        using var dir = new Scratch();
        var film = Clip(dir.Path, "film.mp4", "yuv420p");
        var info = new FileInfo(film);
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "probe-cache.json"),
                          JsonSerializer.Serialize(new Dictionary<string, string>
                          {
                              [$"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"] = "h264|aac",
                          }));
        var tv = new TvCodecs(dir.Path, System.IO.Path.Combine(dir.Path, "no-such-ffprobe.exe"));
        tv.Pruning?.Wait();

        tv.Refresh(film);
        Assert.Equal(false, tv.NeedsConversionCached(film));
        Assert.Equal("h264", tv.CodecsCached(film)?.video);
    }
}
