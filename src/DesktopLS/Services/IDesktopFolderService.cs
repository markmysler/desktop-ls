namespace DesktopLS.Services;

/// <summary>Shows a folder on the Windows desktop (see <see cref="DesktopFolderService"/>).</summary>
public interface IDesktopFolderService
{
    /// <summary>The user's real desktop folder.</summary>
    string OriginalDesktop { get; }

    /// <summary>Shows path on the desktop; throws if it can't.</summary>
    void SetDesktopPath(string path);
}
