using System.Windows;
using System.Windows.Input;
using DesktopLS.Services;
using DesktopLS.ViewModels;

namespace DesktopLS;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DesktopFolderService _desktopFolder;
    private readonly SettingsService _settings;
    private readonly WindowMonitorService _windowMonitor;

    public MainWindow()
    {
        InitializeComponent();

        var navigation = new NavigationService();
        var autocomplete = new AutocompleteService();
        _desktopFolder = new DesktopFolderService();
        _settings = new SettingsService();
        _settings.Load();

        _windowMonitor = new WindowMonitorService(this);
        _windowMonitor.Enabled = _settings.HideOnMaximized;

        _viewModel = new MainViewModel(navigation, autocomplete, _desktopFolder, _settings);
        DataContext = _viewModel;

        // Settings button click
        SettingsButton.Click += (s, e) =>
        {
            SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
            e.Handled = true;
        };

        // Window monitor
        _windowMonitor.MaximizedWindowStateChanged += isMaximized =>
        {
            if (_settings.HideOnMaximized)
                Dispatcher.Invoke(() => Visibility = isMaximized ? Visibility.Hidden : Visibility.Visible);
        };

        // Settings changed subscription
        _settings.SettingsChanged += () =>
        {
            _windowMonitor.Enabled = _settings.HideOnMaximized;
            if (!_settings.HideOnMaximized)
                Visibility = Visibility.Visible;
        };

        Loaded += OnLoaded;
        Closing += OnClosing;

        InputBindings.Add(new KeyBinding(_viewModel.GoBackCommand, Key.Left, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(_viewModel.GoForwardCommand, Key.Right, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(_viewModel.GoUpCommand, Key.Up, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(_viewModel.RefreshCommand, Key.F5, ModifierKeys.None));

        MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch { }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;

        string startPath = Application.Current.Properties["StartPath"] as string
            ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        _viewModel.Initialize(startPath);

        _windowMonitor.Start();

        PathBox.Focus();
        PathBox.SelectAll();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _windowMonitor.Stop();
        _settings.Save();
        _desktopFolder.Dispose();
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                // If autocomplete is open and a suggestion is selected, navigate to it
                if (_viewModel.IsAutocompleteOpen && SuggestionsList.SelectedIndex >= 0 &&
                    SuggestionsList.SelectedItem is string selectedPath)
                {
                    _viewModel.NavigateToSuggestion(selectedPath);
                }
                else
                {
                    // Otherwise navigate to whatever is in the path box
                    _viewModel.NavigateCommand.Execute(null);
                }
                PathBox.CaretIndex = PathBox.Text.Length;
                e.Handled = true;
                break;

            case Key.Tab when _viewModel.IsAutocompleteOpen && _viewModel.Suggestions.Count > 0:
                // Cycle through suggestions (just update selection, don't apply to PathText yet)
                int currentIndex = SuggestionsList.SelectedIndex;
                int nextIndex = (currentIndex + 1) % _viewModel.Suggestions.Count;
                SuggestionsList.SelectedIndex = nextIndex;
                SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
                e.Handled = true;
                break;

            case Key.Escape:
                _viewModel.IsAutocompleteOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void SuggestionsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Single click: fill path box, close dropdown, cursor at end
        if (SuggestionsList.SelectedItem is string path)
        {
            _viewModel.SetPathFromSuggestion(path);
            PathBox.Focus();
            PathBox.CaretIndex = PathBox.Text.Length;
        }
    }

    private void SuggestionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionsList.SelectedItem is string path)
        {
            _viewModel.NavigateToSuggestion(path);
            PathBox.Focus();
            PathBox.CaretIndex = PathBox.Text.Length;
        }
    }

    private void SuggestionsList_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when SuggestionsList.SelectedItem is string path:
                _viewModel.NavigateToSuggestion(path);
                PathBox.Focus();
                e.Handled = true;
                break;

            case Key.Tab when SuggestionsList.SelectedItem is string tabPath:
                _viewModel.SetPathFromSuggestion(tabPath);
                PathBox.Focus();
                PathBox.CaretIndex = PathBox.Text.Length;
                e.Handled = true;
                break;

            case Key.Up:
                if (SuggestionsList.SelectedIndex > 0)
                {
                    SuggestionsList.SelectedIndex--;
                }
                else
                {
                    _viewModel.IsAutocompleteOpen = false;
                    PathBox.Focus();
                    PathBox.CaretIndex = PathBox.Text.Length;
                }
                e.Handled = true;
                break;

            case Key.Down:
                if (SuggestionsList.SelectedIndex < SuggestionsList.Items.Count - 1)
                {
                    SuggestionsList.SelectedIndex++;
                }
                e.Handled = true;
                break;

            case Key.Escape:
                _viewModel.IsAutocompleteOpen = false;
                PathBox.Focus();
                e.Handled = true;
                break;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
