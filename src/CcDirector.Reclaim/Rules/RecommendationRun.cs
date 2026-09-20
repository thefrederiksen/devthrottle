using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Reporting;
using CcDirector.Reclaim.Scanning;

namespace CcDirector.Reclaim.Rules;

/// <summary>
/// Makes the recommendations for one scan: chooses the rules that look inside the folder that was
/// scanned, asks each one, folds each answer, and builds the finished report.
///
/// It is one method because there are two callers - the command line tool, when a person asks, and
/// the background scan the Launcher hosts, when nobody does - and the two must never come to differ
/// in which rules they run or how an answer is judged. Every rule goes through <see cref="RuleFold"/>
/// here, whoever asked.
/// </summary>
public static class RecommendationRun
{
    /// <summary>
    /// Make the recommendations against a scan.
    /// </summary>
    /// <param name="scan">The scan they are made against.</param>
    /// <param name="rules">Every rule this machine has.</param>
    /// <param name="nowUtc">The moment age gates are judged against.</param>
    /// <param name="largestFolders">How many of the largest folders the scan report names.</param>
    public static RecommendationReport Against(
        ScanResult scan,
        IReadOnlyList<IReclaimRule> rules,
        DateTimeOffset nowUtc,
        int largestFolders = ScanReportBuilder.DefaultLargestFolders)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(rules);
        FileLog.Write($"[RecommendationRun] Against: root={scan.RootPath}, rules={rules.Count}");

        var scanReport = ScanReportBuilder.Build(scan, largestFolders);

        var context = new RuleContext
        {
            ScanRootPath = scan.RootPath,
            NowUtc = nowUtc
        };

        // Only the rules that look inside the folder that was scanned. The rest are named in the
        // answer, never merely left out.
        var selection = RuleSelection.For(rules, scan.RootPath);

        var findings = selection.ToRun
            .Select(rule => RuleFold.Fold(rule, rule.Examine(context)))
            .ToList();

        var report = RecommendationBuilder.Build(scanReport, findings, selection.NotRun);

        FileLog.Write(
            $"[RecommendationRun] Against done: root={scan.RootPath}, run={findings.Count}, " +
            $"notRun={selection.NotRun.Count}, verdict={report.Verdict}");
        return report;
    }
}
