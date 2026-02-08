using System.IO;
using System.Text.Json;

namespace DesktopLS.Services;

/// <summary>
/// Stack-based forward/back navigation history with bookmark persistence.
/// </summary>
public sealed class NavigationService
{
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private readonly List<string> _bookmarks = new();
    private string _currentPath = string.Empty;
    private readonly string _bookmarksFile;

    public NavigationService()
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopLS");
        Directory.CreateDirectory(appData);
        _bookmarksFile = Path.Combine(appData, "bookmarks.json");
        LoadBookmarks();
    }

    public string CurrentPath => _currentPath;
    public bool CanGoBack => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;
    public IReadOnlyList<string> Bookmarks => _bookmarks;

    public event Action? NavigationChanged;

    public void NavigateTo(string path)
    {
        if (string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.IsNullOrEmpty(_currentPath))
            _backStack.Push(_currentPath);

        _currentPath = path;
        _forwardStack.Clear();
        NavigationChanged?.Invoke();
    }

    public string? GoBack()
    {
        if (!CanGoBack) return null;
        _forwardStack.Push(_currentPath);
        _currentPath = _backStack.Pop();
        NavigationChanged?.Invoke();
        return _currentPath;
    }

    public string? GoForward()
    {
        if (!CanGoForward) return null;
        _backStack.Push(_currentPath);
        _currentPath = _forwardStack.Pop();
        NavigationChanged?.Invoke();
        return _currentPath;
    }

    public string? GoUp()
    {
        var parent = Directory.GetParent(_currentPath);
        if (parent == null) return null;
        NavigateTo(parent.FullName);
        return _currentPath;
    }

    public void AddBookmark(string path)
    {
        if (!_bookmarks.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            _bookmarks.Add(path);
            SaveBookmarks();
        }
    }

    public void RemoveBookmark(string path)
    {
        _bookmarks.RemoveAll(b => string.Equals(b, path, StringComparison.OrdinalIgnoreCase));
        SaveBookmarks();
    }

    private void LoadBookmarks()
    {
        try
        {
            if (File.Exists(_bookmarksFile))
            {
                string json = File.ReadAllText(_bookmarksFile);
                var loaded = JsonSerializer.Deserialize<List<string>>(json);
                if (loaded != null)
                    _bookmarks.AddRange(loaded);
            }
        }
        catch
        {
            // Silently ignore corrupt bookmark files
        }
    }

    private void SaveBookmarks()
    {
        try
        {
            string json = JsonSerializer.Serialize(_bookmarks, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_bookmarksFile, json);
        }
        catch
        {
            // Best-effort persistence
        }
    }
}
