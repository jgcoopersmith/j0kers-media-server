using J0kersMediaServer.Logging;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The access log skips the dashboard's own polling. These pin down the half
/// of that rule that is easy to lose.
///
/// Five of the skipped paths carry actions on the identical path: POST
/// /api/channels adds a channel, DELETE /api/mounts removes a mount, DELETE
/// /api/history forgets what was watched. Matching on the path alone would
/// silence nine real actions in exchange for quietening six timers - and it
/// would do it silently, because nothing complains about a line that was
/// never written. That very mistake was written and caught before it shipped.
/// </summary>
public class AccessLogHeartbeatTests
{
    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/log")]
    [InlineData("/api/server/session")]
    [InlineData("/api/sessions")]
    [InlineData("/api/history")]
    [InlineData("/api/channels")]
    [InlineData("/api/mounts")]
    [InlineData("/api/playlists")]
    [InlineData("/api/favorites")]
    public void PolledGetsAreNotLogged(string path)
        => Assert.True(AccessLog.IsHeartbeat("GET", path), $"GET {path} is a poll and should be skipped");

    [Theory]
    [InlineData("POST", "/api/channels")]
    [InlineData("DELETE", "/api/channels")]
    [InlineData("POST", "/api/mounts")]
    [InlineData("DELETE", "/api/mounts")]
    [InlineData("POST", "/api/playlists")]
    [InlineData("DELETE", "/api/playlists")]
    [InlineData("POST", "/api/favorites")]
    [InlineData("DELETE", "/api/favorites")]
    [InlineData("DELETE", "/api/history")]
    public void ActionsOnTheSamePathsAreStillLogged(string method, string path)
        => Assert.False(AccessLog.IsHeartbeat(method, path),
               $"{method} {path} is something a person did and must stay in the record");

    /// The set is exact paths, so anything underneath one of them is a person
    /// opening a rotated log or terminating a session, not a poll.
    [Theory]
    [InlineData("GET", "/api/log/file")]
    [InlineData("GET", "/api/log/files")]
    [InlineData("DELETE", "/api/sessions/abc123")]
    [InlineData("GET", "/api/transcode/scan")]
    [InlineData("GET", "/dlna/file/17")]
    [InlineData("GET", "/")]
    public void EverythingElseIsLogged(string method, string path)
        => Assert.False(AccessLog.IsHeartbeat(method, path));

    /// HTTP methods are case-sensitive on the wire but nothing is gained by
    /// letting a lowercase one slip through as a logged request.
    [Fact]
    public void TheMethodComparisonIsCaseInsensitive()
        => Assert.True(AccessLog.IsHeartbeat("get", "/api/sessions"));
}
