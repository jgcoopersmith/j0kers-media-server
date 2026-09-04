using J0kersMediaServer.Media;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// Finding a file's conversion used to mean reading every conversion's
/// source.txt until one matched — 2,904 folders and 1,321 ms on the install
/// this was written for, once per file in the folder a television was opening.
/// A folder of fifty films took the better part of a minute before a single
/// row appeared, and the set gave up and retried part way through.
/// </summary>
public class VodIndexTests
{
    private static string Conversion(TempDir dir, string name, string? source)
    {
        var d = Path.Combine(dir.Path, name);
        Directory.CreateDirectory(d);
        if (source is not null) File.WriteAllText(Path.Combine(d, "source.txt"), source);
        return d;
    }

    private static VodIndex Built(TempDir dir) => Built(dir.Path);

    private static VodIndex Built(string root)
    {
        var index = new VodIndex(root);
        index.StartBuild();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!index.Ready && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(index.Ready, "the index never finished building");
        return index;
    }

    [Fact]
    public void ItFindsTheConversionForASourceFile()
    {
        using var dir = new TempDir();
        var made = Conversion(dir, "vod-dune-a1b2c3d4", @"G:\Films\dune.mkv");

        Assert.Equal(made, Built(dir).DirectoryFor(@"G:\Films\dune.mkv"));
    }

    /// The rule that protects picture: a scaled copy is never the answer, so a
    /// 4K set is not handed 720p because it happened to be in the cache.
    [Fact]
    public void AScaledCopyIsNeverOffered()
    {
        using var dir = new TempDir();
        Conversion(dir, "vod-dune-720p-a1b2c3d4", @"G:\Films\dune.mkv");

        Assert.Null(Built(dir).DirectoryFor(@"G:\Films\dune.mkv"));
    }

    /// ...and when both exist, the full-resolution one wins regardless of the
    /// order the folders happen to be walked in.
    [Fact]
    public void TheFullResolutionCopyWinsOverAScaledOne()
    {
        using var dir = new TempDir();
        Conversion(dir, "vod-dune-480p-11111111", @"G:\Films\dune.mkv");
        var full = Conversion(dir, "vod-dune-a1b2c3d4", @"G:\Films\dune.mkv");
        Conversion(dir, "vod-dune-1080p-22222222", @"G:\Films\dune.mkv");

        Assert.Equal(full, Built(dir).DirectoryFor(@"G:\Films\dune.mkv"));
    }

    [Fact]
    public void AFolderWithNoSourceIsIgnored()
    {
        using var dir = new TempDir();
        Conversion(dir, "vod-orphan-a1b2c3d4", source: null);

        Assert.Equal(0, Built(dir).Count);
    }

    [Fact]
    public void AnUnknownFileHasNoConversion()
    {
        using var dir = new TempDir();
        Conversion(dir, "vod-dune-a1b2c3d4", @"G:\Films\dune.mkv");

        Assert.Null(Built(dir).DirectoryFor(@"G:\Films\something-else.mkv"));
    }

    /// Matching is case-insensitive, because Windows paths are.
    [Fact]
    public void TheLookupIgnoresCase()
    {
        using var dir = new TempDir();
        var made = Conversion(dir, "vod-dune-a1b2c3d4", @"G:\Films\Dune.mkv");

        Assert.Equal(made, Built(dir).DirectoryFor(@"g:\films\dune.MKV"));
    }

    /// Before the build finishes — and it runs in the background at startup —
    /// the answer is "no conversion", which serves originals. That is exactly
    /// what happened before this class existed, so nothing is worse for it.
    [Fact]
    public void AnIndexThatHasNotBuiltAnswersNoConversion()
    {
        using var dir = new TempDir();
        Conversion(dir, "vod-dune-a1b2c3d4", @"G:\Films\dune.mkv");

        Assert.Null(new VodIndex(dir.Path).DirectoryFor(@"G:\Films\dune.mkv"));
    }

    /// A media root that is not there yet must not throw: the server can be
    /// started before the drive holding it is ready.
    [Fact]
    public void AMissingMediaRootIsNotAFailure()
    {
        using var dir = new TempDir();
        var index = Built(Path.Combine(dir.Path, "not-created-yet"));
        Assert.Equal(0, index.Count);
        Assert.Null(index.DirectoryFor(@"G:\Films\dune.mkv"));
    }
}
