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
/// <item>macOS: the startup volume <c>/</c> - on APFS every system volume shares one container, so <c>/</c> reports
/// the container's free space, which is the figure that runs out - and each Fixed volume under <c>/Volumes</c>.</item>
/// </list>
///
/// A DRIVE THAT CANNOT BE READ SAYS SO. The read of each drive is the probe's boundary with the operating system, and
/// the probe contract (<see cref="IResourceProbe.Read"/>) forbids it to throw, so a refusal becomes that one drive's
/// <see cref="DriveSpaceReading.Unreadable"/> with the system's own message, and the other drives still read.
/// </summary>
public static class DiskSpaceReader
{
    /// <summary>Every fixed drive on the machine the Director is running on.</summary>
    public static DiskSpaceSet ReadThisMachine()
    {
        if (OperatingSystem.IsWindows()) return ReadWindows();
        if (OperatingSystem.IsLinux()) return ReadLinux();
        if (OperatingSystem.IsMacOS()) return ReadMacOs();
        return DiskSpaceSet.Unreadable($"free space is read on Windows, Linux and macOS; this operating system is {Environment.OSVersion}");
    }

    /// <summary>
    /// Reads each named drive through <paramref name="read"/>, turning a refusal of one drive into that drive's
    /// unreadable reading. Public so the per-drive failure rule is tested without a machine.
    /// </summary>
    /// <param name="drives">Each drive's display name and the path to read it through, in the order to report them.</param>
    /// <param name="read">Returns (total, free) for a path. May throw <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> for a drive it cannot read.</param>
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
                readings.Add(free > total
                    ? DriveSpaceReading.Unreadable(name, $"the system said {free} bytes are free of {total}, which cannot be true")
                    : DriveSpaceReading.Read(name, total, free));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLog.Write($"[DiskSpaceReader] ReadEach: drive {name} ({path}) unreadable: {ex.Message}");
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
    public static IReadOnlyList<(string Name, string Path)> SelectLinuxMounts(string mountsText)
    {
        ArgumentNullException.ThrowIfNull(mountsText);
        var byDevice = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var raw in mountsText.Split('\n'))
        {
            var fields = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4) continue;
            var device = fields[0];
            var mountPoint = Unescape(fields[1]);
            var options = fields[3].Split(',');

            if (!device.StartsWith("/dev/", StringComparison.Ordinal)) continue;
            if (device.StartsWith("/dev/loop", StringComparison.Ordinal)) continue;
            if (options.Contains("ro")) continue;
            if (mountPoint == "/boot" || mountPoint.StartsWith("/boot/", StringComparison.Ordinal)) continue;
            if (mountPoint.StartsWith("/media/", StringComparison.Ordinal) || mountPoint.StartsWith("/run/media/", StringComparison.Ordinal)) continue;
            if (mountPoint.StartsWith("/snap/", StringComparison.Ordinal)) continue;

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
        DriveInfo[] all;
        try
        {
            all = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[DiskSpaceReader] ReadWindows: the drives could not be listed: {ex.Message}");
            return DiskSpaceSet.Unreadable($"the drives could not be listed: {ex.Message}");
        }

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
        string mounts;
        try
        {
            mounts = File.ReadAllText("/proc/self/mounts");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[DiskSpaceReader] ReadLinux: /proc/self/mounts could not be read: {ex.Message}");
            return DiskSpaceSet.Unreadable($"the mount table /proc/self/mounts could not be read: {ex.Message}");
        }
        return ReadEach(SelectLinuxMounts(mounts), ReadDriveInfo);
    }

    private static DiskSpaceSet ReadMacOs()
    {
        DriveInfo[] all;
        try
        {
            all = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[DiskSpaceReader] ReadMacOs: the volumes could not be listed: {ex.Message}");
            return DiskSpaceSet.Unreadable($"the volumes could not be listed: {ex.Message}");
        }

        var volumes = new List<(string Name, string Path)> { ("/", "/") };
        volumes.AddRange(all
            .Where(d => d.DriveType == DriveType.Fixed && d.Name.StartsWith("/Volumes/", StringComparison.Ordinal))
            .Select(d => (d.Name.TrimEnd('/'), d.Name)));
        return ReadEach(volumes, ReadDriveInfo);
    }

    private static (ulong Total, ulong Free) ReadDriveInfo(string path)
    {
        var drive = new DriveInfo(path);
        return ((ulong)drive.TotalSize, (ulong)drive.AvailableFreeSpace);
    }
}
