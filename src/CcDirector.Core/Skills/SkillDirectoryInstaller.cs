using System.Diagnostics;
using System.Text.RegularExpressions;
using CcDirector.Core.Agents;
using CcDirector.Core.Storage;
using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Skills;

/// <summary>One supporting file of a skill, ready to write.</summary>
/// <param name="RelativePath">Path inside the skill's directory, forward slashes.</param>
/// <param name="Bytes">The file's bytes, already decoded.</param>
/// <param name="Executable">Whether the file gets the executable bit (ignored on Windows).</param>
public sealed record SkillFileBytes(string RelativePath, byte[] Bytes, bool Executable);

/// <summary>A complete skill, ready to become a directory on disk.</summary>
public sealed record SkillBundle(
    string Id,
    int Version,
    string ContentHash,
    string Summary,
    IReadOnlyList<string> Triggers,
    string BodyMarkdown,
    IReadOnlyList<SkillFileBytes> Files,
    string? License = null,
    string? Compatibility = null,
    string? AllowedTools = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Puts the fleet's skills where the launching agent looks for them, so every agent family discovers
/// them through its OWN skills machinery instead of needing a DevThrottle command.
///
/// WHY THIS IS SAFE TO DO NOW. The standing rule was that nothing is deployed to a machine, and its
/// stated reason was that a deployed file would only ever be read by Claude Code while reaching every
/// agent family is the point. That reason no longer holds: all eight families read the same Agent
/// Skills directory, and six share one path. The other reason - that a file on disk goes stale, and a
/// stale skill that looks current is exactly what the central library exists to prevent - is answered
/// by WHEN this runs. The install is refreshed at session launch and RECONCILED, never added to, so a
/// skill switched off or deleted on the Gateway is gone from disk the next time a session starts.
///
/// ONE COPY, LINKED. Every skill is written exactly once, into the shared <c>~/.agents/skills</c> that
/// six of the eight agent families read natively. The two that do not - Claude Code and Cursor - get
/// one LINK PER SKILL inside their own directory pointing at that copy. Three copies is three things
/// that can drift; one copy cannot disagree with itself. On Windows the link is a directory JUNCTION,
/// not a symlink: a directory symlink needs administrator rights or Developer Mode and a junction
/// needs neither. On Linux and macOS it is an ordinary unprivileged symlink.
///
/// THREE RULES THIS CLASS EXISTS TO KEEP:
///
///  1. A skill we did not write is never touched, and neither is the agent's own skills DIRECTORY -
///     we only ever create, replace or remove one named entry inside it. The library is an ADDITIONAL
///     source of skills, and a machine's own skills win a name clash. Every directory we install
///     carries a marker file, and only marked entries are ever overwritten or removed. A name already
///     taken by one of the owner's own skills is left exactly as it is - and logged, because silently
///     declining to install is the kind of thing that has to be findable later.
///     The marker also names WHICH library installed the folder (<see cref="SkillSource"/>), because
///     every Director on the computer shares these folders: a Director replaces or removes only what
///     its own library installed, and the person's own account wins a name over a team.
///  2. Reconcile, never add. What is installed equals what the Gateway currently serves.
///  3. An unreachable Gateway does not stop a session launching. It launches with whatever the last
///     refresh materialized, and a refresh that fails says so rather than pretending.
/// </summary>
public static class SkillDirectoryInstaller
{
    /// <summary>The marker that makes a directory ours. Holds the skill id, version and content hash,
    /// so the record of what we put somewhere is in the place we put it.</summary>
    public const string MarkerFileName = ".devthrottle-skill";

    /// <summary>
    /// The Director's own staging area, filled by the network half. It is deliberately NOT a place any
    /// agent reads: the Gateway fetch rebuilds a skill directory whole, and a half-written skill must
    /// never be visible to an agent mid-write. The launch half reflects this into the one place agents
    /// do read.
    /// </summary>
    public static string StoreRoot() => Path.Combine(CcStorage.Root(), "skills", "installed");

    /// <summary>
    /// Write one skill's directory into <paramref name="parentDirectory"/> as a complete, standard
    /// skill: SKILL.md with the standard's frontmatter at the root, every supporting file at its own
    /// relative path, and our marker. The directory is rebuilt from empty, so a file removed upstream
    /// cannot survive inside it.
    ///
    /// The marker also records <paramref name="source"/>, the library these bytes were fetched from, and the
    /// marker is written LAST - so a skill in the store and the source it came from are recorded together or not
    /// at all (devthrottle_internal#2311, review finding SK-F6). Placement stamps the skill with this source and
    /// with nothing else. Null only when the Gateway did not say whose library it is, and then placement refuses.
    /// </summary>
    public static string Materialize(string parentDirectory, SkillBundle bundle, SkillSource? source)
    {
        if (bundle is null)
            throw new ArgumentNullException(nameof(bundle));

        var skillDirectory = Path.Combine(parentDirectory, bundle.Id);
        if (Directory.Exists(skillDirectory))
            Directory.Delete(skillDirectory, recursive: true);
        Directory.CreateDirectory(skillDirectory);

        var skillMd = SkillMarkdown.Compose(
            bundle.Id, bundle.Summary, bundle.Triggers, bundle.BodyMarkdown,
            bundle.License, bundle.Compatibility, bundle.AllowedTools, bundle.Metadata);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), skillMd);

        foreach (var file in bundle.Files)
        {
            var target = ResolveInside(skillDirectory, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.Bytes);
            if (file.Executable && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, ReadWriteExecute);
        }

        File.WriteAllText(
            Path.Combine(skillDirectory, MarkerFileName),
            $"{bundle.Id}\n{bundle.Version}\n{bundle.ContentHash}\n{source?.MarkerLines()}");
        return skillDirectory;
    }

    /// <summary>
    /// Put the store's skills where <paramref name="kind"/> looks: one real copy in the shared
    /// directory, plus a link per skill in the agent's own directory when the agent does not read the
    /// shared one. Ours are installed or refreshed, ours the store no longer holds are removed, and
    /// everything we did not write is left alone. Synchronous and local - no network, so it is safe on
    /// the launch path. Returns how many skills the agent can now see from us.
    /// </summary>
    /// <param name="kind">The agent being launched, which decides where skills have to appear.</param>
    /// <param name="storeRoot">The materialized store to reconcile from; defaults to the real one.</param>
    /// <param name="pathsOverride">The directories to install into, INSTEAD of the ones
    /// <paramref name="kind"/> implies. Exists so tests exercise this method rather than a copy of it -
    /// the real paths live under the running user's home directory, and a test that wrote there would
    /// scatter skills through the developer's own agent configuration.</param>
    /// <param name="sourceOverride">The library to install as, INSTEAD of the one the store's own record names
    /// (<see cref="SkillSource.Establish"/>). For the ownership tests, which are about what one source may do to
    /// another's folders, not about where the source comes from.</param>
    /// <param name="configuredTeam">Reads the team this Director is set up for, INSTEAD of its own team file
    /// (<see cref="DirectorTeamStore.Load"/>). Exists so tests never read the running user's configuration.</param>
    /// <param name="folderLockWait">How long to wait for another Director to finish with the same folders,
    /// INSTEAD of <see cref="FolderLockWait"/>. Exists so a test of the timeout does not wait ten seconds.</param>
    public static SkillPlacement InstallFor(
        AgentKind kind, string? storeRoot = null, SkillInstallPaths? pathsOverride = null,
        string? reclaimStampPath = null, SkillSource? sourceOverride = null,
        Func<DirectorTeam?>? configuredTeam = null, TimeSpan? folderLockWait = null)
    {
        var store = storeRoot ?? StoreRoot();
        var paths = pathsOverride ?? SkillInstallTargets.For(kind);
        var problems = new List<SkillPlacementProblem>();

        if (paths is null)
        {
            FileLog.Write($"[SkillDirectoryInstaller] InstallFor: kind={kind} has no skills directory - nothing installed");
            return new SkillPlacement(kind, 0, 0, problems, StoreMissing: false, AgentHasNoSkillsDirectory: true);
        }
        if (!Directory.Exists(store))
        {
            FileLog.Write($"[SkillDirectoryInstaller] InstallFor: kind={kind}, no materialized skills at {store} - " +
                          "nothing installed (the Gateway has not been reached yet)");
            return new SkillPlacement(kind, 0, 0, problems, StoreMissing: true, AgentHasNoSkillsDirectory: false);
        }

        // TWO LOCKS, ALWAYS IN THIS ORDER: the shared folders first, then this Director's store.
        //
        // The shared-folder lock is one critical section for the folders every Director on the computer writes,
        // held from the first ownership read to the last change (review finding SK-F1): no other Director can
        // re-stamp a folder between this one deciding it may change it and changing it.
        //
        // The store lock is the one the store refresh takes while it deletes and rebuilds the store (review finding
        // SK-F7). Held here from reading the store's record and each skill's recorded source to copying that
        // skill's bytes, it makes the source stamped on a copy the source recorded with exactly those bytes - a
        // refresh can no longer swap another library's bytes in between. The refresh takes only the store lock, and
        // every placement takes the two in this one order, so no two of them ever wait on each other in a circle.
        //
        // A Director that cannot have both in time changes nothing and says so - it never proceeds unlocked.
        var wait = folderLockWait ?? FolderLockWait;
        var deadline = DateTime.UtcNow + wait;
        using var folderLock = SharedSkillFolderLock.TryAcquire(new[] { paths.SharedRoot, paths.LinkRoot }.OfType<string>(), wait);
        using var storeLock = folderLock is null
            ? null
            : SharedSkillFolderLock.TryAcquire(new[] { store }, Remaining(deadline));
        if (folderLock is null || storeLock is null)
        {
            var waiting = Directory.GetDirectories(store).Where(d => File.Exists(Path.Combine(d, MarkerFileName))).ToList();
            foreach (var skill in waiting)
                problems.Add(new SkillPlacementProblem(Path.GetFileName(skill), paths.SharedRoot, SkillPlacementFault.FolderBusy));
            var busy = new SkillPlacement(kind, waiting.Count, 0, problems, StoreMissing: false, AgentHasNoSkillsDirectory: false);
            FileLog.Write($"[SkillDirectoryInstaller] InstallFor: kind={kind}, " +
                          (folderLock is null ? $"another Director held {paths.SharedRoot}" : $"the store refresh held {store}") +
                          $" for more than {wait.TotalSeconds:0.#}s - nothing installed and nothing removed");
            LogIfIncomplete(busy);
            return busy;
        }

        var held = Directory.GetDirectories(store)
            .Where(d => File.Exists(Path.Combine(d, MarkerFileName)))
            .ToList();

        // WHO is installing. The folders are shared by every Director on this computer, so what this one may
        // replace or remove is decided by who installed it, never by the marker alone (#2311 F7). The source is
        // the one the Gateway named when it served this store; with none, or one that disagrees with this
        // Director's team, nothing is touched (review finding SK-F2).
        var source = sourceOverride;
        if (source is null)
        {
            var established = SkillSource.Establish(store, (configuredTeam ?? DirectorTeamStore.Load)());
            if (established.Source is null)
            {
                foreach (var skill in held)
                    problems.Add(new SkillPlacementProblem(Path.GetFileName(skill), paths.SharedRoot, established.Refusal!.Value));
                var refused = new SkillPlacement(kind, held.Count, 0, problems, StoreMissing: false, AgentHasNoSkillsDirectory: false);
                FileLog.Write($"[SkillDirectoryInstaller] InstallFor: kind={kind}, {established.Reason} - " +
                              "nothing installed and nothing removed");
                LogIfIncomplete(refused);
                return refused;
            }
            source = established.Source;
        }

        // WHERE copies are built and old ones put aside: beside the folder each skills root REALLY is, so every
        // move is a rename on one volume (review finding SK-F9). A root that is a link whose target cannot be
        // found has nowhere safe to build, so nothing is changed and the reason is recorded.
        var sharedStaging = StagingRootFor(paths.SharedRoot);
        var linkStaging = paths.LinkRoot is null ? null : StagingRootFor(paths.LinkRoot);
        if (sharedStaging is null || (paths.LinkRoot is not null && linkStaging is null))
        {
            var unresolved = sharedStaging is null ? paths.SharedRoot : paths.LinkRoot!;
            foreach (var skill in held)
                problems.Add(new SkillPlacementProblem(Path.GetFileName(skill), unresolved, SkillPlacementFault.FolderLinkUnresolved));
            var stuck = new SkillPlacement(kind, held.Count, 0, problems, StoreMissing: false, AgentHasNoSkillsDirectory: false);
            FileLog.Write($"[SkillDirectoryInstaller] InstallFor: kind={kind}, {unresolved} is a link whose target " +
                          "cannot be found - nothing installed and nothing removed");
            LogIfIncomplete(stuck);
            return stuck;
        }

        // A reconciliation that was killed part-way leaves its staging and moved-aside folders behind. Each carries
        // this installer's marker from the moment it was made, and nobody else can be using them while this
        // Director holds the lock - so they go first (review findings SK-F5, SK-F8).
        ClearStaging(sharedStaging);
        if (linkStaging is not null)
            ClearStaging(linkStaging);

        // EACH SKILL IS PLACED AS THE SOURCE RECORDED WITH ITS OWN BYTES (review finding SK-F6). The store's
        // record says whose library this Director is serving; a skill whose bytes were fetched for a different
        // one is not this library's to place, whatever name it shares - it would be another account's skill
        // stamped with this one's ownership. It is not placed, and whatever is already in the folder under its
        // name is left alone. The store refresh drops such bytes; this is the same rule where they hit the disk.
        var placeable = new List<(string StoreCopy, SkillSource Own)>();
        foreach (var skill in held)
        {
            var recorded = SkillSource.RecordedIn(skill);
            Step("source-read", Path.Combine(paths.SharedRoot, Path.GetFileName(skill)));
            if (source.Is(recorded))
            {
                placeable.Add((skill, recorded!.ToSource()));
                continue;
            }
            FileLog.Write($"[SkillDirectoryInstaller] '{Path.GetFileName(skill)}' in the store was fetched for " +
                          $"{recorded?.Describe() ?? "no recorded library"}, but this Director serves {source.Describe()} - " +
                          "not placed");
            problems.Add(new SkillPlacementProblem(Path.GetFileName(skill), paths.SharedRoot, SkillPlacementFault.SourceMismatch));
        }

        // The copy is reconciled BEFORE the links, and the order is load-bearing: a link is created
        // only for a skill that is already present in the shared directory, so no link is ever made
        // pointing at something that is not there.
        var materialized = ReconcileCopies(held, placeable, paths.SharedRoot, sharedStaging, source, problems);
        if (paths.LinkRoot is null)
        {
            var shared = new SkillPlacement(
                kind, held.Count, materialized.Count, problems, StoreMissing: false, AgentHasNoSkillsDirectory: false);
            FileLog.Write($"[SkillDirectoryInstaller] InstallFor: kind={kind} reads the shared path, " +
                          $"installed={materialized.Count}/{held.Count} at {paths.SharedRoot}");
            LogIfIncomplete(shared);
            return shared;
        }

        ReclaimRetiredInstallerCopies(materialized, paths.LinkRoot, reclaimStampPath ?? DefaultReclaimStampPath());
        var linked = ReconcileLinks(paths.SharedRoot, materialized, paths.LinkRoot, linkStaging!, source, problems);
        var result = new SkillPlacement(
            kind, held.Count, linked, problems, StoreMissing: false, AgentHasNoSkillsDirectory: false);
        FileLog.Write($"[SkillDirectoryInstaller] InstallFor: kind={kind}, materialized={materialized.Count} " +
                      $"at {paths.SharedRoot}, linked={linked}/{held.Count} into {paths.LinkRoot}");
        LogIfIncomplete(result);
        return result;
    }

    /// <summary>A placement that fell short says so at the top of its own voice, not buried among the
    /// per-skill lines. The per-skill reasons are already logged where they happen; this is the line
    /// somebody scanning a log will actually see.</summary>
    private static void LogIfIncomplete(SkillPlacement placement)
    {
        if (!placement.IsComplete && !placement.NothingExpected)
            FileLog.Write($"[SkillDirectoryInstaller] {placement.Describe()}");
        // Recorded locally for the Gateway cycle to pick up. Writing a small file is the whole cost this
        // adds to a launch - the report itself goes out on the network half, which is never on this path.
        SkillPlacementLog.Record(placement);
    }

    private static TimeSpan Remaining(DateTime deadline)
    {
        var left = deadline - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>How long a reconciliation waits for another Director to finish with the same folders. A
    /// reconciliation takes well under a second; this is on the session launch path, so it is short.</summary>
    private static readonly TimeSpan FolderLockWait = TimeSpan.FromSeconds(10);

    /// <summary>Where THIS Director recorded, before the record moved beside the folder, that the one-time
    /// reclaim had run. Read only, so a folder this Director already migrated is never migrated again.</summary>
    private static string DefaultReclaimStampPath() =>
        Path.Combine(CcStorage.Root(), "skills", "reclaimed-retired-installer.txt");

    /// <summary>
    /// ONE TIME ONLY, ONCE PER DIRECTORY: move aside the copies the RETIRED installer left behind.
    ///
    /// Until it was removed, the setup wizard wrote the built-in skills straight into
    /// <c>~/.claude/skills/&lt;name&gt;/SKILL.md</c>. Those copies carry no marker, because markers did not
    /// exist yet, so the ownership rule cannot tell them from a skill the owner wrote by hand and
    /// correctly refuses to replace them. The effect measured on a real machine: all three built-in
    /// names were occupied, NOTHING was linked, and Claude Code went on reading a two-month-old copy
    /// while every other agent family read the current one. The central library could not reach the
    /// one agent most people use. Left alone this never heals - the leftovers outlive every release.
    ///
    /// NOTHING IS DELETED. The directory is RENAMED aside with a timestamp, so an owner who really did
    /// write their own skill of that name loses nothing and can put it back. It runs once per
    /// directory, so the "a machine's own skill wins" rule applies unchanged from then on - a leftover is a
    /// one-off migration, not a standing licence to take names.
    ///
    /// ONCE PER DIRECTORY, NOT ONCE PER DIRECTOR (review finding SK-F4). The directory belongs to the user and
    /// every Director on the computer reconciles it, so the record that the migration ran lives BESIDE the
    /// directory (<see cref="ReclaimRecordFor"/>), where every Director reads it. It used to live in each
    /// Director's own storage, so a second Director, with no record of its own, would migrate again and move a
    /// skill the person wrote after the first migration. A Director that recorded the migration the old way
    /// carries its record over to the shared one without moving anything.
    ///
    /// The test is deliberately narrow on BOTH axes. The NAME must be one of the three the retired
    /// installer ever wrote - that list is a closed historical fact, so this can never take a name it
    /// did not put there - and the SHAPE must be its shape: no marker, exactly one file, named
    /// SKILL.md. Anything else is somebody's real work and is not touched, migration or not.
    /// </summary>
    private static void ReclaimRetiredInstallerCopies(
        IReadOnlyList<string> names, string linkRoot, string legacyStampPath)
    {
        var record = ReclaimRecordFor(linkRoot);
        if (File.Exists(record))
            return;
        if (HasReclaimedTheOldWay(linkRoot, legacyStampPath))
        {
            RecordReclaimed(record);
            FileLog.Write($"[SkillDirectoryInstaller] the one-time reclaim of {linkRoot} was recorded by this Director " +
                          $"before the record moved beside the folder - carried over to {record}, nothing moved");
            return;
        }

        foreach (var name in names)
        {
            if (!RetiredInstallerSkillIds.Contains(name))
                continue;
            var candidate = Path.Combine(linkRoot, name);
            if (!Directory.Exists(candidate) || !LooksLikeRetiredInstallerCopy(candidate))
                continue;

            // OUT OF THE SKILLS ROOT, not renamed within it. A directory moved aside in place is still
            // inside a directory the agent scans, so it comes straight back as a skill under a mangled
            // name - observed live: three "<name>.superseded-by-devthrottle-<stamp>" entries showed up
            // in a real session's skill list. The backup is a SIBLING of the skills root, so it is on
            // the same volume (an ordinary move, never a cross-volume copy) and is never scanned.
            var backupRoot = SupersededRootFor(linkRoot);
            Directory.CreateDirectory(backupRoot);
            var movedAside = Path.Combine(backupRoot, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.Move(candidate, movedAside);
            FileLog.Write($"[SkillDirectoryInstaller] '{name}' in {linkRoot} was a leftover from the retired " +
                          $"installer and was blocking the fleet copy. Moved to '{movedAside}' - nothing " +
                          "deleted - and the fleet skill is now linked in its place.");
        }

        RecordReclaimed(record);
    }

    /// <summary>
    /// The only skill names the retired setup wizard ever wrote to disk. A CLOSED list of a historical
    /// fact, not a policy: the wizard shipped the three built-ins and nothing else, and it no longer
    /// exists to add a fourth. Keeping the migration to these names is what makes it impossible for it
    /// to take a name the owner chose - every other name follows the ordinary rule that a machine's own
    /// skill wins, on the first run and on every run after it.
    /// </summary>
    private static readonly HashSet<string> RetiredInstallerSkillIds =
        new(new[] { "dev-throttle", "fleet-comms", "move-session" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a superseded leftover is kept: beside the agent's skills directory, never inside
    /// it. <c>~/.claude/skills</c> becomes <c>~/.claude/skills-superseded-by-devthrottle</c>.</summary>
    public static string SupersededRootFor(string linkRoot)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(linkRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return Path.Combine(parent, Path.GetFileName(linkRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "-superseded-by-devthrottle");
    }

    /// <summary>The retired installer's shape: our marker absent, one file, named SKILL.md.</summary>
    private static bool LooksLikeRetiredInstallerCopy(string directory)
    {
        if (File.Exists(Path.Combine(directory, MarkerFileName)))
            return false;
        if (Directory.GetDirectories(directory).Length > 0)
            return false;
        var files = Directory.GetFiles(directory);
        return files.Length == 1
               && string.Equals(Path.GetFileName(files[0]), "SKILL.md", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The record that the one-time reclaim has run for <paramref name="linkRoot"/>: a file beside it,
    /// never inside it. <c>~/.claude/skills</c> has <c>~/.claude/skills.devthrottle-reclaimed</c>.</summary>
    public static string ReclaimRecordFor(string linkRoot)
    {
        var trimmed = Path.GetFullPath(linkRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(Path.GetDirectoryName(trimmed)!, Path.GetFileName(trimmed) + ".devthrottle-reclaimed");
    }

    private static bool HasReclaimedTheOldWay(string linkRoot, string legacyStampPath) =>
        File.Exists(legacyStampPath)
        && File.ReadAllLines(legacyStampPath).Any(l => string.Equals(l.Trim(), linkRoot, StringComparison.OrdinalIgnoreCase));

    private static void RecordReclaimed(string record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        File.WriteAllText(record, $"The one-time reclaim of the retired installer's copies ran here at {DateTime.UtcNow:o}.{Environment.NewLine}");
    }

    /// <summary>
    /// Reflect the store into the shared directory - the ONE real copy every agent reads, directly or
    /// through a link. Returns the skill names that are this source's in there afterwards, which is what
    /// may be linked: a name the owner already used, or another source keeps, is not ours to link either.
    /// </summary>
    private static List<string> ReconcileCopies(
        IReadOnlyList<string> held, IReadOnlyList<(string StoreCopy, SkillSource Own)> placeable, string sharedRoot,
        string stagingRoot, SkillSource source, List<SkillPlacementProblem> problems)
    {
        Directory.CreateDirectory(sharedRoot);
        var wanted = new HashSet<string>(held.Select(d => Path.GetFileName(d)!), StringComparer.OrdinalIgnoreCase);

        // Remove what THIS SOURCE installed and no longer holds. A folder without our marker is somebody
        // else's skill; a folder another source installed is that source's to withdraw, not ours.
        foreach (var existing in Directory.GetDirectories(sharedRoot))
        {
            var name = Path.GetFileName(existing)!;
            if (wanted.Contains(name) || Decide(existing, source) != Claim.Mine)
                continue;
            Withdraw(existing, stagingRoot);
            FileLog.Write($"[SkillDirectoryInstaller] Removed withdrawn skill '{name}' from {sharedRoot}");
        }

        var ours = new List<string>();
        foreach (var (storeCopy, own) in placeable)
        {
            var name = Path.GetFileName(storeCopy)!;
            var destination = Path.Combine(sharedRoot, name);
            if (Directory.Exists(destination) && !MayWrite(destination, name, sharedRoot, source, problems))
                continue;
            SwapIn(storeCopy, destination, own, stagingRoot);
            ours.Add(name);
        }
        return ours;
    }

    /// <summary>
    /// Put a complete, stamped copy of <paramref name="storeCopy"/> at <paramref name="destination"/> so that the
    /// folder an agent or another Director sees there is never without its marker (review finding SK-F5).
    ///
    /// WHY. The copy used to be rebuilt in place: delete the folder, create it empty, copy the files, then write
    /// the source stamp. A Director killed anywhere in that left a folder at the skill's name with no marker - or
    /// with the marker the store wrote but not the stamp - and every Director after it read that as the owner's
    /// own skill: classified <c>Shadowed</c>, never replaced, never removed. One crash froze the skill for good.
    ///
    /// HOW. The copy is built and stamped inside a staging folder first, and only a COMPLETE folder is ever renamed
    /// to the skill's name. If the name is taken, the old folder is renamed aside before the new one is renamed in,
    /// and deleted after. A rename within one volume is a single step, so at every point the name is either the
    /// old complete folder, nothing, or the new complete folder. A kill between steps leaves staging and
    /// moved-aside folders, which <see cref="ClearStaging"/> removes under the lock on the next reconcile, and the
    /// skill is then simply placed again.
    ///
    /// WHERE. <paramref name="stagingRoot"/> is a SIBLING of the folder the skills root really is
    /// (<see cref="StagingRootFor"/>), not a folder inside it. Inside it, a staging folder would hold a SKILL.md in
    /// a folder the agent scans, and come back as a skill under a mangled name for as long as it existed - which is
    /// exactly what the superseded folders once did (see <see cref="ReclaimRetiredInstallerCopies"/>). Beside the
    /// resolved folder, it is on the same volume, so every move is a rename and never a copy (review finding SK-F9).
    /// </summary>
    private static void SwapIn(string storeCopy, string destination, SkillSource own, string stagingRoot)
    {
        var name = Path.GetFileName(destination);
        var staging = NewStagingFolder(stagingRoot, name, "staging");
        var built = Path.Combine(staging, name);
        CopyTree(storeCopy, built, destination);
        Step("copied", destination);
        StampSource(built, own);
        Step("staged", destination);

        string? aside = null;
        if (Directory.Exists(destination))
        {
            aside = NewStagingFolder(stagingRoot, name, "old");
            Directory.Move(destination, Path.Combine(aside, name));
            Step("moved-aside", destination);
        }
        Directory.Move(built, destination);
        Step("swapped", destination);
        Directory.Delete(staging, recursive: true);
        if (aside is not null)
            Directory.Delete(aside, recursive: true);
    }

    /// <summary>Remove a real skill folder without its name ever showing a half-deleted folder: rename it into a
    /// staging folder, then delete it there. A recursive delete in place can stop part-way and leave a folder with
    /// no marker at the skill's name (review finding SK-F5).</summary>
    private static void Withdraw(string folder, string stagingRoot)
    {
        var name = Path.GetFileName(folder);
        var aside = NewStagingFolder(stagingRoot, name, "withdrawn");
        Directory.Move(folder, Path.Combine(aside, name));
        Step("withdrawn-aside", folder);
        Directory.Delete(aside, recursive: true);
    }

    /// <summary>
    /// Where the folders being built or removed for the skills root <paramref name="root"/> are kept: beside the
    /// folder the root REALLY is, never inside it. <c>~/.agents/skills</c> stages in
    /// <c>~/.agents/skills.devthrottle-staging</c>.
    ///
    /// A ROOT THAT IS A LINK (review finding SK-F9). When <c>~/.agents/skills</c> is a junction or a symbolic link to
    /// <c>D:\agent-skills</c>, its skill folders are physically on D:, and a staging folder beside the link's own
    /// spelling would be on C: - every move between them a cross-volume move, which fails, so no skill would ever be
    /// placed, refreshed or withdrawn again. So the link is followed to its final target and the staging folder
    /// goes beside THAT (<c>D:\agent-skills.devthrottle-staging</c>). Junctions and symbolic links are followed the
    /// same way. Null when the root is a link whose target cannot be found, or is the top of a drive: there is then
    /// nowhere known to be on the right volume, and the caller changes nothing.
    /// </summary>
    public static string? StagingRootFor(string root)
    {
        var full = TrimSeparators(Path.GetFullPath(root));
        var info = new DirectoryInfo(full);
        if (info.LinkTarget is null)
            return BesideIt(full);

        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        if (target is null || !target.Exists)
        {
            FileLog.Write($"[SkillDirectoryInstaller] {full} is a link to '{info.LinkTarget}', which does not exist - " +
                          "nowhere to build a copy on the right volume");
            return null;
        }
        var real = TrimSeparators(target.FullName);
        if (Path.GetDirectoryName(real) is null)
        {
            FileLog.Write($"[SkillDirectoryInstaller] {full} is a link to the top of a drive ({real}) - nowhere beside it " +
                          "to build a copy");
            return null;
        }
        FileLog.Write($"[SkillDirectoryInstaller] {full} is a link to {real} - copies are built beside {real}");
        return BesideIt(real);
    }

    private static string BesideIt(string folder) =>
        Path.Combine(Path.GetDirectoryName(folder)!, Path.GetFileName(folder) + ".devthrottle-staging");

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The file at the top of every folder this installer makes in a staging root, written before
    /// anything else goes in, and the only evidence <see cref="ClearStaging"/> accepts that a folder is its own.</summary>
    public const string StagingMarkerFileName = ".devthrottle-staging";

    /// <summary>The first line of <see cref="StagingMarkerFileName"/>.</summary>
    private const string StagingMarkerSignature = "DevThrottle skill installer - a staging or moved-aside folder; safe to delete";

    /// <summary>
    /// Make a new folder in <paramref name="stagingRoot"/> for <paramref name="name"/> and mark it as this
    /// installer's FIRST, before any content goes in (review finding SK-F8). The role says what it is for - a copy
    /// being built, an old copy moved aside, or a withdrawn one - and the random part keeps two attempts apart.
    /// The skill itself always goes in a subfolder, so the marker never travels with it into the skills folder.
    /// </summary>
    private static string NewStagingFolder(string stagingRoot, string name, string role)
    {
        var folder = Path.Combine(stagingRoot, $"{name}.{Guid.NewGuid():N}.{role}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, StagingMarkerFileName), $"{StagingMarkerSignature}\n{role}\n");
        return folder;
    }

    /// <summary>The names <see cref="NewStagingFolder"/> makes.</summary>
    private static readonly Regex StagingName = new(@"^.+\.[0-9a-f]{32}\.(staging|old|withdrawn)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Remove what an interrupted reconciliation left in <paramref name="stagingRoot"/>. Called only under the
    /// folder lock, so no other Director is building there.
    ///
    /// ONLY WHAT IT CAN PROVE IT MADE (review finding SK-F8). A name of the right shape is not proof: anybody can
    /// make a folder called <c>backup.0123...cdef.old</c>, and a recursive delete of it cannot be undone. A folder is
    /// removed only when it is a real folder, has a name this installer makes, AND carries this installer's
    /// marker as its first line - which <see cref="NewStagingFolder"/> writes before anything else goes in.
    /// Anything else found there is left alone and said so: a folder kept by mistake costs disk, a folder deleted
    /// by mistake is gone.
    /// </summary>
    private static void ClearStaging(string stagingRoot)
    {
        if (!Directory.Exists(stagingRoot))
            return;
        foreach (var leftover in Directory.GetDirectories(stagingRoot))
        {
            if (!IsOurStagingFolder(leftover))
            {
                FileLog.Write($"[SkillDirectoryInstaller] '{leftover}' does not carry this installer's staging marker - left alone");
                continue;
            }
            Directory.Delete(leftover, recursive: true);
            FileLog.Write($"[SkillDirectoryInstaller] removed '{leftover}', left by a reconciliation that did not finish");
        }
    }

    private static bool IsOurStagingFolder(string folder)
    {
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            return false;
        if (!StagingName.IsMatch(Path.GetFileName(folder)))
            return false;
        var marker = Path.Combine(folder, StagingMarkerFileName);
        return File.Exists(marker)
               && string.Equals(File.ReadLines(marker).FirstOrDefault(), StagingMarkerSignature, StringComparison.Ordinal);
    }

    /// <summary>Tells a test where a swap has got to: the step and the skill's visible folder. Per asynchronous
    /// flow, so a test running beside another never sees the other's steps. Null outside tests.</summary>
    internal static readonly AsyncLocal<Action<string, string>?> SwapStepForTests = new();

    private static void Step(string step, string destination) => SwapStepForTests.Value?.Invoke(step, destination);

    /// <summary>
    /// Give an agent that does not read the shared path one link per skill into it. Never touches
    /// <paramref name="linkRoot"/> itself - that folder is the owner's and holds skills we did not
    /// write - only named entries inside it. Returns how many links the agent can now follow.
    /// </summary>
    private static int ReconcileLinks(
        string sharedRoot, IReadOnlyList<string> names, string linkRoot, string stagingRoot, SkillSource source,
        List<SkillPlacementProblem> problems)
    {
        Directory.CreateDirectory(linkRoot);
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        foreach (var existing in Directory.GetDirectories(linkRoot))
        {
            var name = Path.GetFileName(existing)!;
            if (wanted.Contains(name) || !IsWithdrawnByThisSource(existing, sharedRoot, source))
                continue;
            RemoveOurs(existing, stagingRoot);
            FileLog.Write($"[SkillDirectoryInstaller] Removed withdrawn skill '{name}' from {linkRoot}");
        }

        var linked = 0;
        foreach (var name in names)
        {
            var destination = Path.Combine(linkRoot, name);
            if (Directory.Exists(destination) || IsLink(destination))
            {
                // A link into the shared directory points at the one copy, which this source has just
                // made its own - so the link is ours to rebuild whoever made it. A real folder here is a
                // copy from the scheme that preceded links, and follows the same rule as the shared copy.
                if (!IsLinkInto(destination, sharedRoot)
                    && !MayWrite(destination, name, linkRoot, source, problems))
                    continue;
                // Rebuilt rather than inspected, because a link is cheap to make and a link believed to
                // point somewhere it does not is the failure that has no symptom.
                RemoveOurs(destination, stagingRoot);
            }

            // One skill that cannot be linked is recorded and stepped over rather than abandoning the
            // rest. This is NOT a quiet degrade: the failure lands in the result, the caller warns on
            // it, and the count says how many actually arrived. Losing the other skills as well would
            // turn one broken link into a machine with no skills at all.
            try
            {
                CreateDirectoryLink(destination, Path.Combine(sharedRoot, name));
                linked++;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SkillDirectoryInstaller] could not link '{name}' into {linkRoot}: {ex.Message}");
                problems.Add(new SkillPlacementProblem(name, linkRoot, SkillPlacementFault.LinkFailed));
            }
        }
        return linked;
    }

    /// <summary>What this source may do with a folder that occupies a name.</summary>
    private enum Claim
    {
        /// <summary>No marker: a skill DevThrottle did not write. Never touched.</summary>
        NotOurs,

        /// <summary>This source installed it. Refreshed while held, removed when withdrawn.</summary>
        Mine,

        /// <summary>Another source installed it and this one outranks it: the person's own account over
        /// a team. Overwritten while held, never removed when withdrawn.</summary>
        TakeOver,

        /// <summary>Another source installed it and keeps it. Never touched.</summary>
        Yield,
    }

    /// <summary>
    /// THE RULE (devthrottle_internal#2311, live proof F7). A source replaces or removes only what it
    /// installed itself. On a name clash the person's own personal account wins: it takes a name from a
    /// team, and a team never takes one from it. Between two sources of equal rank - two teams, or the
    /// personal account on two different Gateways - the FIRST one installed keeps the name, because the
    /// later one finds it already stamped by someone else and yields.
    ///
    /// A marker with no source was written before sources were recorded, by a Director that had no teams,
    /// so it is the personal account's: a team never takes or removes it, and the personal account takes it
    /// over by re-stamping it. It is never REMOVED by anyone, because which personal library wrote it is
    /// not recorded - a withdrawal that guessed would be deleting the person's skills on a guess. The cost
    /// is that a skill withdrawn in the same moment as this upgrade can stay on disk; a stale extra skill is
    /// recoverable, a deleted one is not.
    /// </summary>
    private static Claim Decide(string folder, SkillSource source)
    {
        var marker = Path.Combine(folder, MarkerFileName);
        if (!File.Exists(marker))
            return Claim.NotOurs;
        var stamp = SkillSource.ReadStamp(File.ReadAllLines(marker));
        if (stamp is not null && source.Wrote(stamp))
            return Claim.Mine;
        if (stamp is null)
            return source.IsPersonal ? Claim.TakeOver : Claim.Yield;
        return source.IsPersonal && !stamp.IsPersonal ? Claim.TakeOver : Claim.Yield;
    }

    /// <summary>
    /// Whether this source may replace the folder already at <paramref name="name"/>. When it may not, the
    /// reason is logged and recorded as a placement problem - a skill quietly not installed is the kind of
    /// absence nobody finds.
    /// </summary>
    private static bool MayWrite(
        string folder, string name, string root, SkillSource source, List<SkillPlacementProblem> problems)
    {
        switch (Decide(folder, source))
        {
            case Claim.Mine:
                return true;
            case Claim.TakeOver:
                FileLog.Write($"[SkillDirectoryInstaller] '{name}' in {root} was installed by {WhoInstalled(folder)}; " +
                              $"{source.Describe()} is the person's own and takes the name");
                return true;
            case Claim.Yield:
                FileLog.Write($"[SkillDirectoryInstaller] '{name}' already exists in {root}, installed by " +
                              $"{WhoInstalled(folder)} - leaving it alone; {source.Describe()} does not take it");
                problems.Add(new SkillPlacementProblem(name, root, SkillPlacementFault.HeldByAnotherSource));
                return false;
            default:
                // The owner's own skill of the same name. It wins, and the fact that it did is recorded.
                FileLog.Write($"[SkillDirectoryInstaller] '{name}' already exists in {root} and was not " +
                              "installed by DevThrottle - leaving it alone; the machine's own skill wins");
                problems.Add(new SkillPlacementProblem(name, root, SkillPlacementFault.Shadowed));
                return false;
        }
    }

    private static string WhoInstalled(string folder) =>
        SkillSource.ReadStamp(File.ReadAllLines(Path.Combine(folder, MarkerFileName)))?.Describe()
        ?? "the personal account (recorded before sources were)";

    /// <summary>Write the installing source into the copy's marker, after the store's own lines (id,
    /// version, content hash), so the folder says who may replace or remove it.</summary>
    private static void StampSource(string skillDirectory, SkillSource source)
    {
        var marker = Path.Combine(skillDirectory, MarkerFileName);
        var sourceKeys = new[] { SkillSource.GatewayIdKey, SkillSource.TenantKey, SkillSource.AccountKey }
            .Concat(SkillSource.RetiredKeys).ToArray();
        var storeLines = File.ReadAllLines(marker)
            .Where(l => !sourceKeys.Any(k => l.StartsWith(k, StringComparison.Ordinal)))
            .Take(3);
        File.WriteAllText(marker, string.Join("\n", storeLines) + "\n" + source.MarkerLines());
    }

    /// <summary>
    /// An entry in an agent's own directory that this source should remove because it no longer holds the
    /// skill: a link to a shared copy this source installed, a copy from the scheme before links that this
    /// source installed, or a link whose shared copy is already gone - it reads as nothing to every agent,
    /// and whoever still holds that skill makes the link again with the copy.
    /// </summary>
    private static bool IsWithdrawnByThisSource(string entry, string sharedRoot, SkillSource source)
    {
        var target = LinkTargetInside(entry, sharedRoot);
        if (target is not null)
            return !Directory.Exists(target) || Decide(target, source) == Claim.Mine;
        return !IsLink(entry) && Decide(entry, source) == Claim.Mine;
    }

    private static bool IsLink(string path) =>
        new DirectoryInfo(path).LinkTarget is not null;

    /// <summary>A link pointing into our shared directory - how a link is recognised as one of ours even
    /// when its target has already gone.</summary>
    private static bool IsLinkInto(string path, string sharedRoot) => LinkTargetInside(path, sharedRoot) is not null;

    /// <summary>Where <paramref name="path"/> points, when it is a link into our shared directory.</summary>
    private static string? LinkTargetInside(string path, string sharedRoot)
    {
        var target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: false)?.FullName;
        return target is not null
               && target.StartsWith(Path.GetFullPath(sharedRoot) + Path.DirectorySeparatorChar,
                                    StringComparison.OrdinalIgnoreCase)
            ? target
            : null;
    }

    /// <summary>Delete one of our entries. A link is removed as a link, so what it points at survives -
    /// a recursive delete through a link would empty the one real copy every other agent reads. A real folder
    /// is withdrawn through the staging folder, so its name never shows it half-deleted.</summary>
    private static void RemoveOurs(string path, string stagingRoot)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            Directory.Delete(path, recursive: false);
        else
            Withdraw(path, stagingRoot);
    }

    /// <summary>
    /// Point <paramref name="linkPath"/> at <paramref name="targetPath"/>.
    ///
    /// On Windows this is a directory JUNCTION and not a symlink. Creating a directory symlink needs
    /// administrator rights or Developer Mode, and the Director runs as the ordinary signed-in user; a
    /// junction needs neither. There is no managed API that creates a junction, so it is made with the
    /// command Windows ships for it. A failure throws rather than silently degrading to a copy: a copy
    /// that looks like a link is exactly the drift this design removed.
    /// </summary>
    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start cmd.exe to create a skill junction.");

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(linkPath))
            throw new InvalidOperationException(
                $"Could not create the skill junction '{linkPath}' -> '{targetPath}': {output.Trim()}");
    }

    private const UnixFileMode ReadWriteExecute =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>Copy a materialized skill over a destination, rebuilding it so a file removed upstream
    /// cannot survive in the copy. <paramref name="visible"/> is the skill's own folder, which a test watches
    /// while the copy is under way.</summary>
    private static void CopyTree(string source, string destination, string visible)
    {
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0)
                File.SetUnixFileMode(target, ReadWriteExecute);
            Step("copying", visible);
        }
    }

    /// <summary>Resolve a supporting file's relative path INSIDE the skill directory, refusing any
    /// path that would land outside it. The Gateway validates paths on write; this is the same rule
    /// enforced again at the point where bytes hit this disk, because a store that was ever wrong
    /// must not be able to write anywhere it likes.</summary>
    private static string ResolveInside(string skillDirectory, string relativePath)
    {
        var root = Path.GetFullPath(skillDirectory);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Skill file path '{relativePath}' resolves outside the skill's own directory. " +
                "Refusing to write it.");
        return combined;
    }
}
