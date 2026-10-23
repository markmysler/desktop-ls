using System.IO;
using System.Runtime.InteropServices;

namespace DesktopLS.Services;

/// <summary>
/// Redirects the Windows Explorer Desktop to an arbitrary folder.
/// Uses SHSetKnownFolderPath for the user desktop. Shortcuts on the shared
/// Public Desktop (C:\Users\Public\Desktop) stay visible: that folder belongs
/// to every user on the PC, so DesktopLS never modifies it.
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

    // Only used to recover items that version 1.0.0 moved off the Public Desktop
    private static readonly string PublicStashDir = Path.Combine(AppDataDir, "public_stash");

    private static readonly string PublicDesktopPath =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    private readonly string _originalUserDesktop;
    private readonly IconLayoutService _iconLayouts = new();
    private bool _disposed;
    private bool _crashStateWritten;
    private string? _currentRedirectedPath;

    public DesktopFolderService()
    {
        Directory.CreateDirectory(AppDataDir);

        // Crash recovery: a state file means a previous run ended while the
        // desktop was redirected. Its content is the user's real desktop.
        string? savedOriginal = ReadSavedOriginal();
        if (savedOriginal != null)
        {
            if (Directory.Exists(savedOriginal) && TrySetKnownFolderPath(savedOriginal))
            {
                TryDelete(UserStateFile);
            }
            else
            {
                // The original isn't reachable yet (e.g. OneDrive/network
                // desktop not mounted) or the restore failed: keep the state
                // file so the next start retries, and keep treating the saved
                // path as the real desktop
                _crashStateWritten = true;
            }
        }

        // Version 1.0.0 moved Public Desktop items into a stash; put back
        // anything such a version left behind
        RestoreLegacyPublicStash();

        _originalUserDesktop = savedOriginal ?? GetKnownFolderPath();
    }

    public string? CurrentPath => _currentRedirectedPath;

    /// <summary>The user's real desktop folder (what Restore goes back to).</summary>
    public string OriginalDesktop => _originalUserDesktop;

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

        if (!TrySetKnownFolderPath(path))
            throw new IOException($"Windows refused to show {path} on the desktop.");
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

        try { _iconLayouts.SaveLayout(_currentRedirectedPath); } catch { }

        // Keep the state file unless the restore really worked, so the next
        // start can still recover
        if (!TrySetKnownFolderPath(_originalUserDesktop)) return;
        _currentRedirectedPath = null;
        _crashStateWritten = false;

        TryDelete(UserStateFile);

        // Restore original desktop icon positions
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

    /// <summary>Points the Desktop known folder at path; false if Windows refused.</summary>
    private static bool TrySetKnownFolderPath(string path)
    {
        var guid = FOLDERID_Desktop;
        int hr = SHSetKnownFolderPath(ref guid, 0, IntPtr.Zero, path);
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        return hr == 0;
    }

    private static string? ReadSavedOriginal()
    {
        try
        {
            if (!File.Exists(UserStateFile)) return null;
            string saved = File.ReadAllText(UserStateFile).Trim();
            return saved.Length > 0 ? saved : null;
        }
        catch { return null; }
    }

    // ── Legacy Public Desktop stash ──────────────────────────────────────

    /// <summary>
    /// Moves items left in the old stash folder back to the Public Desktop.
    /// Never overwrites or deletes: anything whose name already exists on the
    /// Public Desktop stays in the stash for the user to resolve.
    /// </summary>
    private static void RestoreLegacyPublicStash()
    {
        if (!Directory.Exists(PublicStashDir)) return;

        foreach (var item in Directory.GetFileSystemEntries(PublicStashDir))
        {
            string dest = Path.Combine(PublicDesktopPath, Path.GetFileName(item));
            if (File.Exists(dest) || Directory.Exists(dest)) continue;
            try
            {
                if (File.Exists(item)) File.Move(item, dest);
                else if (Directory.Exists(item)) Directory.Move(item, dest);
            }
            catch { /* Public Desktop usually needs admin rights; leave the item stashed */ }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(PublicStashDir).Any())
                Directory.Delete(PublicStashDir);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
