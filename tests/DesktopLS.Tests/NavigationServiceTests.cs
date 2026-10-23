using DesktopLS.Services;

namespace DesktopLS.Tests;

public class NavigationServiceTests
{
    private static bool AllExist(string _) => true;

    [Fact]
    public void Back_and_forward_walk_the_history()
    {
        var nav = new NavigationService();
        nav.NavigateTo(@"C:\a");
        nav.NavigateTo(@"C:\b");
        nav.NavigateTo(@"C:\c");

        Assert.Equal(@"C:\b", nav.GoBack(AllExist));
        Assert.Equal(@"C:\a", nav.GoBack(AllExist));
        Assert.Null(nav.GoBack(AllExist));
        Assert.Equal(@"C:\b", nav.GoForward(AllExist));
        Assert.Equal(@"C:\b", nav.CurrentPath);
    }

    [Fact]
    public void Navigating_clears_forward_history()
    {
        var nav = new NavigationService();
        nav.NavigateTo(@"C:\a");
        nav.NavigateTo(@"C:\b");
        nav.GoBack(AllExist);
        nav.NavigateTo(@"C:\c");

        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public void Navigating_to_the_current_folder_is_ignored()
    {
        var nav = new NavigationService();
        nav.NavigateTo(@"C:\a");
        nav.NavigateTo(@"c:\A");

        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Back_skips_folders_that_were_deleted()
    {
        var nav = new NavigationService();
        nav.NavigateTo(@"C:\a");
        nav.NavigateTo(@"C:\deleted");
        nav.NavigateTo(@"C:\c");

        string? path = nav.GoBack(p => p != @"C:\deleted");

        Assert.Equal(@"C:\a", path);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Back_with_only_deleted_folders_changes_nothing()
    {
        var nav = new NavigationService();
        nav.NavigateTo(@"C:\deleted");
        nav.NavigateTo(@"C:\c");

        Assert.Null(nav.GoBack(_ => false));
        Assert.Equal(@"C:\c", nav.CurrentPath);
        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public void Up_goes_to_an_existing_parent()
    {
        string dir = Directory.CreateTempSubdirectory("dls-nav").FullName;
        string child = Directory.CreateDirectory(Path.Combine(dir, "child")).FullName;
        try
        {
            var nav = new NavigationService();
            nav.NavigateTo(child);

            Assert.Equal(dir, nav.GoUp());
            Assert.True(nav.CanGoBack);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Up_with_no_current_folder_does_nothing()
    {
        Assert.Null(new NavigationService().GoUp());
    }
}
