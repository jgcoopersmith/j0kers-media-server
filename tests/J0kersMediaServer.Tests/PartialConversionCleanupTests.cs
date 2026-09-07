using J0kersMediaServer.Media;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The safety of clearing unfinished conversions is entirely in when it
/// happens, so that is what is tested here.
///
/// An earlier version of this swept every conversion whose playlist had no
/// EXT-X-ENDLIST at startup, with no age check. This server is stopped
/// mid-encode as a matter of routine - a restart, an upgrade, the machine
/// sleeping - and every conversion running at that instant is "unfinished", so
/// the next start deleted it. An upgrade does exactly that: stops a converting
/// server, starts a sweeping one. Hours of encoding went, silently, every time.
///
/// The grace window is what makes the difference, so a test that only proved
/// "stale directories are removed" would be testing the half that was never
/// the problem.
/// </summary>
public class PartialConversionCleanupTests
{
    /// <summary>A conversion directory: some segments, and a playlist that may or may not be finished.</summary>
    private static string Conversion(TempDir root, string name, bool finished, DateTime writtenUtc)
    {
        var dir = Path.Combine(root.Path, name);
        Directory.CreateDirectory(dir);
        var playlist = Path.Combine(dir, "index.m3u8");
        File.WriteAllText(playlist,
            "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nseg_00000.ts\n"
            + (finished ? "#EXT-X-ENDLIST\n" : ""));
        var segment = Path.Combine(dir, "seg_00000.ts");
        File.WriteAllBytes(segment, new byte[2048]);

        foreach (var f in new[] { playlist, segment }) File.SetLastWriteTimeUtc(f, writtenUtc);
        Directory.SetLastWriteTimeUtc(dir, writtenUtc);
        return dir;
    }

    [Fact]
    public void A_conversion_interrupted_moments_ago_is_left_alone()
    {
        // The upgrade case, which is the one that used to destroy work: the
        // server was converting, it was stopped to replace the binary, and the
        // new one is now deciding what to do about the directory it left.
        using var root = new TempDir();
        var dir = Conversion(root, "vod-being-encoded-right-now", finished: false,
                             writtenUtc: DateTime.UtcNow.AddSeconds(-20));

        var stale = FfmpegManager.IsStalePartial(dir, DateTime.UtcNow.AddHours(-24), out _, out _, out _);

        Assert.False(stale);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void A_conversion_nothing_has_touched_for_days_is_cleared()
    {
        using var root = new TempDir();
        var dir = Conversion(root, "vod-abandoned", finished: false,
                             writtenUtc: DateTime.UtcNow.AddDays(-3));

        var stale = FfmpegManager.IsStalePartial(dir, DateTime.UtcNow.AddHours(-24),
                                                 out var size, out var files, out _);

        Assert.True(stale);
        Assert.Equal(2, files);          // the playlist and its one segment
        Assert.True(size > 2000);
    }

    [Fact]
    public void A_finished_conversion_is_never_touched_however_old()
    {
        // Age is only ever a reason to clear something already established as
        // unfinished. A conversion from last year that ran to the end is the
        // library, not litter.
        using var root = new TempDir();
        var dir = Conversion(root, "vod-finished-long-ago", finished: true,
                             writtenUtc: DateTime.UtcNow.AddDays(-400));

        Assert.False(FfmpegManager.IsStalePartial(dir, DateTime.UtcNow.AddHours(-24), out _, out _, out _));
    }

    [Fact]
    public void No_cutoff_clears_nothing()
    {
        // partialConversionGraceHours = 0 means the feature is off, not that
        // everything qualifies.
        using var root = new TempDir();
        var dir = Conversion(root, "vod-abandoned", finished: false,
                             writtenUtc: DateTime.UtcNow.AddDays(-3));

        Assert.False(FfmpegManager.IsStalePartial(dir, null, out _, out _, out _));
    }

    [Fact]
    public void A_fresh_directory_stamp_does_not_protect_stale_contents()
    {
        // The first cut of this seeded the age with the directory's own
        // timestamp before taking the newest file. That stamp moves whenever
        // an entry is added or removed, for reasons that have nothing to do
        // with the conversion progressing - and it kept the real stuck
        // conversion alive: every segment two days old, the directory itself
        // forty seconds old, reported "too recent to clear".
        using var root = new TempDir();
        var dir = Conversion(root, "vod-dead-but-poked", finished: false,
                             writtenUtc: DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);   // something touched the folder

        var stale = FfmpegManager.IsStalePartial(dir, DateTime.UtcNow.AddHours(-24), out _, out _, out var touched);

        Assert.True(stale);
        Assert.True(touched < DateTime.UtcNow.AddHours(-24));
    }

    [Fact]
    public void Age_comes_from_the_newest_file_not_the_directory_stamp()
    {
        // Windows does not move a directory's LastWriteTime when a file inside
        // it is appended to. A conversion running for hours therefore has an
        // old directory stamp and fresh segments, and reading the directory
        // alone would sweep the one thing that must never be swept.
        using var root = new TempDir();
        var dir = Conversion(root, "vod-long-running", finished: false,
                             writtenUtc: DateTime.UtcNow.AddDays(-3));
        // a segment written seconds ago, while the directory still reads as old
        var fresh = Path.Combine(dir, "seg_00001.ts");
        File.WriteAllBytes(fresh, new byte[2048]);
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddSeconds(-5));

        var stale = FfmpegManager.IsStalePartial(dir, DateTime.UtcNow.AddHours(-24), out _, out _, out var touched);

        Assert.False(stale);
        Assert.True(touched > DateTime.UtcNow.AddMinutes(-1));
    }
}
