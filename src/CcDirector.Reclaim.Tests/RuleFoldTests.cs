using CcDirector.Reclaim.Rules;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The one place that decides whether a rule's answer can be believed.
///
/// Every test here is about the same failure: a rule that could not do its work producing an answer
/// that reads exactly like a clean disk. That is the failure this whole mission exists to prevent,
/// and the reason the decision is made here once rather than inside each rule.
/// </summary>
public class RuleFoldTests
{
    [Fact]
    public void Fold_ARuleThatDidItsWork_IsOkAndKeepsEveryCandidate()
    {
        var rule = new StubRule();
        var answer = new RuleAnswer(
            [new RuleControl("records-read", 5, MustNotBeEmpty: true)],
            [Candidate("one", 100), Candidate("two", 250)]);

        var finding = RuleFold.Fold(rule, answer);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Null(finding.BrokenReason);
        Assert.Equal(2, finding.Candidates.Count);
        Assert.Equal(350, finding.CandidateBytes);
    }

    /// <summary>
    /// The mission's hardest requirement, in one test. A rule that compares two lists and finds one of
    /// them empty must say it is broken, because a comparison against an empty list finds nothing
    /// every single time and is indistinguishable from a disk with nothing on it to remove.
    /// </summary>
    [Fact]
    public void Fold_AControlThatMustNotBeEmptyIsNought_ReportsBrokenRatherThanNothingToRemove()
    {
        var rule = new StubRule();
        var answer = new RuleAnswer(
            [
                new RuleControl("records-read", 0, MustNotBeEmpty: true),
                new RuleControl("candidates-examined", 800, MustNotBeEmpty: true)
            ],
            []);

        var finding = RuleFold.Fold(rule, answer);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.NotNull(finding.BrokenReason);
        Assert.Contains("records-read", finding.BrokenReason, StringComparison.Ordinal);
        Assert.Contains("could not do its work", finding.BrokenReason, StringComparison.Ordinal);
        Assert.Contains("verdict: broken", finding.Lines);
    }

    /// <summary>
    /// A broken rule offers nothing, whatever it happened to collect before it found out it was
    /// broken. Carrying those items through would put them in front of a reader under an answer the
    /// rule itself has disowned.
    /// </summary>
    [Fact]
    public void Fold_ABrokenRule_OffersNothingEvenThoughItCollectedCandidates()
    {
        var rule = new StubRule();
        var answer = new RuleAnswer(
            [new RuleControl("records-read", 0, MustNotBeEmpty: true)],
            [Candidate("collected-before-it-knew", 9_000_000)]);

        var finding = RuleFold.Fold(rule, answer);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(0, finding.CandidateBytes);
    }

    /// <summary>
    /// A rule that reports no controls has said nothing about whether it worked, so its empty answer
    /// cannot be told apart from a failure and is not accepted as one. This is what stops a rule
    /// written later from failing open by simply not counting anything.
    /// </summary>
    [Fact]
    public void Fold_ARuleThatCountedNothingAtAll_ReportsBroken()
    {
        var finding = RuleFold.Fold(new StubRule(), new RuleAnswer([], []));

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Contains("no controls", finding.BrokenReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The phase 2 review's finding. A rule that counts nothing at all is refused, but a rule that
    /// counts only things it has declared MAY be empty is the same failure wearing different clothes:
    /// no count it reports can ever alarm, so it can never report broken, so its empty answer is
    /// always believed. The fold accepted that rule until this test was written, and it answered
    /// "verdict: ok, items: 0" - which is "nothing to remove", said by a rule with nothing behind it.
    ///
    /// It matters most for the rules that do not exist yet: phase 5 turns rules into data refreshed
    /// from the Gateway, and this fold is the single gate a refreshed rule passes through.
    /// </summary>
    [Fact]
    public void Fold_ARuleWhoseEveryControlMayBeEmpty_ReportsBrokenBecauseNoCountCouldEverAlarm()
    {
        var answer = new RuleAnswer(
            [new RuleControl("files-examined", 0, MustNotBeEmpty: false)],
            []);

        var finding = RuleFold.Fold(new StubRule(), answer);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Contains("no control that must not be empty", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Empty(finding.Candidates);
        Assert.DoesNotContain("verdict: ok", finding.Lines);
    }

    /// <summary>
    /// The same rule with one load-bearing control that did count something is fine. This is the
    /// other half of the test above: the fold asks for a control that COULD alarm, not for one that
    /// did.
    /// </summary>
    [Fact]
    public void Fold_ARuleWithOneLoadBearingControlThatCounted_IsOkEvenWhenItOffersNothing()
    {
        var answer = new RuleAnswer(
            [
                new RuleControl("names-looked-for", 16, MustNotBeEmpty: true),
                new RuleControl("files-examined", 0, MustNotBeEmpty: false)
            ],
            []);

        var finding = RuleFold.Fold(new StubRule(), answer);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
    }

    [Fact]
    public void Fold_ARuleThatSaidItCouldNotRun_ReportsBrokenWithThatReason()
    {
        var answer = new RuleAnswer([], [], "the record store would not open");

        var finding = RuleFold.Fold(new StubRule(), answer);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Equal("the record store would not open", finding.BrokenReason);
    }

    /// <summary>
    /// A control counting nought is only an alarm when the rule said it must not be empty. A rule is
    /// allowed to count nought of something that genuinely can be nought - an empty cache, no
    /// leftovers - without being called broken.
    /// </summary>
    [Fact]
    public void Fold_AControlThatMayBeEmptyIsNought_IsStillOk()
    {
        var answer = new RuleAnswer(
            [
                new RuleControl("names-looked-for", 16, MustNotBeEmpty: true),
                new RuleControl("folders-matching-one-of-our-names", 0, MustNotBeEmpty: false)
            ],
            []);

        var finding = RuleFold.Fold(new StubRule(), answer);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Null(finding.BrokenReason);
    }

    /// <summary>
    /// Every part of the answer a reader needs in order to act reaches the finished lines. A
    /// recommendation that says only how many bytes could be freed is not a recommendation.
    /// </summary>
    [Fact]
    public void Fold_TheFinishedLines_CarryTheProofWhatIsLostAndHowToGetItBack()
    {
        var answer = new RuleAnswer(
            [new RuleControl("records-read", 3, MustNotBeEmpty: true)],
            [Candidate("one", 10)]);

        var lines = RuleFold.Fold(new StubRule(), answer).Lines;
        var text = string.Join("\n", lines);

        Assert.Contains("rule: stub-rule", text, StringComparison.Ordinal);
        Assert.Contains("proof: a record the system keeps says nothing needs it", text, StringComparison.Ordinal);
        Assert.Contains("removes: what the stub removes", text, StringComparison.Ordinal);
        Assert.Contains("safe-because: why the stub is safe", text, StringComparison.Ordinal);
        Assert.Contains("what-is-lost: what the stub loses", text, StringComparison.Ordinal);
        Assert.Contains("how-to-get-it-back: how the stub comes back", text, StringComparison.Ordinal);
        Assert.Contains("age-gate: 30 days", text, StringComparison.Ordinal);
        Assert.Contains("needs-administrator: yes", text, StringComparison.Ordinal);
        Assert.Contains("control-records-read: 3", text, StringComparison.Ordinal);
    }

    private static ReclaimCandidate Candidate(string name, long bytes) =>
        new($"C:\\fixture\\{name}", bytes, DateTimeOffset.UnixEpoch, "because the stub said so");

    private sealed class StubRule : IReclaimRule
    {
        public string Id => "stub-rule";
        public string Name => "A rule that exists only to be folded";
        public ProofKind Proof => ProofKind.SystemRecord;
        public string WhatItRemoves => "what the stub removes";
        public string WhyItIsSafe => "why the stub is safe";
        public string WhatIsLost => "what the stub loses";
        public string HowToGetItBack => "how the stub comes back";
        public int AgeGateDays => 30;
        public bool NeedsAdministrator => true;
        public string? CommandToRun => "a command the stub prints";
        public string LooksIn => "C:\\fixture";

        public RuleAnswer Examine(RuleContext context) =>
            throw new InvalidOperationException("The fold is given an answer directly; this rule is never examined.");
    }
}
