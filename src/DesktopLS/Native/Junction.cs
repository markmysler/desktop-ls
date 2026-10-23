using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopLS.Native;

/// <summary>
/// NTFS directory junctions (what "mklink /J" makes). A junction is retargeted
/// in place by replacing its reparse data, so its path never stops existing:
/// Explorer, which shows the junction as the desktop, only sees its contents change.
/// Junctions need no admin rights, but they can only point at local folders.
/// </summary>
public static class Junction
{
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_ALL = 0x7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize,
        IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

    /// <summary>The folder a junction points at, or null if path isn't a junction.</summary>
    public static string? GetTarget(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.LinkTarget is { } target ? target : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Makes path a junction to target: creates it if missing, retargets it
    /// otherwise. Refuses to touch an existing ordinary folder.
    /// </summary>
    public static void Point(string path, string target)
    {
        target = Path.GetFullPath(target).TrimEnd('\\');
        if (target.Length == 2 && target[1] == ':') target += "\\"; // a drive root keeps its backslash

        var info = new DirectoryInfo(path);
        if (!info.Exists)
            Directory.CreateDirectory(path);
        else if (info.LinkTarget == null && Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException($"{path} is a normal folder with files in it, not a DesktopLS junction.");

        byte[] data = BuildMountPointData(target);
        using var handle = CreateFile(path, GENERIC_WRITE, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Opening {path} failed");

        // Setting a reparse point with the same tag replaces the old target
        if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Pointing {path} at {target} failed");
    }

    /// <summary>REPARSE_DATA_BUFFER for a mount point: "\??\target" plus a printable name.</summary>
    internal static byte[] BuildMountPointData(string target)
    {
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        byte[] print = Encoding.Unicode.GetBytes(target);

        // 8 bytes of offsets/lengths, then both names, each NUL-terminated
        int pathBytes = substitute.Length + 2 + print.Length + 2;
        var buffer = new byte[8 + 8 + pathBytes];
        using var w = new BinaryWriter(new MemoryStream(buffer));
        w.Write(IO_REPARSE_TAG_MOUNT_POINT);
        w.Write((ushort)(8 + pathBytes));        // ReparseDataLength
        w.Write((ushort)0);                      // Reserved
        w.Write((ushort)0);                      // SubstituteNameOffset
        w.Write((ushort)substitute.Length);      // SubstituteNameLength
        w.Write((ushort)(substitute.Length + 2)); // PrintNameOffset
        w.Write((ushort)print.Length);           // PrintNameLength
        w.Write(substitute); w.Write((ushort)0);
        w.Write(print); w.Write((ushort)0);
        return buffer;
    }
}
