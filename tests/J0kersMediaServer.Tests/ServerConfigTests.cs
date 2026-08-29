using J0kersMediaServer.Config;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// Validate is private, and it stays private: it is reached here through the
/// public Load, which is the only way the server itself ever reaches it. A
/// temp directory holding nothing but a server.json is enough, because the
/// settings and mounts sidecars Load also looks for are simply absent, and
/// their absence is the normal first-run case rather than a special one.
///
/// Load is read-only for these inputs, so nothing here writes to the install's
/// own config directory - which holds the owner's accounts and signing key and
/// must never be what a test points at.
/// </summary>
public class ServerConfigTests
{
    private static string WriteConfig(TempDir dir, string json)
    {
        var path = dir.File("server.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Theory]
    // zero and 65536 are the two values immediately outside the valid range,
    // which is where an off-by-one in the check would show
    [InlineData("{\"control\":{\"port\":0}}", "control.port")]
    [InlineData("{\"control\":{\"port\":65536}}", "control.port")]
    [InlineData("{\"rtsp\":{\"port\":0}}", "rtsp.port")]
    [InlineData("{\"rtsp\":{\"port\":70000}}", "rtsp.port")]
    [InlineData("{\"hls\":{\"port\":-1}}", "hls.port")]
    [InlineData("{\"hls\":{\"port\":65536}}", "hls.port")]
    public void Load_refuses_a_port_outside_the_valid_range(string json, string expectedName)
    {
        using var dir = new TempDir();
        var path = WriteConfig(dir, json);

        var ex = Assert.Throws<InvalidOperationException>(() => ServerConfig.Load(path));

        // the message has to name which port is wrong: this is what the owner
        // sees when the server refuses to start, and "invalid port" alone
        // would leave three places to look
        Assert.Contains(expectedName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a valid TCP port", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // the edges of the range are valid and must not be rejected
    [InlineData(1)]
    [InlineData(65535)]
    [InlineData(9191)]
    public void Load_accepts_a_port_inside_the_valid_range(int port)
    {
        using var dir = new TempDir();
        var path = WriteConfig(dir, "{\"control\":{\"port\":" + port + "}}");

        var cfg = ServerConfig.Load(path);

        Assert.Equal(port, cfg.Control.Port);
    }

    [Fact]
    public void Load_reads_the_file_and_points_the_sidecars_at_its_directory()
    {
        using var dir = new TempDir();
        var path = WriteConfig(dir, "{\"serverName\":\"test server\",\"rtsp\":{\"port\":8555}}");

        var cfg = ServerConfig.Load(path);

        Assert.Equal("test server", cfg.ServerName);
        Assert.Equal(8555, cfg.Rtsp.Port);
        // anything not named in the file keeps its default
        Assert.Equal(8080, cfg.Hls.Port);
        // the sidecars belong next to the config that named them, not next to
        // whatever directory the process happened to start in
        Assert.Equal(Path.GetFullPath(dir.File("mounts.json")), cfg.DynamicMountsFile);
        Assert.Equal(Path.GetFullPath(dir.File("settings.json")), cfg.SettingsFile);
    }

    /// <summary>
    /// No limit unless one is asked for, pinned here because a config that
    /// does not mention this is the normal case rather than an unusual one:
    /// the shipped server.json left it out, so every install ran on whatever
    /// this default was and nobody could see what it was. At the old 10 GB a
    /// library conversion run deleted its own output as fast as it produced
    /// it. Any future change to this value should be a deliberate one, and
    /// this test is where that gets noticed.
    /// </summary>
    [Fact]
    public void A_config_that_does_not_mention_the_cache_limit_has_no_limit()
    {
        using var dir = new TempDir();
        var path = WriteConfig(dir, "{\"serverName\":\"test server\"}");

        var cfg = ServerConfig.Load(path);

        Assert.Equal(0, cfg.Ffmpeg.VodCacheMaxGb);
    }

    /// <summary>
    /// The real file the installer lays down, loaded as the server loads it.
    ///
    /// Not a copy of its contents pasted in here - that would only prove that
    /// JSON parses. The point is that the file which becomes every fresh
    /// install's server.json actually names this limit, because the whole
    /// fault was that it did not: an unnamed setting is invisible to whoever
    /// opens the config looking for it.
    /// </summary>
    [Fact]
    public void The_shipped_default_config_names_the_cache_limit()
    {
        var shipped = FindRepoFile(Path.Combine("installer", "default-server.json"));
        Assert.True(shipped is not null,
            "installer/default-server.json was not found above the test assembly");

        var cfg = ServerConfig.Load(shipped!);

        Assert.Equal(0, cfg.Ffmpeg.VodCacheMaxGb);
    }

    /// <summary>
    /// Walks up from the test assembly to find a file in the repository. The
    /// build output sits several directories below the root and the depth is
    /// not fixed, so the path is searched for rather than counted out.
    /// </summary>
    private static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
