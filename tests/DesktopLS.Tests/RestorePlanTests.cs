using System.Text;
using DesktopLS.Native;
using DesktopLS.Services;
using Xunit;

namespace DesktopLS.Tests;

public class RestorePlanTests
{
    private static readonly (int X, int Y)[] Slots =
        Enumerable.Range(0, 4).SelectMany(c => Enumerable.Range(0, 3).Select(r => (14 + 76 * c, 2 + 100 * r))).ToArray();

    [Fact]
    public void Identify_UsesFileNameFromShellView_AndSkipsAmbiguousNames()
    {
        var items = new List<(string, int, int)> { ("report", 14, 2), ("report", 14, 102), ("notes", 14, 202), ("desktop.ini", 90, 2), ("desktop.ini", 90, 102) };
        var shell = new Dictionary<string, (int, int, string)>
        {
            ["report.pdf"] = (14, 2, "report"),
            ["report.docx"] = (14, 102, "report"),
        };

        var ids = IconLayoutService.Identify(items, shell);

        Assert.Equal(new string?[] { "report.pdf", "report.docx", "notes", null, null }, ids);
    }

    [Fact]
    public void PlanRestore_ParksMoversAndBlockers_InFreeSlotsNoIconWants()
    {
        // a and b swap places; c is unidentified but sits where d must go
        var items = new List<(string, int, int)> { ("a", 14, 2), ("b", 14, 102), ("c", 14, 202), ("d", 90, 2) };
        var ids = new string?[] { "a", "b", null, "d" };
        var saved = new Dictionary<string, (int X, int Y)> { ["a"] = (14, 102), ["b"] = (14, 2), ["d"] = (14, 202), ["gone"] = (90, 102) };

        var plan = IconLayoutService.PlanRestore(items, ids, saved, Slots);

        Assert.Equal(new[] { 0, 1, 3 }, plan.Targets.Keys.OrderBy(k => k));
        Assert.Equal(new[] { 0, 1, 2, 3 }, plan.Parking.Keys.OrderBy(k => k)); // c is in d's way
        // ("gone" isn't on the desktop, so its saved spot is free to park in)
        var occupiedOrWanted = items.Select(i => (i.Item2, i.Item3)).Concat(new[] { (14, 102), (14, 2), (14, 202) }).ToHashSet();
        Assert.All(plan.Parking.Values, slot => Assert.DoesNotContain(slot, occupiedOrWanted));
        Assert.Equal(plan.Parking.Count, plan.Parking.Values.Distinct().Count());
    }

    [Fact]
    public void PlanRestore_NothingToDo_WhenEveryIconIsInPlace()
    {
        var items = new List<(string, int, int)> { ("a", 14, 2) };
        var plan = IconLayoutService.PlanRestore(items, new string?[] { "a" },
            new Dictionary<string, (int X, int Y)> { ["a"] = (14, 2) }, Slots);
        Assert.Empty(plan.Targets);
        Assert.Empty(plan.Parking);
    }

    [Fact]
    public void PlanRestore_SkipsParking_WhenThereAreTooFewFreeSlots()
    {
        var items = new List<(string, int, int)> { ("a", 14, 2), ("b", 14, 102) };
        var plan = IconLayoutService.PlanRestore(items, new string?[] { "a", "b" },
            new Dictionary<string, (int X, int Y)> { ["a"] = (14, 102), ["b"] = (14, 2) }, new[] { (14, 2), (14, 102) });
        Assert.Equal(2, plan.Targets.Count);
        Assert.Empty(plan.Parking);
    }

    [Fact]
    public void PlanRestore_NeverSendsTwoIconsToOneSlot()
    {
        var items = new List<(string, int, int)> { ("a", 14, 2), ("b", 14, 102) };
        var saved = new Dictionary<string, (int X, int Y)> { ["a"] = (90, 2), ["b"] = (90, 2) };
        var plan = IconLayoutService.PlanRestore(items, new string?[] { "a", "b" }, saved, Slots);
        Assert.Single(plan.Targets);
    }

    [Fact]
    public void AssignGroups_PutsSameNamedItemsOnTheirSavedSpots()
    {
        // One desktop.ini is already on a saved spot, the other was moved away
        var items = new List<(string, int, int)> { ("desktop.ini", 14, 102), ("desktop.ini", 2618, 302) };
        var groups = new Dictionary<string, List<(int X, int Y)>> { ["desktop.ini"] = new() { (546, 302), (14, 102) } };
        var positions = new Dictionary<string, (int, int)>();

        var ids = IconLayoutService.AssignGroups(items, new string?[] { null, null }, groups, positions);
        var plan = IconLayoutService.PlanRestore(items, ids, positions, Slots);

        Assert.Equal((546, 302), Assert.Single(plan.Targets).Value);
        Assert.Equal(1, plan.Targets.Keys.Single());
    }

    [Fact]
    public void MountPointData_HasSubstituteAndPrintNames()
    {
        byte[] data = Junction.BuildMountPointData(@"C:\Users\me\Desktop");

        Assert.Equal(0xA0000003u, BitConverter.ToUInt32(data, 0));
        Assert.Equal(data.Length - 8, BitConverter.ToUInt16(data, 4));
        int subLen = BitConverter.ToUInt16(data, 10), printOff = BitConverter.ToUInt16(data, 12), printLen = BitConverter.ToUInt16(data, 14);
        Assert.Equal(@"\??\C:\Users\me\Desktop", Encoding.Unicode.GetString(data, 16, subLen));
        Assert.Equal(@"C:\Users\me\Desktop", Encoding.Unicode.GetString(data, 16 + printOff, printLen));
    }

    [Fact]
    public void UncPaths_AreNetworkPaths()
    {
        Assert.True(DesktopFolderService.IsNetworkPath(@"\\server\share\folder"));
        Assert.False(DesktopFolderService.IsNetworkPath(Path.GetTempPath()));
    }
}
