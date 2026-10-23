using System.Runtime.InteropServices;

namespace DesktopLS.Native;

/// <summary>
/// Reads desktop icon positions through the shell's own view of the desktop
/// (IShellWindows -> IShellBrowser -> IFolderView), keyed by each item's
/// parsing name: the real file name, including the extension.
///
/// Read-only on purpose: IFolderView.SelectAndPositionItems bumps icons that
/// sit on a target slot (with align-to-grid on), cascading down columns, so
/// positions are written through <see cref="DesktopIconManager"/> instead.
///
/// Display names are not unique (with extensions hidden, report.docx and
/// report.pdf both show as "report"), so the list-view approach in
/// <see cref="DesktopIconManager"/> can mix their positions up.
/// </summary>
public static class ShellDesktopView
{
    /// <summary>
    /// Current positions keyed by parsing name, with each item's display name.
    /// Null if the shell view isn't available (callers fall back to the list view).
    /// </summary>
    public static Dictionary<string, (int X, int Y, string DisplayName)>? ReadPositions()
    {
        try
        {
            var (view, folder) = GetDesktopView();
            try
            {
                var result = new Dictionary<string, (int, int, string)>(StringComparer.OrdinalIgnoreCase);
                // The desktop merges the user's and the Public Desktop, so a
                // parsing name can appear twice (both have a hidden
                // desktop.ini). Such names don't identify one item: leave them out.
                var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IntPtr pidl in EnumerateItems(view))
                {
                    try
                    {
                        string? parsing = GetName(folder, pidl, SHGDN_INFOLDER | SHGDN_FORPARSING);
                        string? display = GetName(folder, pidl, SHGDN_INFOLDER);
                        if (string.IsNullOrEmpty(parsing) || view.GetItemPosition(pidl, out POINT pt) != 0) continue;
                        if (!result.TryAdd(parsing, (pt.X, pt.Y, display ?? parsing)))
                            duplicates.Add(parsing);
                    }
                    finally { Marshal.FreeCoTaskMem(pidl); }
                }
                foreach (string name in duplicates) result.Remove(name);
                return result;
            }
            finally { Marshal.ReleaseComObject(view); Marshal.ReleaseComObject(folder); }
        }
        catch (Exception ex)
        {
            Services.Log.Error("Reading icon positions through the shell view failed", ex);
            return null;
        }
    }

    /// <summary>
    /// The file system folder Explorer's desktop is showing, as Explorer
    /// resolved it when it started (null if it can't be asked). With the
    /// Desktop pointed at a junction this is the junction's path, unless that
    /// Explorer resolved the junction to its target.
    /// </summary>
    public static string? GetShownFolderPath()
    {
        try
        {
            var shellWindows = (IShellWindows)Activator.CreateInstance(
                Type.GetTypeFromCLSID(CLSID_ShellWindows, throwOnError: true)!)!;
            try
            {
                object location = CSIDL_DESKTOP;
                object root = Type.Missing;
                dynamic browser = shellWindows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
                try { return (string?)browser.Document.Folder.Self.Path; }
                finally { Marshal.ReleaseComObject(browser); }
            }
            finally { Marshal.ReleaseComObject(shellWindows); }
        }
        catch (Exception ex)
        {
            Services.Log.Error("Asking Explorer which folder the desktop shows failed", ex);
            return null;
        }
    }

    // ── Getting the desktop's IFolderView ────────────────────────────────
    private static (IFolderView View, IShellFolder Folder) GetDesktopView()
    {
        var shellWindows = (IShellWindows)Activator.CreateInstance(
            Type.GetTypeFromCLSID(CLSID_ShellWindows, throwOnError: true)!)!;
        try
        {
            object location = CSIDL_DESKTOP;
            object root = Type.Missing;
            object dispatch = shellWindows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
            try
            {
                var provider = (IServiceProvider)dispatch;
                Guid sid = SID_STopLevelBrowser, iid = typeof(IShellBrowser).GUID;
                Marshal.ThrowExceptionForHR(provider.QueryService(ref sid, ref iid, out object browserObj));
                var browser = (IShellBrowser)browserObj;
                try
                {
                    Marshal.ThrowExceptionForHR(browser.QueryActiveShellView(out IShellView shellView));
                    var view = (IFolderView)shellView;
                    Guid folderIid = typeof(IShellFolder).GUID;
                    Marshal.ThrowExceptionForHR(view.GetFolder(ref folderIid, out object folderObj));
                    return (view, (IShellFolder)folderObj);
                }
                finally { Marshal.ReleaseComObject(browser); }
            }
            finally { Marshal.ReleaseComObject(dispatch); }
        }
        finally { Marshal.ReleaseComObject(shellWindows); }
    }

    private static IEnumerable<IntPtr> EnumerateItems(IFolderView view)
    {
        Marshal.ThrowExceptionForHR(view.ItemCount(SVGIO_ALLVIEW, out int count));
        for (int i = 0; i < count; i++)
            if (view.Item(i, out IntPtr pidl) == 0 && pidl != IntPtr.Zero)
                yield return pidl;
    }

    private static string? GetName(IShellFolder folder, IntPtr pidl, uint flags)
    {
        if (folder.GetDisplayNameOf(pidl, flags, out STRRET strret) != 0) return null;
        return StrRetToBSTR(ref strret, pidl, out string name) == 0 ? name : null;
    }

    // ── Interop ──────────────────────────────────────────────────────────
    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private const int CSIDL_DESKTOP = 0;
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const uint SVGIO_ALLVIEW = 2;
    private const uint SHGDN_INFOLDER = 0x1;
    private const uint SHGDN_FORPARSING = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Explicit, Size = 272)]
    private struct STRRET { [FieldOffset(0)] public uint uType; }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrRetToBSTR(ref STRRET pstr, IntPtr pidl, [MarshalAs(UnmanagedType.BStr)] out string pbstr);

    // Only the vtable slots we call are typed; the others keep their place.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        void _get_Count(); void _Item(); void _NewEnum(); void _Register();
        void _RegisterPending(); void _Revoke(); void _OnNavigate(); void _OnActivated();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object pvarloc,
                            [MarshalAs(UnmanagedType.Struct)] ref object pvarlocRoot,
                            int swClass, out int phwnd, int swfwOptions);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void _GetWindow(); void _ContextSensitiveHelp(); void _InsertMenusSB(); void _SetMenuSB();
        void _RemoveMenusSB(); void _SetStatusTextSB(); void _EnableModelessSB(); void _TranslateAcceleratorSB();
        void _BrowseObject(); void _GetViewStateStream(); void _GetControlWindow(); void _SendControlMsg();
        [PreserveSig] int QueryActiveShellView(out IShellView ppshv);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView { }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void _GetCurrentViewMode(); void _SetCurrentViewMode();
        [PreserveSig] int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
        void _Items(); void _GetSelectionMarkedItem(); void _GetFocusedItem();
        [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void _ParseDisplayName(); void _EnumObjects(); void _BindToObject(); void _BindToStorage();
        void _CompareIDs(); void _CreateViewObject(); void _GetAttributesOf(); void _GetUIObjectOf();
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out STRRET pName);
    }
}
