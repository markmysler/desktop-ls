using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DesktopLS.Native;

namespace DesktopLS.Services;

/// <summary>
/// Persists desktop icon positions per folder + monitor configuration.
/// Key format: "folderPath|monitorSignature"
/// </summary>
public class IconLayoutService
{
    private static readonly string LayoutsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopLS", "layouts.json");

    // Outer key: "path|monitorSig", inner key: filename, value: [x, y]
    private Dictionary<string, Dictionary<string, int[]>> _store;

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

            _store[key] = dict;
            Persist();
        }
        catch { /* best effort */ }
    }

    public void RestoreLayout(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return;
        try
        {
            string key = MakeKey(folderPath);
            if (!_store.TryGetValue(key, out var dict) || dict.Count == 0) return;

            var positions = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, xy) in dict)
                if (xy.Length >= 2)
                    positions[name] = (xy[0], xy[1]);

            DesktopIconManager.WritePositions(positions);
        }
        catch { /* best effort */ }
    }

    public bool HasLayout(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return false;
        string key = MakeKey(folderPath);
        return _store.TryGetValue(key, out var dict) && dict.Count > 0;
    }

    public int GetSavedCount(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return 0;
        string key = MakeKey(folderPath);
        return _store.TryGetValue(key, out var dict) ? dict.Count : 0;
    }

    private static string MakeKey(string folderPath)
        => $"{folderPath}|{DesktopIconManager.GetMonitorSignature()}";

    private void Persist()
    {
        try
        {
            string json = JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(LayoutsFile, json);
        }
        catch { }
    }

    private static Dictionary<string, Dictionary<string, int[]>> Load()
    {
        try
        {
            if (!File.Exists(LayoutsFile)) return new();
            string json = File.ReadAllText(LayoutsFile);
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int[]>>>(json)
                   ?? new();
        }
        catch { return new(); }
    }
}
