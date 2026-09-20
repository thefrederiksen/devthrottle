using System.Globalization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Reporting;
using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Scanning;

namespace CcDirector.Reclaim.Removal;

/// <summary>
/// What one item's reclaim decided. The gate's whole answer - all ten checks - travels with the item,
/// so a machine reading one item sees the reach of every refusal, not just the one that fired.
/// </summary>
public sealed record ReclaimItem
{
    /// <summary>The item's path, as the recommendation spelled it.</summary>
    public required string Path { get; init; }

    /// <summary>The rule that proved the item disposable.</summary>
    public required string RuleId { get; init; }

    /// <summary>Which of the three proofs the rule holds.</summary>
    public required ProofKind Proof { get; init; }

    /// <summary>The bytes the recommendation measured.</summary>
    public required long RecommendedBytes { get; init; }

    /// <summary>The gate's answer at the moment that decided this item.</summary>
    public required GateOutcome Gate { get; init; }

    /// <summary>True when the item was moved into holding. Never true in a dry run.</summary>
    public required bool Moved { get; init; }

    /// <summary>The holding entry the item moved into, when it moved.</summary>
    public required string? HoldingEntryId { get; init; }

    /// <summary>True when the owner's own cleanup command was run for this item.</summary>
    public required bool OwnersCommandRan { get; init; }

    /// <summary>The owner's command's exit code, when it ran.</summary>
    public required int? OwnersCommandExitCode { get; init; }

    /// <summary>
    /// Why the item did not move even though the gate passed it - a move the file system refused, a
    /// command that would not start - or the warning about an entry that moved but whose record
    /// could not be finalized. Null when the item moved cleanly or the gate refused it.
    /// </summary>
    public required string? OutcomeReason { get; init; }
}

/// <summary>What one whole reclaim run did, measured before and after.</summary>
public sealed record ReclaimRunResult
{
    /// <summary>True when the run had the apply flag; false when it was the dry run.</summary>
    public required bool Apply { get; init; }

    /// <summary>The folder the run was asked about.</summary>
    public required string RootPath { get; init; }

    /// <summary>The holding root the run would move into.</summary>
    public required string HoldingRootPath { get; init; }

    /// <summary>The one rule the run was narrowed to, when it was.</summary>
    public required string? RuleFilter { get; init; }

    /// <summary>Why the rule filter could not be used, naming the rules that do look inside.</summary>
    public required string? RuleFilterError { get; init; }

    /// <summary>Why the whole answer means nothing, or null.</summary>
    public required string? BrokenReason { get; init; }

    /// <summary>What every rule found at recommendation time.</summary>
    public required IReadOnlyList<RuleFinding> Findings { get; init; }

    /// <summary>The rules that were not run, each with the folder it looks in.</summary>
    public required IReadOnlyList<RuleNotRun> RulesNotRun { get; init; }

    /// <summary>Every item the rules offered, with the gate's answer for each.</summary>
    public required IReadOnlyList<ReclaimItem> Items { get; init; }

    /// <summary>What the volume said before anything happened.</summary>
    public required VolumeUsage VolumeBefore { get; init; }

    /// <summary>What the volume said after everything happened.</summary>
    public required VolumeUsage VolumeAfter { get; init; }

    /// <summary>The candidate bytes measured before anything happened.</summary>
    public required long CandidateBytesBefore { get; init; }

    /// <summary>The candidate bytes measured after everything happened: what remains of them.</summary>
    public required long CandidateBytesAfter { get; init; }

    /// <summary>The bytes of the items that actually moved into holding.</summary>
    public required long BytesMoved { get; init; }

    /// <summary>The finished sentences this report prints as they stand.</summary>
    public required IReadOnlyList<string> Lines { get; init; }
}

/// <summary>
/// What one reclaim run is asked for.
/// </summary>
public sealed record ReclaimRunRequest
{
    /// <summary>Every rule the machine has; the runner selects the ones that look inside the root.</summary>
    public required IReadOnlyList<IReclaimRule> Rules { get; init; }

    /// <summary>The folder the run was asked about, in its canonical full form.</summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// The holding root for the volume, computed by the caller. The engine holds no default of its
    /// own, so no test ever writes to the root of a real volume.
    /// </summary>
    public required string HoldingRootPath { get; init; }

    /// <summary>The protected paths, read from the storage resolver by the caller at the moment of the run.</summary>
    public required IReadOnlyList<CcStorage.ProtectedPath> ProtectedPaths { get; init; }

    /// <summary>The user's own folders, read from the system's own resolver by the caller.</summary>
    public required IReadOnlyList<string> UserFolders { get; init; }

    /// <summary>True only for an apply run. Dry run is the default, and the only other answer.</summary>
    public required bool Apply { get; init; }

    /// <summary>The moment the age gates are judged against.</summary>
    public required DateTimeOffset NowUtc { get; init; }

    /// <summary>How long a held item must stay before it is purgeable. Thirty days by default.</summary>
    public int HoldingPeriodDays { get; init; } = HoldingStore.DefaultHoldingPeriodDays;

    /// <summary>One rule to run, or null to run every rule that looks inside the root.</summary>
    public string? RuleId { get; init; }
}

/// <summary>
/// Runs one reclaim: selects the rules, examines, folds, measures, gates every candidate, and either
/// reports what it would move or moves exactly what the gate passed.
///
/// In an apply run the gate runs again immediately before each item's own move, because the disk
/// changes between one move and the next. An item whose proof is the owner's own cleanup command is
/// never held, because that command does not offer to put anything back - the rule's "what is lost"
/// already said so.
///
/// The report says plainly that a move to holding frees no space, and that space is freed only when
/// holding is purged. A caller who removed twenty-seven gigabytes and sees no free space is owed
/// that sentence before the fact, not after.
/// </summary>
public static class ReclaimRunner
{
    /// <summary>
    /// Run one reclaim.
    /// </summary>
    /// <param name="request">What was asked for.</param>
    public static ReclaimRunResult Run(ReclaimRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        FileLog.Write(
            $"[ReclaimRunner] Run: root={request.RootPath}, holding={request.HoldingRootPath}, " +
            $"apply={request.Apply}, rule={request.RuleId ?? "all"}");

        var selection = RuleSelection.For(request.Rules, request.RootPath);
        var toRun = selection.ToRun;
        var notRun = selection.NotRun.ToList();
        string? ruleFilterError = null;

        if (request.RuleId is not null)
        {
            var match = toRun.FirstOrDefault(rule =>
                rule.Id.Equals(request.RuleId, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                ruleFilterError = toRun.Count == 0
                    ? $"the rule {request.RuleId} is not among the rules that look inside {request.RootPath}, " +
                      "because no rule on this machine looks inside that folder"
                    : $"the rule {request.RuleId} is not among the rules that look inside {request.RootPath}; " +
                      "the rules that do are: " +
                      string.Join(", ", toRun.Select(rule => $"{rule.Id} (looks in {rule.LooksIn})"));
            }
            else
            {
                // The run is narrowed to one rule. The others that DO look inside are named as not
                // run, so a smaller answer can never be mistaken for a cleaner disk.
                notRun.AddRange(toRun.Where(rule => !ReferenceEquals(rule, match))
                    .Select(rule => new RuleNotRun(rule.Id, rule.Name, rule.LooksIn)));
                toRun = [match];
            }
        }

        if (toRun.Count == 0 && ruleFilterError is null)
        {
            var broken = new ReclaimRunResult
            {
                Apply = request.Apply,
                RootPath = request.RootPath,
                HoldingRootPath = request.HoldingRootPath,
                RuleFilter = request.RuleId,
                RuleFilterError = null,
                BrokenReason =
                    "no rule on this machine looks inside the folder that was asked about, so there is " +
                    "nothing behind this answer: a folder no rule applies to finds nothing to remove every " +
                    "time and looks exactly like a folder with nothing to remove",
                Findings = [],
                RulesNotRun = notRun,
                Items = [],
                VolumeBefore = VolumeReader.Read(request.RootPath),
                VolumeAfter = VolumeReader.Read(request.RootPath),
                CandidateBytesBefore = 0,
                CandidateBytesAfter = 0,
                BytesMoved = 0,
                Lines = []
            };
            return broken with { Lines = Describe(broken) };
        }

        // The recommendation pass. A broken rule offers nothing, exactly as it does in a
        // recommendation, and nothing it offered before it knew it was broken reaches the gate.
        var context = new RuleContext { ScanRootPath = request.RootPath, NowUtc = request.NowUtc };
        var examined = toRun
            .Select(rule => (Rule: rule, Finding: RuleFold.Fold(rule, rule.Examine(context))))
            .ToList();
        var findings = examined.Select(pair => pair.Finding).ToList();

        var pairs = new List<(IReclaimRule Rule, ReclaimCandidate Candidate)>();
        foreach (var (rule, finding) in examined.Where(pair => pair.Finding.Verdict == RuleVerdict.Ok))
        foreach (var candidate in finding.Candidates)
            pairs.Add((rule, candidate));

        var volumeBefore = VolumeReader.Read(request.RootPath);
        var candidateBytesBefore = pairs.Sum(pair => pair.Candidate.Bytes);

        var gate = new RefusalGate(new RefusalGateOptions
        {
            ProtectedPaths = request.ProtectedPaths,
            UserFolders = request.UserFolders,
            HoldingRootPath = request.HoldingRootPath,
            ScanRootPath = request.RootPath,
            Apply = request.Apply,
            NowUtc = request.NowUtc
        });

        // The report pass: every candidate is put through the gate, in a dry run and in an apply
        // alike, so the dry run is not a simulation and the two can never disagree about what is
        // eligible. In a dry run this pass is the whole of the run and its outcomes are the answer;
        // in an apply the gate runs again immediately before each item's own move, and that second
        // answer is the one the item carries.
        var reportOutcomes = pairs.Select(pair => gate.Check(pair.Candidate, pair.Rule)).ToList();

        var items = new List<ReclaimItem>();
        if (!request.Apply)
        {
            for (var index = 0; index < pairs.Count; index++)
            {
                var (rule, candidate) = pairs[index];
                items.Add(new ReclaimItem
                {
                    Path = candidate.Path,
                    RuleId = rule.Id,
                    Proof = rule.Proof,
                    RecommendedBytes = candidate.Bytes,
                    Gate = reportOutcomes[index],
                    Moved = false,
                    HoldingEntryId = null,
                    OwnersCommandRan = false,
                    OwnersCommandExitCode = null,
                    OutcomeReason = null
                });
            }
        }
        else
        {
            var holding = new HoldingStore(request.HoldingRootPath);
            var commandRunner = new OwnerCommandRunner();

            foreach (var (rule, candidate) in pairs)
            {
                // The gate runs again immediately before this item's own move, against the disk as it
                // is at this moment and not as it was when the run started.
                var outcome = gate.Check(candidate, rule);

                if (!outcome.Eligible)
                {
                    items.Add(new ReclaimItem
                    {
                        Path = candidate.Path,
                        RuleId = rule.Id,
                        Proof = rule.Proof,
                        RecommendedBytes = candidate.Bytes,
                        Gate = outcome,
                        Moved = false,
                        HoldingEntryId = null,
                        OwnersCommandRan = false,
                        OwnersCommandExitCode = null,
                        OutcomeReason = null
                    });
                    continue;
                }

                if (rule.Proof == ProofKind.OwnersOwnCommand)
                {
                    // An item cleared by the owner's own command is never held, never restored, and
                    // offered no way back: the command does not allow it and the rule's "what is
                    // lost" already said so. The command runs verbatim, in the rule's own folder when
                    // there is one.
                    var workingDirectory = Directory.Exists(rule.LooksIn) ? rule.LooksIn : request.RootPath;
                    var command = commandRunner.Run(rule.CommandToRun ?? string.Empty, workingDirectory);
                    items.Add(new ReclaimItem
                    {
                        Path = candidate.Path,
                        RuleId = rule.Id,
                        Proof = rule.Proof,
                        RecommendedBytes = candidate.Bytes,
                        Gate = outcome,
                        Moved = false,
                        HoldingEntryId = null,
                        OwnersCommandRan = command.Ran,
                        OwnersCommandExitCode = command.ExitCode,
                        OutcomeReason = command.RefusalReason
                    });
                    continue;
                }

                var hold = holding.Hold(candidate, rule.Id, request.NowUtc, request.HoldingPeriodDays);
                items.Add(new ReclaimItem
                {
                    Path = candidate.Path,
                    RuleId = rule.Id,
                    Proof = rule.Proof,
                    RecommendedBytes = candidate.Bytes,
                    Gate = outcome,
                    Moved = hold.Held,
                    HoldingEntryId = hold.EntryId,
                    OwnersCommandRan = false,
                    OwnersCommandExitCode = null,
                    OutcomeReason = hold.RefusalReason ?? hold.IncompleteReason
                });
            }
        }

        var volumeAfter = VolumeReader.Read(request.RootPath);
        var candidateBytesAfter = pairs
            .Where(pair => Directory.Exists(pair.Candidate.Path) || File.Exists(pair.Candidate.Path))
            .Sum(pair => FolderMeasures.Measure(pair.Candidate.Path).Bytes);
        var bytesMoved = items.Where(item => item.Moved).Sum(item => item.RecommendedBytes);

        var result = new ReclaimRunResult
        {
            Apply = request.Apply,
            RootPath = request.RootPath,
            HoldingRootPath = request.HoldingRootPath,
            RuleFilter = request.RuleId,
            RuleFilterError = ruleFilterError,
            BrokenReason = null,
            Findings = findings,
            RulesNotRun = notRun,
            Items = items,
            VolumeBefore = volumeBefore,
            VolumeAfter = volumeAfter,
            CandidateBytesBefore = candidateBytesBefore,
            CandidateBytesAfter = candidateBytesAfter,
            BytesMoved = bytesMoved,
            Lines = []
        };

        var complete = result with { Lines = Describe(result) };
        FileLog.Write(
            $"[ReclaimRunner] Run done: root={request.RootPath}, apply={request.Apply}, items={complete.Items.Count}, " +
            $"moved={complete.Items.Count(item => item.Moved)}, bytesMoved={complete.BytesMoved}");
        return complete;
    }

    private static IReadOnlyList<string> Describe(ReclaimRunResult result)
    {
        var lines = new List<string>
        {
            result.Apply ? "apply: this run moves what every refusal check passes" : "apply: no - this is a dry run, nothing moves",
            $"root: {result.RootPath}",
            $"holding-root: {result.HoldingRootPath}"
        };

        if (result.RuleFilterError is not null)
        {
            lines.Add($"error: {result.RuleFilterError}");
            return lines;
        }

        if (result.BrokenReason is not null)
        {
            lines.Add($"verdict: broken");
            lines.Add($"reason: {result.BrokenReason}");
            return lines;
        }

        lines.Add(result.Apply ? "verdict: ok, applied" : "verdict: ok, dry run");
        lines.Add($"rules-run: {result.Findings.Count.ToString(CultureInfo.InvariantCulture)}");
        var brokenRules = result.Findings
            .Where(finding => finding.Verdict == RuleVerdict.Broken)
            .ToList();
        lines.Add($"rules-broken: {brokenRules.Count.ToString(CultureInfo.InvariantCulture)}");

        var eligible = result.Items.Where(item => item.Gate.Eligible).ToList();
        var refused = result.Items.Where(item => !item.Gate.Eligible).ToList();

        if (result.Apply)
        {
            var moved = result.Items.Where(item => item.Moved).ToList();
            lines.Add($"items-moved: {moved.Count.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"bytes-moved: {SizeText.Exact(result.BytesMoved)}");
        }
        else
        {
            lines.Add($"items-would-move: {eligible.Count.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"bytes-would-move: {SizeText.Exact(eligible.Sum(item => item.RecommendedBytes))}");
        }

        lines.Add($"items-refused: {refused.Count.ToString(CultureInfo.InvariantCulture)}");

        if (!result.Apply && eligible.Count > 0)
        {
            lines.Add(
                "refusal 9 fired at the run level: this run was made without the apply flag, so nothing " +
                "moves however many checks passed; every check below ran for real, and an apply of the " +
                "same run moves exactly the items named here");
        }

        lines.Add($"candidate-bytes-before: {SizeText.Exact(result.CandidateBytesBefore)}");
        lines.Add($"candidate-bytes-after: {SizeText.Exact(result.CandidateBytesAfter)}");
        VolumeLine(lines, "volume-free-before: ", result.VolumeBefore.FreeBytes, result.VolumeBefore);
        VolumeLine(lines, "volume-free-after: ", result.VolumeAfter.FreeBytes, result.VolumeAfter);

        // The plain sentence the mandate asks for, before the fact and not after it.
        lines.Add(
            "space: a move to holding frees no space. The items above sit in the holding folder and can " +
            "be restored until it is purged; space is freed only when holding is purged, which is its own " +
            "explicit command");

        if (result.RulesNotRun.Count > 0)
        {
            lines.Add(
                $"rules-not-run: {result.RulesNotRun.Count.ToString(CultureInfo.InvariantCulture)} of this " +
                "machine's rules look outside the folder that was asked about and were not run; each is named below");
        }

        if (brokenRules.Count > 0)
        {
            lines.Add(
                "warning: " + $"{brokenRules.Count.ToString(CultureInfo.InvariantCulture)} of " +
                $"{result.Findings.Count.ToString(CultureInfo.InvariantCulture)} rules could not do their work; " +
                "each says why below, and none of them offers anything");
        }

        foreach (var item in result.Items)
        {
            lines.Add(string.Empty);
            lines.Add($"item: {item.Path}");
            lines.Add($"rule: {item.RuleId}");

            if (!item.Gate.Eligible)
            {
                lines.Add($"refused: {item.Gate.Reason ?? item.Gate.ConfigurationReason}");
            }
            else if (item.Moved)
            {
                lines.Add($"moved: yes, {SizeText.Exact(item.RecommendedBytes)}, holding entry {item.HoldingEntryId}");
            }
            else if (item.OwnersCommandRan)
            {
                lines.Add(
                    $"command: ran, exit code {item.OwnersCommandExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
            }
            else if (!result.Apply)
            {
                lines.Add($"would-move: yes, {SizeText.Exact(item.RecommendedBytes)}");
            }

            if (item.OutcomeReason is not null)
                lines.Add($"outcome: {item.OutcomeReason}");
        }

        foreach (var finding in result.Findings)
        {
            lines.Add(string.Empty);
            lines.AddRange(finding.Lines);
        }

        lines.AddRange(AxiOutput.List(
            "rules-not-run",
            ["rule", "name", "looks-in"],
            result.RulesNotRun.Select(rule => (IReadOnlyList<string>)
            [
                AxiOutput.Value(rule.RuleId),
                AxiOutput.Value(rule.RuleName),
                AxiOutput.Value(rule.LooksIn)
            ]).ToList()));

        return lines;
    }

    private static void VolumeLine(List<string> lines, string label, long bytes, VolumeUsage volume)
    {
        lines.Add(volume.Available
            ? label + SizeText.Exact(bytes)
            : $"{label}unknown, because {volume.UnavailableReason}");
    }
}
