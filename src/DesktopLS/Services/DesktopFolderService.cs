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
public sealed class DesktopFolderService : IDesktopFolderService, IDisposable
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
    private readonly SemaphoreSlim _layoutGate = new(1, 1);
    private CancellationTokenSource? _layoutCts;
    private Task? _pendingLayoutRestore;
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
                Log.Error($"Crash recovery: could not restore the desktop to {savedOriginal} yet");
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

        // Stop any layout restore still running for the previous folder, then
        // save the current layout (it must be captured before the switch)
        CancelPendingLayoutRestore();
        _iconLayouts.SaveLayout(_currentRedirectedPath ?? _originalUserDesktop);
        var namesBefore = Native.DesktopIconManager.ReadNames();

        // Write crash-recovery state on first redirect away from original
        // Without it a crash would leave the desktop redirected for good, so
        // don't redirect if it can't be written
        if (!goingToOriginal && !_crashStateWritten)
        {
            try { File.WriteAllText(UserStateFile, _originalUserDesktop); }
            catch (Exception ex)
            {
                throw new IOException($"Can't save recovery info, so the desktop wasn't changed: {ex.Message}", ex);
            }
            _crashStateWritten = true;
        }

        if (!TrySetKnownFolderPath(path))
            throw new IOException($"Windows refused to show {path} on the desktop.");
        _currentRedirectedPath = path;

        // Restore saved layout (or auto-arrange grid for first visit) once
        // Explorer shows the new folder
        _pendingLayoutRestore = StartLayoutRestore(path, namesBefore, arrangeIfNew: true);
    }

    /// <summary>Restores the original desktop. Safe to call multiple times.</summary>
    public void Restore()
    {
        if (_currentRedirectedPath == null) return;

        CancelPendingLayoutRestore();
        _iconLayouts.SaveLayout(_currentRedirectedPath); // logs its own failures
        var namesBefore = Native.DesktopIconManager.ReadNames();

        // Keep the state file unless the restore really worked, so the next
        // start can still recover
        if (!TrySetKnownFolderPath(_originalUserDesktop))
        {
            Log.Error($"Restoring the desktop to {_originalUserDesktop} failed; will retry on next start");
            return;
        }
        _currentRedirectedPath = null;
        _crashStateWritten = false;

        TryDelete(UserStateFile);

        // Restore the original desktop's icon positions. This runs on exit, so
        // wait for it (bounded) instead of leaving it to a task the exiting
        // process would kill.
        var restore = StartLayoutRestore(_originalUserDesktop, namesBefore, arrangeIfNew: false);
        try { restore.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Restore();
    }

    // ── Layout restore after a switch ────────────────────────────────────

    /// <summary>
    /// Restores (or, for a first visit, arranges) the icons of path once
    /// Explorer shows it. Restores run one at a time and a newer switch
    /// cancels an older one, so navigating quickly can't apply one folder's
    /// layout to another.
    /// </summary>
    private Task StartLayoutRestore(string path, HashSet<string> namesBefore, bool arrangeIfNew)
    {
        var cts = new CancellationTokenSource();
        _layoutCts = cts;
        var token = cts.Token;
        bool hasLayout = _iconLayouts.HasLayout(path);
        if (!hasLayout && !arrangeIfNew) return Task.CompletedTask;

        return Task.Run(async () =>
        {
            await _layoutGate.WaitAsync(token);
            try
            {
                await WaitForFolderShownAsync(namesBefore, token);
                token.ThrowIfCancellationRequested();
                if (hasLayout)
                    _iconLayouts.RestoreLayout(path);
                else
                    Native.DesktopIconManager.ArrangeInGrid();
            }
            finally { _layoutGate.Release(); }
        }, token);
    }

    private void CancelPendingLayoutRestore()
    {
        _layoutCts?.Cancel();
        // Let a restore that's mid-write finish before reading positions again
        try { _pendingLayoutRestore?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    /// <summary>
    /// Waits until Explorer's icons are no longer the previous folder's and
    /// the icon count has stopped changing. Checking only the count could
    /// match the old folder's icons before Explorer refreshed.
    /// </summary>
    private static async Task WaitForFolderShownAsync(HashSet<string> namesBefore, CancellationToken token, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool changed = namesBefore.Count == 0;
        int stable = 0, last = -1;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            if (!changed)
            {
                var now = Native.DesktopIconManager.ReadNames();
                changed = !now.SetEquals(namesBefore);
            }
            else
            {
                int count = Native.DesktopIconManager.GetIconCount();
                if (count == last) { if (++stable >= 2) return; }
                else { stable = 0; last = count; }
            }
            await Task.Delay(50, token);
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
            catch (Exception ex)
            {
                // Public Desktop usually needs admin rights; leave the item stashed
                Log.Error($"Moving {item} back to the Public Desktop failed", ex);
            }
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
