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
    private const int ErrorAlreadyExists = 183;

    /// <summary>The most reparse data Windows accepts, header included (MAXIMUM_REPARSE_DATA_BUFFER_SIZE).</summary>
    internal const int MaximumReparseDataBufferSize = 16 * 1024;

    /// <summary>The prefix the kernel expects in front of a junction's target (an NT path).</summary>
    private const string NonInterpretedPathPrefix = @"\??\";

    /// <summary>
    /// Create the junction <paramref name="linkPath"/> pointing at <paramref name="targetPath"/>. Like
    /// <c>mklink /J</c>, it refuses when anything already exists at the link path or the link's parent folder is
    /// missing, and the target need not exist. The folder is created by a call that itself fails when the path
    /// exists, so there is no window in which someone else's folder could be turned into a junction. Throws, and
    /// leaves nothing behind, when the junction cannot be made.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Create(string linkPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        FileLog.Write($"[DirectoryJunction] Create: {linkPath} -> {targetPath}");

        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        var buffer = MountPointReparseData(NonInterpretedPathPrefix + target, target);
        var link = ExtendedLengthPath(Path.GetFullPath(linkPath));

        if (!CreateDirectoryW(link, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            FileLog.Write($"[DirectoryJunction] Create FAILED: {linkPath}: could not create its folder, error {error}");
            if (error == ErrorAlreadyExists)
                throw new IOException($"Could not create the junction '{linkPath}': something already exists there.");
            throw new Win32Exception(error, $"Could not create the folder for the junction '{linkPath}'");
        }

        string? failure = null;
        var failureCode = 0;
        using (var handle = CreateFileW(link, GenericWrite, 0, IntPtr.Zero, OpenExisting,
                   FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero))
        {
            if (handle.IsInvalid)
            {
                failureCode = Marshal.GetLastWin32Error();
                failure = $"Could not open '{linkPath}' to make it a junction";
            }
            else if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                failureCode = Marshal.GetLastWin32Error();
                failure = $"Could not make '{linkPath}' a junction to '{target}'";
            }
        }

        if (failure is not null)
        {
            FileLog.Write($"[DirectoryJunction] Create FAILED: {failure}, error {failureCode}");
            // The folder this call made is removed. If even that fails it is logged, and the ORIGINAL error is the
            // one thrown, because it is the one that says why there is no junction.
            if (!RemoveDirectoryW(link))
                FileLog.Write($"[DirectoryJunction] Create: could not remove the folder it made at '{linkPath}', error {Marshal.GetLastWin32Error()}");
            throw new Win32Exception(failureCode, failure);
        }

        FileLog.Write($"[DirectoryJunction] Created: {linkPath} -> {target}");
    }

    /// <summary>
    /// The extended-length form of a full path, so a direct Windows call accepts a path past 260 characters the way
    /// .NET's own file calls, which add the prefix themselves, do.
    /// </summary>
    internal static string ExtendedLengthPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + fullPath[2..];
        return @"\\?\" + fullPath;
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
        if (8 + dataLength > MaximumReparseDataBufferSize)
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string pathName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectoryW(string pathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[] inBuffer,
        int inBufferSize, IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
