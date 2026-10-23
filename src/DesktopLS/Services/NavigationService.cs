using System.IO;

namespace DesktopLS.Services;

/// <summary>
/// Stack-based forward/back navigation history.
/// </summary>
public sealed class NavigationService
{
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private string _currentPath = string.Empty;

    public string CurrentPath => _currentPath;
    public bool CanGoBack => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;

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

    /// <summary>
    /// Goes back to the most recent entry that still exists, dropping entries
    /// for folders deleted since. Returns null (and changes nothing) if none.
    /// </summary>
    public string? GoBack(Func<string, bool>? exists = null)
        => Move(_backStack, _forwardStack, exists ?? Directory.Exists);

    /// <summary>Forward counterpart of <see cref="GoBack"/>.</summary>
    public string? GoForward(Func<string, bool>? exists = null)
        => Move(_forwardStack, _backStack, exists ?? Directory.Exists);

    private string? Move(Stack<string> from, Stack<string> to, Func<string, bool> exists)
    {
        while (from.Count > 0 && !exists(from.Peek()))
            from.Pop();
        if (from.Count == 0) return null;

        to.Push(_currentPath);
        _currentPath = from.Pop();
        NavigationChanged?.Invoke();
        return _currentPath;
    }

    public string? GoUp()
    {
        if (string.IsNullOrEmpty(_currentPath)) return null;
        var parent = Directory.GetParent(_currentPath);
        if (parent == null || !parent.Exists) return null;
        NavigateTo(parent.FullName);
        return _currentPath;
    }
}
