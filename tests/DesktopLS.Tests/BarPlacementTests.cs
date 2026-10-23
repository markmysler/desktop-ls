using DesktopLS.Native;
using DesktopLS.Services;
using Xunit;
using RECT = DesktopLS.Native.BarPositioner.RECT;

namespace DesktopLS.Tests;

public class BarPlacementTests
{
    private static RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    // A second 1920x1080 monitor to the right of the primary
    private static readonly RECT Second = R(1920, 0, 3840, 1080);

    [Theory]
    [InlineData(0.0, 1920)]
    [InlineData(0.5, 1920 + 610)]
    [InlineData(1.0, 3840 - 700)]
    [InlineData(7.0, 3840 - 700)] // out-of-range settings are clamped
    public void HorizontalPosition_IsAFractionOfTheFreeWidth(double x, int expectedLeft)
        => Assert.Equal(expectedLeft, BarPositioner.HorizontalPosition(Second, 700, x));

    [Fact]
    public void PlacementFromWindow_SnapsToTheNearerEdge_AndKeepsTheMonitor()
    {
        var top = BarPositioner.PlacementFromWindow(R(2530, 300, 3230, 352), Second, @"\\.\DISPLAY2");
        var bottom = BarPositioner.PlacementFromWindow(R(1920, 900, 2620, 952), Second, @"\\.\DISPLAY2");

        Assert.Equal(new BarPlacement(BarEdge.Top, 0.5, @"\\.\DISPLAY2"), top);
        Assert.Equal(new BarPlacement(BarEdge.Bottom, 0, @"\\.\DISPLAY2"), bottom);
    }

    [Fact]
    public void PlacementFromWindow_DraggedPastTheEdge_ClampsToIt()
        => Assert.Equal(1, BarPositioner.PlacementFromWindow(R(3500, 0, 4200, 52), Second, null).X);

    [Fact]
    public void EdgeStrip_SpansTheMonitorWidth()
    {
        var top = BarPositioner.EdgeStrip(Second, 52, BarEdge.Top);
        var bottom = BarPositioner.EdgeStrip(Second, 52, BarEdge.Bottom);

        Assert.Equal((1920, 0, 3840, 52), (top.Left, top.Top, top.Right, top.Bottom));
        Assert.Equal((1920, 1028, 3840, 1080), (bottom.Left, bottom.Top, bottom.Right, bottom.Bottom));
    }
}
