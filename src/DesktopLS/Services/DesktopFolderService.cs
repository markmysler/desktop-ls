using System.IO;
using System.Runtime.InteropServices;

namespace DesktopLS.Services;

/// <summary>
/// Redirects the Windows Explorer Desktop to an arbitrary folder.
/// Uses SHSetKnownFolderPath for the user desktop and stashes the
/// Public Desktop contents (C:\Users\Public\Desktop) so no merged
/// shortcuts bleed through.
/// Saves and restores desktop icon positions per folder.
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
    private readonly IconLayoutService _iconLayouts = new();
    private bool _disposed;
    private bool _publicStashed;
    private bool _crashStateWritten;
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

        bool goingToOriginal = string.Equals(path, _originalUserDesktop, StringComparison.OrdinalIgnoreCase);

        // Save current layout before switching
        _iconLayouts.SaveLayout(_currentRedirectedPath ?? _originalUserDesktop);

        // Write crash-recovery state on first redirect away from original
        if (!goingToOriginal && !_crashStateWritten)
        {
            File.WriteAllText(UserStateFile, _originalUserDesktop);
            _crashStateWritten = true;
        }

        // Stash/restore Public Desktop BEFORE redirect so items vanish
        // before the new folder appears (no lingering icons).
        if (!goingToOriginal && !_publicStashed)
        {
            StashPublicDesktop();
            _publicStashed = true;
        }
        else if (goingToOriginal && _publicStashed)
        {
            RestorePublicDesktop();
            _publicStashed = false;
        }

        SetKnownFolderPath(path);
        _currentRedirectedPath = path;

        // Restore saved layout (or auto-arrange grid for first visit)
        string snapshotPath = path;
        bool hasLayout = _iconLayouts.HasLayout(path);
        int expectedCount = hasLayout ? _iconLayouts.GetSavedCount(path) : 1;
        _ = Task.Run(async () =>
        {
            await WaitForIconsAsync(expectedCount);
            if (hasLayout)
                _iconLayouts.RestoreLayout(snapshotPath);
            else
                Native.DesktopIconManager.ArrangeInGrid();
        });
    }

    /// <summary>Restores the original desktop. Safe to call multiple times.</summary>
    public void Restore()
    {
        if (_currentRedirectedPath == null) return;

        _iconLayouts.SaveLayout(_currentRedirectedPath);

        if (_publicStashed)
        {
            RestorePublicDesktop();
            _publicStashed = false;
        }

        SetKnownFolderPath(_originalUserDesktop);
        _currentRedirectedPath = null;
        _crashStateWritten = false;

        TryDelete(UserStateFile);

        // Restore original desktop icon positions after public items are back
        if (_iconLayouts.HasLayout(_originalUserDesktop))
        {
            string origPath = _originalUserDesktop;
            int origCount = _iconLayouts.GetSavedCount(_originalUserDesktop);
            _ = Task.Run(async () =>
            {
                await WaitForIconsAsync(origCount);
                _iconLayouts.RestoreLayout(origPath);
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Restore();
    }

    // ── Icon readiness polling ────────────────────────────────────────────

    private static async Task WaitForIconsAsync(int expectedMin, int timeoutMs = 2000)
    {
        if (expectedMin <= 0) expectedMin = 1;
        int stable = 0, last = -1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            int count = Native.DesktopIconManager.GetIconCount();
            if (count >= expectedMin)
            {
                if (count == last) { if (++stable >= 2) break; }
                else { stable = 0; last = count; }
            }
            await Task.Delay(50);
        }
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

    private void StashPublicDesktop()
    {
        try
        {
            if (!Directory.Exists(PublicDesktopPath)) return;

            // Build set of names already in the user's desktop so we skip duplicates
            var userNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_originalUserDesktop))
            {
                foreach (var e in Directory.GetFileSystemEntries(_originalUserDesktop))
                    userNames.Add(Path.GetFileName(e));
            }

            Directory.CreateDirectory(PublicStashDir);

            foreach (var item in Directory.GetFileSystemEntries(PublicDesktopPath))
            {
                string name = Path.GetFileName(item);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (userNames.Contains(name)) continue; // already present in user desktop

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
