using System.Diagnostics;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Which launcher processes on this machine belong to an install, and how to enumerate them.
///
/// It is one class because two different jobs have to agree about it exactly. The uninstaller
/// (<see cref="LauncherStopper"/>) stops every launcher of the install it is removing, and the
/// Director's ownership of the launcher's update (<see cref="LauncherUpdateOwner"/>) stops the one
/// launcher whose binary it is about to replace. If those two ever disagreed about what "ours" means,
/// one of them would act on a process the other says is somebody else's - a developer's launcher run
/// from a repository checkout, or a second install serving a different storage root.
/// </summary>
public static class InstalledLauncherProcesses
{
    /// <summary>
    /// The launcher processes running from <paramref name="launcherDir"/>.
    ///
    /// Matched on the command line beginning with that directory plus a separator - a bare prefix
    /// would also match a sibling like "&lt;launcherDir&gt;-dev", which is somebody else's launcher.
    /// The WHOLE command line is compared rather than a parsed executable because macOS <c>ps</c>
    /// does not quote paths and the installed launcher lives under a path containing a space.
    /// </summary>
    public static List<LauncherProcess> Ours(string launcherDir, IEnumerable<LauncherProcess> all)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherDir);
        ArgumentNullException.ThrowIfNull(all);

        var dir = launcherDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefixes = new[] { dir + Path.DirectorySeparatorChar, dir + Path.AltDirectorySeparatorChar };

        return all
            .Where(p => !string.IsNullOrEmpty(p.CommandLine)
                        && prefixes.Any(prefix => p.CommandLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Every cc-launcher process, with its full command line (unfiltered - <see cref="Ours"/> does the
    /// scoping). Windows reads the main module; macOS and Linux ask ps, whose output is NOT quoted, so
    /// the whole argument string is kept and callers compare prefixes rather than parsing an
    /// executable out of it.
    /// </summary>
    public static IReadOnlyList<LauncherProcess> List()
    {
        var found = new List<LauncherProcess>();

        if (OperatingSystem.IsWindows())
        {
            foreach (var p in Process.GetProcessesByName("cc-launcher"))
            {
                try { found.Add(new LauncherProcess(p.Id, p.MainModule?.FileName ?? "")); }
                catch (Exception ex) { EngineLog.Write($"[InstalledLauncherProcesses] pid={p.Id}: {ex.Message}"); }
                finally { p.Dispose(); }
            }
            return found;
        }

        var psi = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-axo");
        psi.ArgumentList.Add("pid=,args=");
        using var ps = Process.Start(psi)
                       ?? throw new InvalidOperationException("could not run /bin/ps");
        var output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(5000);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            var space = trimmed.IndexOf(' ');
            if (space <= 0) continue;
            if (!int.TryParse(trimmed[..space], out var pid)) continue;

            var commandLine = trimmed[(space + 1)..].Trim();
            if (!commandLine.Contains("cc-launcher", StringComparison.Ordinal)) continue;
            found.Add(new LauncherProcess(pid, commandLine));
        }
        return found;
    }
}
