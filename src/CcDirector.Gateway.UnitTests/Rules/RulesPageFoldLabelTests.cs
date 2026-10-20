using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE RULES PAGE LABELS THE STORE CANNOT EASILY PRODUCE, folded from records built by hand (Session Rules phase 3,
/// review finding 1 and observation 7): a live screen rule's firings - typed, not sent, sent without confirmation,
/// never completed - which must never read as a dry run; the other screen decisions; the cut of a screen firing's
/// subject; a live rule's mode; a date outside this year; and a hook rule with two match texts.
/// </summary>
public sealed class RulesPageFoldLabelTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");
    private const string SessionId = "8a1e4b2c-1111-4c3d-9e8f-0a1b2c3d4e5f";

    private static SessionRule ScreenRule(RuleState state, DateTime? createdUtc = null) => new(
        Guid.NewGuid(), "When I run out of allowance, switch me to Opus.", "A session stopped on an allowance notice.",
        new[] { "usage limit" }, Array.Empty<RulePrimitiveCall>(), RuleScope.AllSessions, 300, 5, state,
        state == RuleState.Live ? "device:device-9f2c" : "", createdUtc ?? Now.AddDays(-2), createdUtc ?? Now.AddDays(-2));

    private static SessionRule HookRule(string result, RuleAppliesTo appliesTo, params string[] match) => new(
        Guid.NewGuid(), "Hook rule " + string.Join(" ", match), "", Array.Empty<string>(), Array.Empty<RulePrimitiveCall>(),
        RuleScope.AllSessions, 0, 0, RuleState.DryRun, "", Now.AddDays(-2), Now.AddDays(-2))
    {
        Kind = RuleKinds.Hook,
        AppliesTo = appliesTo,
        Hook = new HookRuleTrigger(HookRuleWords.BeforeCommand, HookRuleWords.Shell, HookRuleWords.Match, match, result),
    };

    private static SessionRuleFiring ScreenFiring(SessionRule rule, string decision, string typed, string outcome, string screen = "the screen") =>
        new(Guid.NewGuid(), rule.Id, SessionId, Now.AddHours(-1), screen, "understood", decision, "reason",
            Array.Empty<RulePrimitiveRun>(), typed, outcome, "grounded");

    private static RulesPageSource SourceOf(params SessionRule[] rules) => new(
        rules, new Dictionary<Guid, RuleFiringTally>(), Array.Empty<RuleFiringPlace>(), Array.Empty<SessionRuleChange>());

    private static RulePageFiring FiringOnPage(SessionRule rule, SessionRuleFiring firing) =>
        Assert.Single(RulesPageFold.RulePageFor(SourceOf(rule), rule.Id, new[] { firing }, Copenhagen, Now)!.Firings);

    [Theory]
    [InlineData(RuleState.DryRun, "", RuleActOutcomes.DryRunPrefix + "nothing was typed. It would have typed: /model opus", "Would have acted", "neutral")]
    [InlineData(RuleState.Live, "/model opus", RuleActOutcomes.TypedPrefix + "/model opus", "Typed into the session", "live")]
    [InlineData(RuleState.Live, "", RuleActOutcomes.NotTypedPrefix + "the Director refused the command", "Tried to type - nothing was sent", "warn")]
    [InlineData(RuleState.Live, "", RuleActOutcomes.SentUnconfirmedPrefix + "/model opus - and nothing confirmed what became of it", "Sent - nothing confirmed it landed", "warn")]
    [InlineData(RuleState.Live, "", RuleActOutcomes.AboutToTypePrefix + "/model opus. This record was written BEFORE the keystroke went out", "Started to type - never completed", "warn")]
    public void RulePageFor_ActFiring_SaysWhatBecameOfItAndNeverCallsALiveFiringADryRun(
        RuleState state, string typed, string outcome, string expectedLabel, string expectedTone)
    {
        // Arrange
        var rule = ScreenRule(state);
        var firing = ScreenFiring(rule, RuleDecisions.Act, typed, outcome);

        // Act
        var onPage = FiringOnPage(rule, firing);

        // Assert
        Assert.Equal(expectedLabel, onPage.DecisionLabel);
        Assert.Equal(expectedTone, onPage.Tone);
        Assert.Equal(char.ToUpperInvariant(outcome[0]) + outcome[1..], onPage.OutcomeLabel);
    }

    [Fact]
    public void RulePageFor_ActFiringWithAnOutcomeNoWriterProduces_IsAnErrorNotAGuess()
    {
        // Arrange
        var rule = ScreenRule(RuleState.Live);
        var firing = ScreenFiring(rule, RuleDecisions.Act, "", "something else entirely");

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => FiringOnPage(rule, firing));
    }

    [Theory]
    [InlineData(RuleDecisions.Decline, "Declined - the rule did not apply", "neutral")]
    [InlineData(RuleDecisions.Abandoned, "Gave up before typing", "warn")]
    [InlineData(RuleDecisions.Refused, "Refused an unusable answer", "warn")]
    public void RulePageFor_OtherScreenDecisions_AreNamedAndToned(string decision, string expectedLabel, string expectedTone)
    {
        // Arrange
        var rule = ScreenRule(RuleState.DryRun);
        var firing = ScreenFiring(rule, decision, "", "nothing was typed, because the rule did not apply");

        // Act
        var onPage = FiringOnPage(rule, firing);

        // Assert
        Assert.Equal(expectedLabel, onPage.DecisionLabel);
        Assert.Equal(expectedTone, onPage.Tone);
        Assert.Equal($"session {SessionId}", onPage.SessionLabel);
        Assert.Equal("Read from the session's screen", onPage.WhereLabel);
        Assert.Equal("", onPage.OverrodeLabel);
        Assert.Empty(onPage.Overrode);
    }

    [Fact]
    public void RulePageFor_LongScreen_SubjectKeepsTheBottomOfTheScreenCutAtTheLimit()
    {
        // Arrange - the top is all "T", the bottom 1000 characters are all "B": the cut must keep the bottom.
        var rule = ScreenRule(RuleState.DryRun);
        var screen = new string('T', 500) + new string('B', RulesPageFold.ScreenSubjectMaxLength);
        var firing = ScreenFiring(rule, RuleDecisions.Decline, "", "declined", screen);

        // Act
        var subject = FiringOnPage(rule, firing).Subject;

        // Assert
        Assert.Equal(1000, RulesPageFold.ScreenSubjectMaxLength);
        Assert.Equal("..." + new string('B', 1000), subject);
    }

    [Fact]
    public void RulePageFor_ScreenAtTheLimit_SubjectIsTheWholeScreen()
    {
        // Arrange
        var rule = ScreenRule(RuleState.DryRun);
        var screen = "T" + new string('B', RulesPageFold.ScreenSubjectMaxLength - 1);
        var firing = ScreenFiring(rule, RuleDecisions.Decline, "", "declined", screen);

        // Act / Assert
        Assert.Equal(screen, FiringOnPage(rule, firing).Subject);
    }

    [Fact]
    public void RulePageFor_LiveScreenRule_SaysLiveWithTheLiveToneAndWhoPromotedIt()
    {
        // Arrange
        var rule = ScreenRule(RuleState.Live);

        // Act
        var page = RulesPageFold.RulePageFor(SourceOf(rule), rule.Id, Array.Empty<SessionRuleFiring>(), Copenhagen, Now)!;

        // Assert
        Assert.Equal("Live", page.Rule.ModeLabel);
        Assert.Equal("live", page.Rule.ModeTone);
        Assert.Equal("Live - types into the session when the rule applies", page.Fields.Single(f => f.Label == "Mode").Value);
        Assert.Equal("device:device-9f2c", page.Fields.Single(f => f.Label == "Promoted by").Value);
        Assert.Equal("Screen - when a session goes idle", page.Fields.Single(f => f.Label == "Kind").Value);
    }

    [Fact]
    public void RulePageFor_LiveHookRule_SaysItCarriesOutWhatItDecides()
    {
        // Arrange - phase 1 refuses to promote a hook rule, but the page must still say a true thing about one.
        var rule = HookRule(HookRuleWords.Deny, RuleAppliesTo.Everywhere, "rm -rf") with { State = RuleState.Live };

        // Act
        var page = RulesPageFold.RulePageFor(SourceOf(rule), rule.Id, Array.Empty<SessionRuleFiring>(), Copenhagen, Now)!;

        // Assert
        Assert.Equal("Live - carries out what it decides", page.Fields.Single(f => f.Label == "Mode").Value);
    }

    [Fact]
    public void RulePageFor_CreatedInAnotherYear_TheDateCarriesTheYear()
    {
        // Arrange - 18 October 2025 at 12:02 UTC is 14:02 in Copenhagen.
        var rule = ScreenRule(RuleState.DryRun, new DateTime(2025, 10, 18, 12, 2, 0, DateTimeKind.Utc));

        // Act
        var page = RulesPageFold.RulePageFor(SourceOf(rule), rule.Id, Array.Empty<SessionRuleFiring>(), Copenhagen, Now)!;

        // Assert
        Assert.Equal("18 October 2025 at 14:02", page.Fields.Single(f => f.Label == "Created").Value);
    }

    [Fact]
    public void Page_HookRuleWithTwoMatchTexts_TheHandlerNamesBoth()
    {
        // Arrange
        var rule = HookRule(HookRuleWords.Deny, RuleAppliesTo.Everywhere, "rm -rf", "git push --force");

        // Act
        var item = Assert.Single(RulesPageFold.Page(SourceOf(rule), Copenhagen, Now).Rules);
        var page = RulesPageFold.RulePageFor(SourceOf(rule), rule.Id, Array.Empty<SessionRuleFiring>(), Copenhagen, Now)!;

        // Assert
        Assert.Equal("Match \"rm -rf\" or \"git push --force\" - deny", item.HandlerLabel);
        Assert.Equal("Match: the command contains \"rm -rf\" or \"git push --force\". Result: deny",
            page.Fields.Single(f => f.Label == "Handler").Value);
    }

    [Fact]
    public void Overrides_TwoMatchTextsEach_OnlyAPairWhereOneContainsTheOtherCounts()
    {
        // Arrange - "push --force origin" contains "push --force"; no other pair contains another.
        var repoAllow = HookRule(HookRuleWords.Allow, RuleAppliesTo.InRepo("github.com/acme/web"), "npm install", "push --force origin");
        var everywhereDeny = HookRule(HookRuleWords.Deny, RuleAppliesTo.Everywhere, "rm -rf", "push --force");
        var unrelated = HookRule(HookRuleWords.Deny, RuleAppliesTo.Everywhere, "rm -rf", "sudo");

        // Act / Assert
        Assert.True(RulesPageFold.Overrides(repoAllow, everywhereDeny));
        Assert.False(RulesPageFold.Overrides(repoAllow, unrelated));
    }

    [Fact]
    public void Page_TallyWithFiringsOutsideToday_CountsThemInTheTotalButNotToday()
    {
        // Arrange - three firings in all, the newest two days ago, none in the recent window.
        var rule = HookRule(HookRuleWords.Deny, RuleAppliesTo.Everywhere, "rm -rf");
        var tally = new RuleFiringTally(3, new DateTime(2026, 10, 18, 12, 2, 0, DateTimeKind.Utc), Array.Empty<DateTime>());
        var source = new RulesPageSource(new[] { rule }, new Dictionary<Guid, RuleFiringTally> { [rule.Id] = tally },
            Array.Empty<RuleFiringPlace>(), Array.Empty<SessionRuleChange>());

        // Act
        var item = Assert.Single(RulesPageFold.Page(source, Copenhagen, Now).Rules);

        // Assert
        Assert.Equal("Fired 3 times, last on 18 October at 14:02", item.FiredLabel);
        Assert.DoesNotContain("fired_today", item.Tabs);
    }

    [Fact]
    public void RecentWindowStartUtc_CoversTheLongestPossibleToday()
    {
        // Act
        var start = RulesPageFold.RecentWindowStartUtc(Now);

        // Assert - a day with a daylight saving change back is 25 hours long.
        Assert.True(Now - start > TimeSpan.FromHours(25));
    }
}
