using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using J0kersMediaServer.Services;
using Xunit;

namespace J0kersMediaServer.Tests;

/// <summary>
/// Background mode can be switched off and on again from the Config dialog:
/// off disposes the TrayIcon, on makes a new one. Every TrayIcon's hidden
/// window is of the same window class, and Windows keeps the window procedure
/// a class was registered with. The class was registered by the first
/// TrayIcon, with that instance's own WndProc, and every later registration
/// failed and was ignored - so the second icon's clicks were delivered to the
/// first, disposed TrayIcon. Its window handle was gone, so the right-click
/// menu never appeared, and Exit - the one documented way to stop a
/// background-mode server - went with it for the life of the process.
///
/// These post the icon's own callback message to its hidden window, the way
/// the shell does for a click, and watch which TrayIcon answers. The menu is
/// never put on screen: ChooseFromMenuForTests is handed the real menu and
/// picks Exit from it by its text.
/// </summary>
[Collection(nameof(LeadTrayTests))]
public class LeadTrayTests
{
    // The message TrayIcon registers with the shell as its callback (WM_APP + 1),
    // with the mouse message in lParam - notify-icon version 3's convention.
    private const int WM_TRAYICON = 0x0400 + 1;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const uint MF_BYPOSITION = 0x0400;

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr hMenu, int pos);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr hMenu, uint item, StringBuilder text, int max, uint flags);

    // Every icon a test keeps hold of stays referenced to the end of the run.
    // Not for the fix's sake: before it, the window class's procedure belonged
    // to whichever TrayIcon registered it first, and if that one were
    // collected the next message would be a callback on a collected delegate,
    // which ends the test host rather than failing a test. Kept, the old
    // behaviour fails as an assertion that says what went wrong.
    private static readonly List<TrayIcon> Kept = [];

    /// <summary>What one TrayIcon's callbacks and menu saw.</summary>
    private sealed class Probe(string name)
    {
        public readonly string Name = name;
        public int Opened, ShutdownRequested, MenusShown;
        public volatile string MenuItems = "";
        public int Total => Volatile.Read(ref Opened) + Volatile.Read(ref ShutdownRequested) + Volatile.Read(ref MenusShown);
    }

    // Every icon ever made in this run, so a click that lands on the wrong one
    // can be named.
    private static readonly List<Probe> All = [];

    private static (TrayIcon Icon, Probe Probe) Make(string name)
    {
        var probe = new Probe(name);
        lock (All) All.Add(probe);
        var icon = new TrayIcon(
            tip: $"j0kers tray test - {name}",
            openDashboard: () => Interlocked.Increment(ref probe.Opened),
            servicesRunning: () => true,
            setServices: _ => { },
            requestShutdown: () => Interlocked.Increment(ref probe.ShutdownRequested));
        icon.ChooseFromMenuForTests = menu => PickExit(menu, probe);
        // hideConsole false: a test host's console, if it has one, is left alone.
        Assert.True(icon.Start(hideConsole: false), $"the {name} tray icon could not be created");
        Assert.NotEqual(IntPtr.Zero, icon.WindowHandleForTests);
        return (icon, probe);
    }

    /// <summary>The person at the menu: reads it, and clicks the Exit item.</summary>
    private static int PickExit(IntPtr menu, Probe probe)
    {
        try
        {
            Interlocked.Increment(ref probe.MenusShown);
            var items = new List<string>();
            var exit = 0;
            for (var i = 0; i < GetMenuItemCount(menu); i++)
            {
                var text = new StringBuilder(128);
                GetMenuString(menu, (uint)i, text, text.Capacity, MF_BYPOSITION);
                if (text.Length == 0) continue;   // a separator
                items.Add(text.ToString());
                if (text.ToString().StartsWith("Exit", StringComparison.Ordinal)) exit = (int)GetMenuItemID(menu, i);
            }
            probe.MenuItems = string.Join(" | ", items);
            return exit;
        }
        catch
        {
            return 0;   // never throw back into the window procedure
        }
    }

    /// <summary>
    /// Posts a click to <paramref name="icon"/>'s window, waits for
    /// <paramref name="answered"/>, and fails naming any OTHER icon that
    /// handled it instead.
    /// </summary>
    private static void Click(TrayIcon icon, Probe own, int mouseMessage, Func<bool> answered, string what)
    {
        Probe[] others;
        lock (All) others = All.Where(p => p != own).ToArray();
        var before = others.Select(p => p.Total).ToArray();

        Assert.True(PostMessage(icon.WindowHandleForTests, WM_TRAYICON, IntPtr.Zero, mouseMessage),
                    "could not post the click to the tray icon's window");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!answered() && DateTime.UtcNow < deadline
               && others.Select(p => p.Total).SequenceEqual(before)) Thread.Sleep(20);
        Thread.Sleep(100);   // anything still on its way

        var wrong = others.Where((p, i) => p.Total != before[i]).Select(p => p.Name).ToArray();
        Assert.True(wrong.Length == 0,
                    $"{what} on the '{own.Name}' icon's window was handled by the TrayIcon '{string.Join("', '", wrong)}' "
                    + "- disposed - instead: the window class kept that instance's window procedure");
        Assert.True(answered(), $"{what} on the '{own.Name}' icon was not answered (opened {own.Opened}, "
                                + $"menus {own.MenusShown}, exit {own.ShutdownRequested})");
    }

    [Fact]
    public void Right_click_Exit_on_the_icon_made_after_background_mode_is_turned_off_and_on_stops_the_server()
    {
        if (!OperatingSystem.IsWindows()) return;   // TrayIcon is inert elsewhere

        // Background mode on: the first icon, and it answers.
        var (first, one) = Make("first");
        Kept.Add(first);
        Click(first, one, WM_LBUTTONDBLCLK, () => Volatile.Read(ref one.Opened) == 1, "a double-click");

        // Off (ApplyTrayMode(false) disposes it), then on again: a second icon.
        first.Dispose();
        var (second, two) = Make("second");
        Kept.Add(second);
        try
        {
            Click(second, two, WM_RBUTTONUP, () => Volatile.Read(ref two.ShutdownRequested) == 1, "a right-click then Exit");
            Assert.Equal(1, Volatile.Read(ref two.MenusShown));
            Assert.Contains("Exit j0kers Media Server", two.MenuItems);
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public void Background_mode_turned_off_and_on_repeatedly_leaves_each_new_icon_answering_its_own_clicks()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Started and stopped once and kept: the icon background mode began with.
        var (startup, _) = Make("startup");
        Kept.Add(startup);
        startup.Dispose();

        for (var turn = 1; turn <= 5; turn++)
        {
            var collected = OneTurn(turn);

            // Program.cs drops the icon it disposed (tray = null). A disposed
            // icon must be collectable, and its collection must not take the
            // window procedure the next one needs with it.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(collected.IsAlive, $"turn {turn}: the disposed TrayIcon is still referenced after a full collection");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OneTurn(int turn)
    {
        var (icon, probe) = Make($"turn {turn}");
        try
        {
            Click(icon, probe, WM_LBUTTONDBLCLK, () => Volatile.Read(ref probe.Opened) == 1, $"turn {turn}: a double-click");
            Click(icon, probe, WM_RBUTTONUP, () => Volatile.Read(ref probe.ShutdownRequested) == 1, $"turn {turn}: a right-click then Exit");
        }
        finally
        {
            icon.Dispose();
        }
        return new WeakReference(icon);
    }
}
