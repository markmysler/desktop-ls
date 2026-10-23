using DesktopLS.Services;

namespace DesktopLS.Tests;

public sealed class AutocompleteServiceTests : IDisposable
{
    private readonly string _root;

    public AutocompleteServiceTests()
    {
        _root = Directory.CreateTempSubdirectory("dls-ac").FullName;
        foreach (var name in new[] { "Projects", "Photos", "Notes", "projects-old" })
            Directory.CreateDirectory(Path.Combine(_root, name));
        File.WriteAllText(Path.Combine(_root, "Pfile.txt"), "not a folder");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Task<IReadOnlyList<string>> Suggest(string partial)
        => new AutocompleteService().GetSuggestionsAsync(partial);

    [Fact]
    public async Task Trailing_separator_lists_subfolders_only()
    {
        var result = await Suggest(_root + Path.DirectorySeparatorChar);

        Assert.Equal(4, result.Count);
        Assert.DoesNotContain(result, p => p.EndsWith("Pfile.txt"));
    }

    [Fact]
    public async Task Prefix_filters_case_insensitively_and_sorts()
    {
        var result = await Suggest(Path.Combine(_root, "p"));

        Assert.Equal(
            new[] { "Photos", "Projects", "projects-old" },
            result.Select(Path.GetFileName));
    }

    [Fact]
    public async Task Missing_parent_gives_no_suggestions()
    {
        var result = await Suggest(Path.Combine(_root, "nope", "x"));
        Assert.Empty(result);
    }

    [Fact]
    public async Task Results_are_capped_at_twenty()
    {
        for (int i = 0; i < 30; i++)
            Directory.CreateDirectory(Path.Combine(_root, $"many{i:D2}"));

        var result = await Suggest(Path.Combine(_root, "many"));
        Assert.Equal(20, result.Count);
    }

    [Fact]
    public async Task A_newer_request_cancels_the_older_one()
    {
        var service = new AutocompleteService();
        var first = service.GetSuggestionsAsync(Path.Combine(_root, "P"));
        var second = service.GetSuggestionsAsync(Path.Combine(_root, "N"));

        Assert.Empty(await first);
        Assert.Equal("Notes", Path.GetFileName(Assert.Single(await second)));
    }
}
