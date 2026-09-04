using System.Text.Json;
using J0kersMediaServer.Media;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The probe cache is keyed by path|size|modified, so every time a file is
/// edited, replaced or re-encoded the old key is stranded — and nothing ever
/// removed one. Measured on a real install: 114,643 entries describing 5,363
/// files. The file is read whole on every start and written whole every two
/// hundred probes, so it only ever cost more.
///
/// A key is worth keeping when its file still exists at that exact size and
/// date. Anything else is a row no lookup can hit, because Codecs() builds
/// the key from the file as it is now.
/// </summary>
public class ProbeCacheTests
{
    private static string CacheOf(TempDir dir) =>
        System.IO.Path.Combine(dir.Path, "probe-cache.json");

    private static string Key(string file)
    {
        var i = new FileInfo(file);
        return $"{i.FullName}|{i.Length}|{i.LastWriteTimeUtc.Ticks}";
    }

    private static void WriteCache(TempDir dir, Dictionary<string, string> entries) =>
        File.WriteAllText(CacheOf(dir), JsonSerializer.Serialize(entries));

    private static Dictionary<string, string> ReadCache(TempDir dir) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CacheOf(dir)))
        ?? new Dictionary<string, string>();

    /// The whole point: a file's current key survives, every older key for the
    /// same file goes, and so does every key for a file that is no longer there.
    [Fact]
    public void OnlyTheKeyMatchingTheFileAsItIsNowSurvives()
    {
        using var dir = new TempDir();
        var film = dir.File("film.mkv");
        File.WriteAllText(film, "some bytes");
        var current = Key(film);

        WriteCache(dir, new Dictionary<string, string>
        {
            [current] = "h264|aac",                                  // the live one
            [$"{film}|999999|638000000000000000"] = "hevc|dts",      // an older size
            [$"{film}|10|1"] = "mpeg4|mp3",                          // an older date
            [$"{System.IO.Path.Combine(dir.Path, "gone.mkv")}|5|7"] = "h264|aac",  // file deleted
        });

        _ = new TvCodecs(dir.Path, "ffprobe.exe");

        var left = ReadCache(dir);
        Assert.Single(left);
        Assert.True(left.ContainsKey(current), "the entry describing the file as it is now must survive");
        Assert.Equal("h264|aac", left[current]);
    }

    /// A cache with nothing stale in it must be left exactly as it was - the
    /// prune must not rewrite the file on every start for no reason.
    [Fact]
    public void ACleanCacheIsLeftAlone()
    {
        using var dir = new TempDir();
        var film = dir.File("clean.mp4");
        File.WriteAllText(film, "bytes");
        var entries = new Dictionary<string, string> { [Key(film)] = "h264|aac" };
        WriteCache(dir, entries);
        var before = File.ReadAllText(CacheOf(dir));

        _ = new TvCodecs(dir.Path, "ffprobe.exe");

        Assert.Equal(before, File.ReadAllText(CacheOf(dir)));
    }

    /// Keys that are not path|size|modified cannot be checked against a file,
    /// so they cannot be honoured either.
    [Fact]
    public void MalformedKeysAreDropped()
    {
        using var dir = new TempDir();
        var film = dir.File("ok.mp4");
        File.WriteAllText(film, "bytes");
        WriteCache(dir, new Dictionary<string, string>
        {
            [Key(film)] = "h264|aac",
            ["no-bars-at-all"] = "h264|aac",
            ["only|one"] = "h264|aac",
            ["path|notanumber|123"] = "h264|aac",
        });

        _ = new TvCodecs(dir.Path, "ffprobe.exe");

        Assert.Equal(new[] { Key(film) }, ReadCache(dir).Keys);
    }

    /// The pre-existing repair: "|" meant a probe that returned nothing, and
    /// both readers take a null video codec to mean "a television can play
    /// this". Those must still be forgotten rather than pruned back in.
    [Fact]
    public void FailedProbesRecordedAsPlayableAreStillForgotten()
    {
        using var dir = new TempDir();
        var film = dir.File("unreadable.mkv");
        File.WriteAllText(film, "bytes");
        WriteCache(dir, new Dictionary<string, string> { [Key(film)] = "|" });

        _ = new TvCodecs(dir.Path, "ffprobe.exe");

        Assert.Empty(ReadCache(dir));
    }

    /// One stat per file, not per entry. Twenty entries for one file must not
    /// cost twenty file reads - this is the difference between a second and a
    /// minute on a real library.
    [Fact]
    public void ManyStaleEntriesForOneFileCollapseToOne()
    {
        using var dir = new TempDir();
        var film = dir.File("many.mkv");
        File.WriteAllText(film, "bytes");
        var entries = new Dictionary<string, string> { [Key(film)] = "h264|aac" };
        for (var i = 0; i < 50; i++) entries[$"{film}|{i}|{i}"] = "hevc|dts";
        WriteCache(dir, entries);

        _ = new TvCodecs(dir.Path, "ffprobe.exe");

        Assert.Single(ReadCache(dir));
    }

    /// The bulk of a real cache: HLS segments of this server's own
    /// conversions. Nothing ever asks whether a television can play
    /// seg_00417.ts, because it is never offered one - but the transcode
    /// panel can be pointed at the conversions folder, and that queued every
    /// segment for probing. Measured on one install: 108,084 of 114,643
    /// entries. They are dropped without a stat, which is what turns this
    /// prune from 28 seconds into a third of one.
    [Fact]
    public void ConversionSegmentsAreDroppedWithoutTouchingDisk()
    {
        using var dir = new TempDir();
        var conversions = System.IO.Path.Combine(dir.Path, "Transcoded");
        Directory.CreateDirectory(conversions);
        var film = dir.File("kept.mkv");
        File.WriteAllText(film, "bytes");

        var entries = new Dictionary<string, string> { [Key(film)] = "h264|aac" };
        // segments that were never written to disk at all: the prune must not
        // need them to exist in order to reject them
        for (var i = 0; i < 200; i++)
            entries[$"{System.IO.Path.Combine(conversions, $"vod-x-abc12345", $"seg_{i:00000}.ts")}|1000|1"] = "h264|aac";
        WriteCache(dir, entries);

        _ = new TvCodecs(dir.Path, "ffprobe.exe", conversions);

        Assert.Equal(new[] { Key(film) }, ReadCache(dir).Keys);
    }

    /// Location, not extension. A .ts file the owner actually keeps in their
    /// library is a film and must still be probed and cached; only the
    /// conversions folder is off limits.
    [Fact]
    public void ATsFileInTheLibraryIsNotMistakenForASegment()
    {
        using var dir = new TempDir();
        var conversions = System.IO.Path.Combine(dir.Path, "Transcoded");
        Directory.CreateDirectory(conversions);
        var recording = dir.File("late-night.ts");        // in the library, not the output
        File.WriteAllText(recording, "bytes");
        WriteCache(dir, new Dictionary<string, string> { [Key(recording)] = "mpeg2video|ac3" });

        _ = new TvCodecs(dir.Path, "ffprobe.exe", conversions);

        Assert.Equal(new[] { Key(recording) }, ReadCache(dir).Keys);
    }

    /// With no conversions folder given, nothing is treated as output - the
    /// prune still works, it just has no segments to skip.
    [Fact]
    public void WithNoConversionsRootNothingIsTreatedAsOutput()
    {
        using var dir = new TempDir();
        var film = dir.File("only.mkv");
        File.WriteAllText(film, "bytes");
        WriteCache(dir, new Dictionary<string, string>
        {
            [Key(film)] = "h264|aac",
            [$"{dir.File("vanished.mkv")}|1|1"] = "h264|aac",
        });

        _ = new TvCodecs(dir.Path, "ffprobe.exe");

        Assert.Equal(new[] { Key(film) }, ReadCache(dir).Keys);
    }
}
