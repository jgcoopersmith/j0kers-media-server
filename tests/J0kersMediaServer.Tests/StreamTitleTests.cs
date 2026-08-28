using J0kersMediaServer.Media;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// StreamTitle is display-only, which is exactly why it is worth pinning down:
/// nothing downstream breaks when it gets a name wrong, so a regression here
/// would surface as an ugly label in the dashboard and nowhere else. The first
/// cases are the worked examples from the class's own doc comment, so that the
/// documentation and the behaviour cannot drift apart silently.
/// </summary>
public class StreamTitleTests
{
    // The separator between title and quality is U+00B7 MIDDLE DOT. It is
    // written as an escape rather than typed, so this source file stays plain
    // ASCII - a rule in this repository since a non-ASCII character in a
    // BOM-less file broke parsing once.
    private const string Dot = "\u00b7";

    [Theory]
    // the three worked examples from the StreamTitle doc comment
    [InlineData("vod-skyfall-2012-1080p-brrip-x264-yify-df019bf7", "Skyfall (2012) " + Dot + " 1080p")]
    [InlineData("vod-batman-begins-2005-eng-dvdrip-cd1-72ef4f5c", "Batman Begins (2005) CD1")]
    [InlineData("ch-nbc-5-1", "Nbc 5 1")]
    // The year is what separates the title from the release metadata, so
    // everything after it is metadata even when it is a tag no list mentions.
    [InlineData("vod-the-lord-of-the-rings-2001-720p-bluray", "The Lord of the Rings (2001) " + Dot + " 720p")]
    // A leading word that happens to look like a year is part of the title
    // rather than a date: the film called 2012 has to keep its name.
    [InlineData("vod-2012-1080p-x264", "2012 " + Dot + " 1080p")]
    // no year at all: the junk tags still have to go, and the quality stays
    [InlineData("vod-the-matrix-1080p-x264", "The Matrix " + Dot + " 1080p")]
    // "disc" marks a part just as "cd" does, and a part marker is upper-cased
    [InlineData("vod-some-film-1999-disc2", "Some Film (1999) DISC2")]
    // a name with neither a vod-/ch- prefix nor a cache hash is cleaned up too
    [InlineData("planet-earth-2006", "Planet Earth (2006)")]
    public void Prettify_produces_the_documented_label(string streamName, string expected) =>
        Assert.Equal(expected, StreamTitle.Prettify(streamName));

    [Fact]
    public void Prettify_lower_cases_small_words_but_never_the_first_one()
    {
        // "the" and "and" are minor words: lowercase inside a title, and the
        // leading one capitalised anyway because a label cannot start small
        Assert.Equal("The Wind and the Willows", StreamTitle.Prettify("the-wind-and-the-willows"));
    }

    [Theory]
    // Nothing usable is left once the release tags are dropped, so the raw
    // name comes back rather than an empty label: a stream with no readable
    // title is still better identified by its directory name than by blanks.
    [InlineData("vod-x264-brrip-yify")]
    [InlineData("vod-bdrip-hevc-aac")]
    public void Prettify_falls_back_to_the_raw_name_when_only_junk_is_left(string streamName) =>
        Assert.Equal(streamName, StreamTitle.Prettify(streamName));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Prettify_returns_blank_input_untouched(string streamName) =>
        Assert.Equal(streamName, StreamTitle.Prettify(streamName));

    [Theory]
    // the worked example from PrettifyFile's own doc comment
    [InlineData("The.Legend.of.Drunken.Master.dvd.avi", "The Legend of Drunken Master")]
    // A filename separates with spaces, underscores and brackets as readily as
    // with dots, and every one of them has to count as the same separator.
    [InlineData("Blade_Runner_1982_[1080p]_BluRay.x264.mkv", "Blade Runner (1982) " + Dot + " 1080p")]
    [InlineData("The Thing (1982) 720p BRRip.mp4", "The Thing (1982) " + Dot + " 720p")]
    [InlineData("Alien.1979.1080p.BluRay.x264.mkv", "Alien (1979) " + Dot + " 1080p")]
    // a file with no extension at all is still a name worth cleaning up
    [InlineData("Nosferatu 1922", "Nosferatu (1922)")]
    public void PrettifyFile_produces_the_documented_label(string fileName, string expected) =>
        Assert.Equal(expected, StreamTitle.PrettifyFile(fileName));

    [Fact]
    public void PrettifyFile_falls_back_to_the_raw_name_when_only_junk_is_left() =>
        Assert.Equal("x264.brrip.mkv", StreamTitle.PrettifyFile("x264.brrip.mkv"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PrettifyFile_returns_blank_input_untouched(string fileName) =>
        Assert.Equal(fileName, StreamTitle.PrettifyFile(fileName));
}
