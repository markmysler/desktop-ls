using DesktopLS.Services;

namespace DesktopLS.Tests;

public class LayoutAndSettingsTests
{
    [Theory]
    [InlineData(@"C:\Projects", @"C:\Projects")]
    [InlineData(@"C:\Projects\", @"C:\Projects")]
    [InlineData(@"C:\Projects\sub\..", @"C:\Projects")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"D:\", @"D:\")]
    public void Folder_paths_are_normalised_for_layout_keys(string input, string expected)
    {
        Assert.Equal(expected, IconLayoutService.NormalizePath(input));
    }

    [Fact]
    public void Layouts_saved_by_older_versions_still_match()
    {
        var old = new Dictionary<string, Dictionary<string, int[]>>
        {
            [@"C:\Projects\|1920x1080@0,0"] = new() { ["readme.txt"] = new[] { 10, 20 } },
        };

        var store = IconLayoutService.NormalizeStore(old);

        // Same folder, different spelling and case
        Assert.True(store.TryGetValue(@"c:\projects|1920x1080@0,0", out var layout));
        Assert.Equal(new[] { 10, 20 }, layout!["README.TXT"]);
    }

    [Fact]
    public void Startup_command_quotes_the_exe_path()
    {
        string? command = SettingsService.StartupCommand();

        Assert.NotNull(command);
        Assert.StartsWith("\"", command);
        Assert.EndsWith(".exe\"", command);
    }
}
