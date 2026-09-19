using CcDirector.Reclaim.Reporting;
using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Scanning;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The whole set of recommendations: what it says when the rules worked, and what it says when they
/// could not.
///
/// The failure this guards against is the one that runs through the whole mission: an answer of
/// "nothing to remove" that no rule ever actually reached. A report built from no rules at all, or
/// on a scan that was itself broken, must say so rather than print a tidy zero.
/// </summary>
public class RecommendationBuilderTests
{
    [Fact]
    public void Build_RulesThatFoundThings_AddsThemUpAndIsOk()
    {
        using var tree = StandardFixture.Build(nameof(Build_RulesThatFoundThings_AddsThemUpAndIsOk));
        var scan = Report(tree.Root);

        var report = RecommendationBuilder.Build(
            scan,
            [Finding("one", RuleVerdict.Ok, 400), Finding("two", RuleVerdict.Ok, 600)],
            []);

        Assert.Equal(ReportVerdict.Ok, report.Verdict);
        Assert.Null(report.BrokenReason);
        Assert.Equal(1000, report.ReclaimableBytes);
        Assert.Equal(2, report.ItemsOffered);
        Assert.Empty(report.BrokenRules);
    }

    /// <summary>
    /// No rule ran at all. There is nothing behind an answer of nothing to remove, so the whole report
    /// is a broken instrument - the same standard the scan itself is held to.
    /// </summary>
    [Fact]
    public void Build_NoRulesAtAll_ReportsBrokenRatherThanNothingToRemove()
    {
        using var tree = StandardFixture.Build(nameof(Build_NoRulesAtAll_ReportsBrokenRatherThanNothingToRemove));

        var report = RecommendationBuilder.Build(Report(tree.Root), [], []);

        Assert.Equal(ReportVerdict.Broken, report.Verdict);
        Assert.Contains("no rule ran at all", report.BrokenReason!, StringComparison.Ordinal);
        Assert.Contains("verdict: broken", report.Lines);
    }

    /// <summary>
    /// Recommendations rest on a scan. A scan that is itself a broken instrument leaves them resting
    /// on nothing, however well the rules ran.
    /// </summary>
    [Fact]
    public void Build_AScanThatIsItselfBroken_MakesTheWholeAnswerBroken()
    {
        using var empty = new FixtureTree(nameof(Build_AScanThatIsItselfBroken_MakesTheWholeAnswerBroken));
        var scan = Report(empty.Root);
        Assert.Equal(ReportVerdict.Broken, scan.Verdict);

        var report = RecommendationBuilder.Build(scan, [Finding("one", RuleVerdict.Ok, 400)], []);

        Assert.Equal(ReportVerdict.Broken, report.Verdict);
        Assert.Contains("rest on nothing", report.BrokenReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// One rule failing does not take the rest down with it. A broken rule offers nothing and says
    /// why; the rules beside it are still good and are still counted.
    /// </summary>
    [Fact]
    public void Build_OneBrokenRuleAmongGoodOnes_LeavesTheOthersStandingAndWarns()
    {
        using var tree = StandardFixture.Build(nameof(Build_OneBrokenRuleAmongGoodOnes_LeavesTheOthersStandingAndWarns));

        var report = RecommendationBuilder.Build(
            Report(tree.Root),
            [Finding("good", RuleVerdict.Ok, 400), Finding("bad", RuleVerdict.Broken, 0)],
            []);

        Assert.Equal(ReportVerdict.Ok, report.Verdict);
        Assert.Equal(400, report.ReclaimableBytes);
        Assert.Equal("bad", Assert.Single(report.BrokenRules).RuleId);
        Assert.Contains(report.Lines, line => line.StartsWith("warning: 1 of 2 rules", StringComparison.Ordinal));
    }

    /// <summary>
    /// The recommendations must say how far the scan beneath them reached. A set of recommendations
    /// built on a scan that could not see a third of the disk would read as a complete answer when it
    /// is not one.
    /// </summary>
    [Fact]
    public void Build_TheScansOwnReachLines_AreCarriedIntoTheRecommendationsWordForWord()
    {
        using var tree = StandardFixture.Build(nameof(Build_TheScansOwnReachLines_AreCarriedIntoTheRecommendationsWordForWord));
        var scan = Report(tree.Root);

        var report = RecommendationBuilder.Build(scan, [Finding("one", RuleVerdict.Ok, 10)], []);

        Assert.NotEmpty(scan.ReachLines);
        foreach (var line in scan.ReachLines)
            Assert.Contains(line, report.Lines);

        Assert.Contains(report.Lines, line => line.StartsWith("unseen: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WhatNoRuleMatched_IsReportedAndSaidToBeNeverOffered()
    {
        using var tree = StandardFixture.Build(nameof(Build_WhatNoRuleMatched_IsReportedAndSaidToBeNeverOffered));
        var scan = Report(tree.Root);

        var report = RecommendationBuilder.Build(scan, [Finding("one", RuleVerdict.Ok, 60)], []);

        Assert.Equal(scan.Scan.BytesSeen - 60, report.UnclassifiedBytes);
        Assert.False(report.RulesSawMoreThanTheScan);
        Assert.Contains(report.Lines, line =>
            line.StartsWith("unclassified: ", StringComparison.Ordinal) &&
            line.Contains("never offered for removal", StringComparison.Ordinal));
    }

    /// <summary>
    /// When the rules measured more than the scan managed to see, the difference is not a count of
    /// anything - it would be floored at nothing and read as a measurement. The report says what
    /// happened instead of printing that zero.
    /// </summary>
    [Fact]
    public void Build_RulesThatMeasuredMoreThanTheScanSaw_SaysSoRatherThanPrintingAFlooredZero()
    {
        using var tree = StandardFixture.Build(nameof(Build_RulesThatMeasuredMoreThanTheScanSaw_SaysSoRatherThanPrintingAFlooredZero));
        var scan = Report(tree.Root);

        var report = RecommendationBuilder.Build(
            scan, [Finding("one", RuleVerdict.Ok, scan.Scan.BytesSeen + 1_000_000)], []);

        Assert.True(report.RulesSawMoreThanTheScan);
        Assert.Contains(report.Lines, line =>
            line.StartsWith("unclassified: cannot be counted here", StringComparison.Ordinal));
    }

    /// <summary>
    /// A rule left out because it looks elsewhere is named with the folder it looks in, so a smaller
    /// answer can never be mistaken for a cleaner disk.
    /// </summary>
    [Fact]
    public void Build_RulesLeftOutBecauseTheyLookElsewhere_AreNamedWithWhereTheyLook()
    {
        using var tree = StandardFixture.Build(nameof(Build_RulesLeftOutBecauseTheyLookElsewhere_AreNamedWithWhereTheyLook));

        var report = RecommendationBuilder.Build(
            Report(tree.Root),
            [Finding("here", RuleVerdict.Ok, 10)],
            [new RuleNotRun("far-away", "A rule that looks elsewhere", "D:\\somewhere\\else")]);

        Assert.Contains(report.Lines, line => line.StartsWith("rules-not-run: 1 of this machine's", StringComparison.Ordinal));
        Assert.Contains(report.Lines, line => line.Contains("far-away", StringComparison.Ordinal));
        Assert.Contains(report.Lines, line => line.Contains("D:\\somewhere\\else", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every rule's own finished sentences reach the whole report, so a screen that prints the report
    /// prints the proof, what is lost and how to get it back without working any of it out again.
    /// </summary>
    [Fact]
    public void Build_EveryRulesOwnLines_ReachTheWholeReport()
    {
        using var tree = StandardFixture.Build(nameof(Build_EveryRulesOwnLines_ReachTheWholeReport));
        var finding = Finding("one", RuleVerdict.Ok, 10);

        var report = RecommendationBuilder.Build(Report(tree.Root), [finding], []);

        foreach (var line in finding.Lines)
            Assert.Contains(line, report.Lines);
    }

    private static ScanReport Report(string root) =>
        ScanReportBuilder.Build(new DirectoryScanner().Scan(new ScanOptions { RootPath = root }), 5);

    private static RuleFinding Finding(string id, RuleVerdict verdict, long bytes)
    {
        var finding = new RuleFinding
        {
            RuleId = id,
            RuleName = $"A rule called {id}",
            Proof = ProofKind.WeMadeIt,
            Verdict = verdict,
            BrokenReason = verdict == RuleVerdict.Broken ? "it could not read what it compares against" : null,
            WhatItRemoves = "what it removes",
            WhyItIsSafe = "why it is safe",
            WhatIsLost = "what is lost",
            HowToGetItBack = "how to get it back",
            AgeGateDays = 7,
            NeedsAdministrator = false,
            CommandToRun = null,
            Controls = [new RuleControl("things-read", 3, MustNotBeEmpty: true)],
            Candidates = verdict == RuleVerdict.Ok && bytes > 0
                ? [new ReclaimCandidate($"C:\\fixture\\{id}", bytes, DateTimeOffset.UnixEpoch, "because")]
                : [],
            Lines = []
        };

        return finding with { Lines = RuleFinding.Describe(finding) };
    }
}
