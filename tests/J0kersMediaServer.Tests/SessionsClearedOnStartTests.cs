using J0kersMediaServer.Auth;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// What the dashboard's signed-in list is for: saying who is on this server
/// now. It answered with a running total of every window that had ever been
/// opened instead, because each start restored the previous run's table and
/// the window the server opens for itself minted a fresh row on top of it.
/// Nothing removed one until it aged out days later, so the number only went
/// up - seven "users logged in" on a server that had been running for
/// seconds, all of them the same account at the same address.
/// </summary>
public sealed class SessionsClearedOnStartTests
{
    [Fact]
    public void A_start_signs_everybody_out()
    {
        using var dir = new TempDir();

        // A table left behind by a previous run, every row well inside both
        // expiries - which is exactly what the old build restored.
        var now = DateTime.UtcNow.ToString("O");
        File.WriteAllText(dir.File("sessions.json"), $$"""
        {
          "AAAAAAAA": { "userId": "09dfa5d3a7f486fc", "createdUtc": "{{now}}",
                        "lastSeenUtc": "{{now}}", "clientHint": "192.168.8.196" },
          "BBBBBBBB": { "userId": "09dfa5d3a7f486fc", "createdUtc": "{{now}}",
                        "lastSeenUtc": "{{now}}", "clientHint": "192.168.8.196" }
        }
        """);

        var auth = new AuthService(new UserStore(dir.Path), "", dir.Path);

        Assert.Equal(0, auth.SignedInCount);
        Assert.Empty(auth.SignedInSessions);

        // And gone from disk, not merely ignored. Left in place, the next
        // save merges onto it and the count starts climbing again.
        Assert.False(File.Exists(dir.File("sessions.json")));
    }
}
