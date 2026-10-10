using System.Runtime.InteropServices;
using System.Text;
using CcDirector.Core.Utilities;
using Microsoft.Win32.SafeHandles;

namespace CcDirector.ControlApi;

/// <summary>
/// The two filesystem facts a path-containment decision needs and that .NET does not expose:
/// how many names a file has, and what a path's real on-disk spelling is.
///
/// Tenant-boundary hardening, Phase 5b, inspection finding M03-I2-01. The Phase 5a containment work
/// resolved reparse points - symbolic links and directory junctions - and called that "the real
/// filesystem identity". It was not. A HARD LINK is a second directory entry for the SAME file
/// object and carries no reparse-point attribute at all, so an in-root name for an outside file
/// passed every check and the file was served. The independent inspector executed exactly that and
/// read a seeded secret through an in-root alias.
///
/// A file with more than one name cannot be proven to live inside a root: the name you were given is
/// inside, and the other name - the one you cannot see from here - may be anywhere on the machine.
/// So containment refuses any candidate whose link count is not exactly one. An ordinary file has
/// exactly one name, so nothing legitimate is lost. When the count CANNOT be established the answer
/// is null and the caller refuses; there is no lexical fallback, because falling back to the lexical
/// answer is the defect itself.
///
/// The casing half of the same finding: the containment comparison assumed every Windows directory
/// compares case-insensitively. It does not - NTFS carries a PER-DIRECTORY case-sensitive flag
/// (fsutil file setCaseSensitiveInfo), so one parent can hold both "repo" and "REPO" as different
/// directories while an ignore-case prefix test accepts the wrong one as inside. Rather than trying
/// to read that flag per directory and decide what an unreadable flag means, the path is folded to
/// its canonical on-disk spelling and the comparison becomes ORDINAL. That decides correctly under
/// both rules: on a case-insensitive parent a differently-spelled request folds onto the one real
/// name, and on a case-sensitive parent "repo" and "REPO" fold to themselves and stay distinct.
/// </summary>
internal static class FilesystemIdentity
{
    // ------------------------------------------------------------------ how many names ----

    /// <summary>
    /// How many directory entries name the file at <paramref name="filePath"/>: 1 for an ordinary
    /// file, more when it is hard-linked. Returns null when the count cannot be established - an
    /// unreadable file, a platform whose link count could not be located (see the Unix section),
    /// any error at all. Callers MUST refuse on null.
    /// </summary>
    internal static int? NameCount(string filePath)
    {
        try
        {
            return OperatingSystem.IsWindows() ? WindowsNameCount(filePath) : UnixNameCount(filePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FilesystemIdentity] NameCount undeterminable for {filePath} FAILED: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// True when the file has exactly one name and so can be contained by the directory it sits in;
    /// false when it has more; null when the question could not be answered. Null must be refused,
    /// never treated as true.
    /// </summary>
    internal static bool? HasExactlyOneName(string filePath) => NameCount(filePath) switch
    {
        null => null,
        1 => true,
        _ => false,
    };

    private static int? WindowsNameCount(string filePath)
    {
        // FILE_READ_ATTRIBUTES only: this asks the filesystem about the entry, it does not read the
        // contents, so it works on files the caller may stat but not read. Every share mode is
        // allowed so an open handle elsewhere does not turn a containment decision into a refusal.
        using var handle = CreateFileW(
            filePath,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        if (!GetFileInformationByHandle(handle, out var info))
            return null;

        return info.NumberOfLinks > int.MaxValue ? int.MaxValue : (int)info.NumberOfLinks;
    }

    // ------------------------------------------------------------- canonical on-disk name ----

    /// <summary>
    /// The path as the filesystem itself spells it - true casing, links already followed, long
    /// (non 8.3) form. On Windows this is <c>GetFinalPathNameByHandle</c>. Everywhere else paths are
    /// already compared case-sensitively and the caller has resolved links itself, so the path is
    /// returned unchanged.
    ///
    /// Components that do not exist cannot be canonicalized - and cannot hide a differently-cased
    /// real entry either, because there is nothing there - so a non-existent suffix is carried
    /// through unchanged on the canonical prefix. Returns null when an EXISTING component cannot be
    /// opened, which the caller must refuse.
    /// </summary>
    internal static string? CanonicalPath(string path)
    {
        if (!OperatingSystem.IsWindows())
            return path;

        try
        {
            return WindowsCanonicalPath(path);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FilesystemIdentity] CanonicalPath undeterminable for {path} FAILED: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    private static string? WindowsCanonicalPath(string path)
    {
        var remainder = new List<string>();
        var current = path;

        while (true)
        {
            using var handle = CreateFileW(
                current,
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                var canonical = FinalPathName(handle);
                if (canonical is null)
                    return null;
                remainder.Reverse();
                return remainder.Count == 0
                    ? canonical
                    : Path.Combine(canonical, Path.Combine([.. remainder]));
            }

            var error = Marshal.GetLastWin32Error();
            if (error is not (ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidName))
                return null; // it exists and we cannot identify it - refuse, never guess

            // Nothing is there. Peel the last component and ask about the parent: a name that does
            // not exist has no on-disk spelling to fold onto, so it is kept exactly as written.
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                return path; // not one component of this path exists - nothing to canonicalize
            remainder.Add(current[(parent.Length)..].TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            current = parent;
        }
    }

    private static string? FinalPathName(SafeFileHandle handle)
    {
        var needed = GetFinalPathNameByHandleW(handle, null, 0, VolumeNameDos);
        if (needed == 0)
            return null;
        var buffer = new char[needed + 1];
        var written = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, VolumeNameDos);
        if (written == 0 || written >= buffer.Length)
            return null;

        var result = new string(buffer, 0, (int)written);
        // GetFinalPathNameByHandle answers in the extended-length namespace. Strip the prefix so the
        // result is an ordinary path the rest of the code can compare and open.
        if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + result[8..];
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
            return result[4..];
        return result;
    }

    // ------------------------------------------------------------------ Windows interop ----

    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000; // required to open a DIRECTORY handle
    private const uint VolumeNameDos = 0x0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorInvalidName = 123;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile, char[]? lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    /// <summary>
    /// BY_HANDLE_FILE_INFORMATION, field for field.
    ///
    /// Every member is written out as a 32-bit value, including the three timestamps, because the
    /// Win32 structure is a run of DWORDs and FILETIMEs and is therefore 4-byte aligned throughout.
    /// Declaring the timestamps as <c>long</c> is the obvious-looking mistake and it is silent: the
    /// runtime aligns a 64-bit field to 8 bytes, inserts padding after the leading attributes DWORD,
    /// and every field after it reads from the wrong offset - so the link count comes back as an
    /// arbitrary number rather than failing. It was caught here by a probe that printed the count for
    /// an ordinary file and got 983040 instead of 1.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    // --------------------------------------------------------------------- Unix interop ----
    //
    // The link count lives in libc's "struct stat", whose layout differs by operating system, by
    // architecture, and by which of several stat entry points the C library actually exports. A
    // hard-coded offset table would be a security decision this build machine cannot verify, and a
    // wrong offset would silently read some other field - the worst possible failure for a check
    // whose whole job is to be trustworthy.
    //
    // So the field is LOCATED EMPIRICALLY, once per process: stat a temporary file (one name), add a
    // hard link (two names), add a second (three), and keep every buffer position whose value tracked
    // exactly 1, then 2, then 3. Positions that survive that are the link count; nothing else in the
    // structure counts up by one as names are added. If no entry point works, or no position
    // survives, the count is undeterminable and every caller refuses. The probe is self-proving: it
    // cannot be wrong about the layout, only unable to determine it.

    private const int StatBufferBytes = 512; // struct stat is at most ~150 bytes on any supported target

    private static readonly Lazy<UnixLinkCount?> UnixReader =
        new(CalibrateUnixLinkCount, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record UnixLinkCount(string EntryPoint, Func<byte[], byte[], int> Stat,
        IReadOnlyList<(int Offset, int Width)> Fields);

    private static int? UnixNameCount(string filePath)
    {
        var reader = UnixReader.Value;
        if (reader is null)
            return null;

        var buffer = new byte[StatBufferBytes];
        if (reader.Stat(NullTerminated(filePath), buffer) != 0)
            return null;

        ulong? agreed = null;
        foreach (var (offset, width) in reader.Fields)
        {
            var value = ReadUnsigned(buffer, offset, width);
            if (agreed is null) agreed = value;
            else if (agreed.Value != value) return null; // the surviving positions disagree - do not guess
        }
        if (agreed is null || agreed.Value < 1 || agreed.Value > int.MaxValue)
            return null;
        return (int)agreed.Value;
    }

    private static UnixLinkCount? CalibrateUnixLinkCount()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccd-linkcount-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, "probe");
            File.WriteAllBytes(probe, []);
            var probeBytes = NullTerminated(probe);

            foreach (var (name, stat) in UnixStatEntryPoints())
            {
                var first = new byte[StatBufferBytes];
                if (SafeCall(() => stat(probeBytes, first)) != 0)
                    continue;

                if (Link(probeBytes, NullTerminated(Path.Combine(dir, "second-name"))) != 0)
                {
                    FileLog.Write("[FilesystemIdentity] Unix link-count calibration cannot create a hard link; " +
                                  "the link count will be reported as undeterminable and containment will refuse.");
                    return null;
                }
                var second = new byte[StatBufferBytes];
                if (SafeCall(() => stat(probeBytes, second)) != 0)
                    continue;

                if (Link(probeBytes, NullTerminated(Path.Combine(dir, "third-name"))) != 0)
                    return null;
                var third = new byte[StatBufferBytes];
                if (SafeCall(() => stat(probeBytes, third)) != 0)
                    continue;

                var fields = LocateCountingField(first, second, third);

                if (fields.Count > 0)
                {
                    FileLog.Write($"[FilesystemIdentity] Unix link count located via '{name}' at " +
                                  string.Join(", ", fields.Select(f => $"offset {f.Offset} width {f.Width}")));
                    return new UnixLinkCount(name, stat, fields);
                }

                File.Delete(Path.Combine(dir, "second-name"));
                File.Delete(Path.Combine(dir, "third-name"));
            }

            FileLog.Write("[FilesystemIdentity] Unix link count could NOT be located in any stat layout; " +
                          "file containment will refuse every candidate rather than decide lexically.");
            return null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FilesystemIdentity] Unix link-count calibration FAILED: {ex.GetType().Name} {ex.Message}");
            return null;
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The stat entry points worth trying, in order. Which one a C library exports depends on the
    /// platform and its age: glibc before 2.33 exports only the versioned <c>__xstat</c>; macOS on
    /// Intel exports the modern 64-bit-inode variant under a decorated name. Every candidate is
    /// PROVEN by the calibration before it is used, so an entry point that is present but wrong is
    /// discarded rather than trusted.
    /// </summary>
    private static IEnumerable<(string Name, Func<byte[], byte[], int> Stat)> UnixStatEntryPoints()
    {
        yield return ("stat", Stat);
        yield return ("stat64", Stat64);
        yield return ("stat$INODE64", StatInode64);
        yield return ("__xstat", (p, b) => XStat(1, p, b));
        yield return ("__xstat64", (p, b) => XStat64(1, p, b));
    }

    /// <summary>
    /// Run one interop call, turning "this C library does not export that symbol" into an ordinary
    /// non-zero result so the next candidate is tried. Nothing here decides anything - the
    /// calibration still has to prove whichever entry point answers.
    /// </summary>
    private static int SafeCall(Func<int> call)
    {
        try { return call(); }
        catch (EntryPointNotFoundException) { return -1; }
        catch (DllNotFoundException) { return -1; }
    }

    private static byte[] NullTerminated(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var terminated = new byte[bytes.Length + 1];
        Array.Copy(bytes, terminated, bytes.Length);
        return terminated;
    }

    private static ulong ReadUnsigned(byte[] buffer, int offset, int width) => width switch
    {
        2 => BitConverter.ToUInt16(buffer, offset),
        4 => BitConverter.ToUInt32(buffer, offset),
        8 => BitConverter.ToUInt64(buffer, offset),
        _ => throw new ArgumentOutOfRangeException(nameof(width)),
    };

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat(byte[] path, byte[] statBuffer);

    [DllImport("libc", EntryPoint = "stat64", SetLastError = true)]
    private static extern int Stat64(byte[] path, byte[] statBuffer);

    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int StatInode64(byte[] path, byte[] statBuffer);

    [DllImport("libc", EntryPoint = "__xstat", SetLastError = true)]
    private static extern int XStat(int version, byte[] path, byte[] statBuffer);

    [DllImport("libc", EntryPoint = "__xstat64", SetLastError = true)]
    private static extern int XStat64(int version, byte[] path, byte[] statBuffer);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(byte[] existingPath, byte[] newPath);

    // ------------------------------------------------------- the calibration, as pure code ----

    /// <summary>
    /// The position search the Unix calibration performs, exposed so it can be exercised on ANY build
    /// machine against synthetic buffers. This is the part that decides where the link count lives,
    /// and it is the part that must not be wrong; the interop around it only fetches bytes.
    /// </summary>
    internal static IReadOnlyList<(int Offset, int Width)> LocateCountingField(
        byte[] first, byte[] second, byte[] third)
    {
        var fields = new List<(int Offset, int Width)>();
        var length = Math.Min(first.Length, Math.Min(second.Length, third.Length));
        foreach (var width in new[] { 2, 4, 8 })
        {
            for (var offset = 0; offset + width <= length; offset++)
            {
                if (ReadUnsigned(first, offset, width) == 1
                    && ReadUnsigned(second, offset, width) == 2
                    && ReadUnsigned(third, offset, width) == 3)
                {
                    fields.Add((offset, width));
                }
            }
        }
        return fields;
    }
}
