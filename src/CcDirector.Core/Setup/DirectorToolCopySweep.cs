using System.Text.RegularExpressions;
using CcDirector.Core.Instances;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Setup;

/// <summary>What the sweep decided to do with one directory it looked at.</summary>
public enum SweepAction
{
    /// <summary>Leave it exactly where it is. Everything the sweep cannot positively classify lands here.</summary>
    Keep,

    /// <summary>A superseded copy of the tools, positively established as disposable on all five conditions.</summary>
    Delete,
}

/// <summary>
/// One decision, with the CONDITION that decided it written out in plain English.
///
/// The reason is not decoration and it is not a log line. A folder surviving does not prove the guard
/// that was meant to save it did the saving: a Director's tools folder survives when the locator says a
/// Director is running there, and it survives identically when the registration would not parse, and
/// again when the sweep never looked at the folder at all. Three different machines, one observation.
/// So the proof reads the reason back, and the reason has to name the condition rather than repeat the
/// outcome.
/// </summary>
public sealed record SweepVerdict(SweepAction Action, string Reason)
{
    public static SweepVerdict Delete(string reason) => new(SweepAction.Delete, reason);
    public static SweepVerdict Keep(string reason) => new(SweepAction.Keep, reason);
}

/// <summary>
/// What is known about the Director that owns the folder a candidate sits in.
/// </summary>
/// <param name="Resolution">
/// What <see cref="DirectorInstanceLocator.Resolve"/> concluded. ONLY
/// <see cref="DirectorResolution.NotRunning"/> permits a deletion: it is a positive finding, and the
/// enum's own documentation says why the others are not - a question nobody could answer must never
/// read as a yes.
/// </param>
/// <param name="IsTheSweepingDirectorsOwnFolder">
/// Whether this folder belongs to the Director running the sweep. It counts as not running for its own
/// folder, because the sweep happens before it opens or restores any session. Compared by instance
/// home, never by process id.
/// </param>
public sealed record OwningDirectorState(DirectorResolution Resolution, bool IsTheSweepingDirectorsOwnFolder);

/// <summary>
/// What a directory entry actually IS, as distinct from what its name and its placement say.
///
/// WHY THIS EXISTS AT ALL. On Windows a directory junction, and on every platform a directory symbolic
/// link, is TRANSPARENT to the file APIs this sweep uses. Two primitives were measured on this machine
/// rather than assumed: <c>Directory.GetDirectories</c> through a junction lists the TARGET's children,
/// and a delete through a junction path destroys the real file at the other end. So a link named
/// <c>bin</c> sitting in a Director folder would pass placement and pass the name rule, and the removal
/// would then empty whatever folder it points at - which can be anywhere on the machine, including the
/// master the sweep had just verified was alive, seconds earlier, through a different path.
///
/// A link's target is content this sweep never enumerated, never classified and can say nothing about.
/// Under the rule this whole class is built on - only what can be POSITIVELY PROVEN disposable is
/// removed - a link is a "cannot tell", and cannot tell KEEPS.
/// </summary>
public enum DirectoryKind
{
    /// <summary>An ordinary directory. The only kind that can ever be a candidate.</summary>
    RealDirectory,

    /// <summary>A junction or a symbolic link. Never a candidate, never enumerated through, never followed.</summary>
    Link,

    /// <summary>
    /// Its attributes could not be read, so whether it is a link is unknown - and unknown is never
    /// permission. It keeps, exactly as an unreadable Director state does.
    /// </summary>
    CouldNotTell,
}

/// <summary>One directory the sweep looked at, what it decided, and what actually happened on disk.</summary>
/// <param name="Path">The full path of the directory.</param>
/// <param name="Action">The verdict.</param>
/// <param name="Reason">The condition that decided it.</param>
/// <param name="Removed">
/// Whether the directory is GONE. False for everything kept, and false for a delete that could not
/// finish - a directory that still exists is never reported as removed.
/// </param>
/// <param name="RemovalFailure">The exact exception message when a removal did not finish; null otherwise.</param>
public sealed record SweptDirectory(
    string Path, SweepAction Action, string Reason, bool Removed = false, string? RemovalFailure = null);

/// <summary>Everything one sweep did, for the log and for the Tools page to render verbatim.</summary>
/// <param name="Swept">Whether the sweep ran at all. False when the lock was held or the master is empty.</param>
/// <param name="Summary">One sentence a person can read.</param>
/// <param name="Looked">Every directory looked at, in the order it was seen.</param>
public sealed record ToolCopySweepResult(bool Swept, string Summary, IReadOnlyList<SweptDirectory> Looked)
{
    /// <summary>The directories that are gone.</summary>
    public IReadOnlyList<SweptDirectory> Removed =>
        Looked.Where(d => d.Removed).ToList();

    /// <summary>The directories a delete was decided for and could not finish.</summary>
    public IReadOnlyList<SweptDirectory> FailedToRemove =>
        Looked.Where(d => d.Action == SweepAction.Delete && !d.Removed).ToList();
}

/// <summary>
/// Deletes the superseded copies of the tools that the per-Director install leak left behind, and
/// NOTHING ELSE.
///
/// WHY IT CAN EXIST AT ALL. Phase 2 takes every copy OFF THE PATH at every Director start, so by the
/// time this runs a copy can no longer answer a command. That ordering is what makes deleting one safe;
/// reversed, this would be removing files something could still be resolving through. The hook in
/// <c>App.axaml.cs</c> keeps the order and says so.
///
/// HOW THE BOUNDARY IS DRAWN. A false delete here is unrecoverable and lands on a real person's
/// machine; a false keep is disk. Those costs are not the same, so the boundary is not in the middle.
/// This ENUMERATES WHAT TO DELETE and never what to skip: a directory is removed only when all five
/// conditions below are positively established, and everything else survives WITH ITS REASON REPORTED.
/// A name nobody thought of, a Director whose state could not be read, a machine whose tools are not
/// installed - each of those is a survival, and each survival is the boundary working rather than
/// leaking.
///
/// THE FIVE CONDITIONS, EACH A PRESENCE:
///
/// 1. PLACEMENT. The directory's parent IS a Director folder
///    (<see cref="CcStorage.IsDirectorInstanceHome"/>) or IS the machine root. Directly - a folder two
///    levels down is never a candidate, and nothing is ever found by searching recursively for a name.
/// 2. NAME. Inside a Director folder: exactly <c>bin</c>, <c>pyenv</c> or <c>python</c>, or the shape
///    <c>python.old-&lt;32 hex&gt;</c>. In the machine root: ONLY that <c>python.old-</c> shape - the
///    root's own <c>bin</c>, <c>pyenv</c> and <c>python</c> are the master and are not candidates at all.
/// 3. THE MASTER IS ALIVE. <c>&lt;machine root&gt;\bin</c> holds a runnable cc-devthrottle right now,
///    asked once at the start. If it does not, NOTHING is deleted ANYWHERE and the report says so. This
///    is the condition that stops this ever becoming a machine with no tools on it.
/// 4. THE OWNING DIRECTOR IS NOT RUNNING. For a candidate inside a Director folder, only
///    <see cref="DirectorResolution.NotRunning"/> permits deletion.
///    <see cref="DirectorResolution.Running"/>, <see cref="DirectorResolution.Ambiguous"/>,
///    <see cref="DirectorResolution.NotSupervised"/> and <see cref="DirectorResolution.Unknown"/> all
///    mean keep. The one exception is the Director doing the sweep, for its OWN folder, which has
///    opened no session yet. A candidate in the machine root has no owning Director and this condition
///    does not apply to it.
/// 5. IT IS A REAL DIRECTORY, NOT A LINK. A junction or symbolic link is content this sweep can prove
///    NOTHING about: the file APIs are transparent to one, so <c>Directory.GetDirectories</c> lists the
///    TARGET's children through it and a delete through it destroys the real files at the other end -
///    both measured on Windows. A link is therefore a "cannot tell", and cannot tell KEEPS. See
///    <see cref="DirectoryKind"/> for where this is established and the three places it is enforced.
///
/// WHAT IT NEVER DOES. It never deletes a FILE. It never follows a link, anywhere - not as a candidate,
/// not as a folder to enumerate through, and not inside a tree it is removing. It never touches
/// <c>app</c>, <c>launcher</c>, a nested <c>instances</c> folder, or any other name - those are REPORTED
/// and left, for the owner to remove by hand with his own word. It never touches a data folder anywhere:
/// sessions, settings, logs, the vault, secrets, config. It never throws: startup must not fail over
/// this, so every outcome is a reported result.
/// </summary>
public static class DirectorToolCopySweep
{
    /// <summary>The three tool directory names a Director folder gets a copy of.</summary>
    private static readonly string[] CopyNames = { "bin", "pyenv", "python" };

    /// <summary>
    /// The shape of a retired interpreter directory: <c>python.old-</c> and 32 hexadecimal characters.
    ///
    /// NARROWER THAN THE MISSION DOCUMENT, DELIBERATELY, AND REVERSIBLE. The mission says "32 letters
    /// and digits". All three of these on the machine that prompted the work are 32 lowercase hex
    /// characters, and nothing in the product writes that name today - so the shape is OBSERVED rather
    /// than generated, and hex is the narrower of the two rules that matches every real instance. A
    /// folder that is letters-and-digits but not hex survives and is reported. That is the boundary
    /// working; widening it later is a one-line change with a test beside it.
    /// </summary>
    private static readonly Regex RetiredInterpreter =
        new(@"^python\.old-[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// How deep the walk through nested <c>instances</c> folders goes before it stops and says so.
    ///
    /// The leak produced <c>instances/default/instances/default</c>, which is two. The bound is not a
    /// guess at how bad it could get - it is a refusal to follow an unbounded structure inside a method
    /// whose binding promise is that Director startup never hangs on it.
    ///
    /// IT IS NOT THE LINK GUARD, AND MUST NOT BE READ AS ONE. It bounds how long the walk can take; it
    /// says nothing about WHAT gets deleted, and a cycle four folders long would sit well inside it.
    /// Condition 5 is what refuses a link, and it refuses one at every level (see
    /// <see cref="DirectoryKind"/>) - which also means a self-referring junction can no longer be
    /// entered at all, so this bound now guards a hang that condition 5 has already made unreachable.
    /// It stays because depth is a promise about this method, not about junctions.
    /// </summary>
    internal const int MaximumNestingDepth = 8;

    /// <summary>
    /// The result of the most recent sweep in this process, for the Tools page to render verbatim.
    /// Null until the sweep has run. The client does not re-derive anything from it - every verdict and
    /// every reason was decided here.
    /// </summary>
    public static ToolCopySweepResult? Last { get; private set; }

    /// <summary>
    /// The sweep the Director runs at every start, immediately after the path repair.
    ///
    /// <paramref name="heavyRepairMutexName"/> IS PASSED IN rather than named here, because the name
    /// belongs to the setup engine (<c>ToolReconciler.HeavyRepairMutexName</c>) and this assembly does
    /// not reference it. Copying the string into this file would put two definitions of one machine-wide
    /// lock in the product, and two definitions of one lock name cannot be kept equal.
    /// </summary>
    public static ToolCopySweepResult RunAtDirectorStart(string heavyRepairMutexName)
    {
        try
        {
            var result = Sweep(
                machineRoot: CcStorage.MachineRoot(),
                masterBinDir: CcStorage.Bin(),
                sweepingDirectorInstanceHome: InstanceContext.InstanceHome,
                heavyRepairMutexName: heavyRepairMutexName);
            Last = result;
            FileLog.Write($"[DirectorToolCopySweep] {result.Summary}");
            foreach (var looked in result.Looked)
                FileLog.Write($"[DirectorToolCopySweep] {looked.Action.ToString().ToUpperInvariant()} {looked.Path} - {looked.Reason}"
                              + (looked.RemovalFailure is { Length: > 0 } f ? $" [NOT REMOVED: {f}]" : ""));
            return result;
        }
        catch (Exception ex)
        {
            // Never throws is a binding promise, not an aspiration: this runs on the startup path and a
            // Director that will not start because a tidy-up failed is a worse machine than one carrying
            // a stale copy of the tools. The copies are already off the path, so nothing they hold can
            // answer a command in the meantime.
            var result = new ToolCopySweepResult(
                false, $"The sweep could not run and nothing was deleted: {ex.Message}", Array.Empty<SweptDirectory>());
            Last = result;
            FileLog.Write($"[DirectorToolCopySweep] {result.Summary}");
            return result;
        }
    }

    /// <summary>
    /// The sweep, aimed at named roots so a rig or a test can exercise it without touching the machine.
    /// </summary>
    /// <param name="machineRoot">The machine root to sweep.</param>
    /// <param name="masterBinDir">The master tools directory, whose health is condition 3.</param>
    /// <param name="sweepingDirectorInstanceHome">The instance home of the Director running the sweep.</param>
    /// <param name="heavyRepairMutexName">The machine-wide install/repair lock, passed in by the caller.</param>
    /// <param name="resolveDirector">
    /// How the state of the Director owning an instance home is read. Production passes null and gets
    /// <see cref="DirectorInstanceLocator"/>; a test supplies each state directly so condition 4 can be
    /// watched failing without a running process.
    /// </param>
    /// <param name="lockWait">
    /// How long to wait for the machine-wide install lock. Ten seconds in production. A test that has
    /// to prove the refusal branch passes zero, because the alternative is a test that sits waiting for
    /// a lock it deliberately arranged to be unavailable.
    /// </param>
    internal static ToolCopySweepResult Sweep(
        string machineRoot,
        string masterBinDir,
        string sweepingDirectorInstanceHome,
        string heavyRepairMutexName,
        Func<string, DirectorResolution>? resolveDirector = null,
        TimeSpan? lockWait = null)
    {
        var looked = new List<SweptDirectory>();

        // CONDITION 3, ASKED ONCE, BEFORE ANYTHING IS LOOKED AT. A machine whose master cannot answer
        // cc-devthrottle is a machine where the copies are the only tools left, and the one copy that can
        // answer a command must never be the one removed.
        var masterAlive = FleetToolPathRepair.HoldsFleetTool(masterBinDir);
        if (!masterAlive)
        {
            var refusal = $"the master holds no cc-devthrottle; nothing was swept anywhere ({masterBinDir})";
            return new ToolCopySweepResult(false, $"Nothing was deleted: {refusal}.", looked);
        }

        // THE LOCK, OR NOTHING. An install or a repair holding it is writing into these very directories.
        // A bounded wait rather than an unbounded one, because this is on the startup path; if it cannot
        // be taken the sweep does not proceed unlocked, it simply does not run, and the copies stay off
        // the path until the next start tries again.
        using var mutex = new Mutex(initiallyOwned: false, heavyRepairMutexName, out _);
        var held = false;
        try
        {
            try { held = mutex.WaitOne(lockWait ?? TimeSpan.FromSeconds(10), exitContext: false); }
            catch (AbandonedMutexException) { held = true; }

            if (!held)
            {
                const string refusal = "another install or repair holds the lock; nothing was swept";
                return new ToolCopySweepResult(false, $"Nothing was deleted: {refusal}.", looked);
            }

            resolveDirector ??= home => new DirectorInstanceLocator(home).Resolve().Outcome;

            // The machine root's own children. Every one is reported, including app, launcher and
            // instances - a directory that was looked at and left is a different fact from one nobody
            // looked at, and only the report can tell them apart.
            foreach (var child in DirectChildDirectories(machineRoot))
                looked.Add(Consider(child, machineRoot, masterAlive, owner: null));

            // Every Director folder, reached by walking instances folders - never by searching for a name.
            foreach (var directorHome in DirectorFolders(machineRoot))
            {
                var isOwn = SamePath(directorHome, sweepingDirectorInstanceHome);
                var resolution = isOwn ? DirectorResolution.NotRunning : SafeResolve(resolveDirector, directorHome);
                var owner = new OwningDirectorState(resolution, isOwn);

                foreach (var child in DirectChildDirectories(directorHome))
                    looked.Add(Consider(child, machineRoot, masterAlive, owner));
            }

            var removed = looked.Count(d => d.Removed);
            var failed = looked.Count(d => d.Action == SweepAction.Delete && !d.Removed);
            var summary = $"Swept {machineRoot}: looked at {looked.Count} directories, removed {removed}, "
                          + $"could not remove {failed}, kept {looked.Count(d => d.Action == SweepAction.Keep)}.";
            return new ToolCopySweepResult(true, summary, looked);
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    }

    /// <summary>Classify one directory and, when the verdict is delete, carry it out.</summary>
    private static SweptDirectory Consider(
        ChildDirectory child, string machineRoot, bool masterAlive, OwningDirectorState? owner)
    {
        var (candidate, kind) = child;
        var verdict = Classify(candidate, kind, machineRoot, masterAlive, owner);
        if (verdict.Action == SweepAction.Keep)
            return new SweptDirectory(candidate, SweepAction.Keep, verdict.Reason);

        var failure = RemoveTree(candidate);
        // NEVER REPORTED AS REMOVED WHILE IT EXISTS. A directory holding one file another process has
        // open is left, reported with the exact message, and tried again at the next start. It is
        // already off the path, so a half-removed copy can never answer a command.
        var gone = !Directory.Exists(candidate);
        return new SweptDirectory(candidate, SweepAction.Delete, verdict.Reason, gone, gone ? null : failure);
    }

    /// <summary>
    /// THE WHOLE RULE, PURE. It takes facts and returns a verdict; it touches no disk and no
    /// environment, so every condition below can be watched failing in a unit test without a rig, a
    /// build, or a running Director.
    /// </summary>
    /// <param name="candidate">The full path of the directory being judged.</param>
    /// <param name="kind">
    /// Condition 5: what the candidate actually IS. Read from disk by the caller and passed in, like
    /// every other fact, so that the rule stays pure and a link can be classified in a test without one.
    /// It is a REQUIRED argument and not an optional one with a permissive default, because a default of
    /// "it is an ordinary directory" is a fact nobody established, and this is the condition whose whole
    /// job is to refuse facts nobody established.
    /// </param>
    /// <param name="machineRoot">The machine root this sweep is running against.</param>
    /// <param name="holdsFleetToolInMaster">Condition 3: does the master hold a runnable cc-devthrottle?</param>
    /// <param name="directorState">
    /// Condition 4: the state of the Director owning the folder this candidate sits in, or null when the
    /// candidate sits in the machine root and has no owning Director.
    /// </param>
    internal static SweepVerdict Classify(
        string candidate, DirectoryKind kind, string machineRoot, bool holdsFleetToolInMaster,
        OwningDirectorState? directorState)
    {
        // CONDITION 3 first, so that a machine with no usable master produces one answer for everything
        // it looked at rather than a mixture that would need reading twice.
        if (!holdsFleetToolInMaster)
            return SweepVerdict.Keep("the master holds no cc-devthrottle; nothing was swept anywhere");

        // CONDITION 5, BEFORE THE NAME AND THE PLACEMENT ARE EVEN LOOKED AT. A link's name and its
        // position are facts about the link; everything that would be DELETED is on the other side of
        // it, where this sweep has never looked. Asking the cheap questions first would let a junction
        // named bin reach a Delete verdict on two facts that say nothing about what gets destroyed.
        if (kind != DirectoryKind.RealDirectory)
            return SweepVerdict.Keep(kind == DirectoryKind.Link
                ? "it is a link, and the sweep never follows one - what it points at was never looked at"
                : "whether it is a link could not be read, and unknown is never permission");

        var trimmed = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent) || name.Length == 0)
            return SweepVerdict.Keep("it has no parent directory to place it by");

        // CONDITION 1: placement, and DIRECTLY. The parent itself has to be the Director folder or the
        // machine root; an ancestor is not enough and a descendant is never a candidate.
        var inMachineRoot = SamePath(parent, machineRoot);
        var inDirectorFolder = CcStorage.IsDirectorInstanceHome(parent);

        if (!inMachineRoot && !inDirectorFolder)
            return SweepVerdict.Keep("it is not directly inside a Director folder or the machine root");

        // CONDITION 2: the name, as an allow-list. The machine root's own bin, pyenv and python are the
        // MASTER - the one install this whole mission exists to leave standing - so in the root only the
        // retired-interpreter shape is a copy at all.
        if (inMachineRoot)
        {
            if (CopyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                return SweepVerdict.Keep("it is the machine's master tools folder, which is never a copy");

            if (!RetiredInterpreter.IsMatch(name))
                return SweepVerdict.Keep(
                    "in the machine root only a python.old- folder with 32 hexadecimal characters is a "
                    + "superseded copy, and this name is not one");

            // No owning Director, so condition 4 does not apply.
            return SweepVerdict.Delete(
                "it is a retired interpreter in the machine root, and the master holds cc-devthrottle");
        }

        var isToolsCopy = CopyNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (!isToolsCopy && !RetiredInterpreter.IsMatch(name))
            return SweepVerdict.Keep(
                "the name is not bin, pyenv, python or python.old- with 32 hexadecimal characters");

        // CONDITION 4: the owning Director. Only NotRunning is a positive finding; everything else is a
        // question that could not be answered, and a question nobody could answer must never read as a
        // yes. There is no branch here that a missing state can fall through.
        if (directorState is null)
            return SweepVerdict.Keep("the state of the Director that owns this folder was not established");

        if (directorState.Resolution != DirectorResolution.NotRunning)
            return SweepVerdict.Keep(directorState.Resolution switch
            {
                DirectorResolution.Running => "a Director is running there",
                DirectorResolution.Ambiguous => "more than one live process claims that Director folder",
                DirectorResolution.NotSupervised => "a live process claims that Director folder and is not the installed Director",
                _ => "whether a Director is running there is unknown, and unknown is never permission",
            });

        var because = directorState.IsTheSweepingDirectorsOwnFolder
            ? "it belongs to the Director doing the sweep, which has opened no session yet"
            : "no Director is running there";
        return SweepVerdict.Delete($"it is a superseded copy of the tools inside a Director folder, and {because}");
    }

    /// <summary>
    /// Every Director folder under <paramref name="machineRoot"/>, found by walking <c>instances</c>
    /// folders and nothing else.
    ///
    /// THE NESTED LEAK IS FOUND HERE, NOT BY A RECURSIVE SEARCH.
    /// <c>instances/default/instances/default</c> is reached because it is a Director folder BY SHAPE,
    /// sitting in its parent's own <c>instances</c> folder. Nothing anywhere looks for a directory named
    /// <c>bin</c>, which is exactly why a <c>bin</c> two levels down inside somebody's repository can
    /// never be found by this.
    /// </summary>
    private static IEnumerable<string> DirectorFolders(string machineRoot)
    {
        var queue = new Queue<(string Root, int Depth)>();
        queue.Enqueue((machineRoot, 0));

        while (queue.Count > 0)
        {
            var (root, depth) = queue.Dequeue();
            if (depth > MaximumNestingDepth) continue;

            foreach (var home in DirectChildDirectories(Path.Combine(root, "instances")))
            {
                // A Director folder that is itself a link is yielded so that it is REPORTED rather than
                // silently absent, but nothing under it is ever listed: DirectChildDirectories refuses
                // to enumerate through a link, so its children never become candidates and it is never
                // descended into for nested Director folders either.
                yield return home.Path;
                if (home.Kind == DirectoryKind.RealDirectory)
                    queue.Enqueue((home.Path, depth + 1));
            }
        }
    }

    /// <summary>One directory entry, with what it actually is read off the disk beside it.</summary>
    private readonly record struct ChildDirectory(string Path, DirectoryKind Kind);

    /// <summary>
    /// The direct child directories of a path, or nothing when it is absent, is a LINK, or cannot be
    /// listed.
    ///
    /// NOTHING IS EVER ENUMERATED THROUGH A LINK, AND THAT IS CONDITION 5 AT THIS LEVEL.
    /// <c>Directory.GetDirectories</c> follows a junction and returns the TARGET's children - measured,
    /// not assumed - so without this refusal a junction named <c>instances</c> would make somebody
    /// else's folders read as Director folders by shape, and a junction standing in for a Director
    /// folder would make its target's children read as candidates. Both are paths to deleting files at
    /// the far end of a link, which is the one thing this sweep must never do.
    ///
    /// A directory that cannot be listed yields NOTHING, which means nothing under it is deleted - the
    /// failure direction that keeps. It is not silent: the caller's report simply never names anything
    /// there, and the log lines below say why.
    /// </summary>
    private static IReadOnlyList<ChildDirectory> DirectChildDirectories(string path)
    {
        if (KindOf(path) == DirectoryKind.Link)
        {
            FileLog.Write($"[DirectorToolCopySweep] {path} is a link, so nothing under it was looked at "
                          + "or swept: the sweep never follows one");
            return Array.Empty<ChildDirectory>();
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(path);
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<ChildDirectory>();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorToolCopySweep] could not list {path}, so nothing under it was swept: {ex.Message}");
            return Array.Empty<ChildDirectory>();
        }

        return children.Select(child => new ChildDirectory(child, KindOf(child))).ToList();
    }

    /// <summary>
    /// What a directory entry IS - an ordinary directory, a link, or something whose attributes could
    /// not be read. The one place the disk is asked; every rule above takes the answer as a fact.
    ///
    /// An ABSENT path answers <see cref="DirectoryKind.CouldNotTell"/> without a log line, because it is
    /// the ordinary case rather than a surprise: most Director folders have no <c>instances</c> folder
    /// of their own, and the caller's listing gives the same empty answer a moment later.
    /// </summary>
    private static DirectoryKind KindOf(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
                ? DirectoryKind.Link
                : DirectoryKind.RealDirectory;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return DirectoryKind.CouldNotTell;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorToolCopySweep] could not read what {path} is, so it was kept: {ex.Message}");
            return DirectoryKind.CouldNotTell;
        }
    }

    /// <summary>Resolve a Director's state without ever letting the answer be an exception.</summary>
    private static DirectorResolution SafeResolve(Func<string, DirectorResolution> resolve, string instanceHome)
    {
        try
        {
            return resolve(instanceHome);
        }
        catch (Exception ex)
        {
            // An unanswerable question is Unknown, which keeps. It is NOT NotRunning, and writing the
            // catch that way round is the entire difference between this and a fail-open.
            FileLog.Write($"[DirectorToolCopySweep] could not read the Director state of {instanceHome}: {ex.Message}");
            return DirectorResolution.Unknown;
        }
    }

    /// <summary>
    /// Remove a directory depth-first, returning the first failure message or null when it all went.
    ///
    /// Depth-first and file-by-file rather than one recursive delete, so that a single file another
    /// process holds open leaves the rest removed and names the file that stopped it, instead of an
    /// opaque failure on the top directory.
    ///
    /// A LINK MET INSIDE THE TREE IS REMOVED AS A LINK AND NEVER WALKED. This is condition 5 at removal
    /// level, and it is a separate hole from the candidate test: a candidate is proved to be an ordinary
    /// directory before it is removed, but a <c>pyenv\lib</c> INSIDE it may still be a junction pointing
    /// somewhere else entirely, and walking it would delete the target's files one by one.
    /// <c>Directory.Delete</c> on the link itself unlinks it and leaves everything at the other end
    /// exactly where it was.
    ///
    /// A directory whose kind could not be read is LEFT WHOLE, not walked and not deleted, and the
    /// failure is recorded - so the parent cannot be removed either and the whole candidate is reported
    /// LEFT with a reason. A tree this sweep cannot read its way through is a tree it does not delete.
    /// </summary>
    private static string? RemoveTree(string directory)
    {
        string? firstFailure = null;

        void Walk(string dir)
        {
            var kind = KindOf(dir);
            if (kind != DirectoryKind.RealDirectory)
            {
                if (kind == DirectoryKind.CouldNotTell)
                {
                    firstFailure ??= $"{dir}: what it is could not be read, so it was left rather than followed";
                    return;
                }

                // Unlink it. Never walk it: everything it names lives on the other side.
                try { Directory.Delete(dir); }
                catch (Exception ex) { firstFailure ??= $"{dir}: {ex.Message}"; }
                return;
            }

            foreach (var child in SafeList(() => Directory.GetDirectories(dir)))
                Walk(child);

            foreach (var file in SafeList(() => Directory.GetFiles(dir)))
            {
                try { File.Delete(file); }
                catch (Exception ex) { firstFailure ??= $"{file}: {ex.Message}"; }
            }

            try { Directory.Delete(dir); }
            catch (Exception ex) { firstFailure ??= $"{dir}: {ex.Message}"; }
        }

        IReadOnlyList<string> SafeList(Func<string[]> list)
        {
            try { return list(); }
            catch (Exception ex)
            {
                firstFailure ??= $"{directory}: {ex.Message}";
                return Array.Empty<string>();
            }
        }

        Walk(directory);
        return firstFailure;
    }

    /// <summary>Two paths naming the same directory, compared the way the rest of the product compares them.</summary>
    private static bool SamePath(string a, string b)
    {
        static string Normalize(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
}
