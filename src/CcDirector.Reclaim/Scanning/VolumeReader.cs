using CcDirector.Core.Utilities;

namespace CcDirector.Reclaim.Scanning;

/// <summary>
/// Asks the volume a scan root sits on how big it is and how much of it is used. That answer is the
/// other half of every report: what the scan saw is only meaningful beside what the volume counts.
/// </summary>
public static class VolumeReader
{
    /// <summary>
    /// Read the volume that holds a path.
    ///
    /// A volume that will not answer is an expected outcome - a drive that is not ready, a path on a
    /// share the operating system will not size - so it comes back as a value with a reason rather
    /// than as an exception. The report then has no second side to its unseen-gap line, and says so by
    /// calling itself broken rather than by printing one side and letting the reader assume the other.
    /// </summary>
    /// <param name="path">Any path on the volume.</param>
    public static VolumeUsage Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path cannot be blank.", nameof(path));

        FileLog.Write($"[VolumeReader] Read: path={path}");

        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            FileLog.Write($"[VolumeReader] Read: path={path}, result=no volume root");
            return new VolumeUsage(false, path, 0, 0, 0, "the operating system does not place this path on any volume");
        }

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                FileLog.Write($"[VolumeReader] Read: path={path}, volume={root}, result=not ready");
                return new VolumeUsage(false, root, 0, 0, 0, "the volume is not ready");
            }

            var total = drive.TotalSize;
            var free = drive.TotalFreeSpace;
            var used = total - free;
            FileLog.Write($"[VolumeReader] Read: path={path}, volume={root}, total={total}, used={used}, free={free}");
            return new VolumeUsage(true, root, total, used, free, null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // The same expected-failure boundary as DirectoryReadResult, and the same reason for not
            // keeping the operating system's own words: they are translated, and every line printed
            // here is plain ASCII.
            FileLog.Write($"[VolumeReader] Read FAILED: path={path}, volume={root}, code={ex.HResult}");
            return new VolumeUsage(false, root, 0, 0, 0, $"the volume would not answer, code {ex.HResult}");
        }
    }


    /// <summary>
    /// The volume a path sits on, as the path itself names it, without asking the volume anything.
    ///
    /// This is the pure half of the two questions this class answers: <see cref="Read"/> asks the
    /// volume about itself, and this asks the path which volume it belongs to. Removal is a move on
    /// the same volume, and holding must sit on the volume the item sits on, so the refusal gate
    /// compares these two names - and it compares them without touching either volume, which is what
    /// makes the comparison answerable on any machine.
    /// </summary>
    /// <param name="path">Any path.</param>
    /// <exception cref="IOException">The path names no volume.</exception>
    public static string VolumeRootOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path cannot be blank.", nameof(path));

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                throw new IOException($"the path {path} names no volume");
            return root;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new IOException($"the path {path} would not resolve, so it names no volume");
        }
    }

    /// <summary>
    /// True when two paths sit on the same volume, compared by the volume roots their paths name.
    /// Compared without regard to letter case, which is how Windows and a default macOS volume
    /// compare two names.
    /// </summary>
    /// <param name="path">One path.</param>
    /// <param name="otherPath">The other path.</param>
    public static bool SameVolume(string path, string otherPath)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(VolumeRootOf(path), VolumeRootOf(otherPath), comparison);
    }

    /// <summary>True when a path is the root of its own volume.</summary>
    /// <param name="path">The path to test.</param>
    public static bool IsVolumeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path cannot be blank.", nameof(path));

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return false;

        return string.Equals(
            full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.Ordinal);
    }
}
