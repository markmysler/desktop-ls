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
    private readonly IDesktopFolderService _desktopFolder;
    private readonly ISettingsService _settings;
    private string _pathText = string.Empty;
    private bool _isAutocompleteOpen;
    private string _statusMessage = string.Empty;

    public MainViewModel(
        NavigationService navigation,
        AutocompleteService autocomplete,
        IDesktopFolderService desktopFolder,
        ISettingsService settings)
    {
        _navigation = navigation;
        _autocomplete = autocomplete;
        _desktopFolder = desktopFolder;
        _settings = settings;

        Suggestions = new ObservableCollection<string>();

        NavigateCommand = new RelayCommand(OnNavigate);
        GoBackCommand = new RelayCommand(OnGoBack, () => _navigation.CanGoBack);
        GoForwardCommand = new RelayCommand(OnGoForward, () => _navigation.CanGoForward);
        GoUpCommand = new RelayCommand(OnGoUp);
        RefreshCommand = new RelayCommand(OnRefresh);

        _navigation.NavigationChanged += () =>
        {
            _pathText = _navigation.CurrentPath;
            OnPropertyChanged(nameof(PathText));
            // Back/Forward availability changed; don't wait for the next
            // mouse/keyboard event to re-enable the buttons
            ((RelayCommand)GoBackCommand).RaiseCanExecuteChanged();
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

    public bool StartupEnabled
    {
        get => _settings.StartupEnabled;
        set
        {
            if (_settings.StartupEnabled != value)
            {
                _settings.StartupEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HideOnMaximized
    {
        get => _settings.HideOnMaximized;
        set
        {
            if (_settings.HideOnMaximized != value)
            {
                _settings.HideOnMaximized = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<string> Suggestions { get; }

    public ICommand NavigateCommand { get; }
    public ICommand GoBackCommand { get; }
    public ICommand GoForwardCommand { get; }
    public ICommand GoUpCommand { get; }
    public ICommand RefreshCommand { get; }

    /// <summary>Error or notice shown in the bar; empty when everything is fine.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public void Initialize(string startPath)
    {
        if (!System.IO.Directory.Exists(startPath))
        {
            string requested = startPath;
            startPath = _desktopFolder.OriginalDesktop;
            if (!ShowFolder(startPath)) return;
            _navigation.NavigateTo(startPath);
            StatusMessage = $"Folder not found: {requested}";
            return;
        }

        if (ShowFolder(startPath))
            _navigation.NavigateTo(startPath);
        _pathText = _navigation.CurrentPath;
        OnPropertyChanged(nameof(PathText));
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

    /// <summary>
    /// Shows a folder on the desktop. Every navigation goes through here so a
    /// failure (folder deleted, access denied, ...) is reported in the bar
    /// instead of crashing the app.
    /// </summary>
    private bool ShowFolder(string path)
    {
        if (!System.IO.Directory.Exists(path))
        {
            StatusMessage = $"Folder not found: {path}";
            return false;
        }

        try
        {
            _desktopFolder.SetDesktopPath(path);
            StatusMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Showing {path} failed", ex);
            StatusMessage = ex.Message;
            return false;
        }
    }

    private void OnNavigate()
    {
        string path = _pathText.Trim();
        if (string.IsNullOrEmpty(path)) return;

        if (ShowFolder(path))
            _navigation.NavigateTo(path);
        IsAutocompleteOpen = false;
    }

    private void OnGoBack()
    {
        string? path = _navigation.GoBack();
        if (path != null) ShowFolder(path);
    }

    private void OnGoForward()
    {
        string? path = _navigation.GoForward();
        if (path != null) ShowFolder(path);
    }

    private void OnGoUp()
    {
        string? path = _navigation.GoUp();
        if (path != null) ShowFolder(path);
    }

    private void OnRefresh()
    {
        string path = _navigation.CurrentPath;
        if (!string.IsNullOrEmpty(path))
            ShowFolder(path);
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
