namespace DesktopLS.Services;

/// <summary>User preferences shown in the settings menu (see <see cref="SettingsService"/>).</summary>
public interface ISettingsService
{
    bool StartupEnabled { get; set; }
    bool HideOnMaximized { get; set; }
}
