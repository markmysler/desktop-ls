namespace DesktopLS.Services;

/// <summary>User preferences shown in the settings menu (see <see cref="SettingsService"/>).</summary>
public interface ISettingsService
{
    bool StartupEnabled { get; set; }
    bool HideOnMaximized { get; set; }

    /// <summary>Register the bar as an app bar, so maximized windows stop at it.</summary>
    bool ReserveSpace { get; set; }
}

/// <summary>The screen edge the bar sits on.</summary>
public enum BarEdge { Top, Bottom }

/// <summary>
/// Where the bar sits: an edge of a monitor, and how far along it (0 = left,
/// 0.5 = centred, 1 = right). Monitor is the device name (e.g. \\.\DISPLAY2);
/// null or a monitor that's gone means the primary one.
/// </summary>
public sealed record BarPlacement(BarEdge Edge = BarEdge.Top, double X = 0.5, string? Monitor = null);
