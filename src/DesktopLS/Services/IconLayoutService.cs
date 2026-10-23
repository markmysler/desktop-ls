using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DesktopLS.Native;

namespace DesktopLS.Services;

/// <summary>
/// Persists desktop icon positions per folder + monitor configuration.
/// Key format: "folderPath|monitorSignature"
/// Thread-safe: saves happen on the UI thread, restores on a background one.
/// </summary>
public class IconLayoutService
{
    private static readonly string LayoutsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopLS", "layouts.json");

    // Outer key: "path|monitorSig", inner key: filename, value: [x, y]
    private Dictionary<string, Dictionary<string, int[]>> _store;
    private readonly object _lock = new();

    public IconLayoutService()
    {
        _store = Load();
    }

    public void SaveLayout(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return;
        try
        {
            var positions = DesktopIconManager.ReadPositions();
            if (positions.Count == 0) return;

            string key = MakeKey(folderPath);
            var dict = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, (x, y)) in positions)
                dict[name] = new[] { x, y };

            lock (_lock)
            {
                _store[key] = dict;
                Persist();
            }
        }
        catch { /* best effort */ }
    }

    public void RestoreLayout(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return;
        try
        {
            string key = MakeKey(folderPath);
            var positions = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                if (!_store.TryGetValue(key, out var dict) || dict.Count == 0) return;
                foreach (var (name, xy) in dict)
                    if (xy.Length >= 2)
                        positions[name] = (xy[0], xy[1]);
            }

            DesktopIconManager.WritePositions(positions);
        }
        catch { /* best effort */ }
    }

    public bool HasLayout(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return false;
        string key = MakeKey(folderPath);
        lock (_lock) return _store.TryGetValue(key, out var dict) && dict.Count > 0;
    }

    public int GetSavedCount(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return 0;
        string key = MakeKey(folderPath);
        lock (_lock) return _store.TryGetValue(key, out var dict) ? dict.Count : 0;
    }

    private static string MakeKey(string folderPath)
        => $"{NormalizePath(folderPath)}|{DesktopIconManager.GetMonitorSignature()}";

    /// <summary>
    /// One spelling per folder, so "C:\x", "C:\x\" and "C:\x\..\x" share a
    /// layout (case is handled by the case-insensitive store).
    /// </summary>
    internal static string NormalizePath(string path)
    {
        try { path = Path.GetFullPath(path); } catch { return path; }
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path.Length > root.Length ? path.TrimEnd('\\', '/') : path;
    }

    private void Persist()
    {
        try
        {
            string json = JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(LayoutsFile, json);
        }
        catch { }
    }

    private static Dictionary<string, Dictionary<string, int[]>> Load()
    {
        try
        {
            if (!File.Exists(LayoutsFile)) return new(StringComparer.OrdinalIgnoreCase);
            string json = File.ReadAllText(LayoutsFile);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int[]>>>(json) ?? new();
            return NormalizeStore(loaded);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// Older versions stored keys with the folder path as typed. Normalise
    /// them so existing layouts keep matching, and make lookups case-insensitive.
    /// </summary>
    internal static Dictionary<string, Dictionary<string, int[]>> NormalizeStore(
        Dictionary<string, Dictionary<string, int[]>> loaded)
    {
        var store = new Dictionary<string, Dictionary<string, int[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, layout) in loaded)
        {
            int bar = key.IndexOf('|');
            string normalized = bar < 0 ? key : NormalizePath(key[..bar]) + key[bar..];
            store[normalized] = new Dictionary<string, int[]>(layout, StringComparer.OrdinalIgnoreCase);
        }
        return store;
    }
}
