using System.IO;
using System.Runtime.InteropServices;

namespace DesktopLS.Services;

/// <summary>
/// Redirects the Windows Explorer Desktop to an arbitrary folder.
/// Uses SHSetKnownFolderPath for the user desktop and stashes the
/// Public Desktop contents (C:\Users\Public\Desktop) so no merged
/// shortcuts bleed through.
/// </summary>
public sealed class DesktopFolderService : IDisposable
{
    private static readonly Guid FOLDERID_Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    [DllImport("shell32.dll")]
    private static extern int SHSetKnownFolderPath(
        ref Guid rfid, uint dwFlags, IntPtr hToken,
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_FLUSH = 0x1000;

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopLS");

    private static readonly string UserStateFile = Path.Combine(AppDataDir, "desktop_original.txt");
    private static readonly string PublicStashDir = Path.Combine(AppDataDir, "public_stash");

    private static readonly string PublicDesktopPath =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    private readonly string _originalUserDesktop;
    private bool _disposed;
    private string? _currentRedirectedPath;

    public DesktopFolderService()
    {
        Directory.CreateDirectory(AppDataDir);

        // Crash recovery: restore user desktop if state file left behind
        if (File.Exists(UserStateFile))
        {
            try
            {
                string saved = File.ReadAllText(UserStateFile).Trim();
                if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
                    SetKnownFolderPath(saved);
            }
            catch { }
            finally { TryDelete(UserStateFile); }
        }

        // Crash recovery: restore public desktop stash if left behind
        RestorePublicDesktop();

        _originalUserDesktop = GetKnownFolderPath();
    }

    public string? CurrentPath => _currentRedirectedPath;

    /// <summary>
    /// Redirects the Windows desktop to show the given folder's contents.
    /// </summary>
    public void SetDesktopPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));
        if (!Directory.Exists(path))
            throw new ArgumentException($"Directory does not exist: {path}", nameof(path));

        // Persist state for crash recovery before first redirect
        if (_currentRedirectedPath == null)
        {
            File.WriteAllText(UserStateFile, _originalUserDesktop);
            StashPublicDesktop();
        }

        SetKnownFolderPath(path);
        _currentRedirectedPath = path;
    }

    /// <summary>Restores the original desktop. Safe to call multiple times.</summary>
    public void Restore()
    {
        if (_currentRedirectedPath == null) return;

        SetKnownFolderPath(_originalUserDesktop);
        RestorePublicDesktop();
        _currentRedirectedPath = null;

        TryDelete(UserStateFile);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Restore();
    }

    // ── Known folder helpers ──────────────────────────────────────────────

    private static string GetKnownFolderPath()
    {
        var guid = FOLDERID_Desktop;
        int hr = SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out IntPtr ptr);
        if (hr != 0) return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        try { return Marshal.PtrToStringUni(ptr) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop); }
        finally { CoTaskMemFree(ptr); }
    }

    private static void SetKnownFolderPath(string path)
    {
        var guid = FOLDERID_Desktop;
        SHSetKnownFolderPath(ref guid, 0, IntPtr.Zero, path);
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
    }

    // ── Public Desktop stash ─────────────────────────────────────────────

    private static void StashPublicDesktop()
    {
        try
        {
            if (!Directory.Exists(PublicDesktopPath)) return;
            Directory.CreateDirectory(PublicStashDir);

            foreach (var item in Directory.GetFileSystemEntries(PublicDesktopPath))
            {
                string name = Path.GetFileName(item);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                string dest = Path.Combine(PublicStashDir, name);
                if (File.Exists(item))
                    File.Move(item, dest, overwrite: true);
                else if (Directory.Exists(item))
                    MoveDirectory(item, dest);
            }
        }
        catch { /* best effort — public desktop may be inaccessible */ }
    }

    private static void RestorePublicDesktop()
    {
        try
        {
            if (!Directory.Exists(PublicStashDir)) return;

            foreach (var item in Directory.GetFileSystemEntries(PublicStashDir))
            {
                string name = Path.GetFileName(item);
                string dest = Path.Combine(PublicDesktopPath, name);
                if (File.Exists(item))
                    File.Move(item, dest, overwrite: true);
                else if (Directory.Exists(item))
                    MoveDirectory(item, dest);
            }

            // Remove stash dir if empty
            if (!Directory.GetFileSystemEntries(PublicStashDir).Any())
                Directory.Delete(PublicStashDir);
        }
        catch { /* best effort */ }
    }

    private static void MoveDirectory(string source, string dest)
    {
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        Directory.Move(source, dest);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
