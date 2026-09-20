using System.Globalization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Scanning;

namespace CcDirector.Reclaim.Removal;

/// <summary>
/// Everything one run of the refusal gate is configured with. The protected paths and the user's own
/// folders arrive as parameters, supplied by the caller from the resolvers that own them - the
/// command line asks CcStorage and the system's own folder resolver, and a test builds its own list
/// over a fixture tree, which is the one way the gate can ever be proven without touching a real
/// machine.
/// </summary>
public sealed record RefusalGateOptions
{
    /// <summary>
    /// Every path that must never be touched, and everything under it. The caller reads these from
    /// <see cref="CcStorage.ProtectedPaths"/> at the moment of the run, never from a copy of its own.
    /// </summary>
    public required IReadOnlyList<CcStorage.ProtectedPath> ProtectedPaths { get; init; }

    /// <summary>
    /// The user's own folders - Documents, Pictures, Videos, Desktop, OneDrive - read from the
    /// system's own resolver by the caller, which follows any folder redirection the machine has.
    /// </summary>
    public required IReadOnlyList<string> UserFolders { get; init; }

    /// <summary>The holding root for the volume being reclaimed.</summary>
    public required string HoldingRootPath { get; init; }

    /// <summary>The folder the run was asked about, in its canonical full form.</summary>
    public required string ScanRootPath { get; init; }

    /// <summary>True only when the caller passed the explicit apply flag.</summary>
    public required bool Apply { get; init; }

    /// <summary>The moment the age gates are judged against, at the check itself.</summary>
    public required DateTimeOffset NowUtc { get; init; }
}

/// <summary>
/// The one component that decides whether an item may be removed. Nothing else in the engine, the
/// tool, or any later screen makes that decision - a check that each caller remembered to run is a
/// check the next caller forgets, and the forgetting is invisible.
///
/// The gate runs for every candidate in every run, and it runs again immediately before an item's
/// own move in an apply run, because the disk changes between one move and the next. Between one
/// item's move and the next item's check, the disk has changed; the next item is checked against the
/// disk as it is at that moment, not as it was when the run started.
///
/// The checks are enumerated, never inferred: the first one that fires stops the gate, and every
/// check after it is reported not-reached, so nobody can later read an unchecked refusal as a passed
/// one. Where a check cannot answer - a path that will not resolve, a folder that will not be listed,
/// a file that will not open - the answer is refuse, and the reason says the check could not answer.
/// </summary>
public sealed class RefusalGate
{
    private readonly RefusalGateOptions _options;
    private readonly StringComparison _pathComparison;

    /// <summary>Build a gate for one run.</summary>
    /// <param name="options">The run's configuration.</param>
    public RefusalGate(RefusalGateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        FileLog.Write(
            $"[RefusalGate] RefusalGate: root={options.ScanRootPath}, holding={options.HoldingRootPath}, " +
            $"apply={options.Apply}, protected={options.ProtectedPaths.Count}, userFolders={options.UserFolders.Count}");
    }

    /// <summary>
    /// Check one item. The owning rule examines again, here, as part of the check: the age gate is
    /// re-judged, the controls are re-read, and the item is compared with what the recommendation
    /// said about it. The rule therefore runs twice for every item that moves - once when the run
    /// builds its recommendations, and once at this check - and that is the honest reading of "the
    /// check that an item is still disposable is made again at the moment of the move".
    /// </summary>
    /// <param name="recommended">The item as the recommendation offered it.</param>
    /// <param name="owningRule">The rule that offered it.</param>
    public GateOutcome Check(ReclaimCandidate recommended, IReclaimRule owningRule)
    {
        ArgumentNullException.ThrowIfNull(recommended);
        ArgumentNullException.ThrowIfNull(owningRule);
        FileLog.Write($"[RefusalGate] Check: path={recommended.Path}, rule={owningRule.Id}");

        // The holding root must sit on the same volume as the item, because removal is a move on the
        // same volume: a move across volumes is a copy and a delete, which is not what this is. A
        // mismatch is a broken configuration, and every item is refused with the reason, never
        // quietly held somewhere else. This is not one of the ten checks; it is the run's own
        // fitness, so every check is reported not reached.
        try
        {
            if (!VolumeReader.SameVolume(recommended.Path, _options.HoldingRootPath))
            {
                return ConfigurationRefused(recommended.Path,
                    $"the holding root {_options.HoldingRootPath} and the item {recommended.Path} sit on " +
                    "different volumes; removal is a move on the same volume, so this is a broken configuration " +
                    "and the item is refused");
            }
        }
        catch (IOException ex)
        {
            return ConfigurationRefused(recommended.Path,
                $"the volume of {recommended.Path} or of the holding root {_options.HoldingRootPath} could not " +
                $"be named, so the holding configuration cannot be judged: {ex.Message}");
        }

        // The path is resolved first, because every later check asks the disk about it. A path that
        // will not resolve at all is refused with the reason why, and the checks before the tenth
        // are reported not-reached, because they could not be run against a path that does not exist.
        string resolved;
        try
        {
            resolved = CanonicalPath.Resolve(recommended.Path);
        }
        catch (IOException ex)
        {
            return Assemble(
                recommended.Path,
                RefusalCheck.NotCanonical,
                $"refusal 10, {GateOutcome.WordsFor(RefusalCheck.NotCanonical)}: {ex.Message}",
                resolved: false);
        }

        RefusalCheck? fired = null;
        string? reason = null;
        RuleFinding? fresh = null;
        ReclaimCandidate? freshCandidate = null;

        // 1. Under a path the storage resolver names as credentials or vault.
        var protectedHit = _options.ProtectedPaths.FirstOrDefault(protectedPath =>
            RuleSelection.IsInside(recommended.Path, protectedPath.Path));
        if (protectedHit is not null)
        {
            fired = RefusalCheck.ProtectedPath;
            reason =
                $"refusal 1, {GateOutcome.WordsFor(RefusalCheck.ProtectedPath)}: this is under " +
                $"{protectedHit.WhatItIs}, at {protectedHit.Path}, which the storage resolver protects and " +
                "nothing may ever remove";
        }

        // 2. Inside a git working tree. The check is the presence of an entry named .git - not a git
        // command - so it answers on a machine where git is not installed, and it refuses an item
        // whose tree claims to be a repository, whatever git would say about it.
        if (fired is null)
        {
            var git = FindGitEntry(recommended.Path);
            if (git.Fired)
            {
                fired = RefusalCheck.GitWorkingTree;
                reason = git.Reason;
            }
        }

        // 3. Under the user's own folders.
        if (fired is null)
        {
            var userFolder = _options.UserFolders.FirstOrDefault(folder =>
                !string.IsNullOrWhiteSpace(folder) && RuleSelection.IsInside(recommended.Path, folder));
            if (userFolder is not null)
            {
                fired = RefusalCheck.UserFolder;
                reason =
                    $"refusal 3, {GateOutcome.WordsFor(RefusalCheck.UserFolder)}: this is under {userFolder}, " +
                    "which is the user's own folder";
            }
        }

        // 4. Reached through a link or junction. The item itself, and every ancestor between the
        // folder the run was asked about and the item, is asked whether it is a link. This runs on the
        // path as spelled, before any resolution, so resolution can never hide a junction it should
        // have caught. One link anywhere on the way down is a refusal, because the item stands
        // somewhere other than where the path says.
        if (fired is null)
        {
            var link = FindLinkOnTheWayDown(recommended.Path);
            if (link.Fired)
            {
                fired = RefusalCheck.LinkOrJunction;
                reason = link.Reason;
            }
        }

        // The fresh examination, made here, once per item, for checks 5, 7 and 8. The owning rule
        // re-examines and the fresh answer is what counts - not the answer the run started with.
        if (fired is null)
        {
            fresh = RuleFold.Fold(owningRule, owningRule.Examine(new RuleContext
            {
                ScanRootPath = _options.ScanRootPath,
                NowUtc = _options.NowUtc
            }));
            freshCandidate = fresh.Candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Path, recommended.Path, _pathComparison));
        }

        // 5. Younger than its rule's age gate, re-measured at the moment of the check the same way
        // the rule measured it: the newest write anywhere inside, not the item's own stamp.
        if (fired is null)
        {
            var gate = _options.NowUtc.AddDays(-owningRule.AgeGateDays);
            if (freshCandidate is not null)
            {
                if (freshCandidate.LastWrittenUtc > gate)
                {
                    fired = RefusalCheck.AgeGate;
                    reason =
                        $"refusal 5, {GateOutcome.WordsFor(RefusalCheck.AgeGate)}: the newest write inside it " +
                        $"is {MomentWords(freshCandidate.LastWrittenUtc)}, and the rule {owningRule.Id} has an age " +
                        $"gate of {owningRule.AgeGateDays.ToString(CultureInfo.InvariantCulture)} days and will not " +
                        $"touch anything written after {MomentWords(gate)}";
                }
            }
            else
            {
                // The fresh examination no longer offers it. When the reason is its age, this check
                // names it; anything else about the change belongs to check 8.
                try
                {
                    var measured = Measure(recommended.Path);
                    if (measured > gate)
                    {
                        fired = RefusalCheck.AgeGate;
                        reason =
                            $"refusal 5, {GateOutcome.WordsFor(RefusalCheck.AgeGate)}: the newest write inside it " +
                            $"is {MomentWords(measured)}, and the rule {owningRule.Id} has an age gate of " +
                            $"{owningRule.AgeGateDays.ToString(CultureInfo.InvariantCulture)} days and will not touch " +
                            $"anything written after {MomentWords(gate)}, even though it passed the gate when it was recommended";
                    }
                }
                catch (IOException ex)
                {
                    fired = RefusalCheck.AgeGate;
                    reason =
                        $"refusal 5, {GateOutcome.WordsFor(RefusalCheck.AgeGate)}: the age could not be " +
                        $"re-measured, because {ex.Message}";
                }
            }
        }

        // 6. An open file inside. Every file inside is asked, held exclusively for an instant and
        // given straight back. One file that will not be held is a refusal - and a folder that cannot
        // be listed, or a file that cannot be asked, is answered as in use, because something about
        // it cannot be told.
        if (fired is null && FolderMeasures.AnythingOpenIn(recommended.Path))
        {
            fired = RefusalCheck.OpenFile;
            reason =
                $"refusal 6, {GateOutcome.WordsFor(RefusalCheck.OpenFile)}: a file inside it is held open, or " +
                "could not be asked whether it is, and an open file is a refusal";
        }

        // 7. The rule's controls are empty at the moment of the move. A broken rule offers nothing,
        // and the fresh answer's controls are what count, not the ones the run started with.
        if (fired is null && fresh is not null && fresh.Verdict == RuleVerdict.Broken)
        {
            fired = RefusalCheck.EmptyControls;
            reason =
                $"refusal 7, {GateOutcome.WordsFor(RefusalCheck.EmptyControls)}: the rule {owningRule.Id} could " +
                $"not do its work at the moment of the move - {fresh.BrokenReason} - so nothing it offered may be acted on";
        }

        // 8. Changed between the recommendation and the removal. Any difference - one byte, one
        // write, the whole item - is a refusal: the recommendation was about an item that no longer
        // exists.
        if (fired is null && fresh is not null)
        {
            if (freshCandidate is null)
            {
                fired = RefusalCheck.ChangedSinceRecommendation;
                reason = PathExists(recommended.Path)
                    ? $"refusal 8, {GateOutcome.WordsFor(RefusalCheck.ChangedSinceRecommendation)}: a fresh " +
                      $"examination by the rule {owningRule.Id} no longer offers it, so the recommendation was " +
                      "about an item that no longer exists"
                    : $"refusal 8, {GateOutcome.WordsFor(RefusalCheck.ChangedSinceRecommendation)}: the item " +
                      "vanished between the recommendation and the move, and there is nothing left to remove";
            }
            else if (freshCandidate.Bytes != recommended.Bytes)
            {
                fired = RefusalCheck.ChangedSinceRecommendation;
                reason =
                    $"refusal 8, {GateOutcome.WordsFor(RefusalCheck.ChangedSinceRecommendation)}: the " +
                    $"recommendation measured {recommended.Bytes.ToString(CultureInfo.InvariantCulture)} bytes and " +
                    $"a fresh examination measures {freshCandidate.Bytes.ToString(CultureInfo.InvariantCulture)}";
            }
            else if (freshCandidate.LastWrittenUtc != recommended.LastWrittenUtc)
            {
                fired = RefusalCheck.ChangedSinceRecommendation;
                reason =
                    $"refusal 8, {GateOutcome.WordsFor(RefusalCheck.ChangedSinceRecommendation)}: the newest " +
                    $"write inside it moved from {MomentWords(recommended.LastWrittenUtc)} to " +
                    $"{MomentWords(freshCandidate.LastWrittenUtc)}";
            }
        }

        // 9. Removal without the explicit apply flag. This one is a property of the run, not of an
        // item: the gate checks it once per run, first, and reports it in the same enumeration. A dry
        // run fires it at the run level while every item's other checks still ran for real, so the
        // dry run and an apply can never disagree about what is eligible.
        //
        // 10. Not canonical after resolution. The path has already resolved; what this refuses is a
        // spelling that differs from the final form - a short name, a trailing dot, a different
        // separator - because the rule examined one path and the gate was about to act on another.
        if (fired is null && !string.Equals(resolved, recommended.Path, _pathComparison))
        {
            fired = RefusalCheck.NotCanonical;
            reason =
                $"refusal 10, {GateOutcome.WordsFor(RefusalCheck.NotCanonical)}: the path as reported is " +
                $"{recommended.Path} and its resolved final form is {resolved}, and the rule examined one path " +
                "while the gate was about to act on another";
        }

        var outcome = Assemble(recommended.Path, fired, reason, resolved: true);
        FileLog.Write(
            $"[RefusalGate] Check done: path={recommended.Path}, eligible={outcome.Eligible}, fired={outcome.FiredCheck}");
        return outcome;
    }

    /// <summary>
    /// Assemble the ten outcomes from the one check that fired, or from none. The fired check is
    /// reported refused with its reason, the checks before it are reported passed (they ran and did
    /// not fire), and the checks after it are reported not-reached. The ninth carries the run's own
    /// answer whenever the item reached it, so the ten-item list never wears a nine-item shape.
    /// </summary>
    private GateOutcome Assemble(string path, RefusalCheck? fired, string? reason, bool resolved)
    {
        var results = new List<CheckResult>(10);
        for (var number = 1; number <= 10; number++)
        {
            var check = (RefusalCheck)number;

            if (fired == check)
            {
                results.Add(new CheckResult(check, CheckOutcome.Refused, reason));
                continue;
            }

            // The unresolvable path is refused by the tenth check, and nothing before it could run.
            if (!resolved && number < 10)
            {
                results.Add(new CheckResult(check, CheckOutcome.NotReached, null));
                continue;
            }

            // The ninth is the run's own answer, and it is only reached when the item got that far.
            if (check == RefusalCheck.NoApplyFlag)
            {
                if (fired is not null && (int)fired.Value < 9)
                {
                    results.Add(new CheckResult(check, CheckOutcome.NotReached, null));
                    continue;
                }

                if (_options.Apply)
                {
                    results.Add(new CheckResult(check, CheckOutcome.Passed, null));
                }
                else
                {
                    results.Add(new CheckResult(check, CheckOutcome.Refused,
                        "refusal 9, removal without the explicit apply flag: this run is a dry run, so nothing " +
                        "moves; run it again with --apply to move what every other check passed"));
                }
                continue;
            }

            // The checks before the one that fired ran and did not fire; the checks after it were
            // never reached.
            if (fired is null || number < (int)fired.Value)
                results.Add(new CheckResult(check, CheckOutcome.Passed, null));
            else
                results.Add(new CheckResult(check, CheckOutcome.NotReached, null));
        }

        var firedCheck = fired is RefusalCheck.NoApplyFlag ? null : fired;
        return new GateOutcome
        {
            Path = path,
            Eligible = fired is null,
            Apply = _options.Apply,
            Checks = results,
            FiredCheck = firedCheck,
            Reason = firedCheck is null ? null : reason,
            ConfigurationReason = null
        };
    }

    private GateOutcome ConfigurationRefused(string path, string reason)
    {
        FileLog.Write($"[RefusalGate] Check refused by configuration: path={path}, reason={reason}");
        return new GateOutcome
        {
            Path = path,
            Eligible = false,
            Apply = _options.Apply,
            Checks = Enumerable.Range(1, 10)
                .Select(number => new CheckResult((RefusalCheck)number, CheckOutcome.NotReached, null))
                .ToList(),
            FiredCheck = null,
            Reason = reason,
            ConfigurationReason = reason
        };
    }

    /// <summary>
    /// The newest write anywhere inside the item, the same way the rule measured it: not the item's
    /// own stamp, which can be old while a file inside was written a minute ago.
    /// </summary>
    private static DateTimeOffset Measure(string path)
    {
        var measured = FolderMeasures.Measure(path);

        DateTimeOffset ownStamp;
        if (Directory.Exists(path))
            ownStamp = new DateTimeOffset(new DirectoryInfo(path).LastWriteTimeUtc, TimeSpan.Zero);
        else
            ownStamp = new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc, TimeSpan.Zero);

        return measured.NewestWriteUtc > ownStamp ? measured.NewestWriteUtc : ownStamp;
    }

    /// <summary>
    /// The answer of a check that walks the disk: whether it fired, and the finished sentence that
    /// says why. A check that could not answer fires, and its sentence says it could not answer -
    /// leaning to keep is the whole posture of this tool.
    /// </summary>
    private readonly record struct WalkAnswer(bool Fired, string Reason);

    /// <summary>
    /// The entry named .git at or above the item, or null. Every ancestor up to the volume root is
    /// LISTED rather than merely asked whether it holds one, because a folder that cannot be listed
    /// could be hiding one, and a check that cannot answer is a refusal, not a pass.
    /// </summary>
    private WalkAnswer FindGitEntry(string itemPath)
    {
        var current = Path.GetFullPath(itemPath);
        while (true)
        {
            try
            {
                if (new DirectoryInfo(current).EnumerateFileSystemInfos().Any(entry =>
                        entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                    return new WalkAnswer(true,
                        $"refusal 2, {GateOutcome.WordsFor(RefusalCheck.GitWorkingTree)}: an entry named .git " +
                        $"stands at {Path.Combine(current, ".git")}; worktrees belong to cc-worktrees, which " +
                        "already proves whether work has landed, and this tool reports them and never rules on them");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new WalkAnswer(true,
                    $"refusal 2, {GateOutcome.WordsFor(RefusalCheck.GitWorkingTree)}: the folder {current} could " +
                    $"not be listed (code {ex.HResult.ToString(CultureInfo.InvariantCulture)}), so this check could " +
                    "not answer, and a check that cannot answer refuses");
            }

            var parent = Path.GetDirectoryName(
                current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent)) return new WalkAnswer(false, string.Empty);
            current = parent;
        }
    }

    /// <summary>
    /// The first link on the way down from the folder the run was asked about to the item - the item
    /// itself included, the folder asked about not - or null. Asked on the path as spelled, before
    /// any resolution, so resolution can never hide a junction it should have caught.
    /// </summary>
    private WalkAnswer FindLinkOnTheWayDown(string itemPath)
    {
        var root = Path.GetFullPath(_options.ScanRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(itemPath);

        while (true)
        {
            string? linkTarget;
            try
            {
                if (Directory.Exists(current))
                    linkTarget = new DirectoryInfo(current).LinkTarget;
                else if (File.Exists(current))
                    linkTarget = new FileInfo(current).LinkTarget;
                else
                    return new WalkAnswer(true,
                        $"refusal 4, {GateOutcome.WordsFor(RefusalCheck.LinkOrJunction)}: {current} is not there " +
                        "to be asked, so this check could not answer, and a check that cannot answer refuses");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new WalkAnswer(true,
                    $"refusal 4, {GateOutcome.WordsFor(RefusalCheck.LinkOrJunction)}: {current} could not be " +
                    $"asked (code {ex.HResult.ToString(CultureInfo.InvariantCulture)}), so this check could not " +
                    "answer, and a check that cannot answer refuses");
            }

            if (linkTarget is not null)
                return new WalkAnswer(true,
                    $"refusal 4, {GateOutcome.WordsFor(RefusalCheck.LinkOrJunction)}: {current} is a link that " +
                    $"names {linkTarget}, and the item stands somewhere other than where the path says");

            var parent = Path.GetDirectoryName(
                current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent))
                return new WalkAnswer(false, string.Empty);

            if (string.Equals(
                    parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root,
                    _pathComparison))
                return new WalkAnswer(false, string.Empty);

            current = parent;
        }
    }

    private static bool PathExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && (Directory.Exists(path) || File.Exists(path));

    private static string MomentWords(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'universal time'", CultureInfo.InvariantCulture);
}
