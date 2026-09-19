using Microsoft.Win32;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Setup;

/// <summary>What a repair attempt did, for the panel to report back verbatim.</summary>
public sealed record PathRepairResult(bool Succeeded, string Detail);

/// <summary>
/// Keeps the MASTER tool directory - the one installed copy of the DevThrottle tools on this machine,
/// <c>&lt;machine root&gt;\bin</c> - first on the search path, and takes off the path every tools
/// directory the master replaces.
///
/// WHICH DIRECTORY IS OURS. The master is. A tools directory sitting inside a Director's own folder of
/// the same machine root is a copy the master replaces, and it comes off the path.
///
/// This used to be stated the other way round, and the wording is worth naming because it is exactly
/// backwards from the truth: the machine root's <c>bin</c> was called the "legacy flat bin" that the
/// move to per-Director folders had superseded, and each Director's own <c>bin</c> was called ours.
/// Nothing ever made a Director's folder an install root - every Director points the CC_DIRECTOR_ROOT
/// setting at its own folder and hands it to every session, so an installer or a tools repair run from
/// inside one took that folder for the whole machine and filled it with a complete copy of the tools.
/// One computer finished with seven copies, each with its own interpreter, each ageing at its own pace,
/// and whichever one happened to be first on the search path was the one that answered. On 17 and 18
/// September 2026 the copy that answered was two months old and told every agent to set a setting for a
/// part of the product that no longer exists.
///
/// WHAT IT WILL NOT TOUCH. A DevThrottle tools directory belonging to a DIFFERENT machine root - a test
/// rig serving its own throwaway root, a second install somewhere else on the disk - is not ours to
/// tidy away. A rig keeping its own tools is what makes a rig safe to run at all.
///
/// NOTHING ON DISK IS TOUCHED HERE. This only ever edits path entries. A copy that is off the path but
/// still on disk can no longer answer a command, which is the whole point; removing the folders
/// themselves is a separate step.
///
/// FOUR THINGS THIS GETS RIGHT, all of which are easy to get wrong:
///
/// 1. It writes the RAW registry value, not the expanded one. Reading the user path through
///    Environment.GetEnvironmentVariable returns it with %USERPROFILE% and friends already expanded;
///    writing that back would bake today's expansion into the user's path permanently and silently
///    destroy every variable reference in it. This reads with DoNotExpandEnvironmentNames and writes
///    back the same RegistryValueKind it found.
///
/// 2. It updates THIS PROCESS's path as well as the saved one. A running process inherited its
///    environment at launch, so saving alone would leave the Director handing its sessions the stale
///    tool until it restarted - the badge would not clear, and the button would read as broken.
///    Sessions ALREADY running keep the old path; nothing can repair those in place, and the panel
///    says so rather than implying otherwise.
///
/// 3. It refuses to promote a directory that holds no cc-devthrottle. The old guard asked only whether
///    the directory EXISTED - and on the machine this was written for it existed and was empty, because
///    those tools had never been installed. The path was reordered perfectly, resolution fell through
///    the empty directory to the same stale install, and the repair reported the failure it had just
///    been asked to fix. A container is not its contents.
///
/// 4. A Director serving a throwaway root does not write the SAVED path. The saved path is permanent
///    machine state and belongs to the install at the machine's default root; a rig, a wizard harness
///    or an unpacked bundle that wrote itself there would outlive its own directory by months. There is
///    one of those on the machine that prompted this work. A rig still repairs its own running path,
///    because that is the path its own sessions inherit.
/// </summary>
public static class FleetToolPathRepair
{
    private const string UserEnvironmentKey = "Environment";
    private const string PathValueName = "Path";

    /// <summary>The command whose presence makes a directory a DevThrottle tool directory.</summary>
    private const string ToolName = "cc-devthrottle";

    /// <summary>
    /// The repair the Director runs at every start, before it opens or restores any session.
    ///
    /// It is not a button and it is not offered. A Director that has just started is the one moment
    /// where both paths can be put right at once: the SAVED path, so every shell and every later
    /// session on this machine reaches the master, and this process's OWN RUNNING path, so the sessions
    /// this Director is about to start reach it too. Repairing only the saved one would leave every
    /// session of this run on whatever the Director inherited at launch.
    ///
    /// It never throws. Startup must not fail because the tools are not installed yet - a fresh install
    /// provisions them minutes later - so a master that is missing or empty is reported and nothing is
    /// written. The caller logs the detail either way.
    /// </summary>
    public static PathRepairResult RepairAtDirectorStart()
    {
        var master = CcStorage.Bin();
        FileLog.Write($"[FleetToolPathRepair] RepairAtDirectorStart: master={master}");

        if (!Directory.Exists(master) || !HoldsFleetTool(master))
        {
            var refusal =
                $"The machine's tools are not installed - there is no {ToolName} in {master}. " +
                "Nothing was taken off the path: the only copy that can answer a command must never be " +
                "the one removed.";
            FileLog.Write($"[FleetToolPathRepair] RepairAtDirectorStart did nothing: {refusal}");
            return new PathRepairResult(false, refusal);
        }

        var running = RepairProcessPath(master);

        string saved;
        if (!OperatingSystem.IsWindows())
        {
            saved = "The saved path was not touched: on macOS and Linux the shell profile owns it.";
        }
        else if (!OwnsTheSavedPath(CcStorage.MachineRoot(), CcStorage.DefaultRoot()))
        {
            saved =
                $"The saved path was not touched: this Director serves {CcStorage.MachineRoot()}, which is " +
                $"not this machine's install root ({CcStorage.DefaultRoot()}). A throwaway root that wrote " +
                "itself into the user's saved path would outlive its own directory.";
        }
        else
        {
            try
            {
                saved = RepairSavedPath(master);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                FileLog.Write($"[FleetToolPathRepair] RepairAtDirectorStart could not write the saved path: {ex.Message}");
                return new PathRepairResult(false, $"Could not update the saved path: {ex.Message}. {running}");
            }
        }

        var detail = $"{saved} {running}";
        FileLog.Write($"[FleetToolPathRepair] RepairAtDirectorStart done: {detail}");
        return new PathRepairResult(true, detail);
    }

    /// <summary>
    /// Does the Director serving <paramref name="machineRoot"/> own this machine's saved path?
    ///
    /// Only the install at the machine's default root does. Everything else serving a root of its own -
    /// a test rig, a wizard harness, an unpacked bundle - repairs its own running path and leaves
    /// permanent machine state alone. Pure, so the rule can be read and tested without a rig.
    /// </summary>
    internal static bool OwnsTheSavedPath(string? machineRoot, string? defaultRoot)
        => !string.IsNullOrWhiteSpace(machineRoot)
           && !string.IsNullOrWhiteSpace(defaultRoot)
           && string.Equals(Normalize(machineRoot), Normalize(defaultRoot), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Move <paramref name="masterBinDir"/> to the front of the saved path and of this process's path,
    /// and take off both every DevThrottle tools directory the master replaces (see
    /// <see cref="IsReplacedByTheMaster"/>). Only path entries are removed - nothing on disk is touched.
    ///
    /// This is what the Fix button on the Tools page calls. The Director runs the same rule by itself at
    /// every start through <see cref="RepairAtDirectorStart"/>; the button stays because a repair the
    /// user asked for should not wait for a restart.
    /// </summary>
    public static PathRepairResult PutFirstOnPath(string masterBinDir)
    {
        FileLog.Write($"[FleetToolPathRepair] PutFirstOnPath: {masterBinDir}");

        if (string.IsNullOrWhiteSpace(masterBinDir))
            throw new ArgumentException("A directory is required.", nameof(masterBinDir));
        if (!Directory.Exists(masterBinDir))
            throw new DirectoryNotFoundException($"Cannot put a directory on the path that does not exist: {masterBinDir}");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Saving a path change is Windows-only here; on macOS and Linux the shell profile owns the path.");

        // The precondition the original repair assumed. Promoting an empty directory changes the order
        // of the path and nothing about what resolves, so it looks like a repair and is not one.
        if (!HoldsFleetTool(masterBinDir))
        {
            var refusal =
                $"The machine's tools are not installed - there is no {ToolName} in {masterBinDir}. " +
                "Install the tools first; putting an empty directory on the path would change nothing.";
            FileLog.Write($"[FleetToolPathRepair] PutFirstOnPath REFUSED: {refusal}");
            return new PathRepairResult(false, refusal);
        }

        try
        {
            var saved = RepairSavedPath(masterBinDir);
            RepairProcessPath(masterBinDir);

            FileLog.Write($"[FleetToolPathRepair] PutFirstOnPath done: {saved}");
            return new PathRepairResult(true, saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            FileLog.Write($"[FleetToolPathRepair] PutFirstOnPath FAILED: {ex.Message}");
            return new PathRepairResult(false, $"Could not update the path: {ex.Message}");
        }
    }

    /// <summary>
    /// The user path exactly as it is STORED, with every %VARIABLE% intact.
    ///
    /// Public because it is the only safe way to read it, and more than one component needs to.
    /// <c>Environment.GetEnvironmentVariable("Path", User)</c> returns the value with variables
    /// already expanded; anything that reads it that way and writes the result back bakes today's
    /// expansion into the user's path permanently and silently destroys every variable reference in
    /// it. That is not a hypothetical - it is what the install finalizer did until this was shared.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string ReadUserPathRaw()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserEnvironmentKey)
            ?? throw new IOException($"The user environment registry key ({UserEnvironmentKey}) is not readable.");
        return key.GetValue(PathValueName, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    }

    /// <summary>
    /// Write the user path back, keeping it expandable when it carries variables. Pass a value that
    /// came from <see cref="ReadUserPathRaw"/> and was edited without expanding anything.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void WriteUserPathRaw(string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserEnvironmentKey, writable: true)
            ?? throw new IOException($"The user environment registry key ({UserEnvironmentKey}) is not writable.");

        // A value holding %VARIABLE% must be stored as ExpandString or the variables stop resolving.
        var kind = value.Contains('%') ? RegistryValueKind.ExpandString : key.GetValueKind(PathValueName);
        key.SetValue(PathValueName, value, kind);
    }

    // Every caller refuses non-Windows before reaching here; this states that for the platform
    // analyzer, which cannot see the guard across the method boundary.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string RepairSavedPath(string masterBinDir)
    {
        var raw = ReadUserPathRaw();
        var rewrite = Rewrite(raw, masterBinDir);
        if (string.Equals(raw, rewrite.Path, StringComparison.Ordinal))
            return $"{masterBinDir} was already first on the saved path, and nothing it replaces was left behind it.";

        WriteUserPathRaw(rewrite.Path);

        var removed = rewrite.Removed.Count == 0
            ? "Nothing else needed removing."
            : $"Removed {rewrite.Removed.Count} replaced DevThrottle entr{(rewrite.Removed.Count == 1 ? "y" : "ies")} " +
              $"from your saved path: {string.Join("; ", rewrite.Removed)}. The files are still on disk.";
        return $"{masterBinDir} is now first on the saved path. {removed}";
    }

    /// <summary>
    /// Put the master first on THIS process's path and take off what it replaces, and say what the
    /// running path now holds. The sentence names the DevThrottle tool entries that survived, by count
    /// and in full, because "the repair ran" and "the running path holds exactly one" are different
    /// claims and only the second one is the goal.
    /// </summary>
    private static string RepairProcessPath(string masterBinDir)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        var rewrite = Rewrite(current, masterBinDir);
        Environment.SetEnvironmentVariable("PATH", rewrite.Path);

        var surviving = DevThrottleToolEntries(rewrite.Path);
        var removed = rewrite.Removed.Count == 0
            ? "nothing it replaces was on it"
            : $"removed {string.Join("; ", rewrite.Removed)}";
        return $"This Director's running path: {removed}; it now holds {surviving.Count} DevThrottle " +
               $"tools entr{(surviving.Count == 1 ? "y" : "ies")}: {string.Join("; ", surviving)}.";
    }

    /// <summary>
    /// The DevThrottle tool directories a path actually holds, asked of the disk. This is the sentence
    /// the goal is stated in - the path holds exactly one - so it is computed and reported rather than
    /// inferred from the fact that a repair ran.
    /// </summary>
    internal static IReadOnlyList<string> DevThrottleToolEntries(string path)
        => (path ?? "").Split(System.IO.Path.PathSeparator)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Where(segment => HoldsFleetTool(Expand(segment)))
            .Select(segment => segment.Trim())
            .ToList();

    /// <summary>The rewritten path and the entries dropped from it, so the panel can name them.</summary>
    public sealed record PathRewrite(string Path, IReadOnlyList<string> Removed);

    /// <summary>Rewrite against the real machine: real directories, the real temp root.</summary>
    internal static PathRewrite Rewrite(string path, string masterBinDir)
        => Rewrite(path, masterBinDir, Directory.Exists, HoldsFleetTool, System.IO.Path.GetTempPath());

    /// <summary>
    /// Put <paramref name="masterBinDir"/> first and drop every DevThrottle tools directory the master
    /// replaces.
    ///
    /// Two entries pointing at two copies of the same command line serve nobody: only the first can
    /// ever win, and the loser sits there waiting to win again the moment the order shifts. So the
    /// repair leaves ONE.
    /// </summary>
    /// <param name="directoryExists">Existence test, taking an EXPANDED path.</param>
    /// <param name="holdsFleetTool">Whether an existing (expanded) directory holds cc-devthrottle.</param>
    /// <param name="tempRoot">The machine temp directory, or null to skip the temp rule.</param>
    internal static PathRewrite Rewrite(
        string path,
        string masterBinDir,
        Func<string, bool> directoryExists,
        Func<string, bool> holdsFleetTool,
        string? tempRoot)
    {
        var normalizedMaster = Normalize(masterBinDir);
        var kept = new List<string>();
        var removed = new List<string>();

        foreach (var segment in (path ?? "").Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;

            // The master's own entry comes out here and goes back at the front below, so a repeated
            // repair cannot accumulate duplicates.
            if (string.Equals(Normalize(segment), normalizedMaster, StringComparison.OrdinalIgnoreCase))
                continue;

            // Expand ONLY to ask questions about the disk. The raw text is what gets written back:
            // %USERPROFILE% must still be %USERPROFILE% afterwards.
            var expanded = Expand(segment);

            if (IsFleetToolDirectory(expanded, normalizedMaster, directoryExists, holdsFleetTool)
                && IsReplacedByTheMaster(expanded, normalizedMaster, directoryExists, tempRoot))
            {
                removed.Add(segment.Trim());
                continue;
            }

            kept.Add(segment);
        }

        kept.Insert(0, masterBinDir);
        return new PathRewrite(string.Join(System.IO.Path.PathSeparator, kept), removed);
    }

    /// <summary>
    /// The installer's path step: make sure the master is on the saved path, and take off what it
    /// replaces. Same rule as the Director's own repair, and deliberately the same code - an installer
    /// that added the master while leaving a Director's copy in FRONT of it would have installed into
    /// the right place and changed nothing about which copy answers.
    ///
    /// It does not reorder. An install that reached in and moved the master to the front of a user's
    /// saved path would be rearranging machine state it was not asked to rearrange; once the copies the
    /// master replaces are off, position no longer decides anything.
    /// </summary>
    public static PathRewrite RewriteForInstall(string path, string masterBinDir)
        => RewriteForInstall(path, masterBinDir, Directory.Exists, HoldsFleetTool, System.IO.Path.GetTempPath());

    /// <inheritdoc cref="RewriteForInstall(string, string)"/>
    internal static PathRewrite RewriteForInstall(
        string path,
        string masterBinDir,
        Func<string, bool> directoryExists,
        Func<string, bool> holdsFleetTool,
        string? tempRoot)
    {
        var normalizedMaster = Normalize(masterBinDir);
        var kept = new List<string>();
        var removed = new List<string>();
        var masterAlreadyOnPath = false;

        foreach (var segment in (path ?? "").Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;

            if (string.Equals(Normalize(segment), normalizedMaster, StringComparison.OrdinalIgnoreCase))
            {
                // Keep the first mention where the user has it; a second one is a duplicate of the
                // master itself and is exactly the accumulation this step exists to stop.
                if (masterAlreadyOnPath) continue;
                masterAlreadyOnPath = true;
                kept.Add(segment);
                continue;
            }

            var expanded = Expand(segment);

            if (IsFleetToolDirectory(expanded, normalizedMaster, directoryExists, holdsFleetTool)
                && IsReplacedByTheMaster(expanded, normalizedMaster, directoryExists, tempRoot))
            {
                removed.Add(segment.Trim());
                continue;
            }

            kept.Add(segment);
        }

        if (!masterAlreadyOnPath) kept.Add(masterBinDir);
        return new PathRewrite(string.Join(System.IO.Path.PathSeparator, kept), removed);
    }

    /// <summary>
    /// Is this path entry a DevThrottle tools directory at all? An existing directory answers for
    /// itself - it holds cc-devthrottle or it does not. A directory that is GONE cannot be asked, so it
    /// is recognised by shape instead, and only by a shape no other product writes: a tools directory
    /// inside a Director's folder of the master's own machine root, or a "bin" directory inside a
    /// cc-director install. Nothing outside those shapes is ever a candidate for removal, so an
    /// ordinary entry whose drive happens to be unplugged is left exactly where it is.
    ///
    /// A vanished entry matters as much as a live one. It is the shape the deletion this mission ends
    /// with leaves behind, and an entry pointing at a folder that is gone is still an entry: it is
    /// searched on every command, and it comes back to life the moment anything recreates the folder.
    /// </summary>
    private static bool IsFleetToolDirectory(
        string expanded,
        string normalizedMaster,
        Func<string, bool> directoryExists,
        Func<string, bool> holdsFleetTool)
        => directoryExists(expanded)
            ? holdsFleetTool(expanded)
            : LooksLikeAnInstallBin(expanded) || IsInsideADirectorFolderOfTheSameRoot(expanded, normalizedMaster);

    /// <summary>
    /// Has the master replaced this tools directory? Three ways, all facts rather than guesses:
    ///
    /// it sits inside a Director's own folder of the master's machine root, which is the copy this
    /// whole mission exists to remove; it is gone from disk, so it can answer nothing and is only
    /// waiting for something to recreate it; or it lives in the temp directory - a test rig or an
    /// unpacked bundle that leaked into the real user path, and there is one of those on the machine
    /// that prompted this.
    ///
    /// What it will NOT claim is a tools directory belonging to a different machine root. That is
    /// somebody else's install, or a rig serving its own root on purpose, and taking it off the path to
    /// tidy ours up would be sabotage dressed as hygiene.
    /// </summary>
    private static bool IsReplacedByTheMaster(
        string expanded, string normalizedMaster, Func<string, bool> directoryExists, string? tempRoot)
    {
        if (IsInsideADirectorFolderOfTheSameRoot(expanded, normalizedMaster)) return true;
        if (!directoryExists(expanded)) return true;
        return IsUnder(expanded, tempRoot);
    }

    /// <summary>
    /// The folder names the installed product uses. A Director's own folder holds sessions, settings
    /// and logs, and one of these inside it is a copy of the tools rather than that Director's data.
    /// </summary>
    private static readonly string[] InstalledFolderNames = { "bin", "pyenv", "python" };

    /// <summary>
    /// Is this path entry a copy of the tools inside a Director's own folder belonging to the master's
    /// machine root?
    ///
    /// It climbs from the entry to the first Director folder above it, keeping the folder it came
    /// through, and asks two questions there: is the folder directly inside that Director one the
    /// INSTALLED PRODUCT uses, and does that Director belong to the master's machine root?
    ///
    /// Climbing rather than matching one shape is what catches the nested folder the leak produced -
    /// there is an <c>instances\default\instances\default</c> on the computer that prompted this work -
    /// and the interpreter and virtual environment directories beside <c>bin</c>, which are the same
    /// copy under different names.
    ///
    /// The name test is the narrow half, and it is load-bearing: a Director's folder is a data folder,
    /// and somebody may legitimately have something else of theirs from inside one on their path. Being
    /// inside a Director's folder is not by itself enough to take an entry off.
    /// </summary>
    private static bool IsInsideADirectorFolderOfTheSameRoot(string expanded, string normalizedMaster)
    {
        var masterRoot = SafeParent(normalizedMaster);
        if (string.IsNullOrEmpty(masterRoot)) return false;

        var child = Normalize(expanded);
        var current = SafeParent(child);
        while (!string.IsNullOrEmpty(current))
        {
            if (CcStorage.IsDirectorInstanceHome(current))
            {
                return InstalledFolderNames.Contains(SafeName(child), StringComparer.OrdinalIgnoreCase)
                       && string.Equals(
                           Normalize(CcStorage.MachineRootOf(current)), masterRoot,
                           StringComparison.OrdinalIgnoreCase);
            }

            child = current;
            current = SafeParent(current);
        }

        return false;
    }

    private static string? SafeParent(string path)
    {
        try { return Normalize(System.IO.Path.GetDirectoryName(path) ?? ""); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool LooksLikeAnInstallBin(string expanded)
    {
        var normalized = Normalize(expanded);
        if (!string.Equals(
                SafeName(normalized), "bin", StringComparison.OrdinalIgnoreCase)) return false;

        return normalized.Split(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, "cc-director", StringComparison.OrdinalIgnoreCase));
    }

    private static string SafeName(string path)
    {
        try { return new DirectoryInfo(path).Name; }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return "";
        }
    }

    private static bool IsUnder(string candidate, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var normalizedRoot = Normalize(root) + System.IO.Path.DirectorySeparatorChar;
        return (Normalize(candidate) + System.IO.Path.DirectorySeparatorChar)
            .StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string Expand(string segment)
    {
        try { return Environment.ExpandEnvironmentVariables(segment.Trim().Trim('"')); }
        catch (ArgumentException) { return segment.Trim(); }
    }

    /// <summary>Does this directory hold a runnable cc-devthrottle? The whole question, on disk.</summary>
    internal static bool HoldsFleetTool(string directory)
    {
        try
        {
            return ExecutableResolver.Resolve(System.IO.Path.Combine(directory, ToolName)) is not null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The path a session we spawn should get: the machine's tools first, whatever the machine path
    /// says. Nothing is removed here - a session inherits the Director's own running path, which the
    /// Director already repaired at start, and this only guarantees which copy of our own command line
    /// wins.
    /// </summary>
    public static string PathWithOwnToolsFirst(string binDir, string? currentPath)
        => MoveToFront(currentPath ?? "", binDir);

    /// <summary>
    /// Return <paramref name="path"/> with <paramref name="entry"/> at the front and any existing copy
    /// of it removed, so repeated repairs cannot accumulate duplicates. Every other entry keeps its
    /// order.
    /// </summary>
    internal static string MoveToFront(string path, string entry)
    {
        var normalizedEntry = Normalize(entry);
        var kept = (path ?? "")
            .Split(Path.PathSeparator)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Where(segment => !string.Equals(Normalize(segment), normalizedEntry, StringComparison.OrdinalIgnoreCase))
            .ToList();

        kept.Insert(0, entry);
        return string.Join(Path.PathSeparator, kept);
    }

    /// <summary>
    /// A comparable form of a path entry. Trailing separators and surrounding whitespace are noise;
    /// unexpanded variables are left alone, because a segment we cannot resolve is one we must not
    /// claim to recognise.
    /// </summary>
    private static string Normalize(string? segment)
        => (segment ?? "").Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
