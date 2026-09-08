using J0kersMediaServer.Media;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// Both of these come from the same assumption: that a conversion's segments
/// sit on a fixed grid, every one exactly the configured interval long.
///
/// That is true while the video is being ENCODED, because the encoder is told
/// to force a keyframe every interval. It is false the moment the video is
/// COPIED — a copy cannot place keyframes, so ffmpeg cuts where the source
/// already has one and the segments come out longer and uneven.
///
/// Two things were built on the assumption without checking it: the whole-film
/// playlist, which claims duration/6 segments of exactly 6 seconds each, and
/// the seek-ahead job, which starts encoding at index * 6 seconds.
/// </summary>
public class SeekAndPlaylistGridTests
{
    // ---- the height a conversion was made at, read back from its name ----
    //
    // The seek job needs it: filling in for a 720p conversion means scaling to
    // 720 as well, and it used to omit -vf entirely and write full-resolution
    // stand-ins in among the scaled ones.

    [Theory]
    [InlineData("vod-dune-part-two-720p-a1b2c3d4", 720)]
    [InlineData("vod-dune-part-two-1080p-a1b2c3d4", 1080)]
    [InlineData("vod-some-film-360p-0f0f0f0f", 360)]
    public void The_height_is_read_back_out_of_a_scaled_conversion_name(string stream, int expected)
    {
        Assert.Equal(expected, FfmpegManager.HeightFromStreamName(stream));
    }

    [Theory]
    [InlineData("vod-dune-part-two-a1b2c3d4")]              // source height: the common case
    [InlineData("vod-blade-runner-2049-deadbeef")]          // a year in the title is not a height
    [InlineData("vod-1080p-in-the-title-abcdef12")]         // ...nor is one in the middle
    public void A_source_height_conversion_reads_as_zero(string stream)
    {
        Assert.Equal(0, FfmpegManager.HeightFromStreamName(stream));
    }

    [Fact]
    public void A_title_ending_in_p_is_not_mistaken_for_a_height()
    {
        // The slug is the file name, so anything can be in it.
        Assert.Equal(0, FfmpegManager.HeightFromStreamName("vod-the-shop-a1b2c3d4"));
    }

    // ---- the whole-film playlist's grid check ----

    private static string Conversion(TempDir root, string name, params double[] segmentSeconds)
    {
        var dir = Path.Combine(root.Path, name);
        Directory.CreateDirectory(dir);
        var sb = new System.Text.StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:6\n");
        foreach (var d in segmentSeconds)
            sb.Append("#EXTINF:").Append(d.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\nseg.ts\n");
        File.WriteAllText(Path.Combine(dir, "index.m3u8"), sb.ToString());
        File.WriteAllText(Path.Combine(dir, "duration.txt"), "6000");
        return dir;
    }

    [Fact]
    public void An_encoded_conversion_is_on_the_grid()
    {
        using var root = new TempDir();
        var dir = Conversion(root, "vod-encoded-a1b2c3d4", 6, 6, 6, 6, 6, 6, 3.2);
        Assert.True(GridCheck(dir));
    }

    [Fact]
    public void A_copied_conversion_is_not()
    {
        // ~10s segments from a 250-frame GOP at 23.976 fps.
        using var root = new TempDir();
        var dir = Conversion(root, "vod-copied-a1b2c3d4", 10.4, 10.4, 10.4, 10.4, 4.1);
        Assert.False(GridCheck(dir));
    }

    [Fact]
    public void One_long_segment_is_not_enough_to_call_it_off()
    {
        // A scene ending where a GOP does. Three is a pattern; one is not.
        using var root = new TempDir();
        var dir = Conversion(root, "vod-mostly-even-a1b2c3d4", 6, 6, 11.5, 6, 6, 6);
        Assert.True(GridCheck(dir));
    }

    [Fact]
    public void Nothing_written_yet_leaves_the_grid_assumed()
    {
        // Before the encoder has written a playlist there is nothing to
        // contradict it, and a player needs something to play.
        using var root = new TempDir();
        var dir = Path.Combine(root.Path, "vod-fresh-a1b2c3d4");
        Directory.CreateDirectory(dir);
        Assert.True(GridCheck(dir));
    }

    /// <summary>Reaches the private check the playlist builder uses.</summary>
    private static bool GridCheck(string dir)
    {
        var m = typeof(J0kersMediaServer.Hls.HlsServer).GetMethod(
            "SegmentsFollowTheGrid",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(m);
        return (bool)m!.Invoke(null, new object[] { dir, FfmpegManager.SegmentSeconds })!;
    }
}
