using DesktopLS.Services;
using DesktopLS.ViewModels;

namespace DesktopLS.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly string _a;
    private readonly string _b;
    private readonly FakeDesktop _desktop;
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        _root = Directory.CreateTempSubdirectory("dls-vm").FullName;
        _a = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        _b = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;
        _desktop = new FakeDesktop(original: _root);
        _vm = new MainViewModel(new NavigationService(), new AutocompleteService(), _desktop, new FakeSettings());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void TypeAndEnter(string path)
    {
        _vm.SetPathFromSuggestion(path);
        _vm.NavigateCommand.Execute(null);
    }

    [Fact]
    public void Navigating_shows_the_folder_on_the_desktop()
    {
        _vm.Initialize(_a);
        TypeAndEnter(_b);

        Assert.Equal(new[] { _a, _b }, _desktop.Shown);
        Assert.Equal(_b, _vm.PathText);
        Assert.Equal(string.Empty, _vm.StatusMessage);
    }

    [Fact]
    public void Missing_folder_is_reported_and_nothing_changes()
    {
        _vm.Initialize(_a);
        TypeAndEnter(Path.Combine(_root, "missing"));

        Assert.Equal(new[] { _a }, _desktop.Shown);
        Assert.StartsWith("Folder not found", _vm.StatusMessage);
        Assert.False(_vm.GoBackCommand.CanExecute(null));
    }

    [Fact]
    public void Failure_to_redirect_is_reported_and_history_is_not_updated()
    {
        _vm.Initialize(_a);
        _desktop.FailNext = new IOException("Access denied");

        TypeAndEnter(_b);

        Assert.Equal("Access denied", _vm.StatusMessage);
        Assert.False(_vm.GoBackCommand.CanExecute(null));
    }

    [Fact]
    public void Missing_start_folder_falls_back_to_the_real_desktop()
    {
        _vm.Initialize(Path.Combine(_root, "missing"));

        Assert.Equal(new[] { _root }, _desktop.Shown);
        Assert.StartsWith("Folder not found", _vm.StatusMessage);
    }

    [Fact]
    public void Back_skips_a_folder_deleted_since_it_was_visited()
    {
        string c = Directory.CreateDirectory(Path.Combine(_root, "c")).FullName;
        _vm.Initialize(_a);
        TypeAndEnter(_b);
        TypeAndEnter(c);
        Directory.Delete(_b);

        _vm.GoBackCommand.Execute(null);

        Assert.Equal(_a, _desktop.Shown[^1]);
        Assert.Equal(_a, _vm.PathText);
    }

    [Fact]
    public void Start_argument_quirk_is_normalised()
    {
        // "D:\" on a command line arrives as D:" (the \" becomes a quote)
        Assert.Equal(@"D:\", App.NormalizeStartArgument("D:\""));
        Assert.Equal(@"C:\Users\x", App.NormalizeStartArgument(" \"C:\\Users\\x\" "));
        Assert.Equal(@"C:\Users\x", App.NormalizeStartArgument(@"C:\Users\x"));
    }

    private sealed class FakeDesktop(string original) : IDesktopFolderService
    {
        public List<string> Shown { get; } = new();
        public Exception? FailNext { get; set; }
        public string OriginalDesktop { get; } = original;

        public void SetDesktopPath(string path)
        {
            if (FailNext is { } e) { FailNext = null; throw e; }
            Shown.Add(path);
        }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public bool StartupEnabled { get; set; }
        public bool HideOnMaximized { get; set; } = true;
    }
}
