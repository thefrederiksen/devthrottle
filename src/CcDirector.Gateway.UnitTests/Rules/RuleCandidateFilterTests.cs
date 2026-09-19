using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// The FREE CHECKS (Session Rules mission, phase 2). Before anything costs a model call, cheap pure code
/// decides whether any rule could possibly apply: the session has to be idle, the screen has to have
/// changed, the rule has to be in scope, under its cooldown and its daily cap, and its trigger words have
/// to be on the screen.
///
/// Every test here asserts a PRESENCE - the stated reason a rule was passed over - rather than the absence
/// of an action. A filter that returned an empty list because it threw looks exactly like a filter that
/// considered every rule and turned each one down, unless the reason is written down.
/// </summary>
public sealed class RuleCandidateFilterTests
{
    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static SessionRule Rule(
        string instruction = "If a session's screen says it has run out of its model allowance, type the command that shows me what is left.",
        IReadOnlyList<string>? triggerWords = null,
        RuleScope? scope = null,
        int cooldownSeconds = 300,
        int dailyCap = 5,
        RuleState state = RuleState.DryRun,
        Guid? id = null) => new(
            id ?? Guid.NewGuid(),
            instruction,
            "A session stopped on a provider allowance notice.",
            triggerWords ?? new[] { "reached your", "limit" },
            Array.Empty<RulePrimitiveCall>(),
            scope ?? RuleScope.AllSessions,
            cooldownSeconds,
            dailyCap,
            state,
            state == RuleState.Live ? "device-9f2c" : "",
            Now,
            Now);

    private static RuleSessionFacts Facts(
        string activityState = "WaitingForInput",
        string repositoryPath = @"D:\ReposFred\scratch") => new(
        SessionId: "sid-1",
        Agent: "RawCli",
        RepositoryPath: repositoryPath,
        Machine: "SOREN_NORTH",
        Mission: "Session Rules",
        ActivityState: activityState);

    private const string TheNotice =
        "You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.";

    private static IReadOnlyList<SessionRuleFiring> NoFirings(Guid _) => Array.Empty<SessionRuleFiring>();

    private static SessionRuleFiring Firing(Guid ruleId, string sessionId, DateTime occurredUtc, string decision) =>
        new(Guid.NewGuid(), ruleId, sessionId, occurredUtc, TheNotice, "", decision, "",
            Array.Empty<RulePrimitiveRun>(), "", "", "");

    // ---- the session-level checks ------------------------------------------------------------------

    [Fact]
    public void A_working_session_is_not_evaluated_at_all_and_says_so()
    {
        var result = RuleCandidateFilter.Choose(
            new[] { Rule() }, Facts(activityState: "Working"), TheNotice, previousScreenText: null, NoFirings, Now);

        Assert.Empty(result.Chosen);
        Assert.Equal(RuleCandidateFilter.SessionIsWorking, result.StoppedBecause);
    }

    [Fact]
    public void A_screen_that_has_not_changed_since_the_last_look_is_not_evaluated_and_says_so()
    {
        var result = RuleCandidateFilter.Choose(
            new[] { Rule() }, Facts(), TheNotice, previousScreenText: TheNotice, NoFirings, Now);

        Assert.Empty(result.Chosen);
        Assert.Equal(RuleCandidateFilter.ScreenUnchanged, result.StoppedBecause);
    }

    [Fact]
    public void A_screen_never_seen_before_counts_as_changed()
    {
        var result = RuleCandidateFilter.Choose(
            new[] { Rule() }, Facts(), TheNotice, previousScreenText: null, NoFirings, Now);

        Assert.Null(result.StoppedBecause);
        Assert.Single(result.Chosen);
    }

    [Fact]
    public void An_empty_screen_is_not_evidence_and_stops_the_pass_with_a_reason()
    {
        var result = RuleCandidateFilter.Choose(
            new[] { Rule() }, Facts(), "   ", previousScreenText: null, NoFirings, Now);

        Assert.Empty(result.Chosen);
        Assert.Equal(RuleCandidateFilter.ScreenIsEmpty, result.StoppedBecause);
    }

    // ---- the per-rule checks ----------------------------------------------------------------------

    [Fact]
    public void A_rule_whose_words_are_not_on_the_screen_is_passed_over_with_a_reason()
    {
        var rule = Rule(triggerWords: new[] { "out of credits" });

        var result = RuleCandidateFilter.Choose(
            new[] { rule }, Facts(), "C:\\scratch> dir", previousScreenText: null, NoFirings, Now);

        Assert.Empty(result.Chosen);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(rule.Id, skipped.RuleId);
        Assert.Contains("none of the words this rule watches for", skipped.Reason);
    }

    [Fact]
    public void The_words_are_matched_ignoring_case()
    {
        var rule = Rule(triggerWords: new[] { "REACHED YOUR" });

        var result = RuleCandidateFilter.Choose(
            new[] { rule }, Facts(), TheNotice, previousScreenText: null, NoFirings, Now);

        Assert.Single(result.Chosen);
    }

    [Fact]
    public void A_rule_scoped_to_another_agent_is_passed_over_and_the_reason_names_both()
    {
        var rule = Rule(scope: new RuleScope("Claude", null, null, null));

        var result = RuleCandidateFilter.Choose(
            new[] { rule }, Facts(), TheNotice, previousScreenText: null, NoFirings, Now);

        Assert.Empty(result.Chosen);
        var skipped = Assert.Single(result.Skipped);
        Assert.Contains("Claude", skipped.Reason);
        Assert.Contains("RawCli", skipped.Reason);
    }

    /// <summary>
    /// A WINDOWS REPOSITORY IS MATCHED THE WAY WINDOWS MATCHES ONE, WHEREVER THE GATEWAY IS RUNNING.
    ///
    /// The scope and the session name one directory in two casings, and on the machine that reported them
    /// those are the same directory. This used to be answered with the case rules of whatever host the code
    /// was executing on, so it was true on a Windows desktop and false on the hosted Linux Gateway - where
    /// rules actually run - and the rule silently stopped firing. Separators are unified here too, because
    /// the same place reaches us written both ways.
    /// </summary>
    [Fact]
    public void A_rule_scoped_to_this_sessions_repository_is_a_candidate()
    {
        var rule = Rule(scope: new RuleScope(null, @"d:/reposfred/scratch\", null, null));

        var result = RuleCandidateFilter.Choose(
            new[] { rule }, Facts(), TheNotice, previousScreenText: null, NoFirings, Now);

        Assert.Single(result.Chosen);
    }

    /// <summary>
    /// AND A POSIX REPOSITORY IS MATCHED THE WAY LINUX AND MACOS MATCH ONE - which is exactly, because two
    /// names differing in case there are two different directories.
    ///
    /// This is the same defect read from the other end, and it is the half that fails on a Windows host: the
    /// old comparison took its case rules from the executing machine, so on a Windows developer's machine
    /// these two unrelated directories answered as one place and a rule scoped to one repository would have
    /// acted on a session in another.
    /// </summary>
    [Fact]
    public void A_rule_scoped_to_a_POSIX_repository_does_not_match_a_different_casing_of_it()
    {
        var rule = Rule(scope: new RuleScope(null, "/users/dev/reposfred/scratch", null, null));

        var result = RuleCandidateFilter.Choose(
            new[] { rule },
            Facts(repositoryPath: "/Users/dev/ReposFred/scratch"),
            TheNotice, previousScreenText: null, NoFirings, Now);

        Assert.Empty(result.Chosen);
        var skipped = Assert.Single(result.Skipped);
        Assert.Contains("/users/dev/reposfred/scratch", skipped.Reason);
        Assert.Contains("/Users/dev/ReposFred/scratch", skipped.Reason);
    }

    /// <summary>The same POSIX repository, written with and without its trailing separator, is one place.</summary>
    [Fact]
    public void A_rule_scoped_to_a_POSIX_repository_matches_it_written_with_a_trailing_separator()
    {
        var rule = Rule(scope: new RuleScope(null, "/Users/dev/ReposFred/scratch/", null, null));

        var result = RuleCandidateFilter.Choose(
            new[] { rule },
            Facts(repositoryPath: "/Users/dev/ReposFred/scratch"),
            TheNotice, previousScreenText: null, NoFirings, Now);

        Assert.Single(result.Chosen);
    }

    [Fact]
    public void A_rule_that_acted_on_this_session_inside_its_cooldown_is_passed_over_with_a_reason()
    {
        var rule = Rule(cooldownSeconds: 300);
        IReadOnlyList<SessionRuleFiring> Firings(Guid id) => new[]
        {
            Firing(id, "sid-1", Now.AddSeconds(-120), RuleDecisions.Act),
        };

        var result = RuleCandidateFilter.Choose(
            new[] { rule }, Facts(), TheNotice, previousScreenText: null, Firings, Now);

        Assert.Empty(result.Chosen);
        var skipped = Assert.Single(result.Skipped);
        Assert.Contains("300", skipped.Reason);
        Assert.Contains("120", skipped.Reason);
    }

    [Fact]
    public void A_DECLINE_does_not_start_the_cooldown_because_nothing_was_done()
    {
        var rule = Rule(cooldownSeconds: 300);
        IReadOnlyList<SessionRuleFiring> Firings(Guid id) => new[]
        {
            Firing(id, "sid-1", Now.AddSeconds(-1), RuleDecisions.Decline),
        };

        var result = RuleCandidateFilter.Choose(
            new[] { rule }, Facts(), TheNotice, previousScreenText: null, Firings, Now);

        Assert.Single(result.Chosen);
    }

    [Fact]
    public void A_cooldown_is_counted_per_session_so_another_sessions_firing_does_not_hold_this_one_back()
    {
        var rule = Rule(cooldownSeconds: 300);
        IReadOnlyList<SessionRuleFiring> Firings(Guid id) => new[]
        {
            Firing(id, "some-other-session", Now.AddSeconds(-1), RuleDecisions.Act),
        };

        var result = RuleCandidateFilter.Choose(
            new[] { rule }, Facts(), TheNotice, previousScreenText: null, Firings, Now);

        Assert.Single(result.Chosen);
    }

    [Fact]
    public void A_rule_that_has_hit_its_daily_cap_on_this_session_is_passed_over_with_a_reason()
    {
        var rule = Rule(cooldownSeconds: 1, dailyCap: 2);
        IReadOnlyList<SessionRuleFiring> Firings(Guid id) => new[]
        {
            Firing(id, "sid-1", Now.AddHours(-5), RuleDecisions.Act),
            Firing(id, "sid-1", Now.AddHours(-4), RuleDecisions.Act),
        };

        var result = RuleCandidateFilter.Choose(
            new[] { rule }, Facts(), TheNotice, previousScreenText: null, Firings, Now);

        Assert.Empty(result.Chosen);
        var skipped = Assert.Single(result.Skipped);
        Assert.Contains("2", skipped.Reason);
        Assert.Contains("cap", skipped.Reason);
    }

    [Fact]
    public void Yesterdays_firings_do_not_count_against_todays_cap()
    {
        var rule = Rule(cooldownSeconds: 1, dailyCap: 2);
        IReadOnlyList<SessionRuleFiring> Firings(Guid id) => new[]
        {
            Firing(id, "sid-1", Now.AddDays(-1), RuleDecisions.Act),
            Firing(id, "sid-1", Now.AddDays(-1).AddHours(1), RuleDecisions.Act),
        };

        var result = RuleCandidateFilter.Choose(
            new[] { rule }, Facts(), TheNotice, previousScreenText: null, Firings, Now);

        Assert.Single(result.Chosen);
    }

    [Fact]
    public void A_dry_run_rule_is_still_evaluated_because_dry_run_reports_what_it_would_have_done()
    {
        var result = RuleCandidateFilter.Choose(
            new[] { Rule(state: RuleState.DryRun) }, Facts(), TheNotice, previousScreenText: null, NoFirings, Now);

        Assert.Single(result.Chosen);
    }

    [Fact]
    public void Every_rule_is_accounted_for_either_as_a_candidate_or_with_a_stated_reason()
    {
        var chosen = Rule(triggerWords: new[] { "limit" });
        var passedOver = Rule(triggerWords: new[] { "nothing like this on the screen" });

        var result = RuleCandidateFilter.Choose(
            new[] { chosen, passedOver }, Facts(), TheNotice, previousScreenText: null, NoFirings, Now);

        var accountedFor = result.Chosen.Select(r => r.Id).Concat(result.Skipped.Select(s => s.RuleId)).ToList();
        Assert.Equal(2, accountedFor.Count);
        Assert.Contains(chosen.Id, accountedFor);
        Assert.Contains(passedOver.Id, accountedFor);
    }
}
