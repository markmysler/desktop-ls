using System.Windows;
using System.Windows.Input;
using DesktopLS.Native;
using DesktopLS.Services;
using DesktopLS.ViewModels;

namespace DesktopLS;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DesktopFolderService _desktopFolder;
    private readonly SettingsService _settings;
    private readonly WindowMonitorService _windowMonitor;
    private readonly BarPositioner _positioner;

    public MainWindow()
    {
        InitializeComponent();

        var navigation = new NavigationService();
        var autocomplete = new AutocompleteService();
        _desktopFolder = new DesktopFolderService();
        ((App)Application.Current).DesktopFolder = _desktopFolder;
        if (_desktopFolder.NeedsExplorerRestart &&
            MessageBox.Show(
                "To switch folders on the desktop, DesktopLS has to restart Windows Explorer once.\n\n" +
                "The taskbar disappears for a few seconds and open File Explorer windows close. " +
                "This is only needed the first time.\n\nRestart Explorer now? (Choose No to wait until you next sign in.)",
                "DesktopLS", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
        {
            _desktopFolder.RestartExplorerForSetup();
        }
        _settings = new SettingsService();
        _settings.Load();

        _windowMonitor = new WindowMonitorService(this);
        _windowMonitor.Enabled = _settings.HideOnMaximized && !_settings.ReserveSpace;

        _viewModel = new MainViewModel(navigation, autocomplete, _desktopFolder, _settings);
        DataContext = _viewModel;

        // Settings button click
        SettingsButton.Click += (s, e) =>
        {
            SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
            e.Handled = true;
        };

        _positioner = new BarPositioner(this, _settings);

        // Window monitor. With space reserved, maximized windows stop at the
        // bar, so there's nothing to hide from.
        _windowMonitor.MaximizedWindowStateChanged += isMaximized =>
        {
            if (_settings.HideOnMaximized && !_settings.ReserveSpace)
                Dispatcher.Invoke(() => Visibility = isMaximized ? Visibility.Hidden : Visibility.Visible);
        };

        // Settings changed subscription
        _settings.SettingsChanged += () =>
        {
            _windowMonitor.Enabled = _settings.HideOnMaximized && !_settings.ReserveSpace;
            if (!_windowMonitor.Enabled)
                Visibility = Visibility.Visible;
            _positioner.Apply();
        };

        Loaded += OnLoaded;
        Closing += OnClosing;

        InputBindings.Add(new KeyBinding(_viewModel.GoBackCommand, Key.Left, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(_viewModel.GoForwardCommand, Key.Right, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(_viewModel.GoUpCommand, Key.Up, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(_viewModel.RefreshCommand, Key.F5, ModifierKeys.None));

        // Drag anywhere; on release the bar snaps to the nearer edge of the
        // monitor it's on and remembers the spot
        MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch { }
            _positioner.OnDragged();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _positioner.Attach();

        string startPath = Application.Current.Properties["StartPath"] as string
            ?? _desktopFolder.OriginalDesktop;
        _viewModel.Initialize(startPath);

        _windowMonitor.Start();

        PathBox.Focus();
        PathBox.SelectAll();
    }

    private void UndoSetup_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        if (MessageBox.Show(
                "This points your Desktop back at its real folder, turns off Run on startup, restarts Windows Explorer once " +
                "(the taskbar disappears for a few seconds and open File Explorer windows close), and closes DesktopLS.\n\n" +
                "Your files and saved icon layouts are kept. Opening DesktopLS again sets it up again.\n\nContinue?",
                "DesktopLS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            _desktopFolder.Restore();             // show the real desktop first
            _viewModel.StartupEnabled = false;
            DesktopFolderService.UndoSetup(restartExplorer: true);
        }
        catch (Exception ex)
        {
            Log.Error("Undoing the DesktopLS setup failed", ex);
            MessageBox.Show($"Couldn't put the Desktop back: {ex.Message}", "DesktopLS",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _windowMonitor.Stop();
        _positioner.Dispose(); // gives reserved space back
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
