using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Move a finished temporary file over its destination in one step, even while another reader has the
/// destination open.
///
/// WHY THIS EXISTS. On Windows, <c>File.Move(temp, path, overwrite: true)</c> uses the classic rename, which
/// refuses to replace a file any other handle has open and throws <see cref="UnauthorizedAccessException"/>.
/// A file shared between Directors on one root is read by the other Directors at any moment, so the writer
/// failed whenever a reader happened to be looking (issue #3220, still red on main in #3393). The POSIX rename
/// Windows has offered on NTFS since Windows 10 1709 replaces the name while the old file stays readable to
/// whoever already holds it, which is exactly what <c>rename(2)</c> does on Linux and macOS. A reader must
/// open the destination with <see cref="FileShare.Delete"/> for the replace to go through.
///
/// A volume that cannot do a POSIX rename (FAT32, some network shares) makes the replace throw with the
/// Windows error, rather than quietly falling back to the classic rename that fails under readers.
/// </summary>
public static class AtomicFileReplace
{
    private const uint DeleteAccess = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint OpenExisting = 3;
    private const int FileRenameInfoEx = 22;
    private const uint FileRenameFlagReplaceIfExists = 0x1;
    private const uint FileRenameFlagPosixSemantics = 0x2;

    /// <summary>Move <paramref name="source"/> over <paramref name="destination"/>, replacing it atomically.</summary>
    public static void Replace(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            // rename(2): already atomic, and already indifferent to readers holding the old file.
            File.Move(source, destination, overwrite: true);
            return;
        }

        ReplaceOnWindows(Path.GetFullPath(source), Path.GetFullPath(destination));
    }

    private static void ReplaceOnWindows(string source, string destination)
    {
        using var handle = CreateFileW(source, DeleteAccess | Synchronize,
            FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"[AtomicFileReplace] could not open {source} to move it over {destination}");

        // FILE_RENAME_INFO: a 4-byte Flags field, then a pointer-aligned RootDirectory handle, a 4-byte
        // FileNameLength in bytes, and the UTF-16 name itself. RootDirectory is left zero, so the name is a
        // full path.
        var nameOffset = (2 * IntPtr.Size) + 4;
        var nameBytes = destination.Length * 2;
        var size = nameOffset + nameBytes + 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, 0, unchecked((int)(FileRenameFlagReplaceIfExists | FileRenameFlagPosixSemantics)));
            Marshal.WriteInt32(buffer, 2 * IntPtr.Size, nameBytes);
            Marshal.Copy(destination.ToCharArray(), 0, buffer + nameOffset, destination.Length);

            if (!SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"[AtomicFileReplace] could not move {source} over {destination}; the volume must support a POSIX rename (NTFS on Windows 10 1709 or later)");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass,
        IntPtr lpFileInformation, uint dwBufferSize);
}
