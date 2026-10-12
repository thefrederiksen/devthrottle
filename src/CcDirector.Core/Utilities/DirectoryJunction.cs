using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Makes a Windows directory JUNCTION in this process, with no child process.
///
/// A junction and not a symbolic link: creating a directory symbolic link needs administrator rights or
/// Developer Mode, and the Director runs as the ordinary signed-in user; a junction needs neither. .NET has no
/// managed call that creates one, so this writes the mount-point reparse data itself - exactly what
/// <c>mklink /J</c> does inside cmd.exe. Doing it here instead of starting cmd.exe means one less process per
/// skill link on every launch, and tests that place skills start no process at all (Reliable Tests).
/// </summary>
public static class DirectoryJunction
{
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    /// <summary>The prefix the kernel expects in front of a junction's target (an NT path).</summary>
    private const string NonInterpretedPathPrefix = @"\??\";

    /// <summary>
    /// Create the junction <paramref name="linkPath"/> pointing at <paramref name="targetPath"/>. Like
    /// <c>mklink /J</c>, it refuses when something already exists at the link path. The target need not exist.
    /// Throws, and leaves nothing behind, when the junction cannot be made.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Create(string linkPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        FileLog.Write($"[DirectoryJunction] Create: {linkPath} -> {targetPath}");

        if (Directory.Exists(linkPath) || File.Exists(linkPath) || new FileInfo(linkPath).LinkTarget is not null)
            throw new IOException($"Could not create the junction '{linkPath}': something already exists there.");

        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        var buffer = MountPointReparseData(NonInterpretedPathPrefix + target, target);

        Directory.CreateDirectory(linkPath);
        try
        {
            using var handle = CreateFileW(linkPath, GenericWrite, 0, IntPtr.Zero, OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open '{linkPath}' to make it a junction");

            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not make '{linkPath}' a junction to '{target}'");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectoryJunction] Create FAILED: {linkPath} -> {target}: {ex.Message}");
            Directory.Delete(linkPath);
            throw;
        }

        FileLog.Write($"[DirectoryJunction] Created: {linkPath} -> {target}");
    }

    /// <summary>
    /// The REPARSE_DATA_BUFFER for a mount point: tag, data length, reserved, then the substitute name (the NT
    /// path the kernel follows) and the print name (what a directory listing shows), each null-terminated.
    /// </summary>
    internal static byte[] MountPointReparseData(string substituteName, string printName)
    {
        var substitute = Encoding.Unicode.GetBytes(substituteName);
        var print = Encoding.Unicode.GetBytes(printName);
        const int nameHeader = 8; // four ushort offsets and lengths
        var dataLength = nameHeader + substitute.Length + 2 + print.Length + 2;
        if (dataLength > ushort.MaxValue)
            throw new PathTooLongException($"The junction target is too long: '{printName}'.");

        var buffer = new byte[8 + dataLength];
        using var writer = new BinaryWriter(new MemoryStream(buffer));
        writer.Write(IoReparseTagMountPoint);
        writer.Write((ushort)dataLength);
        writer.Write((ushort)0);                        // reserved
        writer.Write((ushort)0);                        // substitute name offset
        writer.Write((ushort)substitute.Length);        // substitute name length, in bytes, no terminator
        writer.Write((ushort)(substitute.Length + 2));  // print name offset
        writer.Write((ushort)print.Length);             // print name length
        writer.Write(substitute);
        writer.Write((ushort)0);
        writer.Write(print);
        writer.Write((ushort)0);
        return buffer;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[] inBuffer,
        int inBufferSize, IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
