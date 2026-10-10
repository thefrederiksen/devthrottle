namespace CcDirector.Setup.Engine;

/// <summary>
/// The per-user install must belong to the user. On macOS, a launch agent whose program, folder or
/// log files belong to root is refused by launchd before it runs a line: "78: EX_CONFIG", job state
/// "spawn failed", an empty stderr, and only the system log says why - "Service could not initialize:
/// posix_spawn(...), error 0xd - Permission denied" - because launchd opens StandardOutPath and
/// StandardErrorPath as the user before it starts the program (#3411). Reproduced on macOS 15.7.9 and
/// 26.6.2 by making the launcher's log folder, its log files, or the whole cc-director folder root-owned.
///
/// Root-owned files get there when an install is run with sudo: macOS keeps HOME under sudo, so every
/// folder the install creates lands in the user's own home owned by root. The install refuses to run
/// as root (<see cref="RefuseRootMessage"/>); this repairs a machine where it already happened.
///
/// The repair is one administrator prompt - the standard macOS password dialog - that hands the files
/// back with chown. It is offered only where a person is at the screen (the setup wizard). Anywhere else,
/// and when the prompt is declined, the install stops with the exact command that does the same thing,
/// because a launcher that launchd refuses cannot be worked around.
/// </summary>
public static class MacFileOwnership
{
    /// <summary>Runs a short command and returns its exit code and combined output.</summary>
    public delegate (int Exit, string Output) CommandRunner(string executable, string arguments);

    /// <summary>
    /// What must belong to the user. <see cref="Trees"/> are ours and are checked and repaired all the way
    /// down. <see cref="Folders"/> are shared parents (~/Applications, ~/Library/LaunchAgents, ~/.local/bin) that a sudo
    /// run may have CREATED: only the folder itself is checked and repaired, never what other apps keep in it.
    /// A root-owned parent matters on its own: replacing anything inside it needs the parent to be writable.
    /// </summary>
    public sealed record Targets(IReadOnlyList<string> Trees, IReadOnlyList<string> Folders);

    /// <summary>
    /// The runner for production use. The repair waits on a person typing a password into the macOS
    /// prompt, so it gets ProcessRunner's long default bound, never a short command timeout.
    /// </summary>
    public static readonly CommandRunner DefaultRunner = (exe, args) => ProcessRunner.Run(exe, args);

    /// <summary>The setup wizard's bundle, which install-mac.sh places beside the Director.</summary>
    public const string SetupAppName = "DevThrottle Setup.app";

    /// <summary>Everything an install writes in the user's home, and the parents it may have created.</summary>
    public static Targets InstallTargets(InstallLayout layout, string launchAgentPlistPath) => new(
        [
            layout.LocalRoot,
            Path.Combine(layout.MacAppsDir, "Director.app"),
            Path.Combine(layout.MacAppsDir, "CC Director.app"),
            Path.Combine(layout.MacAppsDir, SetupAppName),
            launchAgentPlistPath,
            // The shell start-up files the install appends its PATH block to (InstallFinalizer). Files, so the
            // recursive repair changes only the file itself.
            Path.Combine(Path.GetDirectoryName(layout.MacAppsDir)!, ".zshrc"),
            Path.Combine(Path.GetDirectoryName(layout.MacAppsDir)!, ".bash_profile"),
        ],
        [
            layout.MacAppsDir,
            Path.GetDirectoryName(launchAgentPlistPath)!,
            // The cc-* command links: the Python tools step creates this folder and replaces links in it.
            Path.GetDirectoryName(layout.MacUserBinDir)!,
            layout.MacUserBinDir,
        ]);

    /// <summary>What the install says, and does, when it is run as root on macOS.</summary>
    public const string RefuseRootMessage =
        "Do not run the DevThrottle installer with sudo. It installs into your own user folder, and files "
        + "created as root there stop macOS from starting DevThrottle. Run the same command again without sudo.";

    /// <summary>How many offending paths a message names; the count says how many there are in all.</summary>
    internal const int PathsNamed = 5;

    /// <summary>
    /// Make sure every target belongs to the current user. Runs BEFORE an install writes anything: a
    /// root-owned install folder fails the first write (a log file, a placed binary) long before the launcher
    /// step is reached. When something does not belong to the user and <paramref name="offerPrompt"/> is
    /// true, one administrator prompt repairs it. Returns null when everything is the user's, or a message
    /// saying why the install cannot go on, which always ends with the command that repairs it by hand.
    /// </summary>
    /// <param name="offerPrompt">True only where a person is at the screen. A headless caller (the command
    /// line, an agent, an SSH session) must never wait on a dialog nobody can see.</param>
    /// <param name="step">Receives one line per thing done, for the caller's log and steps list.</param>
    public static string? EnsureOwnedByUser(CommandRunner run, Targets targets, bool offerPrompt, Action<string> step)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(step);

        var (uidExit, uidOutput) = run("/usr/bin/id", "-u");
        var (gidExit, gidOutput) = run("/usr/bin/id", "-g");
        var uid = uidOutput.Trim();
        var gid = gidOutput.Trim();
        if (uidExit != 0 || gidExit != 0 || !int.TryParse(uid, out _) || !int.TryParse(gid, out _))
            return $"Could not resolve the user and group id (exit {uidExit}/{gidExit}), so the install cannot check who owns its files.";

        IReadOnlyList<string> notOwned;
        try
        {
            notOwned = FindNotOwned(run, targets, uid);
        }
        catch (Exception ex)
        {
            return $"Could not check who owns the DevThrottle files: {ex.Message}";
        }

        if (notOwned.Count == 0)
        {
            step("file ownership: every install file belongs to this user");
            return null;
        }

        step($"file ownership: {notOwned.Count} path(s) do not belong to this user: {Describe(notOwned)}");

        string repairFailure;
        if (!offerPrompt)
        {
            repairFailure = "this command does not ask for a password";
        }
        else if (Repair(run, targets, uid, gid) is { } failure)
        {
            repairFailure = failure;
        }
        else
        {
            try
            {
                notOwned = FindNotOwned(run, targets, uid);
            }
            catch (Exception ex)
            {
                return $"Repaired the file ownership, but could not check the result: {ex.Message}";
            }
            if (notOwned.Count == 0)
            {
                step("file ownership: repaired with an administrator prompt");
                return null;
            }
            repairFailure = $"{notOwned.Count} path(s) still do not belong to you after the repair: {Describe(notOwned)}";
        }

        step($"file ownership: NOT repaired - {repairFailure}");
        return "Some DevThrottle files belong to another user (usually because an install was run with sudo), and macOS "
               + $"will not start DevThrottle until they belong to you. They were not repaired: {repairFailure}. "
               + $"To repair them yourself, run this in Terminal and then run the install again: {ManualCommand(targets)}";
    }

    /// <summary>
    /// The targets the user does not own: anything under a tree (find), and each shared folder itself (stat). Targets that
    /// do not exist are skipped. A search that fails outright throws: "could not check" must never read as
    /// "all owned".
    /// </summary>
    public static IReadOnlyList<string> FindNotOwned(CommandRunner run, Targets targets, string uid)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);

        var existing = Existing(targets);
        var found = new List<string>();
        if (existing.Trees.Count > 0)
            found.AddRange(Find(run, existing.Trees, $"! -user {uid} -print"));
        if (existing.Folders.Count > 0)
            found.AddRange(FoldersNotOwned(run, existing.Folders, uid));
        return found;
    }

    /// <summary>
    /// The shared folders the user does not own, read with stat. Not find: macOS find will not report even
    /// the folder itself when the user cannot open it ("Permission denied"), and a root-owned folder with
    /// mode 700 is exactly the case being checked.
    /// </summary>
    private static List<string> FoldersNotOwned(CommandRunner run, IReadOnlyList<string> folders, string uid)
    {
        var quoted = string.Join(' ', folders.Select(p => $"\"{p}\""));
        var (exit, output) = run("/usr/bin/stat", $"-f %u {quoted}");
        var owners = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (exit != 0 || owners.Length != folders.Count)
            throw new InvalidOperationException($"could not check who owns {string.Join(", ", folders)} (stat exit {exit}): {output.Trim()}");
        return folders.Where((_, i) => owners[i] != uid).ToList();
    }

    private static List<string> Find(CommandRunner run, IReadOnlyList<string> roots, string expression)
    {
        var quoted = string.Join(' ', roots.Select(p => $"\"{p}\""));
        var (exit, output) = run("/usr/bin/find", $"{quoted} {expression}");
        var found = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => roots.Any(root => line.StartsWith(root, StringComparison.Ordinal)))
            .ToList();

        // find exits 1 when it could not read part of the tree. A folder it cannot read is itself listed
        // (it is not ours), so that exit is only a failure when nothing was found to explain it.
        if (exit != 0 && found.Count == 0)
            throw new InvalidOperationException($"could not check who owns the install files (find exit {exit}): {output.Trim()}");

        return found;
    }

    private static Targets Existing(Targets targets) => new(
        targets.Trees.Where(p => File.Exists(p) || Directory.Exists(p)).ToList(),
        targets.Folders.Where(Directory.Exists).ToList());

    /// <summary>
    /// Hand the targets back to the user with one macOS administrator prompt. Every named target is passed,
    /// existing or not: a target inside a root-owned parent is invisible to the user until the parent is
    /// repaired, and the repair runs as root, which can see it. Returns null when the prompt ran, or why not.
    /// </summary>
    public static string? Repair(CommandRunner run, Targets targets, string uid, string gid)
    {
        ArgumentNullException.ThrowIfNull(run);

        var scriptPath = Path.Combine(Path.GetTempPath(), $"devthrottle-repair-{Guid.NewGuid():N}.applescript");
        try
        {
            File.WriteAllText(scriptPath, AppleScript(targets, uid, gid));
            var (exit, output) = run("/usr/bin/osascript", $"\"{scriptPath}\"");
            EngineLog.Write($"[MacFileOwnership] Repair: osascript exit={exit} {output.Trim()}");
            if (exit == 0) return null;
            return output.Contains("-128", StringComparison.Ordinal)
                ? "the password prompt was cancelled"
                : $"the repair did not run (osascript exit {exit}): {output.Trim()}";
        }
        catch (Exception ex)
        {
            return $"the repair could not be started ({ex.GetType().Name}): {ex.Message}";
        }
        finally
        {
            // The script holds nothing secret; a file left behind must not turn a repair that worked into a failure.
            try { File.Delete(scriptPath); }
            catch (Exception ex) { EngineLog.Write($"[MacFileOwnership] could not remove {scriptPath} FAILED: {ex.Message}"); }
        }
    }

    /// <summary>The AppleScript that runs chown with administrator privileges. Pure, for tests.</summary>
    public static string AppleScript(Targets targets, string uid, string gid)
    {
        if (targets.Trees.Count + targets.Folders.Count == 0) throw new ArgumentException("nothing to repair", nameof(targets));
        if (uid.Length == 0 || gid.Length == 0 || !uid.All(char.IsAsciiDigit) || !gid.All(char.IsAsciiDigit))
            throw new ArgumentException($"user and group ids must be numbers (got '{uid}', '{gid}')");

        static string Quoted(IEnumerable<string> paths) =>
            string.Join(" & \" \" & ", paths.Select(p => $"quoted form of \"{Escape(p)}\""));

        // Folders first: a root-owned parent the user cannot open hides what is inside it, so the trees are
        // named, not discovered, and repaired after their parents by a loop that runs as root and so can see them.
        var parts = new List<string>();
        if (targets.Folders.Count > 0)
            parts.Add($"\"/bin/sh -c \" & quoted form of \"{Escape(ChownExisting(recursive: false))}\" & \" {uid}:{gid} \" & {Quoted(targets.Folders)}");
        if (targets.Trees.Count > 0)
            parts.Add($"\"/bin/sh -c \" & quoted form of \"{Escape(ChownExisting(recursive: true))}\" & \" {uid}:{gid} \" & {Quoted(targets.Trees)}");
        return $"do shell script {string.Join(" & \"; \" & ", parts)} "
               + $"with prompt \"{Escape(PromptText)}\" with administrator privileges\n";
    }

    /// <summary>
    /// The exact command a person can run instead of the prompt. Every path is single-quoted for the shell,
    /// so nothing in a path ($, backtick, a quote) can run as a command when it is pasted under sudo.
    /// </summary>
    public static string ManualCommand(Targets targets)
    {
        // The ids are expanded by the person's own shell, before sudo, so they are the person's, not root's.
        const string ids = "\"$(id -u):$(id -g)\"";
        var commands = new List<string>();
        if (targets.Folders.Count > 0)
            commands.Add($"sudo /bin/sh -c {ShellQuote(ChownExisting(recursive: false))} {ids} {string.Join(' ', targets.Folders.Select(ShellQuote))}");
        if (targets.Trees.Count > 0)
            commands.Add($"sudo /bin/sh -c {ShellQuote(ChownExisting(recursive: true))} {ids} {string.Join(' ', targets.Trees.Select(ShellQuote))}");
        return string.Join(" && ", commands);
    }

    /// <summary>
    /// A shell loop that hands each path that EXISTS to the owner in $0 and fails on a real chown failure.
    /// Run as root, it sees paths a root-owned parent hides from the user; a named path that does not exist
    /// is skipped, because chown refuses one even with -f.
    /// </summary>
    internal static string ChownExisting(bool recursive) =>
        $"for p in \"$@\"; do if [ -e \"$p\" ]; then /usr/sbin/chown {(recursive ? "-R " : "")}\"$0\" \"$p\" || exit 1; fi; done";

    /// <summary>POSIX single quoting: it's becomes 'it'\''s'.</summary>
    internal static string ShellQuote(string s) => "'" + s.Replace("'", @"'\''") + "'";

    /// <summary>
    /// A plain macOS alert, for the setup wizard when it must stop before its own window exists. The text is
    /// passed as arguments to the script, never spliced into it, so nothing in a path can run.
    /// </summary>
    public static void ShowAlert(string title, string message)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
        foreach (var arg in new[]
                 {
                     "-e", "on run argv",
                     "-e", "display alert (item 1 of argv) message (item 2 of argv) as critical",
                     "-e", "end run",
                     title, message,
                 })
            psi.ArgumentList.Add(arg);
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit();
    }

    /// <summary>The offending paths for a message: the first few, and how many there are in all.</summary>
    public static string Describe(IReadOnlyList<string> notOwned)
    {
        var named = string.Join(", ", notOwned.Take(PathsNamed));
        return notOwned.Count > PathsNamed ? $"{named} and {notOwned.Count - PathsNamed} more" : named;
    }

    internal const string PromptText =
        "DevThrottle needs to repair its own files. Some were created by an install run with sudo, "
        + "and macOS will not start DevThrottle until they belong to you again.";

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
