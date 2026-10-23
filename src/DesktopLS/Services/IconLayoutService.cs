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
            // Key icons by real file name (unique) via the shell view; fall back
            // to list-view display names if the shell view isn't available
            var dict = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            var shell = ShellDesktopView.ReadPositions();
            if (shell != null)
            {
                foreach (var (parsingName, (x, y, _)) in shell)
                    dict[parsingName] = new[] { x, y };
            }
            else
            {
                foreach (var (name, (x, y)) in DesktopIconManager.ReadPositions())
                    dict[name] = new[] { x, y };
            }

            // Items that can't be told apart (same name twice, e.g. the user's
            // and the Public Desktop's hidden desktop.ini) are saved as a group
            // of spots, so a restore can put each back on one of them
            var items = DesktopIconManager.ReadItems();
            var ids = Identify(items, shell);
            foreach (var group in Enumerable.Range(0, items.Count).Where(i => ids[i] == null)
                         .GroupBy(i => items[i].Name, StringComparer.OrdinalIgnoreCase))
                dict[GroupKey(group.Key)] = group.SelectMany(i => new[] { items[i].X, items[i].Y }).ToArray();
            if (dict.Count == 0) return;

            string key = MakeKey(folderPath);

            lock (_lock)
            {
                _store[key] = dict;
                Persist();
            }
        }
        catch (Exception ex) { Log.Error($"Saving icon layout for {folderPath} failed", ex); }
    }

    public void RestoreLayout(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return;
        try
        {
            string key = MakeKey(folderPath);
            var positions = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, List<(int X, int Y)>>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                if (!_store.TryGetValue(key, out var dict) || dict.Count == 0) return;
                foreach (var (name, xy) in dict)
                {
                    if (name.StartsWith(GroupPrefix, StringComparison.Ordinal))
                        groups[name[GroupPrefix.Length..]] = Enumerable.Range(0, xy.Length / 2).Select(k => (xy[2 * k], xy[2 * k + 1])).ToList();
                    else if (xy.Length >= 2)
                        positions[name] = (xy[0], xy[1]);
                }
            }

            // With align-to-grid on, Explorer won't put an icon on an occupied
            // slot: it pushes it (and everything after it in that column)
            // down. So icons that must move first go to free slots no icon
            // wants ("parking"), then to their targets, which are empty by
            // then. Anything else sitting on a target is parked too. A few
            // rounds catch icons Explorer still bumped.
            DesktopIconManager.ClearFocusAndSelection();
            for (int round = 0; round < 3; round++)
            {
                var items = DesktopIconManager.ReadItems();
                var ids = AssignGroups(items, Identify(items, ShellDesktopView.ReadPositions()), groups, positions);
                var plan = PlanRestore(items, ids, positions,
                    DesktopIconManager.GridSlots(items.Select(i => (i.X, i.Y)).ToList()));
                if (plan.Targets.Count == 0) return;

                if (plan.Parking.Count > 0)
                {
                    var park = plan.Parking.ToDictionary(kv => SpotKey(items[kv.Key].Name, items[kv.Key].X, items[kv.Key].Y), kv => kv.Value);
                    DesktopIconManager.WritePositions((display, cur) =>
                        park.TryGetValue(SpotKey(display, cur.X, cur.Y), out var slot) ? slot : null);
                    // (no pause needed: moves are sent synchronously and done on return)
                }

                // Find each icon again where it actually landed, then place it
                var parked = DesktopIconManager.ReadItems();
                var parkedIds = AssignGroups(parked, Identify(parked, ShellDesktopView.ReadPositions()), groups, positions);
                var place = PlanRestore(parked, parkedIds, positions, Array.Empty<(int, int)>()).Targets
                    .ToDictionary(kv => SpotKey(parked[kv.Key].Name, parked[kv.Key].X, parked[kv.Key].Y), kv => kv.Value);
                DesktopIconManager.WritePositions((display, cur) =>
                    place.TryGetValue(SpotKey(display, cur.X, cur.Y), out var target) ? target : null);
            }
        }
        catch (Exception ex) { Log.Error($"Restoring icon layout for {folderPath} failed", ex); }
    }

    /// <summary>
    /// The saved-layout key of each list-view item: its file name, found via
    /// the shell view (display name + current position identify it, even when
    /// two items share a display name). Layouts saved by older versions are
    /// keyed by display name, so a display name shown only once also counts.
    /// Null for items that can't be told apart (e.g. the user's and the
    /// Public Desktop's hidden desktop.ini).
    /// </summary>
    internal static string?[] Identify(IReadOnlyList<(string Name, int X, int Y)> items,
        Dictionary<string, (int X, int Y, string DisplayName)>? shell)
    {
        var bySpot = new Dictionary<string, string>(StringComparer.Ordinal);
        if (shell != null)
            foreach (var (fileName, (x, y, display)) in shell)
                bySpot[SpotKey(display, x, y)] = fileName;

        var nameCount = items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return items.Select(i =>
            bySpot.TryGetValue(SpotKey(i.Name, i.X, i.Y), out var file) ? file :
            nameCount[i.Name] == 1 ? i.Name : null).ToArray();
    }

    private const string GroupPrefix = "\u0001group:";

    private static string GroupKey(string displayName) => GroupPrefix + displayName;

    /// <summary>
    /// Gives each unidentified item one of its group's saved spots: items
    /// already on one keep it, the others take the remaining spots in order.
    /// Adds a synthetic key per assigned item to positions.
    /// </summary>
    internal static string?[] AssignGroups(IReadOnlyList<(string Name, int X, int Y)> items, string?[] ids,
        IReadOnlyDictionary<string, List<(int X, int Y)>> groups, Dictionary<string, (int, int)> positions)
    {
        var result = (string?[])ids.Clone();
        foreach (var group in Enumerable.Range(0, items.Count).Where(i => ids[i] == null)
                     .GroupBy(i => items[i].Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!groups.TryGetValue(group.Key, out var saved)) continue;
            var spots = saved.ToList();
            var members = group.ToList();
            foreach (int i in members.ToList())
                if (spots.Remove((items[i].X, items[i].Y))) members.Remove(i);
            foreach (int i in group)
            {
                bool keeps = !members.Contains(i);
                if (!keeps && spots.Count == 0) continue;
                var spot = keeps ? (items[i].X, items[i].Y) : spots[0];
                if (!keeps) spots.RemoveAt(0);
                string id = $"{GroupKey(group.Key)}#{spot.Item1},{spot.Item2}";
                positions[id] = spot;
                result[i] = id;
            }
        }
        return result;
    }

    /// <summary>What moving icons back to a saved layout takes.</summary>
    /// <param name="Targets">Item index → saved position, for icons not there yet.</param>
    /// <param name="Parking">Item index → free slot to move it to first. Empty if
    /// there aren't enough free slots; the icons then go straight to their targets.</param>
    internal sealed record RestorePlan(Dictionary<int, (int X, int Y)> Targets, Dictionary<int, (int X, int Y)> Parking);

    internal static RestorePlan PlanRestore(IReadOnlyList<(string Name, int X, int Y)> items, IReadOnlyList<string?> ids,
        IReadOnlyDictionary<string, (int X, int Y)> saved, IReadOnlyList<(int X, int Y)> gridSlots)
    {
        // Saved position of each identified item; two items can't share one
        var wanted = new Dictionary<int, (int X, int Y)>();
        var claimed = new HashSet<(int, int)>();
        for (int i = 0; i < items.Count; i++)
            if (ids[i] is { } id && saved.TryGetValue(id, out var pos) && claimed.Add(pos))
                wanted[i] = pos;

        var targets = wanted.Where(kv => kv.Value != (items[kv.Key].X, items[kv.Key].Y))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var parking = new Dictionary<int, (int X, int Y)>();
        if (targets.Count == 0) return new RestorePlan(targets, parking);

        // Park the movers, plus anything else in the way of a target
        var targetSpots = targets.Values.ToHashSet();
        var toPark = targets.Keys.Concat(Enumerable.Range(0, items.Count)
            .Where(i => !targets.ContainsKey(i) && targetSpots.Contains((items[i].X, items[i].Y)))).ToList();

        var occupied = items.Select(i => (i.X, i.Y)).ToHashSet();
        var free = gridSlots.Where(s => !occupied.Contains(s) && !claimed.Contains(s)).Distinct().ToList();
        if (free.Count >= toPark.Count)
            for (int k = 0; k < toPark.Count; k++)
                parking[toPark[k]] = free[k];

        return new RestorePlan(targets, parking);
    }

    private static string SpotKey(string displayName, int x, int y) => $"{displayName}\u0001{x},{y}";

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
        catch (Exception ex) { Log.Error("Writing layouts.json failed", ex); }
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
        catch (Exception ex)
        {
            Log.Error("Reading layouts.json failed; starting with no saved layouts", ex);
            return new(StringComparer.OrdinalIgnoreCase);
        }
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
