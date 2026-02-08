using System.Collections.Generic;
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
    private const int WM_SETREDRAW = 0x000B;
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

    // System metrics
    private const int SM_CXICONSPACING = 38;
    private const int SM_CYICONSPACING = 39;

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
        public IntPtr pszText;
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

    // P/Invokes
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesRead);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

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

            const int MaxPath = 260;
            uint textBufSize = (uint)(MaxPath * 2);
            uint lvItemSize = (uint)Marshal.SizeOf<LVITEMW>();
            uint ptSize = (uint)Marshal.SizeOf<POINT>();

            // Allocate one block: LVITEMW + text buffer + POINT (reused across all items)
            uint totalSize = lvItemSize + textBufSize + ptSize;
            IntPtr remoteBlock = VirtualAllocEx(hProc, IntPtr.Zero, totalSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteBlock == IntPtr.Zero) return result;

            IntPtr remoteText = remoteBlock + (int)lvItemSize;
            IntPtr remotePt = remoteText + (int)textBufSize;

            byte[] itemBytes = new byte[lvItemSize];
            byte[] textBytes = new byte[textBufSize];
            byte[] ptBytes = new byte[ptSize];

            try
            {
                var item = new LVITEMW { mask = LVIF_TEXT, iSubItem = 0, pszText = remoteText, cchTextMax = MaxPath };

                for (int i = 0; i < count; i++)
                {
                    item.iItem = i;
                    MarshalToBytes(item, itemBytes);
                    WriteProcessMemory(hProc, remoteBlock, itemBytes, lvItemSize, out _);
                    SendMessage(lv, LVM_GETITEMW, (IntPtr)i, remoteBlock);
                    ReadProcessMemory(hProc, remoteText, textBytes, textBufSize, out _);
                    string name = Encoding.Unicode.GetString(textBytes).TrimEnd('\0');
                    if (string.IsNullOrEmpty(name)) continue;

                    SendMessage(lv, LVM_GETITEMPOSITION, (IntPtr)i, remotePt);
                    ReadProcessMemory(hProc, remotePt, ptBytes, ptSize, out _);
                    var pt = BytesToStruct<POINT>(ptBytes);
                    result[name] = (pt.X, pt.Y);
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

            int style = GetWindowLong(lv, GWL_STYLE);
            if ((style & LVS_AUTOARRANGE) != 0)
                SetWindowLong(lv, GWL_STYLE, style & ~LVS_AUTOARRANGE);

            const int MaxPath = 260;
            uint textBufSize = (uint)(MaxPath * 2);
            uint lvItemSize = (uint)Marshal.SizeOf<LVITEMW>();
            uint ptSize = (uint)Marshal.SizeOf<POINT>();
            uint totalSize = lvItemSize + textBufSize + ptSize;

            IntPtr remoteBlock = VirtualAllocEx(hProc, IntPtr.Zero, totalSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteBlock == IntPtr.Zero) return;

            IntPtr remoteText = remoteBlock + (int)lvItemSize;
            IntPtr remotePt = remoteText + (int)textBufSize;

            byte[] itemBytes = new byte[lvItemSize];
            byte[] textBytes = new byte[textBufSize];
            byte[] ptBytes = new byte[ptSize];

            try
            {
                var indexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var item = new LVITEMW { mask = LVIF_TEXT, iSubItem = 0, pszText = remoteText, cchTextMax = MaxPath };

                for (int i = 0; i < count; i++)
                {
                    item.iItem = i;
                    MarshalToBytes(item, itemBytes);
                    WriteProcessMemory(hProc, remoteBlock, itemBytes, lvItemSize, out _);
                    SendMessage(lv, LVM_GETITEMW, (IntPtr)i, remoteBlock);
                    ReadProcessMemory(hProc, remoteText, textBytes, textBufSize, out _);
                    string name = Encoding.Unicode.GetString(textBytes).TrimEnd('\0');
                    if (!string.IsNullOrEmpty(name)) indexMap[name] = i;
                }

                // Suppress redraws while repositioning so all icons snap at once
                SendMessage(lv, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                try
                {
                    foreach (var (name, (x, y)) in positions)
                    {
                        if (!indexMap.TryGetValue(name, out int idx)) continue;
                        var pt = new POINT { X = x, Y = y };
                        MarshalToBytes(pt, ptBytes);
                        WriteProcessMemory(hProc, remotePt, ptBytes, ptSize, out _);
                        SendMessage(lv, LVM_SETITEMPOSITION32, (IntPtr)idx, remotePt);
                    }
                }
                finally
                {
                    SendMessage(lv, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                    InvalidateRect(lv, IntPtr.Zero, true);
                }
            }
            finally { VirtualFreeEx(hProc, remoteBlock, 0, MEM_RELEASE); }

            SetWindowLong(lv, GWL_STYLE, style);
        }
        finally { CloseHandle(hProc); }
    }

    /// <summary>
    /// Places all desktop icons in a column-major grid fitting within each monitor's work area.
    /// Used when visiting a folder for the first time (no saved layout).
    /// </summary>
    public static void ArrangeInGrid()
    {
        IntPtr lv = GetDesktopListView();
        if (lv == IntPtr.Zero) return;

        int count = (int)SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return;

        // Icon spacing from system (includes label height)
        int sx = GetSystemMetrics(SM_CXICONSPACING);
        int sy = GetSystemMetrics(SM_CYICONSPACING);
        if (sx <= 0) sx = 96;
        if (sy <= 0) sy = 96;

        // Collect work areas sorted left-to-right
        _workAreaCollector = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, WorkAreaEnumCallback, IntPtr.Zero);
        var workAreas = _workAreaCollector;
        _workAreaCollector = null;

        if (workAreas.Count == 0) return;
        workAreas.Sort((a, b) => a.Left != b.Left ? a.Left.CompareTo(b.Left) : a.Top.CompareTo(b.Top));

        // Build flat list of (x,y) slots across all monitors, column-major
        var slots = new List<(int x, int y)>();
        foreach (var wa in workAreas)
        {
            int rows = Math.Max(1, (wa.Bottom - wa.Top) / sy);
            int cols = Math.Max(1, (wa.Right - wa.Left) / sx);
            for (int c = 0; c < cols; c++)
                for (int r = 0; r < rows; r++)
                    slots.Add((wa.Left + c * sx, wa.Top + r * sy));
        }

        // Read icon names, assign slots
        GetWindowThreadProcessId(lv, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, pid);
        if (hProc == IntPtr.Zero) return;

        try
        {
            int style = GetWindowLong(lv, GWL_STYLE);
            if ((style & LVS_AUTOARRANGE) != 0)
                SetWindowLong(lv, GWL_STYLE, style & ~LVS_AUTOARRANGE);

            const int MaxPath = 260;
            uint textBufSize = (uint)(MaxPath * 2);
            uint lvItemSize = (uint)Marshal.SizeOf<LVITEMW>();
            uint ptSize = (uint)Marshal.SizeOf<POINT>();
            uint totalSize = lvItemSize + textBufSize + ptSize;

            IntPtr remoteBlock = VirtualAllocEx(hProc, IntPtr.Zero, totalSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteBlock == IntPtr.Zero) return;

            IntPtr remoteText = remoteBlock + (int)lvItemSize;
            IntPtr remotePt = remoteText + (int)textBufSize;

            byte[] itemBytes = new byte[lvItemSize];
            byte[] textBytes = new byte[textBufSize];
            byte[] ptBytes = new byte[ptSize];

            try
            {
                var item = new LVITEMW { mask = LVIF_TEXT, iSubItem = 0, pszText = remoteText, cchTextMax = MaxPath };

                // Suppress redraws so all icons snap into grid at once
                SendMessage(lv, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                try
                {
                    for (int i = 0; i < Math.Min(count, slots.Count); i++)
                    {
                        item.iItem = i;
                        MarshalToBytes(item, itemBytes);
                        WriteProcessMemory(hProc, remoteBlock, itemBytes, lvItemSize, out _);
                        SendMessage(lv, LVM_GETITEMW, (IntPtr)i, remoteBlock);

                        var (x, y) = slots[i];
                        var pt = new POINT { X = x, Y = y };
                        MarshalToBytes(pt, ptBytes);
                        WriteProcessMemory(hProc, remotePt, ptBytes, ptSize, out _);
                        SendMessage(lv, LVM_SETITEMPOSITION32, (IntPtr)i, remotePt);
                    }
                }
                finally
                {
                    SendMessage(lv, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                    InvalidateRect(lv, IntPtr.Zero, true);
                }
            }
            finally { VirtualFreeEx(hProc, remoteBlock, 0, MEM_RELEASE); }

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

    [ThreadStatic]
    private static List<RECT>? _workAreaCollector;

    private static bool WorkAreaEnumCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
    {
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(hMonitor, ref mi))
            _workAreaCollector?.Add(mi.rcWork);
        return true;
    }

    // ── Internal helpers ─────────────────────────────────────────────────

    private static IntPtr GetDesktopListView()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            IntPtr shellView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView != IntPtr.Zero)
            {
                IntPtr lv = FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null);
                if (lv != IntPtr.Zero) return lv;
            }

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

    private static void MarshalToBytes<T>(T s, byte[] dest) where T : struct
    {
        IntPtr ptr = Marshal.AllocHGlobal(dest.Length);
        try { Marshal.StructureToPtr(s, ptr, false); Marshal.Copy(ptr, dest, 0, dest.Length); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static T BytesToStruct<T>(byte[] bytes) where T : struct
    {
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, ptr, bytes.Length); return Marshal.PtrToStructure<T>(ptr); }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
