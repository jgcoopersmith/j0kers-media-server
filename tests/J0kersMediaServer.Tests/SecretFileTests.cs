using J0kersMediaServer.Services;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The accounts file holds password hashes and key digests, and the whole
/// point of SecretFile is that another account on the machine cannot open it.
///
/// Two faults together meant it usually was not protected at all. The path was
/// marked "done" before checking the file existed, so protecting it on a first
/// run — before any account had been written — ticked the job off without
/// doing it. And the save path renamed a temp file over the top, which leaves
/// a NEW file inheriting the folder's permissions, so even a successful
/// protect was undone by the next write.
///
/// These tests are about the write keeping what the protect established.
///
/// Be clear about the limit: the permission assertion only bites on Unix,
/// where the mode is readable. On Windows the equivalent is the DACL, and
/// reading that needs the ACL API this project's platform-neutral target does
/// not carry. The obvious substitute — comparing creation time to catch a
/// rename-over — was tried and does not work: NTFS tunneling preserves it
/// across a rename too, so the test would have passed either way. It was
/// removed rather than left in looking like coverage.
/// </summary>
public class SecretFileTests
{
    [Fact]
    public void Writing_over_an_existing_secret_keeps_its_permissions()
    {
        // The property that matters, asserted where it can be: the mode set
        // before the write is still there after it.
        //
        // Only on Unix. On Windows the equivalent is the DACL, which needs
        // either the ACL API — not in a platform-neutral net10.0 reference set
        // — or the NTFS file id through P/Invoke. Creation time does not
        // separate the two cases: NTFS tunneling preserves it across a
        // rename-over as well, which was measured rather than assumed, so a
        // test resting on it would have passed whether or not the fix worked.
        using var dir = new TempDir();
        var path = dir.File("users.json");
        SecretFile.WriteAllText(path, "{\"users\":[]}");
        if (OperatingSystem.IsWindows())
        {
            // still worth asserting the write itself
            SecretFile.WriteAllText(path, "{\"users\":[{\"id\":\"a\"}]}");
            Assert.Equal("{\"users\":[{\"id\":\"a\"}]}", File.ReadAllText(path));
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        SecretFile.WriteAllText(path, "{\"users\":[{\"id\":\"a\"}]}");

        Assert.Equal("{\"users\":[{\"id\":\"a\"}]}", File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void The_first_write_creates_the_file()
    {
        using var dir = new TempDir();
        var path = dir.File("sessions.json");
        Assert.False(File.Exists(path));

        SecretFile.WriteAllText(path, "[]");

        Assert.True(File.Exists(path));
        Assert.Equal("[]", File.ReadAllText(path));
    }

    [Fact]
    public void No_temp_file_is_left_behind()
    {
        // The rename left nothing; File.Replace can, if the fallback runs.
        using var dir = new TempDir();
        var path = dir.File("users.json");
        SecretFile.WriteAllText(path, "one");
        SecretFile.WriteAllText(path, "two");

        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Protecting_a_file_that_does_not_exist_yet_does_not_tick_the_job_off()
    {
        // The first-run case: UserStore protects on construction, before it
        // has written anything. That must not count as done, or the file is
        // never restricted for the life of the install.
        using var dir = new TempDir();
        var path = dir.File("users.json");

        SecretFile.Protect(path);          // nothing there — must be a no-op, not a tick
        File.WriteAllText(path, "{}");
        SecretFile.Protect(path);          // now it exists, and this must actually run

        if (OperatingSystem.IsWindows()) return;   // icacls' result is Windows' business
        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
