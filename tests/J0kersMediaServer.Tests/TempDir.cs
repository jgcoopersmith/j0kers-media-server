namespace J0kersMediaServer.Tests;

/// <summary>
/// A throwaway directory that deletes itself at the end of the test.
///
/// Several of the things worth testing are defined by what they do to files -
/// the sidecar quarantining a corrupt file, the media signer persisting its
/// key, the config loader reading a server.json - so those tests need a real
/// directory rather than a mock. They must never be pointed at the install's
/// own config directory: that folder holds the owner's accounts, playlists and
/// signing key, and a test that writes there would be editing live data.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        // under the machine temp root, with a name nothing else will collide
        // with, so parallel test classes cannot tread on each other
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "j0kers-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Absolute path of a file inside this directory. The file need not exist.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        // cleanup failing is not a test failure: on Windows an antivirus scan
        // can still hold a handle for a moment after the last close, and the
        // directory is in temp either way
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
