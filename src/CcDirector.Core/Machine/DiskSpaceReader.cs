using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// Reads free and total space on every FIXED drive of this machine, for the DISK bar the owner brought back on
/// 7 November 2026 (see <see cref="ResourceSample"/>). Each platform probe calls <see cref="ReadThisMachine"/> once per
/// five-second sample, on the sampler's own thread, never the user-interface thread.
///
/// WHICH DRIVES. Only the ones that can run out under the owner's feet and can be read without waiting on anything:
/// <list type="bullet">
/// <item>Windows: every drive letter whose type is Fixed. Network and removable drives are never touched - a read of
/// a network drive waits on the network, and of a removable one on the device.</item>
/// <item>Linux: every mounted filesystem backed by a real block device under <c>/dev/</c>, mounted read-write,
/// each device once. That leaves out network filesystems (their source is a server, not <c>/dev/</c>), memory
/// filesystems and container overlays, snap packages (read-only <c>/dev/loop</c> images that are always 100% full
/// and would show red forever), the small <c>/boot</c> and <c>/boot/efi</c> partitions (a 1 GB boot partition is
/// always under 10 GB and would show red forever, and nothing a user does fills it), and removable media mounted
/// under <c>/media</c> or <c>/run/media</c>.</item>
/// <item>Linux also leaves out FUSE filesystems (type <c>fuse</c> or <c>fuse.*</c>; <c>fuseblk</c>, a real disk
/// through FUSE, stays): one with no source name mounts as <c>/dev/fuse</c>, is network-backed as often as not, and a
/// read of it can wait. And a mount point that is not a directory - a host file bind-mounted into a container, such as
/// <c>/etc/hosts</c> - is not a drive.</item>
/// <item>macOS: the startup volume <c>/</c> only. On APFS every system volume shares one container, so <c>/</c>
/// reports the container's free space, which is the figure that runs out. Volumes under <c>/Volumes</c> are not read:
/// .NET cannot tell a read-only disk image (Fixed, 0 bytes free - red forever) from a real disk there, and two volumes
/// of one APFS container would report the same free space twice.</item>
/// </list>
///
/// A DRIVE THAT CANNOT BE READ SAYS SO. The read of each drive is the probe's boundary with the operating system, and
/// the probe contract (<see cref="IResourceProbe.Read"/>) forbids it to throw, so a refusal becomes that one drive's
/// <see cref="DriveSpaceReading.Unreadable"/> with the system's own message, and the other drives still read. A drive
/// the system says is zero bytes in size is unreadable too: no drive is, and the Gateway refuses such a figure.
///
/// LOGGED ONCE PER CHANGE. A drive that cannot be read is read again every five seconds; its reason is written to the
/// log when it first appears and again only when it changes, never on every read.
/// </summary>
public static class DiskSpaceReader
{
    /// <summary>
    /// Every fixed drive on the machine the Director is running on. Never throws (the probe contract): a failure to
    /// list the drives is the whole set's unreadable reason.
    /// </summary>
    public static DiskSpaceSet ReadThisMachine()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ReadWindows();
            if (OperatingSystem.IsLinux()) return ReadLinux();
            if (OperatingSystem.IsMacOS()) return ReadMacOs();
            return DiskSpaceSet.Unreadable($"free space is read on Windows, Linux and macOS; this operating system is {Environment.OSVersion}");
        }
        catch (Exception ex)
        {
            // not-an-error: the meter shows the drives as "cannot read" with this reason
            var reason = $"the drives could not be listed: {ex.GetType().Name}: {ex.Message}";
            LogOnChange("listing", reason);
            return DiskSpaceSet.Unreadable(reason);
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LastLogged = new(StringComparer.Ordinal);

    /// <summary>Writes <paramref name="message"/> for <paramref name="key"/> when it differs from the last one written.</summary>
    private static void LogOnChange(string key, string message)
    {
        if (LastLogged.TryGetValue(key, out var last) && last == message) return;
        LastLogged[key] = message;
        FileLog.Write($"[DiskSpaceReader] {key}: {message} (written again only if this changes)");
    }

    /// <summary>
    /// Reads each named drive through <paramref name="read"/>, turning a refusal of one drive into that drive's
    /// unreadable reading. Public so the per-drive failure rule is tested without a machine.
    /// </summary>
    /// <param name="drives">Each drive's display name and the path to read it through, in the order to report them.</param>
    /// <param name="read">Returns (total, free) for a path. May throw for a drive it cannot read.</param>
    public static DiskSpaceSet ReadEach(IReadOnlyList<(string Name, string Path)> drives, Func<string, (ulong Total, ulong Free)> read)
    {
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(read);
        var readings = new List<DriveSpaceReading>(drives.Count);
        foreach (var (name, path) in drives)
        {
            try
            {
                var (total, free) = read(path);
                string? impossible = total == 0
                    ? "the system said the drive is 0 bytes in size, which cannot be true"
                    : free > total
                        ? $"the system said {free} bytes are free of {total}, which cannot be true"
                        : null;
                if (impossible is null)
                {
                    readings.Add(DriveSpaceReading.Read(name, total, free));
                    continue;
                }
                LogOnChange($"drive {name} ({path})", impossible);
                readings.Add(DriveSpaceReading.Unreadable(name, impossible));
            }
            catch (Exception ex)
            {
                // not-an-error: an unreadable drive is shown on the meter as "cannot read" with this reason
                LogOnChange($"drive {name} ({path})", ex.Message);
                readings.Add(DriveSpaceReading.Unreadable(name, ex.Message));
            }
        }
        return DiskSpaceSet.Read(readings);
    }

    /// <summary>
    /// Picks the Linux filesystems to read from the text of <c>/proc/self/mounts</c> (see the class summary for which
    /// and why). Each line is "device mount-point type options dump pass", with spaces in a path written as \040.
    /// Returns (display name, mount point) pairs, each device once, at its shortest mount point.
    /// </summary>
    /// <param name="mountsText">The mount table.</param>
    /// <param name="isDirectory">Whether a mount point is a directory; a bind-mounted file is not a drive.</param>
    public static IReadOnlyList<(string Name, string Path)> SelectLinuxMounts(string mountsText, Func<string, bool> isDirectory)
    {
        ArgumentNullException.ThrowIfNull(mountsText);
        ArgumentNullException.ThrowIfNull(isDirectory);
        var byDevice = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var raw in mountsText.Split('\n'))
        {
            var fields = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4) continue;
            var device = fields[0];
            var mountPoint = Unescape(fields[1]);
            var type = fields[2];
            var options = fields[3].Split(',');

            if (!device.StartsWith("/dev/", StringComparison.Ordinal)) continue;
            if (device.StartsWith("/dev/loop", StringComparison.Ordinal)) continue;
            if (options.Contains("ro")) continue;
            if (mountPoint == "/boot" || mountPoint.StartsWith("/boot/", StringComparison.Ordinal)) continue;
            if (mountPoint.StartsWith("/media/", StringComparison.Ordinal) || mountPoint.StartsWith("/run/media/", StringComparison.Ordinal)) continue;
            if (mountPoint.StartsWith("/snap/", StringComparison.Ordinal)) continue;
            if (type == "fuse" || type.StartsWith("fuse.", StringComparison.Ordinal)) continue;
            if (!isDirectory(mountPoint)) continue;

            if (byDevice.TryGetValue(device, out var existing))
            {
                if (mountPoint.Length < existing.Length) byDevice[device] = mountPoint;
                continue;
            }
            byDevice[device] = mountPoint;
            order.Add(device);
        }
        return order.Select(device => (byDevice[device], byDevice[device])).ToList();
    }

    private static string Unescape(string field) =>
        field.Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);

    private static DiskSpaceSet ReadWindows()
    {
        // A failure to list the drives is caught in ReadThisMachine and becomes the set's reason.
        var all = DriveInfo.GetDrives();

        // DriveType is read from the drive letter's registration and does not touch the device, so it is safe on a
        // network or removable drive; the size read that follows is only made for Fixed ones.
        var fixedDrives = all
            .Where(d => d.DriveType == DriveType.Fixed)
            .Select(d => (Name: d.Name.TrimEnd('\\', '/'), Path: d.Name))
            .ToList();
        return ReadEach(fixedDrives, ReadDriveInfo);
    }

    private static DiskSpaceSet ReadLinux()
    {
        // A failure to read the mount table is caught in ReadThisMachine and becomes the set's reason.
        var mounts = File.ReadAllText("/proc/self/mounts");
        return ReadEach(SelectLinuxMounts(mounts, Directory.Exists), ReadDriveInfo);
    }

    private static DiskSpaceSet ReadMacOs() => ReadEach([("/", "/")], ReadDriveInfo);

    private static (ulong Total, ulong Free) ReadDriveInfo(string path)
    {
        var drive = new DriveInfo(path);
        return ((ulong)drive.TotalSize, (ulong)drive.AvailableFreeSpace);
    }
}
