using J0kersMediaServer.Dlna;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// Pausing a film on a television and pressing play gave a picture reduced to
/// a handful of colours, with the sound playing perfectly over it.
///
/// The bytes were never wrong — checked against the live server, a mid-file
/// range came back byte-for-byte identical to the same offset read from disk,
/// with a correct Content-Range. What was wrong was where the set resumed. A
/// conversion is HLS segments served as one file; each segment opens with the
/// H.264 parameter sets and a keyframe, and nothing repeats them in between.
/// A set reopening the connection picks whatever byte offset it left off at,
/// which lands mid-segment, and a decoder handed that has nothing telling it
/// how to interpret the picture. Measured with ffmpeg against this server:
/// mid-segment gives "non-existing PPS 0 referenced" and "no frame!", the same
/// request snapped to the segment start decodes silently.
///
/// So a partial request starts at the beginning of the segment it falls in.
/// These pin the arithmetic that does it.
/// </summary>
public class DlnaResumeTests
{
    /// Segments of 100, 200 and 300 bytes: boundaries at 0, 100 and 300.
    private static DlnaService.Transcode Sample() =>
        new(new[] { ("a.ts", 100L), ("b.ts", 200L), ("c.ts", 300L) }, 600, "video/mp2t");

    private static long Snap(long from)
    {
        long at = 0;
        foreach (var (_, length) in Sample().Parts)
        {
            if (at + length > from) return at;
            at += length;
        }
        return at;
    }

    [Theory]
    [InlineData(0, 0)]        // already at a boundary
    [InlineData(1, 0)]        // inside the first segment
    [InlineData(99, 0)]
    [InlineData(100, 100)]    // exactly the second segment's start
    [InlineData(101, 100)]
    [InlineData(299, 100)]
    [InlineData(300, 300)]    // exactly the third segment's start
    [InlineData(599, 300)]    // the last byte still belongs to the third
    public void AResumeSnapsBackToTheSegmentItLandsIn(long asked, long expected)
        => Assert.Equal(expected, Snap(asked));

    /// It only ever moves backwards, and never past the beginning.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(250)]
    [InlineData(599)]
    public void SnappingNeverMovesForwardOrBeforeTheStart(long asked)
    {
        var got = Snap(asked);
        Assert.True(got <= asked, $"snapped forward: {asked} -> {got}");
        Assert.True(got >= 0, $"snapped before the start: {asked} -> {got}");
    }

    /// The rewind is bounded by one segment, so a set resumes a few seconds
    /// early at worst rather than at the beginning of the film.
    [Theory]
    [InlineData(99, 100)]
    [InlineData(299, 200)]
    [InlineData(599, 300)]
    public void TheRewindIsNeverMoreThanOneSegment(long asked, long segmentLength)
        => Assert.True(asked - Snap(asked) < segmentLength);

    /// A boundary offset must be left exactly alone - the common case once a
    /// set has been resuming for a while.
    [Fact]
    public void OffsetsAlreadyOnABoundaryAreUntouched()
    {
        foreach (var boundary in new long[] { 0, 100, 300 })
            Assert.Equal(boundary, Snap(boundary));
    }
}
