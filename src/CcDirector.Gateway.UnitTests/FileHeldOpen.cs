using System.Runtime.InteropServices;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Does THIS process hold <c>path</c> open? The observation a "dispose releases the file" test rests on, made on every
/// operating system rather than only on Windows.
///
/// WHY IT IS NEEDED. On Windows a file held open blocks deleting its folder, so a test could observe a held file by
/// trying the delete. On Linux and macOS the delete succeeds whatever is open, so those tests were Windows only and were
/// skipped everywhere else - and a skipped test reads like a pass in every report. Here each system is asked directly:
/// Linux through the process's own descriptor table, <c>/proc/self/fd</c>; macOS through <c>libproc</c>, which lists the
/// process's descriptors and the path of each; Windows by the delete the tests always used.
/// </summary>
internal static class FileHeldOpen
{
    /// <summary>True when a descriptor or handle of this process is open on <paramref name="path"/>.</summary>
    public static bool ByThisProcess(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsLinux()) return LinuxDescriptorPaths().Any(p => p == full);
        if (OperatingSystem.IsMacOS()) return MacDescriptorPaths().Any(p => p == full);
        if (OperatingSystem.IsWindows()) return WindowsHeld(full);
        throw new PlatformNotSupportedException($"no way to ask {RuntimeInformation.OSDescription} which files this process holds open");
    }

    private static IEnumerable<string> LinuxDescriptorPaths()
    {
        foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            string? target = null;
            try { target = File.ResolveLinkTarget(fd, returnFinalTarget: false)?.FullName; }
            catch (IOException) { } // the descriptor closed between the listing and the read
            if (target is not null) yield return target;
        }
    }

    /// <summary>On Windows an open handle on a file makes deleting it fail; nothing is deleted when it is held.</summary>
    private static bool WindowsHeld(string full)
    {
        try
        {
            using var exclusive = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    // ---- macOS: libproc (non-variadic, so safe to call on Apple silicon) -------------------------------------------

    private const int ProcPidListFds = 1;          // PROC_PIDLISTFDS
    private const int ProcPidFdVnodePathInfo = 2;  // PROC_PIDFDVNODEPATHINFO
    private const uint ProxFdTypeVnode = 1;        // PROX_FDTYPE_VNODE
    private const int ProcFdInfoSize = 8;          // struct proc_fdinfo { int32 fd; uint32 type; }
    private const int VnodeFdInfoWithPathSize = 1200; // struct vnode_fdinfowithpath
    private const int VnodePathOffset = 24 + 152;  // proc_fileinfo (24) + vnode_info (152), then char vip_path[1024]

    [DllImport("libproc.dylib")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[]? buffer, int bufferSize);

    [DllImport("libproc.dylib")]
    private static extern int proc_pidfdinfo(int pid, int fd, int flavor, byte[] buffer, int bufferSize);

    private static IEnumerable<string> MacDescriptorPaths()
    {
        var pid = Environment.ProcessId;
        var needed = proc_pidinfo(pid, ProcPidListFds, 0, null, 0);
        if (needed <= 0) throw new InvalidOperationException("libproc could not list this process's descriptors");
        var list = new byte[needed * 2];
        var got = proc_pidinfo(pid, ProcPidListFds, 0, list, list.Length);
        var info = new byte[VnodeFdInfoWithPathSize];
        for (var i = 0; i + ProcFdInfoSize <= got; i += ProcFdInfoSize)
        {
            if (BitConverter.ToUInt32(list, i + 4) != ProxFdTypeVnode) continue;
            var fd = BitConverter.ToInt32(list, i);
            if (proc_pidfdinfo(pid, fd, ProcPidFdVnodePathInfo, info, info.Length) != VnodeFdInfoWithPathSize) continue;
            var end = Array.IndexOf(info, (byte)0, VnodePathOffset);
            var path = System.Text.Encoding.UTF8.GetString(info, VnodePathOffset, (end < 0 ? info.Length : end) - VnodePathOffset);
            if (path.Length > 0) yield return path;
        }
    }
}
