using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The component store rule, against a question the test decides the answer to.
///
/// This is the one rule whose subject cannot be measured by walking a folder: most of the store's
/// files are hard links, and only Windows knows which bytes belong to the store alone. So the whole
/// question is what the rule does when Windows answers, and - the case the mandate names - what it
/// does when Windows cannot be asked. A rule that cannot determine the size reports a control
/// counting nought and says BROKEN, never nothing to remove.
/// </summary>
public class ComponentStoreRuleTests
{
    [Fact]
    public void Examine_AWindowsThatAnswered_OffersTheStoreSizedByItsOwnAnalysis()
    {
        var rule = new ComponentStoreRule(
            new AnsweredAnalysis(actualSizeBytes: 9_000_000_000, bytesItsCommandCanClear: 1_600_000_000),
            @"C:\Windows\WinSxS");
        var finding = Fold(rule);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        var offered = Assert.Single(finding.Candidates);
        Assert.Equal(@"C:\Windows\WinSxS", offered.Path);
        Assert.Equal(1_600_000_000, offered.Bytes);
        Assert.Equal(1, Control(finding, "component-store-size-known"));
    }

    /// <summary>
    /// The case the mandate names. The mission has never measured the component store here, and
    /// asking needs an administrator this tool never raises itself to - so on an ordinary run the
    /// size cannot be determined. That is a control counting nought and a BROKEN verdict with the
    /// reason said, not an answer of nothing to remove, which is what a clean disk would say.
    /// </summary>
    [Fact]
    public void Examine_AWindowsThatCouldNotBeAsked_ReportsBrokenWithAControlCountingNought()
    {
        var rule = new ComponentStoreRule(
            new UnansweredAnalysis("asking Windows how big the component store is needs an administrator"),
            @"C:\Windows\WinSxS");

        var finding = Fold(rule);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(0, Control(finding, "component-store-size-known"));
        Assert.Contains("needs an administrator", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Contains("verdict: broken", finding.Lines);
    }

    [Fact]
    public void Examine_AComponentStoreThatIsNotThere_ReportsBroken()
    {
        var rule = new ComponentStoreRule(
            new AnsweredAnalysis(1, 1),
            Path.Combine(Path.GetTempPath(), "cc-reclaim-tests", "a-store-that-is-not-there"));

        var finding = Fold(rule);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Contains("is not there", finding.BrokenReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Examine_AMachineThatIsNotWindows_ReportsBroken()
    {
        var rule = new ComponentStoreRule(
            new NotWindowsAnalysis(), @"C:\Windows\WinSxS");

        var finding = Fold(rule);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Contains("not running Windows", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Empty(finding.Candidates);
    }

    /// <summary>
    /// The items this rule names are cleared by the owner's own command, which does not offer to
    /// put anything back, so they are not offered for holding - and the mandate requires the
    /// rule's "what is lost" to say so plainly.
    /// </summary>
    [Fact]
    public void Examine_WhatIsLost_SaysNothingIsHeldBecauseTheCommandPutsNothingBack()
    {
        var finding = Fold(new ComponentStoreRule(
            new AnsweredAnalysis(1, 1), @"C:\Windows\WinSxS"));
        var text = string.Join("\n", finding.Lines);

        Assert.Contains("Nothing is held for this rule", text, StringComparison.Ordinal);
        Assert.Contains("how-to-get-it-back: there is no way back", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule never raises itself to an administrator: it says it needs one and prints the exact
    /// command the owner runs.
    /// </summary>
    [Fact]
    public void Examine_TheRule_NeedsAnAdministratorAndPrintsTheOwnersCleanupCommand()
    {
        var finding = Fold(new ComponentStoreRule(
            new AnsweredAnalysis(1, 1), @"C:\Windows\WinSxS"));
        var text = string.Join("\n", finding.Lines);

        Assert.True(finding.NeedsAdministrator);
        Assert.Contains("needs-administrator: yes", text, StringComparison.Ordinal);
        Assert.Equal("Dism.exe /Online /Cleanup-Image /StartComponentCleanup", finding.CommandToRun);
        Assert.Contains("command: Dism.exe /Online /Cleanup-Image /StartComponentCleanup", text, StringComparison.Ordinal);
    }

    private static RuleFinding Fold(ComponentStoreRule rule) =>
        RuleFold.Fold(rule, rule.Examine(new RuleContext
        {
            ScanRootPath = @"C:\",
            NowUtc = DateTimeOffset.UtcNow
        }));

    private static long Control(RuleFinding finding, string name) =>
        finding.Controls.Single(control => control.Name == name).Count;

    private sealed class AnsweredAnalysis(long actualSizeBytes, long bytesItsCommandCanClear)
        : IComponentStoreAnalysis
    {
        public ComponentStoreQuestion Ask() =>
            new(true, actualSizeBytes, bytesItsCommandCanClear, null);
    }

    private sealed class UnansweredAnalysis(string reason) : IComponentStoreAnalysis
    {
        public ComponentStoreQuestion Ask() => ComponentStoreQuestion.Unanswered(reason);
    }

    private sealed class NotWindowsAnalysis : IComponentStoreAnalysis
    {
        public ComponentStoreQuestion Ask() =>
            throw new PlatformNotSupportedException(
                "The Windows component store analysis is a Windows command, and this machine is not running Windows.");
    }
}
