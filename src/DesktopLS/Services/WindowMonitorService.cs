using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace DesktopLS.Services;

/// <summary>
/// Monitors for maximized or full-screen windows on the bar's monitor.
/// Raises event when a maximized window appears or disappears.
/// </summary>
public sealed class WindowMonitorService : IDisposable
{
    private readonly Window _ownerWindow;
    private IntPtr _ownerHwnd;
    private Timer? _monitorTimer;
    private bool _lastMaximizedState;
    private bool _disposed;

    public bool Enabled { get; set; } = true;

    public event Action<bool>? MaximizedWindowStateChanged;

    public WindowMonitorService(Window ownerWindow)
    {
        _ownerWindow = ownerWindow;
    }

    public void Start()
    {
        _ownerHwnd = new WindowInteropHelper(_ownerWindow).Handle;
        _monitorTimer = new Timer(_ => CheckMaximizedState(), null, 0, 500);
    }

    public void Stop()
    {
        _monitorTimer?.Dispose();
        _monitorTimer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void CheckMaximizedState()
    {
        if (!Enabled) return;

        try
        {
            bool hasMaximized = HasMaximizedWindowOnBarMonitor();
            if (hasMaximized != _lastMaximizedState)
            {
                _lastMaximizedState = hasMaximized;
                MaximizedWindowStateChanged?.Invoke(hasMaximized);
            }
        }
        catch
        {
            // Best-effort monitoring
        }
    }

    /// <summary>
    /// True if a visible window is maximized or full screen on the monitor the
    /// bar is on. Everything is compared in physical pixels via monitor
    /// handles, so it works at any display scaling.
    /// </summary>
    private bool HasMaximizedWindowOnBarMonitor()
    {
        IntPtr barMonitor = MonitorFromWindow(_ownerHwnd, MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(barMonitor, ref mi)) return false;
        uint ownPid = (uint)Environment.ProcessId;
        bool found = false;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd) || IsCloaked(hWnd))
                return true;

            // Skip our own windows (bar, popups) and the shell's desktop/taskbar
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == ownPid || IsShellWindow(hWnd))
                return true;

            if (MonitorFromWindow(hWnd, MONITOR_DEFAULTTONULL) != barMonitor)
                return true;

            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            bool maximized = GetWindowPlacement(hWnd, ref placement) && placement.showCmd == SW_SHOWMAXIMIZED;

            if (maximized || IsFullScreen(hWnd, mi.rcMonitor))
            {
                found = true;
                return false; // Stop enumeration
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>A borderless window covering the whole monitor (games, video players).</summary>
    private static bool IsFullScreen(IntPtr hWnd, RECT monitor)
    {
        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        // Overlays (tool windows, click-through layers) often span the screen too
        if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)) != 0)
            return false;
        return GetWindowRect(hWnd, out RECT r) &&
               r.Left <= monitor.Left && r.Top <= monitor.Top &&
               r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
    }

    /// <summary>Windows on another virtual desktop or suspended UWP apps are "cloaked".</summary>
    private static bool IsCloaked(IntPtr hWnd)
        => DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool IsShellWindow(IntPtr hWnd)
    {
        var name = new System.Text.StringBuilder(32);
        GetClassName(hWnd, name, name.Capacity);
        string cls = name.ToString();
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    // P/Invoke declarations

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int SW_SHOWMAXIMIZED = 3;
    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_CLOAKED = 14;
}
