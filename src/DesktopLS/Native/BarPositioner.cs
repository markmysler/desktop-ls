using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DesktopLS.Services;
using Microsoft.Win32;

namespace DesktopLS.Native;

/// <summary>
/// Keeps the bar on its edge (top or bottom) of a monitor and, when space is
/// reserved, registers it as an app bar (SHAppBarMessage) so maximized
/// windows stop at it instead of covering it.
///
/// Works in physical pixels with SetWindowPos: monitor and work-area
/// rectangles come in pixels, and converting them to WPF units would need
/// each monitor's own DPI.
/// </summary>
public sealed class BarPositioner : IDisposable
{
    private readonly Window _window;
    private readonly SettingsService _settings;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private uint _callbackMessage;
    private bool _registered;
    private bool _applying;

    public BarPositioner(Window window, SettingsService settings)
    {
        _window = window;
        _settings = settings;
    }

    /// <summary>Call once the window has a handle (Loaded).</summary>
    public void Attach()
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _callbackMessage = RegisterWindowMessage("DesktopLS.AppBar");
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Apply();
    }

    /// <summary>Puts the bar where the settings say (and reserves space if enabled).</summary>
    public void Apply()
    {
        if (_hwnd == IntPtr.Zero || _applying) return;
        _applying = true;
        try
        {
            var placement = _settings.Placement;
            var monitor = FindMonitor(placement.Monitor);
            if (monitor == null || !GetWindowRect(_hwnd, out RECT window)) return;
            int w = window.Right - window.Left, h = window.Bottom - window.Top;

            int y;
            if (_settings.ReserveSpace)
            {
                var strip = ReserveStrip(monitor.Value.Bounds, h, placement.Edge);
                y = placement.Edge == BarEdge.Top ? strip.Top : strip.Bottom - h;
            }
            else
            {
                Unregister();
                y = placement.Edge == BarEdge.Top ? monitor.Value.Work.Top : monitor.Value.Work.Bottom - h;
            }
            int x = HorizontalPosition(monitor.Value.Bounds, w, placement.X);
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch (Exception ex) { Log.Error("Positioning the bar failed", ex); }
        finally { _applying = false; }
    }

    /// <summary>
    /// After the user dragged the bar: snap it to the nearer edge of the
    /// monitor it was dropped on, remember where, and re-apply.
    /// </summary>
    public void OnDragged()
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out RECT window)) return;
        IntPtr hmon = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hmon, ref info)) return;

        _settings.Placement = PlacementFromWindow(window, info.rcMonitor, info.szDevice);
        Apply();
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _source?.RemoveHook(WndProc);
        Unregister();
    }

    // ── Placement maths (pure, tested) ───────────────────────────────────

    /// <summary>Left edge for a bar of width w at fraction x of the monitor's free width.</summary>
    internal static int HorizontalPosition(RECT monitor, int w, double x)
        => monitor.Left + (int)Math.Round(Math.Clamp(x, 0, 1) * Math.Max(0, monitor.Right - monitor.Left - w));

    /// <summary>Edge and horizontal fraction for a bar dropped at window on monitor.</summary>
    internal static BarPlacement PlacementFromWindow(RECT window, RECT monitor, string? device)
    {
        int w = window.Right - window.Left;
        int free = monitor.Right - monitor.Left - w;
        double x = free <= 0 ? 0.5 : Math.Clamp((window.Left - monitor.Left) / (double)free, 0, 1);
        double centreY = (window.Top + window.Bottom) / 2.0;
        var edge = centreY < (monitor.Top + monitor.Bottom) / 2.0 ? BarEdge.Top : BarEdge.Bottom;
        return new BarPlacement(edge, Math.Round(x, 3), device);
    }

    /// <summary>The full-width strip of height h on an edge of monitor.</summary>
    internal static RECT EdgeStrip(RECT monitor, int h, BarEdge edge) => edge == BarEdge.Top
        ? new RECT { Left = monitor.Left, Right = monitor.Right, Top = monitor.Top, Bottom = monitor.Top + h }
        : new RECT { Left = monitor.Left, Right = monitor.Right, Top = monitor.Bottom - h, Bottom = monitor.Bottom };

    // ── App bar ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reserves a strip of height h on the edge; Windows moves it past other
    /// app bars (like the taskbar) and returns where it ended up.
    /// </summary>
    private RECT ReserveStrip(RECT monitor, int h, BarEdge edge)
    {
        if (!_registered)
        {
            var reg = NewData();
            reg.uCallbackMessage = _callbackMessage;
            _registered = SHAppBarMessage(ABM_NEW, ref reg) != IntPtr.Zero;
        }

        var data = NewData();
        data.uEdge = edge == BarEdge.Top ? ABE_TOP : ABE_BOTTOM;
        data.rc = EdgeStrip(monitor, h, edge);
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        // QUERYPOS may move the strip past the taskbar; keep our height
        if (edge == BarEdge.Top) data.rc.Bottom = data.rc.Top + h;
        else data.rc.Top = data.rc.Bottom - h;
        SHAppBarMessage(ABM_SETPOS, ref data);
        return data.rc;
    }

    private void Unregister()
    {
        if (!_registered) return;
        var data = NewData();
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
    }

    private APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Another app bar (e.g. the taskbar) moved or resized: take our place again
        if (_registered && msg == (int)_callbackMessage && wParam.ToInt32() == ABN_POSCHANGED)
            _window.Dispatcher.BeginInvoke(Apply);
        return IntPtr.Zero;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _window.Dispatcher.BeginInvoke(Apply);

    // ── Monitors ─────────────────────────────────────────────────────────

    private static (RECT Bounds, RECT Work)? FindMonitor(string? device)
    {
        (RECT, RECT)? primary = null, match = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hmon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hmon, ref info))
            {
                if ((info.dwFlags & MONITORINFOF_PRIMARY) != 0) primary = (info.rcMonitor, info.rcWork);
                if (device != null && string.Equals(info.szDevice, device, StringComparison.OrdinalIgnoreCase))
                    match = (info.rcMonitor, info.rcWork);
            }
            return true;
        }, IntPtr.Zero);
        return match ?? primary;
    }

    // ── Interop ──────────────────────────────────────────────────────────

    private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    private const uint ABE_TOP = 1, ABE_BOTTOM = 3;
    private const int ABN_POSCHANGED = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITORINFOF_PRIMARY = 1;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
}
