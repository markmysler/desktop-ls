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
}
