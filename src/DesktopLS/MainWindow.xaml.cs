using System.Windows;
using System.Windows.Input;
using DesktopLS.Services;
using DesktopLS.ViewModels;

namespace DesktopLS;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DesktopFolderService _desktopFolder;

    public MainWindow()
    {
        InitializeComponent();

        var navigation = new NavigationService();
        var autocomplete = new AutocompleteService();
        _desktopFolder = new DesktopFolderService();

        _viewModel = new MainViewModel(navigation, autocomplete, _desktopFolder);
        DataContext = _viewModel;

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

        PathBox.Focus();
        PathBox.SelectAll();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _desktopFolder.Dispose();
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _viewModel.NavigateCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Down when _viewModel.IsAutocompleteOpen:
                SuggestionsList.Focus();
                if (SuggestionsList.Items.Count > 0)
                    SuggestionsList.SelectedIndex = 0;
                e.Handled = true;
                break;

            case Key.Escape:
                _viewModel.IsAutocompleteOpen = false;
                e.Handled = true;
                break;

            case Key.Tab when _viewModel.IsAutocompleteOpen && _viewModel.Suggestions.Count > 0:
                _viewModel.SetPathFromSuggestion(_viewModel.Suggestions[0]);
                PathBox.CaretIndex = PathBox.Text.Length;
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
        }
    }

    private void SuggestionsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SuggestionsList.SelectedItem is string path)
        {
            _viewModel.NavigateToSuggestion(path);
            PathBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.IsAutocompleteOpen = false;
            PathBox.Focus();
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
