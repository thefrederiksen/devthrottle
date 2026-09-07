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
    /// <summary>What a cc-launcher process whose path could not be read has to be treated as.</summary>
    internal enum UnreadableVerdict
    {
        /// <summary>It could plausibly be ours, so the machine is undecidable until it can be read.</summary>
        CouldBeOurs,

        /// <summary>It cannot be ours - it has exited, or it belongs to another logon session.</summary>
        CannotBeOurs,
    }

    /// <summary>
    /// A cc-launcher process whose path could not be read: undecidable if it could plausibly be OURS,
    /// and ignored if it could not. Pure, so it can be driven with the inputs a real machine will not
    /// produce on demand.
    ///
    /// A NULL MODULE IS UNREADABLE, NOT AN EMPTY COMMAND LINE. The enumeration used to coalesce null
    /// to "", which <see cref="Ours"/> then drops for having no matching prefix - the exact fail-open
    /// the throw exists to close, surviving in the one shape that raises no exception.
    ///
    /// WHY THE SESSION IS CONSULTED. Making every unreadable process undecidable is right for one that
    /// might be ours and far too broad for one that cannot be. The enumeration is machine-wide, while
    /// the install is per-user under that user's own local application data - so on a machine with
    /// several people signed in, ONE other user's launcher (whose module a non-elevated read is
    /// refused) would make this user's launcher update undecidable on every pass, for ever, with
    /// nothing wrong on this user's machine at all.
    ///
    /// The residual limit, stated rather than hidden: the SAME user signed in twice has two sessions
    /// and could genuinely be running our binary in the other one. That case is missed. Narrowing
    /// further needs the process's owning account, which is not readable cheaply.
    /// </summary>
    internal static UnreadableVerdict Classify(string? commandLine, bool? hasExited, int? sessionId, int ourSession)
    {
        if (!string.IsNullOrEmpty(commandLine)) return UnreadableVerdict.CannotBeOurs;  // it was readable
        if (hasExited == true) return UnreadableVerdict.CannotBeOurs;                   // simply gone
        if (sessionId is null) return UnreadableVerdict.CouldBeOurs;                    // cannot even place it
        return sessionId == ourSession ? UnreadableVerdict.CouldBeOurs : UnreadableVerdict.CannotBeOurs;
    }

    /// <summary>Read a process property that may throw, as an absence rather than an exception.</summary>
    private static T? TryRead<T>(Func<T?> read) where T : struct
    {
        try { return read(); }
        catch { return null; }
    }

    /// <summary>Log the verdict, and add to the undecidable list only when it could be ours.</summary>
    private static void Note(int pid, UnreadableVerdict verdict, string why, List<string> unreadable)
    {
        if (verdict == UnreadableVerdict.CouldBeOurs)
        {
            EngineLog.Write($"[InstalledLauncherProcesses] pid={pid}: {why}; it could be ours, so the machine is undecidable");
            unreadable.Add($"{pid} ({why})");
            return;
        }
        EngineLog.Write($"[InstalledLauncherProcesses] pid={pid}: {why}, but it cannot be ours (exited, or another logon session)");
    }

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
            var ourSession = Process.GetCurrentProcess().SessionId;

            foreach (var p in Process.GetProcessesByName("cc-launcher"))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                    {
                        found.Add(new LauncherProcess(p.Id, path));
                        continue;
                    }

                    var verdict = Classify(path, TryRead<bool>(() => p.HasExited), TryRead<int>(() => p.SessionId), ourSession);
                    Note(p.Id, verdict, "its main module could not be named", unreadable);
                }
                catch (Exception ex)
                {
                    var verdict = Classify(null, TryRead<bool>(() => p.HasExited), TryRead<int>(() => p.SessionId), ourSession);
                    Note(p.Id, verdict, ex.Message, unreadable);
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
