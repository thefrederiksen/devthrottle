using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Reporting;

namespace CcDirector.Reclaim.Rules;

/// <summary>
/// A finished set of recommendations for one disk.
///
/// The engine decides what the rules mean and writes the sentences; nothing that renders this works
/// any of it out again - critical rule 7 in CLAUDE.md. The Director's screen in a later phase prints
/// <see cref="Lines"/> as they stand, which is why every judgement is made here and none of it is
/// left for a screen to guess at.
/// </summary>
public sealed record RecommendationReport
{
    /// <summary>The report on the scan these recommendations were made against.</summary>
    public required ScanReport Scan { get; init; }

    /// <summary>Whether this set of recommendations can be believed at all.</summary>
    public required ReportVerdict Verdict { get; init; }

    /// <summary>
    /// Why the recommendations are a broken instrument, in one finished sentence, or null when they
    /// are not. An individual rule that could not do its work does NOT put the whole report here -
    /// it says so on its own and offers nothing, and the rules beside it are still good. This is for
    /// the case where the whole answer means nothing: no rule ran at all, or the scan underneath it
    /// was itself broken.
    /// </summary>
    public required string? BrokenReason { get; init; }

    /// <summary>What every rule found, in the order the rules were run.</summary>
    public required IReadOnlyList<RuleFinding> Findings { get; init; }

    /// <summary>
    /// The machine's rules that were NOT run, because they look somewhere outside the folder that was
    /// asked about. They are named rather than passed over: a tool that quietly ran fewer rules than
    /// it has would report less to remove and read exactly like a cleaner disk.
    /// </summary>
    public required IReadOnlyList<RuleNotRun> RulesNotRun { get; init; }

    /// <summary>The rules that could not do their work and therefore offer nothing.</summary>
    public IReadOnlyList<RuleFinding> BrokenRules =>
        Findings.Where(finding => finding.Verdict == RuleVerdict.Broken).ToList();

    /// <summary>The bytes every rule that did its work proved disposable, added together.</summary>
    public long ReclaimableBytes =>
        Findings.Where(finding => finding.Verdict == RuleVerdict.Ok).Sum(finding => finding.CandidateBytes);

    /// <summary>How many items are offered across every rule.</summary>
    public long ItemsOffered =>
        Findings.Where(finding => finding.Verdict == RuleVerdict.Ok).Sum(finding => (long)finding.Candidates.Count);

    /// <summary>
    /// The bytes the scan saw that no rule matched. These are never offered for removal, whatever
    /// they are and however large they grow: the tool enumerates what to remove, never what to skip.
    ///
    /// Every rule that ran looks inside the folder that was scanned, so this really is a subtraction
    /// of one part of what was seen from the whole of it. Where the rules found MORE than the scan
    /// saw - which happens when the scan could not read a folder the rules could - the difference
    /// falls below nought, and <see cref="RulesSawMoreThanTheScan"/> says so rather than letting the
    /// number be quietly floored at nothing.
    /// </summary>
    public long UnclassifiedBytes => Math.Max(0L, Scan.Scan.BytesSeen - ReclaimableBytes);

    /// <summary>
    /// True when the rules measured more bytes than the scan managed to see. It means the scan could
    /// not read something the rules could, so the unclassified number below would otherwise be a
    /// floor rather than a measurement.
    /// </summary>
    public bool RulesSawMoreThanTheScan => ReclaimableBytes > Scan.Scan.BytesSeen;

    /// <summary>The report itself: finished sentences, printed as they stand.</summary>
    public required IReadOnlyList<string> Lines { get; init; }
}

/// <summary>Builds the finished recommendations from a scan report and what the rules found.</summary>
public static class RecommendationBuilder
{
    /// <summary>
    /// Build the recommendations.
    /// </summary>
    /// <param name="scan">The report on the scan they are made against.</param>
    /// <param name="findings">What every rule found, in the order the rules were run.</param>
    /// <param name="notRun">The machine's rules that look outside this folder and were left out.</param>
    public static RecommendationReport Build(
        ScanReport scan,
        IReadOnlyList<RuleFinding> findings,
        IReadOnlyList<RuleNotRun> notRun)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(notRun);
        FileLog.Write($"[RecommendationBuilder] Build: root={scan.Scan.RootPath}, rules={findings.Count}");

        var brokenReason = FindBrokenReason(scan, findings);
        var report = new RecommendationReport
        {
            Scan = scan,
            RulesNotRun = notRun,
            Verdict = brokenReason is null ? ReportVerdict.Ok : ReportVerdict.Broken,
            BrokenReason = brokenReason,
            Findings = findings,
            Lines = []
        };
        var complete = report with { Lines = Describe(report) };

        FileLog.Write(
            $"[RecommendationBuilder] Build done: root={scan.Scan.RootPath}, " +
            $"reclaimable={complete.ReclaimableBytes}, broken={complete.BrokenRules.Count}");
        return complete;
    }

    // The instrument check for the whole answer. A rule that could not do its work says so by itself
    // and the rules beside it still stand; these two are different, because they leave the reader
    // with an answer of "nothing to remove" that no rule ever actually reached.
    private static string? FindBrokenReason(ScanReport scan, IReadOnlyList<RuleFinding> findings)
    {
        if (findings.Count == 0)
        {
            return "no rule ran at all, so there is nothing behind this answer: a disk with no rules " +
                   "applied to it finds nothing every time and looks exactly like a disk with nothing to remove";
        }

        if (scan.Verdict == ReportVerdict.Broken)
        {
            return "the scan these recommendations were made against is a broken instrument, so they " +
                   $"rest on nothing: {scan.BrokenReason}";
        }

        return null;
    }

    private static IReadOnlyList<string> Describe(RecommendationReport report)
    {
        var lines = new List<string>
        {
            $"verdict: {(report.Verdict == ReportVerdict.Ok ? "ok" : "broken")}"
        };

        if (report.BrokenReason is not null)
            lines.Add($"reason: {report.BrokenReason}");

        lines.AddRange(
        [
            $"root: {report.Scan.Scan.RootPath}",
            $"rules-run: {report.Findings.Count.ToString(CultureInfo.InvariantCulture)}",
            $"rules-broken: {report.BrokenRules.Count.ToString(CultureInfo.InvariantCulture)}",
            $"items-offered: {report.ItemsOffered.ToString(CultureInfo.InvariantCulture)}",
            $"reclaimable: {SizeText.Exact(report.ReclaimableBytes)}"
        ]);

        // The report's reach, carried straight from the scan report's own list so the two can never
        // disagree and a reworded line there can never leave this report silently without one. It is
        // required output: recommendations built on a scan that could not see a third of the disk
        // would read as a complete answer when they are not one.
        lines.AddRange(report.Scan.ReachLines);

        // Said plainly rather than left to be worked out: everything the scan saw that no rule
        // matched is reported and never offered. The tool enumerates what to remove, never what to
        // skip, so the size of this number is not a problem to be solved by loosening a rule.
        lines.Add(report.RulesSawMoreThanTheScan
            ? "unclassified: cannot be counted here, because the rules measured more bytes than the scan " +
              "managed to see - the scan could not read something the rules could, and the folders it was " +
              "refused are named in the report of that scan"
            : $"unclassified: {SizeText.Exact(report.UnclassifiedBytes)} seen by the scan and matched by no rule, " +
              "which is never offered for removal");

        // Rules left out are named, never merely absent, so a smaller answer can never be mistaken
        // for a cleaner disk.
        if (report.RulesNotRun.Count > 0)
        {
            lines.Add(
                $"rules-not-run: {report.RulesNotRun.Count.ToString(CultureInfo.InvariantCulture)} of this " +
                "machine's rules look outside the folder that was asked about and were not run; each is named below");
        }

        if (report.BrokenRules.Count > 0)
        {
            lines.Add(
                "warning: " +
                $"{report.BrokenRules.Count.ToString(CultureInfo.InvariantCulture)} of " +
                $"{report.Findings.Count.ToString(CultureInfo.InvariantCulture)} rules could not do their work; " +
                "each says why below, and none of them offers anything");
        }

        foreach (var finding in report.Findings)
        {
            lines.Add(string.Empty);
            lines.AddRange(finding.Lines);
        }

        lines.AddRange(AxiOutput.List(
            "rules-not-run",
            ["rule", "name", "looks-in"],
            report.RulesNotRun.Select(rule => (IReadOnlyList<string>)
            [
                AxiOutput.Value(rule.RuleId),
                AxiOutput.Value(rule.RuleName),
                AxiOutput.Value(rule.LooksIn)
            ]).ToList()));

        return lines;
    }
}
