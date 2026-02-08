using System.IO;

namespace DesktopLS.Services;

/// <summary>
/// Path autocomplete with debounced directory enumeration.
/// </summary>
public sealed class AutocompleteService
{
    private CancellationTokenSource? _debounceCts;

    /// <summary>
    /// Gets autocomplete suggestions for a partial path (debounced).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSuggestionsAsync(string partialPath, CancellationToken cancellationToken = default)
    {
        // Cancel previous debounce
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _debounceCts.Token;

        try
        {
            // 50ms debounce
            await Task.Delay(50, token);
            return await Task.Run(() => GetSuggestionsSync(partialPath), token);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> GetSuggestionsSync(string partialPath)
    {
        if (string.IsNullOrWhiteSpace(partialPath))
            return GetDriveRoots();

        // If the path ends with a separator, enumerate children
        if (partialPath.EndsWith(Path.DirectorySeparatorChar) || partialPath.EndsWith(Path.AltDirectorySeparatorChar))
        {
            if (Directory.Exists(partialPath))
                return EnumerateSubdirectories(partialPath);
            return Array.Empty<string>();
        }

        // Otherwise, find the parent dir and filter by prefix
        string? parentDir = Path.GetDirectoryName(partialPath);
        if (string.IsNullOrEmpty(parentDir))
            return GetDriveRoots().Where(d => d.StartsWith(partialPath, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!Directory.Exists(parentDir))
            return Array.Empty<string>();

        string prefix = Path.GetFileName(partialPath);
        return EnumerateSubdirectories(parentDir)
            .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    private static IReadOnlyList<string> EnumerateSubdirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IReadOnlyList<string> GetDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
