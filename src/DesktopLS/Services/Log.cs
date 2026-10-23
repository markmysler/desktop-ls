using System.IO;

namespace DesktopLS.Services;

/// <summary>
/// Minimal file log at %LocalAppData%\DesktopLS\log.txt, for failures that
/// are handled (the app keeps running) but worth knowing about when
/// something misbehaves. Rotated to log.old.txt at 1 MB. Never throws.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopLS", "log.txt");

    public static void Error(string what, Exception? ex = null)
        => Write("ERROR", ex == null ? what : $"{what}: {ex.GetType().Name}: {ex.Message}");

    public static void Info(string message) => Write("INFO", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                var info = new FileInfo(LogFile);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(LogFile, Path.ChangeExtension(LogFile, ".old.txt"), overwrite: true);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
