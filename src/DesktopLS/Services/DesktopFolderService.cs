using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DesktopLS.Native;

namespace DesktopLS.Services;

/// <summary>
/// Shows an arbitrary folder on the Windows desktop.
///
/// The Desktop known folder points at a junction DesktopLS owns
/// (%LOCALAPPDATA%\DesktopLS\DesktopView). Switching folders retargets that
/// junction and asks Explorer to refresh it. Explorer only reads the Desktop
/// location when it starts (and, since Windows 11 25H2's October 2026
/// updates, ignores every notification that it changed), so pointing the
/// Desktop somewhere new on each switch no longer works; refreshing the same
/// path with new contents does. While DesktopLS isn't switching, the junction
/// points at the user's real desktop, so the desktop looks and works as usual.
///
/// Setting this up needs Explorer to restart once (or a sign-out).
/// Shortcuts on the shared Public Desktop (C:\Users\Public\Desktop) stay
/// visible: that folder belongs to every user on the PC, so DesktopLS never
/// modifies it.
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSH = 0x1000;

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopLS");

    /// <summary>The junction the Desktop known folder points at.</summary>
    public static readonly string JunctionPath = Path.Combine(AppDataDir, "DesktopView");

    // The user's real desktop folder (where the junction points when idle)
    private static readonly string RealDesktopFile = Path.Combine(AppDataDir, "desktop_real.txt");

    // Explorer instance that was running when the Desktop was moved to the
    // junction; until it's gone, the desktop still shows the old location
    private static readonly string SetupPendingFile = Path.Combine(AppDataDir, "setup_pending.txt");

    // Earlier versions redirected the Desktop itself and kept the real one here
    private static readonly string LegacyStateFile = Path.Combine(AppDataDir, "desktop_original.txt");

    // Only used to recover items that version 1.0.0 moved off the Public Desktop
    private static readonly string PublicStashDir = Path.Combine(AppDataDir, "public_stash");

    private static readonly string PublicDesktopPath =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    private readonly string _realDesktop;
    private readonly IconLayoutService _iconLayouts = new();
    private bool _disposed;
    private readonly SemaphoreSlim _layoutGate = new(1, 1);
    private CancellationTokenSource? _layoutCts;
    private Task? _pendingLayoutRestore;
    private string _shownPath;

    // Drawing of the desktop icons is locked during a switch (see
    // DesktopIconManager.LockDrawing). The lock belongs to the UI thread, so
    // restores hand the unlock back to it; the generation makes sure an older
    // switch's unlock can't end a newer switch's lock.
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private int _lockGeneration;
    private bool _drawingLocked;

    public DesktopFolderService()
    {
        Directory.CreateDirectory(AppDataDir);

        // Version 1.0.0 moved Public Desktop items into a stash; put back
        // anything such a version left behind
        RestoreLegacyPublicStash();

        _realDesktop = FindRealDesktop();
        AtomicFile.WriteAllText(RealDesktopFile, _realDesktop);
        _shownPath = _realDesktop;

        // Crash recovery: the junction may still point at the folder a
        // previous run was showing. Put the real desktop back.
        string? target = Junction.GetTarget(JunctionPath);
        bool wasElsewhere = target != null && !SamePath(target, _realDesktop);
        if (wasElsewhere)
        {
            var namesBefore = DesktopIconManager.ReadNames();
            int lockGen = LockDrawingFor(_realDesktop, arrangeIfNew: false);
            Junction.Point(JunctionPath, _realDesktop);
            RefreshJunction();
            _pendingLayoutRestore = StartLayoutRestore(_realDesktop, namesBefore, arrangeIfNew: false, lockGen);
        }
        else
        {
            Junction.Point(JunctionPath, _realDesktop);
        }

        // Point the Desktop at the junction (once; it stays there)
        if (!SamePath(GetKnownFolderPath(), JunctionPath))
        {
            if (!TrySetKnownFolderPath(JunctionPath))
                throw new IOException("Windows refused to move the Desktop to DesktopLS's folder.");
            RecordSetupPending();
        }
        TryDelete(LegacyStateFile);
    }

    /// <summary>The user's real desktop folder.</summary>
    public string OriginalDesktop => _realDesktop;

    public string CurrentPath => _shownPath;

    /// <summary>
    /// True until Explorer has restarted since the Desktop was moved to the
    /// junction: the desktop can't switch folders before that.
    /// </summary>
    public bool NeedsExplorerRestart
    {
        get
        {
            try
            {
                if (!File.Exists(SetupPendingFile)) return false;
                var parts = File.ReadAllText(SetupPendingFile).Split('|');
                if (parts.Length == 2 && int.TryParse(parts[0], out int pid) && long.TryParse(parts[1], out long started))
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        if (p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                            p.StartTime.ToUniversalTime().Ticks == started)
                            return true;
                    }
                    catch (ArgumentException) { } // that Explorer is gone
                }
                TryDelete(SetupPendingFile);
                return false;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Restarts Explorer so it shows the desktop through the junction. Only
    /// needed once, right after setup. Open File Explorer windows close.
    /// </summary>
    public void RestartExplorerForSetup()
    {
        int session = Process.GetCurrentProcess().SessionId;
        var old = Process.GetProcessesByName("explorer").Where(p => p.SessionId == session).ToList();
        var oldIds = old.Select(p => p.Id).ToHashSet();
        foreach (var p in old)
        {
            // Force-kill: a normal exit would save Explorer's view state,
            // which is the same either way, but is slower
            try { p.Kill(); p.WaitForExit(3000); }
            catch (Exception ex) { Log.Error($"Stopping explorer.exe ({p.Id}) failed", ex); }
            finally { p.Dispose(); }
        }

        // Windows normally restarts the shell by itself; start it only if not,
        // or a second explorer.exe would open a File Explorer window
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000 && !NewExplorerRunning(session, oldIds)) Thread.Sleep(100);
        if (!NewExplorerRunning(session, oldIds))
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
        }
        TryDelete(SetupPendingFile);
    }

    /// <summary>
    /// Shows the given folder's contents on the desktop.
    /// </summary>
    public void SetDesktopPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));
        if (!Directory.Exists(path))
            throw new ArgumentException($"Directory does not exist: {path}", nameof(path));

        // One spelling per folder ("C:\x/y" -> "C:\x\y"); switching to the
        // folder already shown has nothing to do
        path = IconLayoutService.NormalizePath(path);
        if (SamePath(path, _shownPath)) return;

        if (IsNetworkPath(path))
            throw new ArgumentException("Network folders can't be shown on the desktop (Windows junctions only reach local drives).");
        if (SamePath(path, JunctionPath) || IsInside(path, AppDataDir))
            throw new ArgumentException("That's DesktopLS's own folder.");
        if (NeedsExplorerRestart)
            throw new InvalidOperationException("Explorer has to restart once before DesktopLS can switch folders: reopen DesktopLS and choose Yes, or sign out and back in.");

        // Stop any layout restore still running for the previous folder, then
        // save the current layout (it must be captured before the switch)
        CancelPendingLayoutRestore();
        _iconLayouts.SaveLayout(_shownPath);
        var namesBefore = DesktopIconManager.ReadNames();

        // Restore saved layout (or auto-arrange grid for first visit) once
        // Explorer shows the new folder; nothing is drawn until then
        bool arrangeIfNew = !SamePath(path, _realDesktop);
        int lockGen = LockDrawingFor(path, arrangeIfNew);
        try
        {
            Junction.Point(JunctionPath, path);
        }
        catch
        {
            UnlockDrawing(lockGen);
            throw;
        }
        _shownPath = path;
        RefreshJunction();
        _pendingLayoutRestore = StartLayoutRestore(path, namesBefore, arrangeIfNew, lockGen);
    }

    /// <summary>Shows the real desktop again. Safe to call multiple times.</summary>
    public void Restore()
    {
        if (SamePath(_shownPath, _realDesktop)) return;

        CancelPendingLayoutRestore();
        _iconLayouts.SaveLayout(_shownPath); // logs its own failures
        var namesBefore = DesktopIconManager.ReadNames();

        int lockGen = LockDrawingFor(_realDesktop, arrangeIfNew: false);
        try
        {
            try { Junction.Point(JunctionPath, _realDesktop); }
            catch (Exception ex)
            {
                // The next start retries (it always points the junction home)
                Log.Error($"Pointing the desktop back at {_realDesktop} failed", ex);
                return;
            }
            _shownPath = _realDesktop;
            RefreshJunction();

            // This runs on exit, so wait for it (bounded) instead of leaving it
            // to a task the exiting process would kill
            var restore = StartLayoutRestore(_realDesktop, namesBefore, arrangeIfNew: false, lockGen);
            try { restore.Wait(TimeSpan.FromSeconds(8)); } catch { }
        }
        finally
        {
            // The UI thread is busy here, so the restore's own unlock can't run
            UnlockDrawing(lockGen);
        }
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
    private Task StartLayoutRestore(string path, HashSet<string> namesBefore, bool arrangeIfNew, int lockGen)
    {
        var cts = new CancellationTokenSource();
        _layoutCts = cts;
        var token = cts.Token;
        bool hasLayout = _iconLayouts.HasLayout(path);
        if (!hasLayout && !arrangeIfNew)
        {
            UnlockDrawing(lockGen);
            return Task.CompletedTask;
        }

        // Not passing token to Task.Run: the finally below must always run,
        // or the desktop would stay locked
        return Task.Run(async () =>
        {
            try
            {
                await _layoutGate.WaitAsync(token);
                try
                {
                    if (!await WaitForFolderShownAsync(namesBefore, token))
                    {
                        // Never touch icons unless Explorer really shows this folder
                        Log.Error($"Explorer didn't show {path} in time; icon positions left untouched");
                        return;
                    }
                    token.ThrowIfCancellationRequested();
                    if (hasLayout)
                        _iconLayouts.RestoreLayout(path);
                    else
                        DesktopIconManager.ArrangeInGrid();
                }
                finally { _layoutGate.Release(); }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (_ui != null) _ui.Post(_ => UnlockDrawing(lockGen), null);
            }
        });
    }

    /// <summary>
    /// Locks drawing of the desktop icons if a restore will follow the switch
    /// to path. Returns the lock's generation (0 if nothing was locked).
    /// </summary>
    private int LockDrawingFor(string path, bool arrangeIfNew)
    {
        if (!arrangeIfNew && !_iconLayouts.HasLayout(path)) return 0;
        if (!_drawingLocked) _drawingLocked = DesktopIconManager.LockDrawing();
        return _drawingLocked ? ++_lockGeneration : 0;
    }

    /// <summary>Unlocks drawing, unless a newer switch has locked it since.</summary>
    private void UnlockDrawing(int lockGen)
    {
        if (lockGen == 0 || lockGen != _lockGeneration || !_drawingLocked) return;
        DesktopIconManager.UnlockDrawing();
        _drawingLocked = false;
    }

    private void CancelPendingLayoutRestore()
    {
        _layoutCts?.Cancel();
        // Let a restore that's mid-write finish before reading positions again
        try { _pendingLayoutRestore?.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    /// <summary>
    /// Waits until Explorer's icons are no longer the previous folder's and
    /// the icon count has stopped changing. Checking only the count could
    /// match the old folder's icons before Explorer refreshed.
    /// </summary>
    /// <returns>
    /// True once the new folder is shown and settled. False if Explorer still
    /// shows the previous folder's icons when the timeout runs out; callers
    /// must then leave the icons alone, or they would rearrange the wrong
    /// folder (e.g. the user's real desktop).
    /// </returns>
    private static async Task<bool> WaitForFolderShownAsync(HashSet<string> namesBefore, CancellationToken token, int timeoutMs = 6000)
    {
        var sw = Stopwatch.StartNew();
        bool changed = namesBefore.Count == 0;
        int stable = 0, last = -1;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            if (!changed)
            {
                var now = DesktopIconManager.ReadNames();
                changed = !now.SetEquals(namesBefore);
            }
            else
            {
                int count = DesktopIconManager.GetIconCount();
                if (count == last) { if (++stable >= 3) return true; }
                else { stable = 0; last = count; }
            }
            await Task.Delay(40, token);
        }
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Tells Explorer the junction's contents changed, so the desktop re-reads it.</summary>
    private static void RefreshJunction()
        => SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, JunctionPath, IntPtr.Zero);

    /// <summary>
    /// The user's real desktop: the Desktop location itself before setup,
    /// afterwards what setup recorded. Older versions left the real one in
    /// their crash-recovery file when they ended while redirected.
    /// </summary>
    private static string FindRealDesktop()
    {
        string? legacy = ReadFile(LegacyStateFile);
        if (legacy != null && Directory.Exists(legacy)) return legacy;

        string known = GetKnownFolderPath();
        if (!SamePath(known, JunctionPath)) return known;

        string? recorded = ReadFile(RealDesktopFile);
        if (recorded != null && Directory.Exists(recorded)) return recorded;

        // Recorded file lost: the profile's standard Desktop folder
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
    }

    private static void RecordSetupPending()
    {
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            var shell = Process.GetProcessesByName("explorer").Where(p => p.SessionId == session)
                .OrderBy(p => p.StartTime).FirstOrDefault();
            if (shell != null)
                AtomicFile.WriteAllText(SetupPendingFile, $"{shell.Id}|{shell.StartTime.ToUniversalTime().Ticks}");
        }
        catch (Exception ex) { Log.Error("Recording that Explorer needs a restart failed", ex); }
    }

    private static bool NewExplorerRunning(int session, HashSet<int> oldIds)
    {
        var procs = Process.GetProcessesByName("explorer");
        try { return procs.Any(p => p.SessionId == session && !oldIds.Contains(p.Id)); }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    internal static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            string? root = Path.GetPathRoot(path);
            return root != null && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return false; }
    }

    private static bool IsInside(string path, string folder)
        => IconLayoutService.NormalizePath(path).StartsWith(IconLayoutService.NormalizePath(folder) + "\\",
            StringComparison.OrdinalIgnoreCase) || SamePath(path, folder);

    private static bool SamePath(string a, string b)
        => string.Equals(IconLayoutService.NormalizePath(a), IconLayoutService.NormalizePath(b),
            StringComparison.OrdinalIgnoreCase);

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
        return SHSetKnownFolderPath(ref guid, 0, IntPtr.Zero, path) == 0;
    }

    private static string? ReadFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            string text = File.ReadAllText(file).Trim();
            return text.Length > 0 ? text : null;
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
