using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DesktopLS.Services;

namespace DesktopLS.ViewModels;

/// <summary>
/// ViewModel for the floating toolbar: path input, autocomplete, navigation.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly NavigationService _navigation;
    private readonly AutocompleteService _autocomplete;
    private readonly DesktopFolderService _desktopFolder;
    private string _pathText = string.Empty;
    private bool _isAutocompleteOpen;

    public MainViewModel(
        NavigationService navigation,
        AutocompleteService autocomplete,
        DesktopFolderService desktopFolder)
    {
        _navigation = navigation;
        _autocomplete = autocomplete;
        _desktopFolder = desktopFolder;

        Suggestions = new ObservableCollection<string>();

        NavigateCommand = new RelayCommand(OnNavigate);
        GoBackCommand = new RelayCommand(OnGoBack, () => _navigation.CanGoBack);
        GoForwardCommand = new RelayCommand(OnGoForward, () => _navigation.CanGoForward);
        GoUpCommand = new RelayCommand(OnGoUp);
        BookmarkCommand = new RelayCommand(OnBookmark);
        RefreshCommand = new RelayCommand(OnRefresh);

        _navigation.NavigationChanged += () =>
        {
            _pathText = _navigation.CurrentPath;
            OnPropertyChanged(nameof(PathText));
            OnPropertyChanged(nameof(Bookmarks));
        };
    }

    public string PathText
    {
        get => _pathText;
        set
        {
            if (SetField(ref _pathText, value))
                UpdateAutocompleteSuggestions();
        }
    }

    public bool IsAutocompleteOpen
    {
        get => _isAutocompleteOpen;
        set => SetField(ref _isAutocompleteOpen, value);
    }

    public ObservableCollection<string> Suggestions { get; }
    public IReadOnlyList<string> Bookmarks => _navigation.Bookmarks;

    public ICommand NavigateCommand { get; }
    public ICommand GoBackCommand { get; }
    public ICommand GoForwardCommand { get; }
    public ICommand GoUpCommand { get; }
    public ICommand BookmarkCommand { get; }
    public ICommand RefreshCommand { get; }

    public void Initialize(string startPath)
    {
        _navigation.NavigateTo(startPath);
        _pathText = startPath;
        OnPropertyChanged(nameof(PathText));
        _desktopFolder.SetDesktopPath(startPath);
    }

    /// <summary>Fills the path box with a suggestion without navigating.</summary>
    public void SetPathFromSuggestion(string path)
    {
        _pathText = path;
        OnPropertyChanged(nameof(PathText));
        IsAutocompleteOpen = false;
    }

    /// <summary>Fills the path box with a suggestion and navigates.</summary>
    public void NavigateToSuggestion(string path)
    {
        SetPathFromSuggestion(path);
        OnNavigate();
    }

    private void OnNavigate()
    {
        string path = _pathText.Trim();
        if (string.IsNullOrEmpty(path) || !System.IO.Directory.Exists(path))
            return;

        _navigation.NavigateTo(path);
        _desktopFolder.SetDesktopPath(path);
        IsAutocompleteOpen = false;
    }

    private void OnGoBack()
    {
        string? path = _navigation.GoBack();
        if (path != null) _desktopFolder.SetDesktopPath(path);
    }

    private void OnGoForward()
    {
        string? path = _navigation.GoForward();
        if (path != null) _desktopFolder.SetDesktopPath(path);
    }

    private void OnGoUp()
    {
        string? path = _navigation.GoUp();
        if (path != null) _desktopFolder.SetDesktopPath(path);
    }

    private void OnBookmark()
    {
        if (!string.IsNullOrEmpty(_navigation.CurrentPath))
            _navigation.AddBookmark(_navigation.CurrentPath);
        OnPropertyChanged(nameof(Bookmarks));
    }

    private void OnRefresh()
    {
        string path = _navigation.CurrentPath;
        if (!string.IsNullOrEmpty(path))
            _desktopFolder.SetDesktopPath(path);
    }

    private async void UpdateAutocompleteSuggestions()
    {
        if (string.IsNullOrWhiteSpace(_pathText))
        {
            IsAutocompleteOpen = false;
            return;
        }

        var results = await _autocomplete.GetSuggestionsAsync(_pathText);
        Suggestions.Clear();
        foreach (var s in results)
            Suggestions.Add(s);

        IsAutocompleteOpen = Suggestions.Count > 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
