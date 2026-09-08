using System.Text.RegularExpressions;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// A conversion directory is named vod-{slug}{-720p}-{8 hex}, where the middle
/// part is present only when a height was asked for. DLNA must be given
/// full-resolution conversions only, so VodIndex decided which was which by
/// matching that shape in the name.
///
/// It cannot be decided that way. The slug is the file's own name, so a film
/// called "2012 Skyfall 720p.mkv" produces a directory ending in exactly the
/// same shape as a real 720p conversion — and the two are textually identical.
/// Measured on this library: the source is 1280x534 and its conversion is
/// 1280x534, and it sat on "Convert DLNA" permanently, because re-converting
/// produced the same name and was rejected again.
///
/// The height is written beside the segments now. These tests pin the
/// ambiguity itself, so nobody tries to solve it with a cleverer regex.
/// </summary>
public class ScaledConversionNameTests
{
    /// The pattern VodIndex used, copied here deliberately: this test is about
    /// what it can and cannot tell apart.
    private static readonly Regex Scaled = new(@"-\d+p-[0-9a-f]{8}$", RegexOptions.Compiled);

    [Theory]
    [InlineData("vod-dune-part-two-720p-a1b2c3d4")]      // a real scaled copy
    [InlineData("vod-2012-skyfall-720p-41199ce1")]       // a title that ends in 720p, full resolution
    [InlineData("vod-aaf-sstroba-1080p-deadbeef")]       // likewise
    public void The_name_alone_cannot_tell_a_scaled_copy_from_a_title_that_ends_in_one(string dir)
    {
        // Every one of these matches. That is the point: the shape is the same,
        // so a name-only rule must reject all of them or none.
        Assert.Matches(Scaled, dir);
    }

    [Theory]
    [InlineData("vod-dune-part-two-a1b2c3d4")]
    [InlineData("vod-blade-runner-2049-deadbeef")]
    public void A_plain_source_height_name_does_not_match(string dir)
    {
        Assert.DoesNotMatch(Scaled, dir);
    }

    [Theory]
    [InlineData("0", false)]      // source height
    [InlineData("720", true)]
    [InlineData("1080", true)]
    [InlineData("", false)]       // unreadable: not a claim that it is scaled
    [InlineData("nonsense", false)]
    public void The_recorded_height_answers_it_outright(string recorded, bool expectedScaled)
    {
        // What VodIndex.IsScaled does with height.txt, stated as a table. A
        // value it cannot parse must not be read as "scaled" — that would hide
        // a good conversion from the television, which is the failure being
        // fixed here.
        var scaled = int.TryParse(recorded, out var h) && h > 0;
        Assert.Equal(expectedScaled, scaled);
    }
}
