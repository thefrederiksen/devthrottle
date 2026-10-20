using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using CcDirector.RuleHook;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE RULES PAGE FOLD (Session Rules phase 3, wire contracts P1-P3), over a real store and without HTTP. Every
/// label, tab, count and verdict the clients show is decided here, so this is where each one is proven: the tab
/// memberships, "fired today" in the viewer's own day, changed by an agent against changed by a person, the
/// firing record naming the rules a firing overrode, and the preview - whose decision for a sample command must be
/// the hook's own decision (ruling T6) and whose standing overrides must always be true (ruling T7).
/// </summary>
public sealed class RulesPageFoldTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase Db => _db ??= _h.Open();
    private GatewayDatabase? _db;

    public void Dispose() => _h.Dispose();

    // Tuesday 20 October 2026, 06:00 UTC - 08:00 in Copenhagen, which is two hours ahead of UTC in October.
    private static readonly DateTime Now = new(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Earlier = new(2026, 10, 18, 12, 2, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");
    private const string Repo = "github.com/acme/web";
    private static readonly Guid AgentSession = Guid.Parse("8a1e4b2c-1111-4c3d-9e8f-0a1b2c3d4e5f");

    private static SessionRule Hook(
        SessionRuleStore store, string sentence, RuleAppliesTo appliesTo, string result, string[] match,
        RuleChangeActor? actor = null, DateTime? at = null) =>
        store.CreateHook(sentence, appliesTo, HookRuleWords.BeforeCommand, HookRuleWords.Shell, HookRuleWords.Match,
            match, result, at ?? Earlier, actor ?? AnActor.Owner);

    private static SessionRule Screen(SessionRuleStore store) => store.Create(
        "When I run out of allowance, switch me to Opus.",
        "A session stopped on a provider allowance notice.",
        new[] { "usage limit" },
        new[]
        {
            RulePrimitiveCall.To("matches_any",
                RuleArgument.FromInput("text", RuleInput.ScreenText),
                RuleArgument.LiteralList("terms", new[] { "usage limit" })),
        },
        RuleScope.AllSessions, 300, 5, Earlier, AnActor.Owner);

    private static HookDecision Decision(
        Guid rule, DateTime at, string decision = HookRuleWords.WouldDeny, Guid[]? overridden = null, string subject = "rm -rf build") =>
        new(1, "decision-" + Guid.NewGuid().ToString("N"), AgentSession.ToString(), at, "claude_code", HookRuleWords.BeforeCommand,
            "PreToolUse", HookRuleWords.Shell, "Bash", subject, "devlinux", Repo, decision, rule,
            overridden ?? Array.Empty<Guid>(), new[] { rule }.Concat(overridden ?? Array.Empty<Guid>()).ToList(), 812);

    private static void Record(SessionRuleStore store, params HookDecision[] decisions)
    {
        var answer = store.RecordHookFirings(decisions);
        Assert.Empty(answer.Refused);
        Assert.Equal(decisions.Length, answer.Accepted);
    }

    // ---- P1: the tabs ----------------------------------------------------------------------------------------

    [Fact]
    public void Page_OneRuleOfEachShape_EveryTabHoldsItsRulesAndCountsThem()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var screen = Screen(store);
        var firedByAgent = Hook(store, "Never run rm -rf in this repository.", RuleAppliesTo.InRepo(Repo),
            HookRuleWords.Deny, new[] { "rm -rf" }, AnActor.Session(AgentSession, "claude_code"));
        var quiet = Hook(store, "Allow git push everywhere.", RuleAppliesTo.Everywhere, HookRuleWords.Allow, new[] { "git push" });
        Record(store, Decision(firedByAgent.Id, Now.AddHours(-1)));

        // Act
        var page = RulesPageFold.Page(SessionRuleWire.ReadPageSource(store), Copenhagen, Now);

        // Assert
        Assert.Equal("Tuesday 20 October", page.Today);
        Assert.Equal(
            new[] { ("all", "All", 3), ("screen", "Screen", 1), ("hook", "Hook", 2), ("session_start", "Session start", 0),
                    ("fired_today", "Fired today", 1), ("changed_by_agents", "Changed by agents", 1) },
            page.Tabs.Select(t => (t.Id, t.Label, t.Count)));
        Assert.Equal(new[] { "all", "screen" }, page.Rules.Single(r => r.Id == screen.Id).Tabs);
        Assert.Equal(new[] { "all", "hook", "fired_today", "changed_by_agents" }, page.Rules.Single(r => r.Id == firedByAgent.Id).Tabs);
        Assert.Equal(new[] { "all", "hook" }, page.Rules.Single(r => r.Id == quiet.Id).Tabs);
    }

    [Fact]
    public void Page_HookRule_EveryLabelIsAFinishedString()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = Hook(store, "Never run rm -rf in this repository.", RuleAppliesTo.InRepoOnMachines(Repo, new[] { "devlinux" }),
            HookRuleWords.Deny, new[] { "rm -rf" });
        Record(store, Decision(rule.Id, new DateTime(2026, 10, 20, 5, 0, 0, DateTimeKind.Utc)),
            Decision(rule.Id, new DateTime(2026, 10, 20, 5, 42, 0, DateTimeKind.Utc)));

        // Act
        var item = Assert.Single(RulesPageFold.Page(SessionRuleWire.ReadPageSource(store), Copenhagen, Now).Rules);

        // Assert
        Assert.Equal("Never run rm -rf in this repository.", item.Sentence);
        Assert.Equal("Hook", item.KindLabel);
        Assert.Equal("Before a shell command", item.TriggerLabel);
        Assert.Equal("In github.com/acme/web on devlinux", item.ScopeLabel);
        Assert.Equal("Dry run", item.ModeLabel);
        Assert.Equal("neutral", item.ModeTone);
        Assert.Equal("Match \"rm -rf\" - deny", item.HandlerLabel);
        Assert.Equal("Fired 2 times today, last at 07:42", item.FiredLabel);
        Assert.Equal("", item.ChangedLabel);
    }

    [Fact]
    public void Page_FiringTodayInTheViewersZoneButYesterdayInUtc_IsFiredTodayOnlyInTheViewersZone()
    {
        // Arrange - 23:30 UTC on the 19th is 01:30 on the 20th in Copenhagen.
        var store = new SessionRuleStore(Db);
        var rule = Hook(store, "Never run rm -rf.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "rm -rf" });
        Record(store, Decision(rule.Id, new DateTime(2026, 10, 19, 23, 30, 0, DateTimeKind.Utc)));
        var source = SessionRuleWire.ReadPageSource(store);

        // Act
        var inCopenhagen = RulesPageFold.Page(source, Copenhagen, Now);
        var inUtc = RulesPageFold.Page(source, TimeZoneInfo.Utc, Now);

        // Assert
        Assert.Contains("fired_today", inCopenhagen.Rules.Single().Tabs);
        Assert.Equal(1, inCopenhagen.Tabs.Single(t => t.Id == "fired_today").Count);
        Assert.Equal("Fired 1 time today, last at 01:30", inCopenhagen.Rules.Single().FiredLabel);
        Assert.DoesNotContain("fired_today", inUtc.Rules.Single().Tabs);
        Assert.Equal(0, inUtc.Tabs.Single(t => t.Id == "fired_today").Count);
        Assert.Equal("Fired 1 time, last yesterday at 23:30", inUtc.Rules.Single().FiredLabel);
    }

    [Fact]
    public void Page_ChangedByAnAgentAndByAPerson_OnlyTheAgentsRuleIsOnTheTabAndSaysWho()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var byPerson = Hook(store, "By a person.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "a" });
        var byAgent = Hook(store, "By an agent.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "b" },
            AnActor.Session(AgentSession, "claude_code"), new DateTime(2026, 10, 20, 5, 15, 0, DateTimeKind.Utc));

        // Act
        var page = RulesPageFold.Page(SessionRuleWire.ReadPageSource(store), Copenhagen, Now);

        // Assert
        Assert.DoesNotContain("changed_by_agents", page.Rules.Single(r => r.Id == byPerson.Id).Tabs);
        Assert.Equal("", page.Rules.Single(r => r.Id == byPerson.Id).ChangedLabel);
        Assert.Contains("changed_by_agents", page.Rules.Single(r => r.Id == byAgent.Id).Tabs);
        Assert.Equal($"Changed by Session {AgentSession} (claude_code) at 07:15", page.Rules.Single(r => r.Id == byAgent.Id).ChangedLabel);
        Assert.Equal(1, page.Tabs.Single(t => t.Id == "changed_by_agents").Count);
    }

    [Fact]
    public void Page_NoRules_EveryTabIsEmptyAndSessionStartSaysItIsNotBuilt()
    {
        // Act
        var page = RulesPageFold.Page(SessionRuleWire.ReadPageSource(new SessionRuleStore(Db)), Copenhagen, Now);

        // Assert
        Assert.Empty(page.Rules);
        Assert.All(page.Tabs, t => Assert.Equal(0, t.Count));
        Assert.All(page.Tabs, t => Assert.NotEqual("", t.EmptyText));
        Assert.Equal(
            "No session start rules. Rules that act when a session starts are not built yet.",
            page.Tabs.Single(t => t.Id == "session_start").EmptyText);
    }

    [Fact]
    public void Page_DeletedRule_IsOnNoTab()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var gone = Hook(store, "Gone.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "x" }, AnActor.Session(AgentSession));
        Record(store, Decision(gone.Id, Now.AddMinutes(-5)));
        store.Delete(gone.Id, Now, AnActor.Session(AgentSession));

        // Act
        var page = RulesPageFold.Page(SessionRuleWire.ReadPageSource(store), Copenhagen, Now);

        // Assert
        Assert.Empty(page.Rules);
        Assert.All(page.Tabs, t => Assert.Equal(0, t.Count));
    }

    // ---- P2: one rule's page ---------------------------------------------------------------------------------

    [Fact]
    public void RulePageFor_RuleWithNoFirings_SaysNeverFiredAndGivesTheEmptyText()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = Hook(store, "Never run rm -rf.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "rm -rf" });

        // Act
        var page = RulesPageFold.RulePageFor(SessionRuleWire.ReadPageSource(store), rule.Id, Copenhagen, Now)!;

        // Assert
        Assert.Equal("Never fired", page.Rule.FiredLabel);
        Assert.Empty(page.Firings);
        Assert.Equal("This rule has not fired yet.", page.FiringsEmptyText);
        Assert.Equal(
            new[] { "Sentence", "Kind", "Scope", "Mode", "Handler", "Promoted by", "Created" },
            page.Fields.Select(f => f.Label));
        Assert.Equal("Hook - before a shell command", page.Fields[1].Value);
        Assert.Equal("Everywhere (least specific: everywhere)", page.Fields[2].Value);
        Assert.Equal("Dry run - records what it would have done; nothing is blocked", page.Fields[3].Value);
        Assert.Equal("Match: the command contains \"rm -rf\". Result: deny", page.Fields[4].Value);
        Assert.Equal("Not promoted", page.Fields[5].Value);
        Assert.Equal("18 October at 14:02", page.Fields[6].Value);
        var created = Assert.Single(page.Changes);
        Assert.Equal(("18 October at 14:02", "Created", "device:device-9f2c"), (created.WhenLabel, created.ActionLabel, created.ByLabel));
    }

    [Fact]
    public void RulePageFor_FiringThatOverrodeTwoRulesOneSinceDeleted_NamesBoth()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var everywhere = Hook(store, "Never run rm -rf.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "rm -rf" });
        var gone = Hook(store, "Never run rm.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "rm" });
        var repo = Hook(store, "Allow rm -rf in this repo.", RuleAppliesTo.InRepo(Repo), HookRuleWords.Allow, new[] { "rm -rf" });
        Record(store, Decision(repo.Id, new DateTime(2026, 10, 20, 5, 42, 0, DateTimeKind.Utc), HookRuleWords.WouldAllow,
            new[] { everywhere.Id, gone.Id }));
        store.Delete(gone.Id, Now, AnActor.Owner);

        // Act
        var firing = Assert.Single(RulesPageFold.RulePageFor(SessionRuleWire.ReadPageSource(store), repo.Id, Copenhagen, Now)!.Firings);

        // Assert
        Assert.Equal("Today at 07:42", firing.WhenLabel);
        Assert.Equal("Would have allowed", firing.DecisionLabel);
        Assert.Equal("allow", firing.Tone);
        Assert.Equal($"Session {AgentSession} (claude_code)", firing.SessionLabel);
        Assert.Equal("devlinux - github.com/acme/web", firing.WhereLabel);
        Assert.Equal("rm -rf build", firing.Subject);
        Assert.Equal("Overrode 2 rules", firing.OverrodeLabel);
        Assert.Equal(
            new[] { (everywhere.Id, "Never run rm -rf."), (gone.Id, $"(deleted rule {gone.Id})") },
            firing.Overrode.Select(o => (o.Id, o.Sentence)));
        Assert.Equal("Dry run: recorded what this rule would have decided; the command was not blocked.", firing.OutcomeLabel);
    }

    [Fact]
    public void RulePageFor_UnknownRule_IsNull()
    {
        // Act
        var page = RulesPageFold.RulePageFor(SessionRuleWire.ReadPageSource(new SessionRuleStore(Db)), Guid.NewGuid(), Copenhagen, Now);

        // Assert
        Assert.Null(page);
    }

    // ---- P3: the preview -------------------------------------------------------------------------------------

    [Fact]
    public void Preview_NoRules_SaysNoRuleReachesAndHasNoDecision()
    {
        // Act
        var preview = RulesPageFold.Preview(
            SessionRuleWire.ReadPageSource(new SessionRuleStore(Db)), "devlinux", "git@github.com:Acme/web.git", null);

        // Assert
        Assert.Equal("devlinux", preview.Machine);
        Assert.Equal(Repo, preview.Repo);
        Assert.Equal("0 rules reach devlinux in github.com/acme/web.", preview.Headline);
        Assert.Empty(preview.Rules);
        Assert.Equal("No rule reaches devlinux in github.com/acme/web.", preview.EmptyText);
        Assert.Null(preview.Decision);
        Assert.Empty(preview.Suggestions.Machines);
        Assert.Empty(preview.Suggestions.Repos);
    }

    [Fact]
    public void Preview_RepoAllowOverAnEverywhereDeny_TheRepoRuleOverridesItWithoutACommand()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var everywhere = Hook(store, "Never run rm -rf.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "rm -rf" });
        var repo = Hook(store, "Allow rm -rf build in this repo.", RuleAppliesTo.InRepo(Repo), HookRuleWords.Allow, new[] { "rm -rf build" });
        var screen = Screen(store);

        // Act
        var preview = RulesPageFold.Preview(SessionRuleWire.ReadPageSource(store), "devlinux", Repo, "");

        // Assert
        Assert.Equal("3 rules reach devlinux in github.com/acme/web.", preview.Headline);
        Assert.Null(preview.Decision);
        Assert.Equal(repo.Id, preview.Rules[0].Id);
        var repoItem = preview.Rules.Single(r => r.Id == repo.Id);
        Assert.Equal("Repo (rank 3)", repoItem.RankLabel);
        Assert.Equal("Allow", repoItem.ResultLabel);
        Assert.Equal(new[] { everywhere.Id }, repoItem.Overrides.Select(o => o.Id));
        Assert.Empty(repoItem.OverriddenBy);
        Assert.Equal("Overrides 1 rule: \"Never run rm -rf.\"", repoItem.VerdictLabel);
        var everywhereItem = preview.Rules.Single(r => r.Id == everywhere.Id);
        Assert.Equal(new[] { repo.Id }, everywhereItem.OverriddenBy.Select(o => o.Id));
        Assert.Equal("Overridden by 1 rule: \"Allow rm -rf build in this repo.\"", everywhereItem.VerdictLabel);
        var screenItem = preview.Rules.Single(r => r.Id == screen.Id);
        Assert.Empty(screenItem.Overrides);
        Assert.Empty(screenItem.OverriddenBy);
    }

    [Fact]
    public void Overrides_MoreSpecificRuleWhoseTextDoesNotContainTheOthers_IsNotAStandingOverride()
    {
        // Arrange - "rm" does not contain "rm -rf build", so "rm x" reaches only the repo rule: no statement is
        // always true, so none is made.
        var store = new SessionRuleStore(Db);
        var everywhere = Hook(store, "Never run rm -rf build.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "rm -rf build" });
        var repo = Hook(store, "Allow rm.", RuleAppliesTo.InRepo(Repo), HookRuleWords.Allow, new[] { "rm" });

        // Act / Assert
        Assert.False(RulesPageFold.Overrides(repo, everywhere));
        Assert.False(RulesPageFold.Overrides(everywhere, repo));
    }

    [Fact]
    public void Overrides_RankOrder_TheMoreSpecificRuleWinsAndAtOneRankDenyWins()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var everywhereDeny = Hook(store, "Never run rm -rf.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "rm -rf" });
        var machineAllow = Hook(store, "Allow rm -rf on devlinux.", RuleAppliesTo.OnMachines(new[] { "devlinux" }), HookRuleWords.Allow, new[] { "rm -rf" });
        var machineDeny = Hook(store, "Never run rm -rf on devlinux.", RuleAppliesTo.OnMachines(new[] { "devlinux" }), HookRuleWords.Deny, new[] { "rm -rf" });
        var sameResult = Hook(store, "Never run rm -rf here.", RuleAppliesTo.InRepo(Repo), HookRuleWords.Deny, new[] { "rm -rf" });

        // Act / Assert
        Assert.True(RulesPageFold.Overrides(machineAllow, everywhereDeny));
        Assert.False(RulesPageFold.Overrides(everywhereDeny, machineAllow));
        Assert.True(RulesPageFold.Overrides(machineDeny, machineAllow));
        Assert.False(RulesPageFold.Overrides(machineAllow, machineDeny));
        Assert.False(RulesPageFold.Overrides(sameResult, everywhereDeny));
    }

    [Theory]
    [InlineData("rm -rf build")]
    [InlineData("rm -rf /")]
    [InlineData("sudo rm -rf build && git push")]
    [InlineData("git push --force")]
    [InlineData("ls")]
    public void Preview_SampleCommand_DecidesExactlyAsTheHooksOwnResolverDoes(string command)
    {
        // Arrange - rules at every rank, both results, some agreeing and some not.
        var store = new SessionRuleStore(Db);
        Hook(store, "Never run rm -rf.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "rm -rf" });
        Hook(store, "Allow rm -rf on devlinux.", RuleAppliesTo.OnMachines(new[] { "devlinux" }), HookRuleWords.Allow, new[] { "rm -rf" });
        Hook(store, "Allow rm -rf build here.", RuleAppliesTo.InRepo(Repo), HookRuleWords.Allow, new[] { "rm -rf build" });
        Hook(store, "Never force push here on devlinux.", RuleAppliesTo.InRepoOnMachines(Repo, new[] { "devlinux" }), HookRuleWords.Deny, new[] { "push --force" });
        Hook(store, "Allow git push.", RuleAppliesTo.Everywhere, HookRuleWords.Allow, new[] { "git push" });
        Hook(store, "Never run sudo.", RuleAppliesTo.InRepo(Repo), HookRuleWords.Deny, new[] { "sudo" });
        Hook(store, "Elsewhere only.", RuleAppliesTo.InRepo("github.com/acme/other"), HookRuleWords.Deny, new[] { "rm" });

        // What the hook decides: the W1 rules, in W1 order, as the Director writes them into the rules file.
        var file = new RulesFile
        {
            Version = 1,
            Rules = store.EffectiveHookRules("devlinux", Repo).Select(e => new HookRule
            {
                Id = e.Rule.Id.ToString(),
                Rank = e.Rank,
                AppliesTo = e.Rule.AppliesTo.Kind,
                Event = e.Rule.Hook!.Event,
                Tool = e.Rule.Hook.Tool,
                Match = e.Rule.Hook.Match.ToList(),
                Result = e.Rule.Hook.Result,
                Reason = e.Rule.Instruction,
                State = "dry_run",
            }).ToList(),
        };
        var hook = RuleResolver.Decide(RuleResolver.Validate(file), command);

        // Act
        var decision = RulesPageFold.Preview(SessionRuleWire.ReadPageSource(store), "devlinux", Repo, command).Decision!;

        // Assert
        Assert.Equal(command, decision.Command);
        Assert.Equal(hook.DecidingRule?.Id ?? "", decision.DecidingRuleId);
        Assert.Equal(hook.OverriddenRuleIds, decision.OverriddenRuleIds);
        Assert.Equal(hook.MatchedRuleIds, decision.MatchedRuleIds);
        var expected = hook.Decision switch
        {
            RuleResolver.WouldDeny => ("Would deny", "deny"),
            RuleResolver.WouldAllow => ("Would allow", "allow"),
            _ => ("No rule matched this command", "neutral"),
        };
        Assert.Equal(expected, (decision.VerdictLabel, decision.Tone));
    }

    [Fact]
    public void Preview_RepoAllowOverAnEverywhereDenyWithACommand_SaysWhichDecidedAndWhichItOverrode()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var everywhere = Hook(store, "Never run rm -rf.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "rm -rf" });
        var repo = Hook(store, "Allow rm -rf in this repo.", RuleAppliesTo.InRepo(Repo), HookRuleWords.Allow, new[] { "rm -rf" });

        // Act
        var decision = RulesPageFold.Preview(SessionRuleWire.ReadPageSource(store), "devlinux", Repo, "rm -rf build").Decision!;

        // Assert
        Assert.Equal("Would allow", decision.VerdictLabel);
        Assert.Equal("allow", decision.Tone);
        Assert.Equal("\"Allow rm -rf in this repo.\" (repo) decides. It overrode \"Never run rm -rf.\" (everywhere).", decision.Summary);
        Assert.Equal(repo.Id.ToString(), decision.DecidingRuleId);
        Assert.Equal(new[] { everywhere.Id.ToString() }, decision.OverriddenRuleIds);
    }

    [Fact]
    public void Preview_CommandMatchingNothing_SaysNoRuleMatchedAndNamesNoRule()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        Hook(store, "Never run rm -rf.", RuleAppliesTo.Everywhere, HookRuleWords.Deny, new[] { "rm -rf" });

        // Act
        var decision = RulesPageFold.Preview(SessionRuleWire.ReadPageSource(store), "devlinux", "", "ls -la").Decision!;

        // Assert
        Assert.Equal("No rule matched this command", decision.VerdictLabel);
        Assert.Equal("neutral", decision.Tone);
        Assert.Equal("", decision.DecidingRuleId);
        Assert.Empty(decision.OverriddenRuleIds);
        Assert.Empty(decision.MatchedRuleIds);
        Assert.Contains("devlinux with no repository", decision.Summary);
    }

    [Fact]
    public void Preview_Suggestions_AreTheMachinesAndReposTheRulesAndFiringsName()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = Hook(store, "Never.", RuleAppliesTo.InRepoOnMachines("https://github.com/Acme/Other.git", new[] { "SOREN_NORTH" }),
            HookRuleWords.Deny, new[] { "x" });
        Hook(store, "Not on the laptop.", RuleAppliesTo.ExceptOnMachines(new[] { "laptop" }), HookRuleWords.Deny, new[] { "y" });
        Record(store, Decision(rule.Id, Now.AddMinutes(-1)));

        // Act
        var suggestions = RulesPageFold.Preview(SessionRuleWire.ReadPageSource(store), "devlinux", "", null).Suggestions;

        // Assert
        Assert.Equal(new[] { "devlinux", "laptop", "SOREN_NORTH" }, suggestions.Machines);
        Assert.Equal(new[] { "github.com/acme/other", Repo }, suggestions.Repos);
    }

    [Fact]
    public void Preview_NoMachine_IsRefusedWithAReason()
    {
        // Act
        var ex = Assert.Throws<RuleRejectedException>(() =>
            RulesPageFold.Preview(SessionRuleWire.ReadPageSource(new SessionRuleStore(Db)), " ", Repo, null));

        // Assert
        Assert.Contains("machine", ex.Reason);
    }

    // ---- T3: the viewer's zone -------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus_Mons")]
    public void ReadZone_MissingOrUnknown_IsRefusedAndNeverReadAsUtc(string? tz)
    {
        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => RulesPageFold.ReadZone(tz));

        // Assert
        Assert.Contains("zone", ex.Reason);
    }

    [Fact]
    public void ReadZone_IanaName_IsThatZone()
    {
        // Act
        var zone = RulesPageFold.ReadZone("Europe/Copenhagen");

        // Assert
        Assert.Equal(Copenhagen.BaseUtcOffset, zone.BaseUtcOffset);
    }
}
