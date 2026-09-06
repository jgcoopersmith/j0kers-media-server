using J0kersMediaServer.Config;
using J0kersMediaServer.Services;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// The parts of "start with Windows" that can be checked without touching the
/// machine.
///
/// Nothing here reads or writes the real registry, and that is deliberate
/// rather than an omission: the only key involved is
/// HKCU\...\CurrentVersion\Run, there is one of it per user, and a test that
/// died between writing and cleaning up would leave the owner's machine
/// launching a server at every logon. The registry half is verified by hand
/// against a running server instead - see the change ledger.
///
/// What is covered here is the half that actually broke things in practice:
/// the command string, whose quoting decides whether a path with a space in it
/// (which every default install has - "Programs\j0kers Media Server") starts
/// or silently does nothing.
/// </summary>
public class WindowsAutostartTests
{
    [Fact]
    public void The_command_quotes_the_executable_and_the_config()
    {
        var command = WindowsAutostart.ComposeCommand(
            @"C:\Users\someone\AppData\Local\Programs\j0kers Media Server\j0kers-media-server.exe",
            @"C:\Users\someone\AppData\Local\Programs\j0kers Media Server\server.json");

        // Both halves contain a space. Unquoted, Windows would try to run
        // "C:\Users\someone\AppData\Local\Programs\j0kers" and fail with
        // nothing to show for it.
        Assert.Equal(
            "\"C:\\Users\\someone\\AppData\\Local\\Programs\\j0kers Media Server\\j0kers-media-server.exe\" "
            + "\"C:\\Users\\someone\\AppData\\Local\\Programs\\j0kers Media Server\\server.json\"",
            command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_config_file_the_command_is_the_executable_alone(string? configPath)
    {
        // Composition only. Nothing may actually REGISTER this form: a bare
        // executable inherits Explorer's working directory at logon and finds
        // none of its own state there. EnsureConfigFile below is what keeps
        // the enable path away from it.
        var command = WindowsAutostart.ComposeCommand(@"C:\srv\j0kers-media-server.exe", configPath);

        Assert.Equal("\"C:\\srv\\j0kers-media-server.exe\"", command);
    }

    [Fact]
    public void A_server_on_built_in_defaults_gets_a_config_file_to_name()
    {
        // The failure this prevents: with no config file the logon command is a
        // bare exe, so at the next sign-in the server resolves settings.json
        // and users.json under C:\Windows\system32, finds neither, and comes up
        // on default ports with no accounts at all.
        using var dir = new TempDir();
        var config = ServerConfig.Load(Path.Combine(dir.Path, "server.json"));   // absent — defaults
        Assert.Equal("", config.ConfigFile);

        var path = config.EnsureConfigFile();

        Assert.True(File.Exists(path));
        Assert.True(Path.IsPathRooted(path));
        Assert.Equal("{}", File.ReadAllText(path));
        Assert.Equal(path, config.ConfigFile);
        // and it lands beside the state it exists to pin down
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(config.SettingsFile)),
                     Path.GetDirectoryName(path));
    }

    [Fact]
    public void An_existing_config_file_is_named_as_it_is_and_not_rewritten()
    {
        using var dir = new TempDir();
        var path = dir.File("server.json");
        File.WriteAllText(path, """{"serverName":"kept"}""");
        var config = ServerConfig.Load(path);

        Assert.Equal(Path.GetFullPath(path), config.EnsureConfigFile());
        Assert.Equal("""{"serverName":"kept"}""", File.ReadAllText(path));
    }

    [Fact]
    public void A_stale_entry_is_not_rewritten_to_a_bare_executable()
    {
        // Refresh runs at every start. With no config file to name, the only
        // command it could write is the bare form above — strictly worse than
        // whatever is already registered — so it must do nothing at all.
        if (!OperatingSystem.IsWindows()) return;

        var before = WindowsAutostart.Registered();
        WindowsAutostart.Refresh(true, "");
        WindowsAutostart.Refresh(true, null);

        Assert.Equal(before, WindowsAutostart.Registered());
    }

    [Fact]
    public void Turning_it_off_where_it_is_not_supported_is_not_an_error()
    {
        // Saving the Config dialog on macOS or Linux posts startWithWindows
        // false along with everything else. That must not fail the whole save.
        if (OperatingSystem.IsWindows()) return;

        Assert.True(WindowsAutostart.Apply(false, null, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Turning_it_on_where_it_is_not_supported_says_so()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.False(WindowsAutostart.Apply(true, null, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void The_setting_survives_the_settings_sidecar()
    {
        using var dir = new TempDir();
        var path = dir.File("server.json");
        File.WriteAllText(path, "{}");
        File.WriteAllText(dir.File("settings.json"), """{"startWithWindows":true}""");

        var config = ServerConfig.Load(path);

        Assert.True(config.StartWithWindows);
    }

    [Fact]
    public void The_config_file_it_was_loaded_from_is_recorded_absolutely()
    {
        // The logon entry names this path, because a Run entry inherits
        // Explorer's working directory rather than the install directory.
        using var dir = new TempDir();
        var path = dir.File("server.json");
        File.WriteAllText(path, "{}");

        var config = ServerConfig.Load(path);

        Assert.Equal(Path.GetFullPath(path), config.ConfigFile);
        Assert.True(Path.IsPathRooted(config.ConfigFile));
    }

    [Fact]
    public void Running_on_built_in_defaults_records_no_config_file()
    {
        var config = ServerConfig.Load(null);

        Assert.Equal("", config.ConfigFile);
    }
}
