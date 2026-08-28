using J0kersMediaServer.Media;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// JsonSidecar exists because the obvious read and the obvious write both lose
/// data, so the tests are about what happens when things go wrong rather than
/// when they go right. The quarantine case is the important one: before it,
/// an unreadable file was overwritten by the next save and the user's
/// playlists were simply gone.
///
/// The class is internal, reached here through the InternalsVisibleTo the
/// server grants this assembly. Widening it to public so a test could see it
/// would be a worse trade than naming the one assembly allowed to look.
/// </summary>
public class JsonSidecarTests
{
    /// <summary>Stands in for the real sidecar shapes: a list of small records.</summary>
    public sealed class Pin
    {
        public string Name { get; set; } = "";
        public int Position { get; set; }
        public bool Watched { get; set; }
    }

    [Fact]
    public void Save_then_Load_round_trips_the_value()
    {
        using var dir = new TempDir();
        var file = dir.File("pins.json");

        var saved = new List<Pin>
        {
            new() { Name = "vod-skyfall-2012", Position = 1_234, Watched = true },
            new() { Name = "ch-nbc-5-1", Position = 0, Watched = false },
        };
        JsonSidecar.Save(file, saved, "test");

        var loaded = JsonSidecar.Load<List<Pin>>(file, "test");

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
        Assert.Equal("vod-skyfall-2012", loaded[0].Name);
        Assert.Equal(1_234, loaded[0].Position);
        Assert.True(loaded[0].Watched);
        Assert.Equal("ch-nbc-5-1", loaded[1].Name);
        Assert.False(loaded[1].Watched);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        using var dir = new TempDir();
        var file = dir.File("pins.json");

        JsonSidecar.Save(file, new List<Pin> { new() { Name = "a" } }, "test");

        // the write lands in a sibling temp file and is moved over the target,
        // so a successful save leaves exactly one file
        Assert.Equal(new[] { file }, Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Load_of_a_file_that_is_not_there_returns_null()
    {
        using var dir = new TempDir();

        // the ordinary first run: no sidecar yet, and the caller starts empty
        Assert.Null(JsonSidecar.Load<List<Pin>>(dir.File("nothing-here.json"), "test"));
    }

    [Fact]
    public void Load_of_a_corrupt_file_returns_null_and_sets_the_original_aside()
    {
        using var dir = new TempDir();
        var file = dir.File("pins.json");

        // what a half-written file looks like after the process was killed
        const string Damaged = "[{\"name\":\"vod-skyfall-2012\",\"position\":12";
        File.WriteAllText(file, Damaged);

        var loaded = JsonSidecar.Load<List<Pin>>(file, "test");

        Assert.Null(loaded);

        // The original must not still be sitting where the next save would
        // overwrite it, and its bytes must be recoverable from the .corrupt
        // copy: that recoverability is the entire reason the branch exists.
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(file + ".corrupt"));
        Assert.Equal(Damaged, File.ReadAllText(file + ".corrupt"));
    }

    [Fact]
    public void A_second_corruption_replaces_the_quarantined_copy_rather_than_piling_up()
    {
        using var dir = new TempDir();
        var file = dir.File("pins.json");

        File.WriteAllText(file, "first damaged copy {");
        Assert.Null(JsonSidecar.Load<List<Pin>>(file, "test"));

        File.WriteAllText(file, "second damaged copy {");
        Assert.Null(JsonSidecar.Load<List<Pin>>(file, "test"));

        // one generation is kept, so the quarantine cannot grow without bound
        // and the move never fails because the destination already exists
        Assert.Equal(new[] { file + ".corrupt" }, Directory.GetFiles(dir.Path));
        Assert.Equal("second damaged copy {", File.ReadAllText(file + ".corrupt"));
    }
}
