using System.Runtime.InteropServices;

namespace J0kersMediaServer.Services;

/// <summary>
/// Moves a file or folder to the Windows Recycle Bin — an undoable delete, so
/// a mistake can be put back rather than being gone. Uses the shell's
/// SHFileOperation with FOF_ALLOWUNDO, the same path the Explorer "Delete" key
/// takes.
/// </summary>
public static class RecycleBin
{
    /// <summary>Sends one file or directory to the Recycle Bin. Throws on failure.</summary>
    public static void Send(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Recycle Bin is only available on Windows.");
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("empty path", nameof(path));

        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException("nothing to delete", full);

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = full + "\0\0",           // pFrom is a double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var rc = SHFileOperation(ref op);
        if (rc != 0) throw new IOException($"recycle failed (SHFileOperation code 0x{rc:X}) for {full}");
        if (op.fAnyOperationsAborted) throw new IOException($"recycle aborted for {full}");
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
