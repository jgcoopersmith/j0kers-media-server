using System.Runtime.InteropServices;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Services;

/// <summary>
/// Windows notification-area ("system tray") icon, so the server can run as
/// a background daemon with its console hidden but still be reachable:
/// double-click opens the dashboard, right-click gives a menu.
///
/// Implemented with raw Win32 rather than WinForms so the project keeps a
/// single cross-platform target framework; everything here is inert on
/// non-Windows systems.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_DESTROY = 0x0002;
    private const int WM_COMMAND = 0x0111;
    private const int WM_TRAYICON = 0x0400 + 1; // WM_APP + 1
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;

    private const int SW_HIDE = 0, SW_SHOW = 5;

    private const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;

    private const int IdOpen = 1, IdConsole = 2, IdServices = 3, IdExit = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public int message;
        public IntPtr wParam;
        public IntPtr lParam;
        public int time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, int min, int max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint flags, int idNewItem, string? item);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y,
        int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string exeFileName, int iconIndex);

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private readonly string _tip;
    private readonly Action _openDashboard;
    private readonly Func<bool> _servicesRunning;
    private readonly Action<bool> _setServices;
    private readonly Action _requestShutdown;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private Thread? _thread;
    private WndProcDelegate? _wndProc; // must stay rooted: native code holds the pointer
    private bool _consoleVisible;
    private volatile bool _disposed;

    public TrayIcon(string tip, Action openDashboard, Func<bool> servicesRunning,
        Action<bool> setServices, Action requestShutdown)
    {
        _tip = tip.Length > 120 ? tip[..120] : tip;
        _openDashboard = openDashboard;
        _servicesRunning = servicesRunning;
        _setServices = setServices;
        _requestShutdown = requestShutdown;
    }

    /// <summary>
    /// Creates the tray icon on its own message-pump thread. When
    /// <paramref name="hideConsole"/> is set the console window is hidden, so
    /// the server behaves as a background daemon.
    /// </summary>
    public bool Start(bool hideConsole)
    {
        if (!OperatingSystem.IsWindows()) return false;

        var ready = new ManualResetEventSlim(false);
        var ok = false;
        _thread = new Thread(() =>
        {
            try
            {
                ok = CreateWindowAndIcon();
            }
            catch (Exception ex)
            {
                Log.Warn("tray", $"could not create tray icon: {ex.Message}");
            }
            finally
            {
                ready.Set();
            }
            if (ok) PumpMessages();
        })
        {
            IsBackground = true,
            Name = "tray",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(5000);

        if (ok && hideConsole)
        {
            var console = GetConsoleWindow();
            if (console != IntPtr.Zero)
            {
                ShowWindow(console, SW_HIDE);
                _consoleVisible = false;
            }
        }
        else
        {
            _consoleVisible = true;
        }
        return ok;
    }

    private bool CreateWindowAndIcon()
    {
        var hInstance = GetModuleHandle(null);
        _wndProc = WndProc;
        var className = "J0kersMediaServerTray";
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = className,
        };
        RegisterClassEx(ref wc); // a duplicate registration is harmless here

        _hwnd = CreateWindowEx(0, className, "j0kers", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return false;

        // reuse the executable's own joker icon
        var exe = Environment.ProcessPath ?? "";
        if (exe.Length > 0) _icon = ExtractIcon(hInstance, exe, 0);

        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _icon;
        data.szTip = _tip;
        return Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private void PumpMessages()
    {
        while (!_disposed && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAYICON:
                var evt = (int)lParam;
                if (evt == WM_LBUTTONDBLCLK) SafeInvoke(_openDashboard);
                else if (evt == WM_RBUTTONUP) ShowMenu();
                return IntPtr.Zero;

            case WM_COMMAND:
                HandleCommand((int)wParam & 0xFFFF);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MF_STRING, IdOpen, "Open dashboard");
            AppendMenu(menu, MF_STRING, IdConsole, _consoleVisible ? "Hide console" : "Show console");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, IdServices,
                _servicesRunning() ? "Stop streaming services" : "Start streaming services");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, IdExit, "Exit j0kers Media Server");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd); // required or the menu won't dismiss properly
            var cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0x0000, IntPtr.Zero, IntPtr.Zero); // WM_NULL, per the classic quirk
            if (cmd != 0) HandleCommand(cmd);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HandleCommand(int id)
    {
        switch (id)
        {
            case IdOpen:
                SafeInvoke(_openDashboard);
                break;
            case IdConsole:
                ToggleConsole();
                break;
            case IdServices:
                SafeInvoke(() => _setServices(!_servicesRunning()));
                break;
            case IdExit:
                SafeInvoke(_requestShutdown);
                break;
        }
    }

    private void ToggleConsole()
    {
        var console = GetConsoleWindow();
        if (console == IntPtr.Zero) return;
        _consoleVisible = IsWindowVisible(console);
        ShowWindow(console, _consoleVisible ? SW_HIDE : SW_SHOW);
        _consoleVisible = !_consoleVisible;
    }

    /// <summary>Shows a balloon notification from the tray icon.</summary>
    public void Notify(string title, string message)
    {
        if (!OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero) return;
        var data = NewData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = title.Length > 60 ? title[..60] : title;
        data.szInfo = message.Length > 250 ? message[..250] : message;
        data.dwInfoFlags = 0;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private static void SafeInvoke(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Warn("tray", $"menu action failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero) return;

        var data = NewData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        // restore the console so a following shutdown log isn't invisible
        var console = GetConsoleWindow();
        if (console != IntPtr.Zero && !IsWindowVisible(console)) ShowWindow(console, SW_SHOW);
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }
}
