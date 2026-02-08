using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopLS.Native;

/// <summary>
/// Reads and writes desktop icon positions via cross-process Win32 ListView operations.
/// The shell desktop is a SysListView32 inside SHELLDLL_DefView inside Progman (or WorkerW).
/// </summary>
public static class DesktopIconManager
{
    // ── Window messages ───────────────────────────────────────────────────
    private const int LVM_FIRST = 0x1000;
    private const int LVM_GETITEMCOUNT = LVM_FIRST + 4;
    private const int LVM_GETITEMW = LVM_FIRST + 75;
    private const int LVM_GETITEMPOSITION = LVM_FIRST + 16;
    private const int LVM_SETITEMPOSITION32 = LVM_FIRST + 49;

    // LVITEM flags
    private const int LVIF_TEXT = 0x0001;

    // Process access
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;

    // Window styles
    private const int GWL_STYLE = -16;
    private const int LVS_AUTOARRANGE = 0x0100;

    // VirtualAllocEx flags
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LVITEMW
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;   // pointer to remote buffer
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    // P/Invokes
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesRead);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObject);

    // Monitor enumeration
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // ── Public API ────────────────────────────────────────────────────────

    public static Dictionary<string, (int X, int Y)> ReadPositions()
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        IntPtr lv = GetDesktopListView();
        if (lv == IntPtr.Zero) return result;

        GetWindowThreadProcessId(lv, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, pid);
        if (hProc == IntPtr.Zero) return result;

        try
        {
            int count = (int)SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
            if (count <= 0) return result;

            // Allocate remote memory: LVITEMW struct + 260-char text buffer
            const int MaxPath = 260;
            uint textBufSize = (uint)(MaxPath * 2); // UTF-16
            uint lvItemSize = (uint)Marshal.SizeOf<LVITEMW>();
            uint totalSize = lvItemSize + textBufSize;

            IntPtr remoteBlock = VirtualAllocEx(hProc, IntPtr.Zero, totalSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteBlock == IntPtr.Zero) return result;

            IntPtr remoteTextPtr = remoteBlock + (int)lvItemSize;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    // Build LVITEMW locally, pointing pszText to remote text buffer
                    var item = new LVITEMW
                    {
                        mask = LVIF_TEXT,
                        iItem = i,
                        iSubItem = 0,
                        pszText = remoteTextPtr,
                        cchTextMax = MaxPath
                    };

                    byte[] itemBytes = StructToBytes(item);
                    WriteProcessMemory(hProc, remoteBlock, itemBytes, lvItemSize, out _);

                    SendMessage(lv, LVM_GETITEMW, (IntPtr)i, remoteBlock);

                    // Read text back
                    byte[] textBytes = new byte[textBufSize];
                    ReadProcessMemory(hProc, remoteTextPtr, textBytes, textBufSize, out _);
                    string name = Encoding.Unicode.GetString(textBytes).TrimEnd('\0');
                    if (string.IsNullOrEmpty(name)) continue;

                    // Get position
                    IntPtr remotePoint = VirtualAllocEx(hProc, IntPtr.Zero, (uint)Marshal.SizeOf<POINT>(), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    if (remotePoint == IntPtr.Zero) continue;
                    try
                    {
                        SendMessage(lv, LVM_GETITEMPOSITION, (IntPtr)i, remotePoint);
                        byte[] ptBytes = new byte[Marshal.SizeOf<POINT>()];
                        ReadProcessMemory(hProc, remotePoint, ptBytes, (uint)ptBytes.Length, out _);
                        var pt = BytesToStruct<POINT>(ptBytes);
                        result[name] = (pt.X, pt.Y);
                    }
                    finally { VirtualFreeEx(hProc, remotePoint, 0, MEM_RELEASE); }
                }
            }
            finally { VirtualFreeEx(hProc, remoteBlock, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(hProc); }

        return result;
    }

    public static void WritePositions(Dictionary<string, (int X, int Y)> positions)
    {
        if (positions.Count == 0) return;
        IntPtr lv = GetDesktopListView();
        if (lv == IntPtr.Zero) return;

        GetWindowThreadProcessId(lv, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, pid);
        if (hProc == IntPtr.Zero) return;

        try
        {
            int count = (int)SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
            if (count <= 0) return;

            // Disable auto-arrange so we can place icons freely
            int style = GetWindowLong(lv, GWL_STYLE);
            if ((style & LVS_AUTOARRANGE) != 0)
                SetWindowLong(lv, GWL_STYLE, style & ~LVS_AUTOARRANGE);

            const int MaxPath = 260;
            uint textBufSize = (uint)(MaxPath * 2);
            uint lvItemSize = (uint)Marshal.SizeOf<LVITEMW>();
            uint totalSize = lvItemSize + textBufSize;

            IntPtr remoteBlock = VirtualAllocEx(hProc, IntPtr.Zero, totalSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteBlock == IntPtr.Zero) return;

            IntPtr remoteTextPtr = remoteBlock + (int)lvItemSize;

            try
            {
                // Build a name→index map
                var indexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    var item = new LVITEMW
                    {
                        mask = LVIF_TEXT,
                        iItem = i,
                        iSubItem = 0,
                        pszText = remoteTextPtr,
                        cchTextMax = MaxPath
                    };
                    WriteProcessMemory(hProc, remoteBlock, StructToBytes(item), lvItemSize, out _);
                    SendMessage(lv, LVM_GETITEMW, (IntPtr)i, remoteBlock);

                    byte[] textBytes = new byte[textBufSize];
                    ReadProcessMemory(hProc, remoteTextPtr, textBytes, textBufSize, out _);
                    string name = Encoding.Unicode.GetString(textBytes).TrimEnd('\0');
                    if (!string.IsNullOrEmpty(name))
                        indexMap[name] = i;
                }

                // Set positions
                IntPtr remotePt = VirtualAllocEx(hProc, IntPtr.Zero, (uint)Marshal.SizeOf<POINT>(), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (remotePt == IntPtr.Zero) return;
                try
                {
                    foreach (var (name, (x, y)) in positions)
                    {
                        if (!indexMap.TryGetValue(name, out int idx)) continue;
                        var pt = new POINT { X = x, Y = y };
                        WriteProcessMemory(hProc, remotePt, StructToBytes(pt), (uint)Marshal.SizeOf<POINT>(), out _);
                        SendMessage(lv, LVM_SETITEMPOSITION32, (IntPtr)idx, remotePt);
                    }
                }
                finally { VirtualFreeEx(hProc, remotePt, 0, MEM_RELEASE); }
            }
            finally { VirtualFreeEx(hProc, remoteBlock, 0, MEM_RELEASE); }

            // Restore original style
            SetWindowLong(lv, GWL_STYLE, style);
        }
        finally { CloseHandle(hProc); }
    }

    public static int GetIconCount()
    {
        IntPtr lv = GetDesktopListView();
        if (lv == IntPtr.Zero) return 0;
        return (int)SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
    }

    [ThreadStatic]
    private static List<string>? _monitorCollector;

    public static string GetMonitorSignature()
    {
        _monitorCollector = new List<string>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnumCallback, IntPtr.Zero);
        var monitors = _monitorCollector;
        _monitorCollector = null;
        monitors.Sort(StringComparer.Ordinal);
        return string.Join("|", monitors);
    }

    private static bool MonitorEnumCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
    {
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(hMonitor, ref mi))
        {
            int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
            int h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            _monitorCollector?.Add($"{w}x{h}@{mi.rcMonitor.Left},{mi.rcMonitor.Top}");
        }
        return true;
    }

    // ── Internal helpers ─────────────────────────────────────────────────

    private static IntPtr GetDesktopListView()
    {
        // Try Progman → SHELLDLL_DefView → SysListView32
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            IntPtr shellView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView != IntPtr.Zero)
            {
                IntPtr lv = FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null);
                if (lv != IntPtr.Zero) return lv;
            }

            // Windows 10/11: icons may be under a WorkerW child of Progman
            IntPtr workerW = IntPtr.Zero;
            while (true)
            {
                workerW = FindWindowEx(progman, workerW, "WorkerW", null);
                if (workerW == IntPtr.Zero) break;
                shellView = FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView != IntPtr.Zero)
                {
                    IntPtr lv = FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null);
                    if (lv != IntPtr.Zero) return lv;
                }
            }
        }
        return IntPtr.Zero;
    }

    private static byte[] StructToBytes<T>(T s) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] arr = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try { Marshal.StructureToPtr(s, ptr, false); Marshal.Copy(ptr, arr, 0, size); }
        finally { Marshal.FreeHGlobal(ptr); }
        return arr;
    }

    private static T BytesToStruct<T>(byte[] bytes) where T : struct
    {
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, ptr, bytes.Length); return Marshal.PtrToStructure<T>(ptr); }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
