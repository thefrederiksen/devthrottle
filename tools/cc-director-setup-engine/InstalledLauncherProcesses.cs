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
    /// The answer to "which launcher processes are on this machine", given what could be read and what
    /// could not.
    ///
    /// A LAUNCHER WE COULD NOT READ IS NOT A LAUNCHER THAT IS NOT THERE. The enumeration used to
    /// swallow a per-process failure and carry on with a shorter list, which reads downstream as "that
    /// process is somebody else's" - and both callers treat "not ours" as "nothing to stop". So a
    /// launcher whose command line could not be read would be left running while the uninstaller's stop
    /// reported a clean machine, and while the Director's swap replaced the binary out from under it.
    ///
    /// There is no honest partial answer, because the question is exactly WHICH processes are ours and
    /// one that cannot be placed makes that undecidable. Both callers already handle an unreadable list
    /// correctly - the stop refuses to certify, the update owner holds the pass - so the way to reach
    /// that handling is to say the list could not be read.
    ///
    /// Separated from the enumeration so it can be driven with a known-bad input, which a real
    /// unreadable process cannot be manufactured to provide.
    /// </summary>
    /// <exception cref="InvalidOperationException">Anything at all could not be read.</exception>
    public static IReadOnlyList<LauncherProcess> Resolve(
        IReadOnlyList<LauncherProcess> readable, IReadOnlyList<string> unreadable)
    {
        ArgumentNullException.ThrowIfNull(readable);
        ArgumentNullException.ThrowIfNull(unreadable);

        if (unreadable.Count > 0)
            throw new InvalidOperationException(
                $"{unreadable.Count} cc-launcher process(es) could not be read, so which processes belong to "
                + $"this install cannot be established: {string.Join("; ", unreadable)}");

        return readable;
    }

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
    ///
    /// THROWS rather than returning a shorter list when a cc-launcher process cannot be read at all -
    /// see the comment at that throw. Callers must treat the exception as "undecidable", never as
    /// "none running".
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A cc-launcher process is running whose command line could not be read, so which processes
    /// belong to this install cannot be established.
    /// </exception>
    public static IReadOnlyList<LauncherProcess> List()
    {
        var found = new List<LauncherProcess>();

        if (OperatingSystem.IsWindows())
        {
            var unreadable = new List<string>();
            foreach (var p in Process.GetProcessesByName("cc-launcher"))
            {
                try { found.Add(new LauncherProcess(p.Id, p.MainModule?.FileName ?? "")); }
                catch (Exception ex)
                {
                    EngineLog.Write($"[InstalledLauncherProcesses] pid={p.Id}: {ex.Message}");
                    unreadable.Add($"{p.Id} ({ex.Message})");
                }
                finally { p.Dispose(); }
            }

            return Resolve(found, unreadable);
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
