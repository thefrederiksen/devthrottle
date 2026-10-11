using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Write a whole file so that every reader - in this process or in another Director sharing the root -
/// sees either the old contents or the new, never half of either, and so that the write goes through
/// while a reader holds the old file open.
///
/// <para><b>WHY THIS EXISTS.</b> On Windows, <c>File.Move(temp, path, overwrite: true)</c> uses the
/// classic rename, which refuses to replace a file any other handle has open and throws
/// <see cref="UnauthorizedAccessException"/>. A file shared between Directors on one root is read by the
/// other Directors at any moment, so the writer failed whenever a reader happened to be looking (issue
/// #3220, still red on main in #3393). The POSIX rename Windows has offered on NTFS since Windows 10
/// 1709 replaces the name while the old file stays readable to whoever already holds it, which is
/// exactly what <c>rename(2)</c> does on Linux and macOS. A reader must open the destination with
/// <see cref="FileShare.Delete"/> for the replace to go through.</para>
///
/// <para><b>WHY THE TEMPORARY FILE IS WRITTEN AND RENAMED THROUGH ONE HANDLE, NEVER CLOSED IN BETWEEN.</b>
/// The first version wrote the temporary file with <c>File.WriteAllText</c>, closed it, opened it again
/// and renamed it. Closing a freshly written file is the moment Microsoft Defender's real-time protection
/// takes it to scan, and while that scan is running the filter driver refuses to let the file be renamed:
/// both the POSIX rename and the classic one answered <c>Access is denied</c> (Win32 error 5) for about
/// 1.3 seconds, sometimes far longer, with no handle open on either file that any sharing check or the
/// Restart Manager could see. Under <c>RepositoryRegistryConcurrencyTests</c> that was one red run in
/// five (measured 11 October 2026: 6 of 30, then 5 of 30, then 4 of 30, every one error 5 on the rename).
/// With the bytes written through the very handle that renames the file, the file is never closed before
/// it has its final name, so there is no closed-then-reopened temporary for the scanner to catch
/// mid-flight. That removed most of the failures, not all of them: the scanner can still veto the rename
/// itself, so the rename waits out an Access-denied refusal for a bounded time (see
/// <see cref="RenameThroughScannerVeto"/>).</para>
///
/// <para>A volume that cannot do a POSIX rename (FAT32, some network shares) makes the write throw with
/// the Windows error, rather than quietly falling back to the classic rename that fails under readers.</para>
/// </summary>
public static class AtomicFileReplace
{
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint CreateNew = 1;
    private const uint FileAttributeNormal = 0x80;
    private const int FileDispositionInfo = 4;
    private const int FileRenameInfoEx = 22;
    private const uint FileRenameFlagReplaceIfExists = 0x1;
    private const uint FileRenameFlagPosixSemantics = 0x2;

    /// <summary>
    /// Write <paramref name="text"/> (UTF-8, no byte order mark, the same bytes <c>File.WriteAllText</c>
    /// writes) to <paramref name="destination"/> atomically: a reader sees the old file or the new one.
    /// </summary>
    /// <remarks>
    /// The text goes into a uniquely named temporary file beside the destination, which is then renamed
    /// over it. A unique name, because a fixed one would itself be the thing two writers race over.
    /// The directory must already exist.
    /// </remarks>
    public static void WriteAllText(string destination, string text)
    {
        var fullDestination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullDestination)
            ?? throw new ArgumentException($"[AtomicFileReplace] {destination} has no directory", nameof(destination));
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");
        var bytes = Encoding.UTF8.GetBytes(text);

        if (!OperatingSystem.IsWindows())
        {
            // rename(2): already atomic, and already indifferent to readers holding the old file.
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, fullDestination, overwrite: true);
            return;
        }

        WriteOnWindows(temporary, fullDestination, bytes);
    }

    private static void WriteOnWindows(string temporary, string destination, byte[] bytes)
    {
        using var handle = CreateFileW(temporary, GenericWrite | DeleteAccess | Synchronize,
            FileShare.Read | FileShare.Delete, IntPtr.Zero, CreateNew, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"[AtomicFileReplace] could not create {temporary} to write {destination}: {Describe(error)}");
        }

        try
        {
            RandomAccess.Write(handle, bytes, fileOffset: 0);
            RenameThroughScannerVeto(() => TryRename(handle, destination), temporary, destination,
                TimeProvider.System, Thread.Sleep);
        }
        catch
        {
            // The temporary never reached its name: take it with the handle, so nothing is left beside
            // the list for the next reader to trip over. The failure already in flight is the one reported.
            MarkDeleteOnClose(handle);
            throw;
        }
    }

    /// <summary>How long a rename may be refused with Access-denied before the write fails.</summary>
    internal static readonly TimeSpan ScannerVetoDeadline = TimeSpan.FromSeconds(10);

    /// <summary>How long to pause between attempts while the rename is refused.</summary>
    internal static readonly TimeSpan ScannerVetoPause = TimeSpan.FromMilliseconds(50);

    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// Rename, waiting out an antivirus scan that is holding the file.
    ///
    /// <para><b>WHY A BOUNDED WAIT, AND WHY ONLY FOR ACCESS-DENIED.</b> Writing through one handle removed
    /// the closed-then-reopened temporary the scanner used to catch, but measured on 11 October 2026 the
    /// real-time scanner still vetoed the rename with Win32 error 5 in about one run in ten, with no handle
    /// visible to any sharing check. Every user's machine runs a scanner, so a list write that fails the
    /// moment one looks at the file is a product defect, not a test problem. A scanner releases the file in
    /// about a second and a half; ten seconds covers that with room, and past it the write fails with an
    /// error that names the veto - it never waits forever and never swallows the failure. Every other error
    /// (a volume with no POSIX rename, a missing directory) fails at once, unchanged.</para>
    /// </summary>
    /// <param name="tryRename">One rename attempt: 0 when it went through, otherwise the Win32 error.</param>
    internal static void RenameThroughScannerVeto(Func<int> tryRename, string source, string destination,
        TimeProvider clock, Action<TimeSpan> pause)
    {
        var started = clock.GetTimestamp();
        var vetoes = 0;
        while (true)
        {
            var error = tryRename();
            if (error == 0)
            {
                if (vetoes > 0)
                    FileLog.Write($"[AtomicFileReplace] RenameThroughScannerVeto: {destination} written after {vetoes} access-denied refusal(s) in {clock.GetElapsedTime(started).TotalMilliseconds:F0} ms");
                return;
            }

            if (error != ErrorAccessDenied)
                throw new Win32Exception(error,
                    $"[AtomicFileReplace] could not move {source} over {destination}: {Describe(error)}; " +
                    "the volume must support a POSIX rename (NTFS on Windows 10 1709 or later)");

            vetoes++;
            var waited = clock.GetElapsedTime(started);
            if (waited >= ScannerVetoDeadline)
            {
                FileLog.Write($"[AtomicFileReplace] RenameThroughScannerVeto FAILED: {destination} refused {vetoes} times over {waited.TotalSeconds:F1} s");
                throw new Win32Exception(error,
                    $"[AtomicFileReplace] could not move {source} over {destination}: {Describe(error)}, " +
                    $"still refused after {waited.TotalSeconds:F1} s ({vetoes} attempts). Something is holding the " +
                    "new file - usually an antivirus scan that has not finished; check the antivirus log for this path.");
            }

            pause(ScannerVetoPause);
        }
    }

    /// <summary>One POSIX rename through the open handle: 0 when it went through, otherwise the Win32 error.</summary>
    private static int TryRename(SafeFileHandle handle, string destination)
    {
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

            return SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)size)
                ? 0
                : Marshal.GetLastWin32Error();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void MarkDeleteOnClose(SafeFileHandle handle)
    {
        // FILE_DISPOSITION_INFO is a single BOOLEAN; the buffer is one byte set to TRUE.
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            SetFileInformationByHandle(handle, FileDispositionInfo, buffer, 1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The Windows error as Windows words it, with its number, so a failure says which one it was.</summary>
    private static string Describe(int error) => $"{new Win32Exception(error).Message} (Win32 error {error})";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass,
        IntPtr lpFileInformation, uint dwBufferSize);
}
