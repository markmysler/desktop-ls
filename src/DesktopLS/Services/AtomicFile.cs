using System.IO;

namespace DesktopLS.Services;

/// <summary>
/// Writes a file so readers see either the old or the new content, never a
/// half-written one (e.g. if the app is killed mid-write).
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        // A rename within one volume replaces the target in a single step
        File.Move(temp, path, overwrite: true);
    }
}
