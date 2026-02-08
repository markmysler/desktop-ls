using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace DesktopLS.Services;

/// <summary>
/// Monitors for maximized windows on the primary monitor.
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
            bool hasMaximized = HasMaximizedWindowOnPrimaryMonitor();
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

    private bool HasMaximizedWindowOnPrimaryMonitor()
    {
        var primaryBounds = GetPrimaryMonitorBounds();
        bool found = false;

        EnumWindows((hWnd, lParam) =>
        {
            // Skip our own window
            if (hWnd == _ownerHwnd)
                return true;

            // Only check visible windows
            if (!IsWindowVisible(hWnd))
                return true;

            // Check if maximized
            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hWnd, ref placement))
                return true;

            if (placement.showCmd != SW_SHOWMAXIMIZED)
                return true;

            // Check if on primary monitor
            if (GetWindowRect(hWnd, out RECT rect))
            {
                // Window is on primary if its center is within primary monitor bounds
                int centerX = (rect.Left + rect.Right) / 2;
                int centerY = (rect.Top + rect.Bottom) / 2;

                if (centerX >= primaryBounds.Left && centerX < primaryBounds.Right &&
                    centerY >= primaryBounds.Top && centerY < primaryBounds.Bottom)
                {
                    found = true;
                    return false; // Stop enumeration
                }
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static RECT GetPrimaryMonitorBounds()
    {
        return new RECT
        {
            Left = 0,
            Top = 0,
            Right = (int)SystemParameters.PrimaryScreenWidth,
            Bottom = (int)SystemParameters.PrimaryScreenHeight
        };
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
}
