using J0kersMediaServer.Control;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The Transcode window answers two questions, and for a long time it answered
/// only one of them - whether a television could decode the file - while the
/// point of this server is that media is there *instantly*, which is the other
/// one.
///
/// They are genuinely independent:
///
///   PC/VLC plays through HLS, so "instant" means a finished conversion exists,
///   of any resolution. Without one, pressing play waits for ffmpeg.
///
///   A television plays the file itself when it can decode it, and is otherwise
///   handed a conversion - but only a FULL-RESOLUTION one, because a 720p copy
///   is not what a 4K set asked for (VodIndex excludes scaled names).
///
/// So a scaled conversion of an HEVC film is instant here and unplayable there;
/// an h264 file with no conversion is the reverse. Four states, and this is the
/// table they come from.
/// </summary>
public class TranscodeReadinessTests
{
    private static (bool pc, bool? dlna) Ask(
        bool converted, bool fullRes, bool? needsConversion,
        bool codecsKnowable = true, bool force = false)
        => ControlApi.Readiness(converted, fullRes, needsConversion, codecsKnowable, force);

    [Fact]
    public void Converted_and_playable_on_the_set_is_ready()
    {
        var (pc, dlna) = Ask(converted: true, fullRes: true, needsConversion: false);
        Assert.True(pc);
        Assert.True(dlna);
    }

    [Fact]
    public void A_full_resolution_conversion_makes_the_set_ready_even_when_the_original_is_undecodable()
    {
        // This is the ordinary case for HEVC: the set cannot play the file, so
        // it is handed the conversion instead, and both halves are satisfied.
        var (pc, dlna) = Ask(converted: true, fullRes: true, needsConversion: true);
        Assert.True(pc);
        Assert.True(dlna);
    }

    [Fact]
    public void A_scaled_only_conversion_leaves_the_television_with_nothing()
    {
        // Converted, so the dashboard is instant - but the only copy is scaled,
        // and DLNA is never handed one. "Convert DLNA".
        var (pc, dlna) = Ask(converted: true, fullRes: false, needsConversion: true);
        Assert.True(pc);
        Assert.False(dlna);
    }

    [Fact]
    public void No_conversion_but_the_set_can_play_it_means_only_the_dashboard_waits()
    {
        // "Convert PC/VLC": nothing is wrong on the television, but playing it
        // here would start an encode and make somebody wait for it.
        var (pc, dlna) = Ask(converted: false, fullRes: false, needsConversion: false);
        Assert.False(pc);
        Assert.True(dlna);
    }

    [Fact]
    public void Neither_a_conversion_nor_a_decodable_original_needs_converting()
    {
        var (pc, dlna) = Ask(converted: false, fullRes: false, needsConversion: true);
        Assert.False(pc);
        Assert.False(dlna);
    }

    [Fact]
    public void Codecs_not_read_yet_is_unknown_rather_than_a_promise()
    {
        // Saying "Ready" about a file nobody has looked at is how a pill ends
        // up lying. Null travels to the client, which paints "checking...".
        var (pc, dlna) = Ask(converted: false, fullRes: false, needsConversion: null);
        Assert.False(pc);
        Assert.Null(dlna);
    }

    [Fact]
    public void A_conversion_settles_the_question_even_before_the_codecs_are_read()
    {
        // Nothing needs to be known about the original: the set is going to be
        // handed the conversion whatever the original turns out to be.
        var (_, dlna) = Ask(converted: true, fullRes: true, needsConversion: null);
        Assert.True(dlna);
    }

    [Fact]
    public void With_no_ffmpeg_nothing_is_hidden()
    {
        // DlnaShouldList offers everything when there is no codec knowledge at
        // all, rather than hiding a whole library. This must agree with it.
        var (_, dlna) = Ask(converted: false, fullRes: false, needsConversion: null, codecsKnowable: false);
        Assert.True(dlna);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Forced_substitution_makes_the_conversion_the_only_answer(bool fullRes, bool expected)
    {
        // dlnaUseTranscode is for a set that rejects something the codec check
        // thinks is fine, so the original never counts, however decodable it
        // looks.
        var (_, dlna) = Ask(converted: fullRes, fullRes: fullRes, needsConversion: false, force: true);
        Assert.Equal(expected, dlna);
    }

    [Fact]
    public void Pc_readiness_is_the_conversion_and_nothing_else()
    {
        // It does not care what a television thinks, at any resolution.
        Assert.True(Ask(converted: true, fullRes: false, needsConversion: true).pc);
        Assert.True(Ask(converted: true, fullRes: true, needsConversion: false).pc);
        Assert.False(Ask(converted: false, fullRes: true, needsConversion: false).pc);
    }
}
