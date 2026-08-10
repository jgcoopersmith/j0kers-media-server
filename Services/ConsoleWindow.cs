using System.Runtime.InteropServices;
using System.Text;

namespace J0kersMediaServer.Services;

/// <summary>
/// The server is a windowed binary (<c>WinExe</c>), so double-clicking the
/// desktop icon opens no console window — the dashboard's Log card is where
/// a running server says what it is doing.
///
/// A windowed process launched *from a terminal* would normally be mute,
/// which would make <c>dotnet run</c> and any command-line use pointless.
/// So on startup it attaches to the console it was launched from, if there
/// is one, and writes there as before. Nothing is created that wasn't
/// already open: a process started from Explorer stays windowless.
/// </summary>
public static class ConsoleWindow
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    // CharSet.Unicode is not optional here: the default is Ansi, which hands
    // the wide-character MessageBoxW single-byte text and paints the box with
    // whatever those bytes happen to mean as UTF-16 — a screen of nonsense
    // instead of the error.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Whether this process has a console to write to at all.</summary>
    public static bool Present { get; private set; }

    /// <summary>
    /// Attaches to the launching terminal's console when there is one.
    /// Call before anything is written, since the standard streams have to
    /// be rebuilt: a windowed process starts with them pointing nowhere,
    /// and attaching a console afterwards does not repoint them.
    /// </summary>
    public static void AttachToParent()
    {
        if (!OperatingSystem.IsWindows()) { Present = true; return; }
        try
        {
            if (!AttachConsole(AttachParentProcess)) return;   // launched from Explorer
            // UTF8Encoding(false): Encoding.UTF8 carries a byte-order mark,
            // which a terminal prints as stray characters before the first line
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Present = true;
        }
        catch
        {
            // no console is a normal state here, not a failure
        }
    }

    /// <summary>True when a console window exists and belongs to this process.</summary>
    public static bool HasWindow() =>
        OperatingSystem.IsWindows() && GetConsoleWindow() != IntPtr.Zero;

    /// <summary>
    /// Reports a fatal startup problem. Without a console there is nowhere
    /// for it to go, and double-clicking an icon that silently does nothing
    /// is the worst possible answer to a bad config file — so that case gets
    /// a message box instead.
    /// </summary>
    public static void Fatal(string message)
    {
        if (Present)
        {
            Console.Error.WriteLine(message);
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            const uint MbIconError = 0x00000010;
            try { MessageBoxW(IntPtr.Zero, message, "j0kers Media Server", MbIconError); } catch { }
        }
    }
}
