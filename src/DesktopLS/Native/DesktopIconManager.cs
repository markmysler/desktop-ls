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
    private const int LVM_SETITEMSTATE = LVM_FIRST + 43;
    private const int LVM_GETITEMSPACING = LVM_FIRST + 51;
    private const uint LVIS_FOCUSED = 0x0001;
    private const uint LVIS_SELECTED = 0x0002;

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
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;

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
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint MessageTimeoutMs = 1000;

    /// <summary>
    /// SendMessage to Explorer's list view with a timeout: if Explorer is hung
    /// the call returns (with 0) instead of freezing DesktopLS.
    /// </summary>
    private static IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        return SendMessageTimeout(hWnd, msg, wParam, lParam, SMTO_ABORTIFHUNG, MessageTimeoutMs, out IntPtr result) == IntPtr.Zero
            ? IntPtr.Zero
            : result;
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
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

    /// <summary>Positions by display name (a name shown twice keeps one of them).</summary>
    public static Dictionary<string, (int X, int Y)> ReadPositions()
    {
        var result = new Dictionary<string, (int X, int Y)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, x, y) in ReadItems()) result[name] = (x, y);
        return result;
    }

    /// <summary>Every icon's display name, duplicates included.</summary>
    public static List<string> ReadAllNames() => ReadItems().Select(i => i.Name).ToList();

    /// <summary>Every icon's display name and position, duplicates included.</summary>
    public static List<(string Name, int X, int Y)> ReadItems()
    {
        var result = new List<(string, int, int)>();
        IntPtr lv = GetDesktopListView();
        if (lv == IntPtr.Zero) return result;

        GetWindowThreadProcessId(lv, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, pid);
        if (hProc == IntPtr.Zero) { LogOpenFailure(); return result; }

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
                    if (!WriteProcessMemory(hProc, remoteBlock, itemBytes, lvItemSize, out _)) continue;
                    SendMessage(lv, LVM_GETITEMW, (IntPtr)i, remoteBlock);
                    if (!ReadProcessMemory(hProc, remoteText, textBytes, textBufSize, out _)) continue;
                    string name = DecodeName(textBytes);
                    if (string.IsNullOrEmpty(name)) continue;

                    if (SendMessage(lv, LVM_GETITEMPOSITION, (IntPtr)i, remotePt) == IntPtr.Zero) continue;
                    if (!ReadProcessMemory(hProc, remotePt, ptBytes, ptSize, out _)) continue;
                    var pt = BytesToStruct<POINT>(ptBytes);
                    result.Add((name, pt.X, pt.Y));
                }
            }
            finally { VirtualFreeEx(hProc, remoteBlock, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(hProc); }

        return result;
    }

    /// <summary>Moves icons to positions looked up by display name.</summary>
    public static void WritePositions(Dictionary<string, (int X, int Y)> positions)
    {
        if (positions.Count == 0) return;
        WritePositions((name, _) => positions.TryGetValue(name, out var p) ? p : null);
    }

    /// <summary>
    /// Moves icons with LVM_SETITEMPOSITION32. For each icon, targetFor gets
    /// its display name and current position and returns where it should go
    /// (or null to leave it). Unlike IFolderView.SelectAndPositionItems this
    /// never bumps icons that occupy a target slot.
    /// </summary>
    public static void WritePositions(Func<string, (int X, int Y), (int X, int Y)?> targetFor)
    {
        IntPtr lv = GetDesktopListView();
        if (lv == IntPtr.Zero) return;

        GetWindowThreadProcessId(lv, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, pid);
        if (hProc == IntPtr.Zero) { LogOpenFailure(); return; }

        try
        {
            int count = (int)SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
            if (count <= 0) return;

            // Auto-arrange would undo our positions; turn it off and always
            // turn it back on, even if something below fails
            IntPtr style = GetWindowLongPtr(lv, GWL_STYLE);
            if ((style.ToInt64() & LVS_AUTOARRANGE) != 0)
                SetWindowLongPtr(lv, GWL_STYLE, new IntPtr(style.ToInt64() & ~LVS_AUTOARRANGE));
            try
            {
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
                    // Work out every target first (from the current layout), then move
                    var targets = new List<(int Index, int X, int Y)>();
                    var item = new LVITEMW { mask = LVIF_TEXT, iSubItem = 0, pszText = remoteText, cchTextMax = MaxPath };

                    for (int i = 0; i < count; i++)
                    {
                        item.iItem = i;
                        MarshalToBytes(item, itemBytes);
                        if (!WriteProcessMemory(hProc, remoteBlock, itemBytes, lvItemSize, out _)) continue;
                        SendMessage(lv, LVM_GETITEMW, (IntPtr)i, remoteBlock);
                        if (!ReadProcessMemory(hProc, remoteText, textBytes, textBufSize, out _)) continue;
                        string name = DecodeName(textBytes);
                        if (string.IsNullOrEmpty(name)) continue;

                        if (SendMessage(lv, LVM_GETITEMPOSITION, (IntPtr)i, remotePt) == IntPtr.Zero) continue;
                        if (!ReadProcessMemory(hProc, remotePt, ptBytes, ptSize, out _)) continue;
                        var current = BytesToStruct<POINT>(ptBytes);

                        if (targetFor(name, (current.X, current.Y)) is { } target)
                            targets.Add((i, target.X, target.Y));
                    }

                    // Suppress redraws while repositioning so all icons snap at once
                    SendMessage(lv, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                    try
                    {
                        foreach (var (idx, x, y) in targets)
                        {
                            var pt = new POINT { X = x, Y = y };
                            MarshalToBytes(pt, ptBytes);
                            if (!WriteProcessMemory(hProc, remotePt, ptBytes, ptSize, out _)) continue;
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
            }
            finally { SetWindowLongPtr(lv, GWL_STYLE, style); }
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

        // The desktop list view spans the whole virtual screen, so its (0,0) is
        // the virtual screen's top-left, which is negative when a monitor sits
        // left of or above the primary. Convert screen coordinates to it.
        int originX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int originY = GetSystemMetrics(SM_YVIRTUALSCREEN);

        // Build flat list of (x,y) slots across all monitors, column-major
        var slots = new List<(int x, int y)>();
        foreach (var wa in workAreas)
        {
            int rows = Math.Max(1, (wa.Bottom - wa.Top) / sy);
            int cols = Math.Max(1, (wa.Right - wa.Left) / sx);
            for (int c = 0; c < cols; c++)
                for (int r = 0; r < rows; r++)
                    slots.Add((wa.Left - originX + c * sx, wa.Top - originY + r * sy));
        }

        // Read icon names, assign slots
        GetWindowThreadProcessId(lv, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, pid);
        if (hProc == IntPtr.Zero) { LogOpenFailure(); return; }

        try
        {
            // Auto-arrange would undo our positions; turn it off and always
            // turn it back on, even if something below fails
            IntPtr style = GetWindowLongPtr(lv, GWL_STYLE);
            if ((style.ToInt64() & LVS_AUTOARRANGE) != 0)
                SetWindowLongPtr(lv, GWL_STYLE, new IntPtr(style.ToInt64() & ~LVS_AUTOARRANGE));
            try
            {
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
                            var (x, y) = slots[i];
                            var pt = new POINT { X = x, Y = y };
                            MarshalToBytes(pt, ptBytes);
                            if (!WriteProcessMemory(hProc, remotePt, ptBytes, ptSize, out _)) continue;
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
            }
            finally { SetWindowLongPtr(lv, GWL_STYLE, style); }
        }
        finally { CloseHandle(hProc); }
    }

    /// <summary>
    /// Clears keyboard focus and selection on the desktop icons. A focused
    /// icon shows its full (possibly multi-line) label, which covers the grid
    /// slot below it; with align-to-grid on, Explorer then refuses to place
    /// another icon there and pushes it (and the rest of the column) down.
    /// </summary>
    public static void ClearFocusAndSelection()
    {
        IntPtr lv = GetDesktopListView();
        if (lv == IntPtr.Zero) return;

        GetWindowThreadProcessId(lv, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, pid);
        if (hProc == IntPtr.Zero) { LogOpenFailure(); return; }
        try
        {
            uint size = (uint)Marshal.SizeOf<LVITEMW>();
            IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero) return;
            try
            {
                var item = new LVITEMW { stateMask = LVIS_FOCUSED | LVIS_SELECTED, state = 0 };
                byte[] bytes = new byte[size];
                MarshalToBytes(item, bytes);
                if (WriteProcessMemory(hProc, remote, bytes, size, out _))
                    SendMessage(lv, LVM_SETITEMSTATE, (IntPtr)(-1), remote); // -1 = all items
            }
            finally { VirtualFreeEx(hProc, remote, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(hProc); }
    }

    /// <summary>
    /// Every slot of the desktop's icon grid on all monitors, column-major
    /// per monitor, in list-view coordinates. The grid pitch is the list
    /// view's icon spacing; its offset is taken from where icons sit now
    /// (Explorer snaps icons to it with align-to-grid on).
    /// </summary>
    public static List<(int X, int Y)> GridSlots(IReadOnlyCollection<(int X, int Y)> currentPositions)
    {
        var slots = new List<(int X, int Y)>();
        IntPtr lv = GetDesktopListView();
        if (lv == IntPtr.Zero) return slots;

        long spacing = (long)SendMessage(lv, LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero);
        int sx = (int)(spacing & 0xFFFF), sy = (int)((spacing >> 16) & 0xFFFF);
        if (sx <= 0 || sy <= 0) return slots;

        // Most common offset of the icons on the grid (e.g. 14,2)
        int offX = MostCommon(currentPositions.Select(p => Mod(p.X, sx)), 0);
        int offY = MostCommon(currentPositions.Select(p => Mod(p.Y, sy)), 0);

        _workAreaCollector = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, WorkAreaEnumCallback, IntPtr.Zero);
        var workAreas = _workAreaCollector;
        _workAreaCollector = null;
        workAreas.Sort((a, b) => a.Left != b.Left ? a.Left.CompareTo(b.Left) : a.Top.CompareTo(b.Top));

        int originX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int originY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        foreach (var wa in workAreas)
        {
            int left = wa.Left - originX, top = wa.Top - originY;
            int right = wa.Right - originX, bottom = wa.Bottom - originY;
            int firstX = left + Mod(offX - left, sx), firstY = top + Mod(offY - top, sy);
            for (int x = firstX; x + sx <= right; x += sx)
                for (int y = firstY; y + sy <= bottom; y += sy)
                    slots.Add((x, y));
        }
        return slots;
    }

    private static int Mod(int a, int m) => ((a % m) + m) % m;

    private static int MostCommon(IEnumerable<int> values, int fallback)
        => values.GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).DefaultIfEmpty(fallback).First();

    [DllImport("user32.dll")] private static extern bool LockWindowUpdate(IntPtr hWndLock);

    /// <summary>
    /// Stops the desktop icons from being drawn, so a folder switch and the
    /// layout restore after it show up as one change instead of icons
    /// appearing in Explorer's default spots and then moving. Unlike
    /// WM_SETREDRAW, Explorer can't turn this back on while it refreshes.
    /// Must be undone with <see cref="UnlockDrawing"/> on the same thread.
    /// </summary>
    public static bool LockDrawing()
    {
        IntPtr lv = GetDesktopListView();
        return lv != IntPtr.Zero && LockWindowUpdate(lv);
    }

    /// <summary>Draws the desktop icons again (all at once).</summary>
    public static void UnlockDrawing()
    {
        LockWindowUpdate(IntPtr.Zero);
        IntPtr lv = GetDesktopListView();
        if (lv != IntPtr.Zero) InvalidateRect(lv, IntPtr.Zero, true);
    }

    /// <summary>Names of the icons currently on the desktop.</summary>
    public static HashSet<string> ReadNames()
        => new(ReadPositions().Keys, StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Finds the desktop's SysListView32. It lives in SHELLDLL_DefView under
    /// Progman, or, after a wallpaper slideshow / Win+Tab / on some builds,
    /// under a WorkerW window: either a top-level one or a child of Progman.
    /// </summary>
    private static IntPtr GetDesktopListView()
    {
        IntPtr progman = FindWindow("Progman", null);
        IntPtr lv = ListViewUnder(progman);
        if (lv != IntPtr.Zero) return lv;

        foreach (IntPtr parent in new[] { IntPtr.Zero, progman })
        {
            IntPtr workerW = IntPtr.Zero;
            while ((workerW = FindWindowEx(parent, workerW, "WorkerW", null)) != IntPtr.Zero)
            {
                lv = ListViewUnder(workerW);
                if (lv != IntPtr.Zero) return lv;
            }
        }
        return IntPtr.Zero;
    }

    private static IntPtr ListViewUnder(IntPtr host)
    {
        if (host == IntPtr.Zero) return IntPtr.Zero;
        IntPtr shellView = FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null);
        return shellView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>
    /// The text buffer is reused for every item, so after a short name it
    /// still holds the tail of a longer previous one. Stop at the first NUL.
    /// </summary>
    internal static string DecodeName(byte[] utf16)
    {
        string text = Encoding.Unicode.GetString(utf16);
        int end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }

    private static void LogOpenFailure()
        => Services.Log.Error($"Opening Explorer's process failed (Win32 error {Marshal.GetLastWin32Error()})");

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
