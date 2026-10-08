using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// THE TEAM'S GOVERNANCE TAB (Teams v1). Over a real, throwaway, fully migrated Gateway database with an injected clock:
///  - each role reads or is refused as the role table says: the Owner, Managers and Developers see the rules, only the
///    Owner and Managers may change them, a Collaborator has no tab and a stranger is told there is no such team;
///  - EVERY change is recorded - who, what and when - in the same write as the change, newest first, and a refused or
///    invalid change saves nothing and records nothing;
///  - the required and suggested skills and workflows come only from the team's own library;
///  - "who may read what" is read from the role table, so it cannot state a rule the product does not enforce.
/// </summary>
public sealed class TeamGovernanceTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";
    private const string Stranger = "sub-stranger";

    private static readonly TeamGovernanceLibraryEntry ReviewSkill = new(TeamGovernanceCatalog.KindSkill, "review-before-merge", "Review before merge");
    private static readonly TeamGovernanceLibraryEntry ReleaseWorkflow = new(TeamGovernanceCatalog.KindWorkflow, "release-checklist", "Release checklist");
    private static readonly IReadOnlyList<TeamGovernanceLibraryEntry> Library = new[] { ReviewSkill, ReleaseWorkflow };

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TeamGovernanceStore _governance;
    private readonly TeamRegistry _teams;
    private DateTime _now = new(2026, 10, 6, 9, 14, 0, DateTimeKind.Utc);
    private readonly string _team;

    public TeamGovernanceTests()
    {
        _db = _harness.Open();
        var tenants = new TenantRegistry(_db);
        _governance = new TeamGovernanceStore(_db, () => _now);
        _teams = new TeamRegistry(_db, tenants, () => _now, governance: _governance);

        foreach (var (subject, email) in new[]
                 {
                     (Owner, "soren@acme.example"), (Manager, "peter@acme.example"), (Developer, "rob@acme.example"),
                     (Collaborator, "mike@client.example"), (Stranger, "stranger@else.example"),
                 })
            tenants.MintOrLookupBySubject(subject, email);
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Collaborator, TeamRole.Collaborator).IsDone);
    }

    public void Dispose() => _harness.Dispose();

    private static TeamGovernanceChangeRequest Ask(
        Dictionary<string, bool>? review = null, Dictionary<string, bool>? agents = null,
        TeamGovernanceItemChange[]? items = null, Dictionary<string, int?>? limits = null) =>
        new(review ?? new(), agents ?? new(), items ?? Array.Empty<TeamGovernanceItemChange>(), limits ?? new());

    private TeamGovernanceView View(string caller)
    {
        var result = _teams.DescribeTeamGovernance(_team, caller, Library);
        Assert.Equal(TeamGovernanceViewOutcome.Found, result.Outcome);
        return result.View!;
    }

    private int ChangeRows()
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.TeamGovernanceChanges.AsNoTracking().Count(c => c.TeamId == _team);
    }

    // ---- Reading, by role ------------------------------------------------------------------------------------------

    [Fact]
    public void Describe_ANewTeam_ShowsTheDefaults_EveryAgentAllowed_NoReviewRule_NoLimit_AndNoChanges()
    {
        var view = View(Owner);

        Assert.True(view.CanChange);
        Assert.Null(view.Note);
        Assert.Equal("Acme - the rules every member's sessions work under. Only the Owner and Managers change them.", view.Summary);
        Assert.Equal(new[] { "agentReviewsPullRequests", "noSelfMerge", "workStartsAsAssignedIssue" }, view.Review.Select(r => r.Id));
        Assert.All(view.Review, r => Assert.False(r.On));
        Assert.Equal(new[] { "Claude Code", "Codex", "Any other agent" }, view.Agents.Select(a => a.Label));
        Assert.All(view.Agents, a => Assert.True(a.On));
        Assert.All(view.Limits, l => Assert.Equal((null, "No limit"), (l.Value, l.Display)));
        Assert.Empty(view.Library.Items);
        Assert.Equal(new[] { "Release checklist", "Review before merge" }, view.Library.Choices.Select(c => c.Name));
        Assert.Empty(view.Changes);
    }

    [Theory]
    [InlineData(Manager, true)]
    [InlineData(Developer, false)]
    public void Describe_AManagerMayChange_ADeveloperReadsTheSameRules_WithTheSentence_AndNoChoices(string caller, bool mayChange)
    {
        var view = View(caller);

        Assert.Equal(mayChange, view.CanChange);
        Assert.Equal(mayChange ? null : TeamRegistry.OwnerAndManagersChangeGovernance, view.Note);
        Assert.Equal(mayChange ? 2 : 0, view.Library.Choices.Count);
        Assert.Equal(3, view.Review.Count);
    }

    [Fact]
    public void Describe_ACollaborator_IsRefusedWithTheRoleTablesSentence_AndAStranger_IsNotFound()
    {
        var collaborator = _teams.DescribeTeamGovernance(_team, Collaborator, Library);
        Assert.Equal(TeamGovernanceViewOutcome.Forbidden, collaborator.Outcome);
        Assert.Contains("see the team's governance rules", collaborator.Refusal);

        Assert.Equal(TeamGovernanceViewOutcome.NotFound, _teams.DescribeTeamGovernance(_team, Stranger, Library).Outcome);
        Assert.Equal(TeamGovernanceViewOutcome.NotFound, _teams.DescribeTeamGovernance("no-such-team", Owner, Library).Outcome);
    }

    [Fact]
    public void ReadAccess_IsReadFromTheRoleTable()
    {
        var rows = TeamRegistry.ReadAccessRows().ToDictionary(r => r.Label, r => r.Who);

        Assert.Equal("Only that member", rows["A member's sessions and transcripts"]);
        Assert.Equal("Owner and Managers", rows["Prompts quoted on a Mentor page"]);
        Assert.Equal("The member, Owner and Managers", rows["Each member's Mentor page"]);
        Assert.Equal("Names and status only", rows["Fleet Map"]);
        // The cells behind them, so a changed cell shows here as a changed answer rather than a stale sentence.
        Assert.False(TeamPermissions.Allows(TeamRole.Owner, TeamAction.JoinOrWatchSomeoneElsesSession));
        Assert.False(TeamPermissions.Allows(TeamRole.Developer, TeamAction.ReadPromptsQuotedOnMentorPage));
    }

    // ---- Changing, by role -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Owner, "soren@acme.example")]
    [InlineData(Manager, "peter@acme.example")]
    public void Change_ByTheOwnerOrAManager_IsSaved_AndRecordedWithWhoWhatAndWhen(string caller, string name)
    {
        var result = _teams.ChangeTeamGovernance(_team, caller, Ask(review: new() { ["noSelfMerge"] = true }), Library);

        Assert.Equal(TeamGovernanceChangeOutcome.Done, result.Outcome);
        var view = View(Developer);
        Assert.True(view.Review.Single(r => r.Id == "noSelfMerge").On);
        var line = Assert.Single(view.Changes);
        Assert.Equal($"{name} switched on \"Nobody merges their own agent's work\"", line.Sentence);
        Assert.Equal("Tue 6 Oct 2026, 09:14 UTC", line.When);
    }

    [Theory]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void Change_ByADeveloperOrCollaborator_IsForbidden_AndSavesAndRecordsNothing(string caller)
    {
        var result = _teams.ChangeTeamGovernance(_team, caller,
            Ask(review: new() { ["noSelfMerge"] = true }, agents: new() { ["codex"] = false }), Library);

        Assert.Equal(TeamGovernanceChangeOutcome.Forbidden, result.Outcome);
        Assert.Contains("change the team's governance rules", result.Refusal);
        Assert.False(View(Owner).Review.Single(r => r.Id == "noSelfMerge").On);
        Assert.Equal(0, ChangeRows());
    }

    [Fact]
    public void Change_ByAStranger_IsNotFound_AndSavesNothing()
    {
        var result = _teams.ChangeTeamGovernance(_team, Stranger, Ask(agents: new() { ["codex"] = false }), Library);

        Assert.Equal(TeamGovernanceChangeOutcome.NotFound, result.Outcome);
        Assert.Equal(0, ChangeRows());
    }

    // ---- Every change recorded --------------------------------------------------------------------------------------

    [Fact]
    public void Change_OfSeveralRules_WritesOneLinePerChange_NewestFirst_AndSkipsWhatDidNotDiffer()
    {
        _teams.ChangeTeamGovernance(_team, Owner, Ask(agents: new() { ["otherAgents"] = false }), Library);
        _now = _now.AddMinutes(2);
        var result = _teams.ChangeTeamGovernance(_team, Manager, Ask(
            review: new() { ["agentReviewsPullRequests"] = true, ["noSelfMerge"] = false },   // the second is already off
            items: new[] { new TeamGovernanceItemChange(ReviewSkill.Kind, ReviewSkill.Id, TeamGovernanceCatalog.LevelRequired) },
            limits: new() { ["agentHoursPerWeek"] = 45, ["sessionsAtOnce"] = 8, ["keepMentorPagesMonths"] = 12 }), Library);

        Assert.Equal(5, result.Changes);
        var view = View(Owner);
        Assert.Equal(new[]
        {
            "peter@acme.example made \"Review before merge\" required",
            "peter@acme.example set \"Keep Mentor pages\" to 12 months",
            "peter@acme.example set \"Sessions running at once per member\" to 8",
            "peter@acme.example set \"Agent hours per member per week\" to 45 h",
            "peter@acme.example switched on \"An agent reviews every pull request before a person merges it\"",
            "soren@acme.example switched off \"Any other agent\"",
        }, view.Changes.Select(c => c.Sentence));
        Assert.Equal(new[] { "45 h", "8", "12 months" }, view.Limits.Select(l => l.Display));
        Assert.False(view.Agents.Single(a => a.Id == "otherAgents").On);
    }

    [Fact]
    public void Change_ThatDiffersInNothing_SavesNothing_AndRecordsNothing()
    {
        var result = _teams.ChangeTeamGovernance(_team, Owner,
            Ask(review: new() { ["noSelfMerge"] = false }, agents: new() { ["codex"] = true }, limits: new() { ["sessionsAtOnce"] = null }), Library);

        Assert.Equal((TeamGovernanceChangeOutcome.Done, 0), (result.Outcome, result.Changes));
        Assert.Equal(0, ChangeRows());
        using var ctx = _db.CreateUnscopedContext();
        Assert.False(ctx.TeamGovernance.Any(g => g.TeamId == _team));
    }

    [Fact]
    public void Change_RemovingALimit_IsRecorded()
    {
        _teams.ChangeTeamGovernance(_team, Owner, Ask(limits: new() { ["sessionsAtOnce"] = 8 }), Library);
        _now = _now.AddMinutes(1);
        _teams.ChangeTeamGovernance(_team, Owner, Ask(limits: new() { ["sessionsAtOnce"] = null }), Library);

        var view = View(Owner);
        Assert.Equal("No limit", view.Limits.Single(l => l.Id == "sessionsAtOnce").Display);
        Assert.Equal("soren@acme.example removed the limit on \"Sessions running at once per member\"", view.Changes[0].Sentence);
    }

    // ---- Required and suggested skills and workflows ----------------------------------------------------------------

    [Fact]
    public void Items_ComeFromTheTeamsLibrary_ChangeLevel_AndComeOff_EachRecorded()
    {
        Done(Ask(items: new[]
        {
            new TeamGovernanceItemChange(ReviewSkill.Kind, ReviewSkill.Id, TeamGovernanceCatalog.LevelRequired),
            new TeamGovernanceItemChange(ReleaseWorkflow.Kind, ReleaseWorkflow.Id, TeamGovernanceCatalog.LevelSuggested),
        }));
        var view = View(Owner);
        Assert.Equal(new[] { ("Review before merge", "Required"), ("Release checklist", "Suggested") },
            view.Library.Items.Select(i => (i.Name, i.Level)));
        Assert.Empty(view.Library.Choices);

        _now = _now.AddMinutes(1);
        Done(Ask(items: new[] { new TeamGovernanceItemChange(ReleaseWorkflow.Kind, ReleaseWorkflow.Id, TeamGovernanceCatalog.LevelRequired) }));
        _now = _now.AddMinutes(1);
        Done(Ask(items: new[] { new TeamGovernanceItemChange(ReviewSkill.Kind, ReviewSkill.Id, TeamGovernanceCatalog.LevelNone) }));

        view = View(Owner);
        Assert.Equal(new[] { ("Release checklist", "Required") }, view.Library.Items.Select(i => (i.Name, i.Level)));
        Assert.Equal(new[]
        {
            "soren@acme.example took \"Review before merge\" off the required and suggested skills and workflows",
            "soren@acme.example made \"Release checklist\" required",
        }, view.Changes.Take(2).Select(c => c.Sentence));
    }

    [Fact]
    public void Items_NotInTheTeamsLibrary_AreRefused_AndNothingInTheSameChangeIsSaved()
    {
        var result = _teams.ChangeTeamGovernance(_team, Owner, Ask(
            review: new() { ["noSelfMerge"] = true },
            items: new[] { new TeamGovernanceItemChange(TeamGovernanceCatalog.KindSkill, "a-built-in-or-made-up-skill", TeamGovernanceCatalog.LevelRequired) }), Library);

        Assert.Equal(TeamGovernanceChangeOutcome.Invalid, result.Outcome);
        Assert.Contains("not in the team's library", result.Refusal);
        Assert.False(View(Owner).Review.Single(r => r.Id == "noSelfMerge").On);
        Assert.Equal(0, ChangeRows());
    }

    [Fact]
    public void Items_ThatLeaveTheLibrary_StayOnTheList_UnderTheirLastName_AndSaySo()
    {
        Done(Ask(items: new[] { new TeamGovernanceItemChange(ReviewSkill.Kind, ReviewSkill.Id, TeamGovernanceCatalog.LevelRequired) }));

        var view = _teams.DescribeTeamGovernance(_team, Owner, new[] { ReleaseWorkflow }).View!;

        var item = Assert.Single(view.Library.Items);
        Assert.Equal(("Review before merge", TeamRegistry.GovernanceItemGone), (item.Name, item.Gone));
    }

    // ---- Refusals that save nothing ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("agentHoursPerWeek", 0)]
    [InlineData("agentHoursPerWeek", 169)]
    [InlineData("sessionsAtOnce", 101)]
    [InlineData("keepMentorPagesMonths", -1)]
    public void Limits_OutOfRange_AreInvalid_AndSaveNothing(string limit, int value)
    {
        var result = _teams.ChangeTeamGovernance(_team, Owner, Ask(limits: new() { [limit] = value }), Library);

        Assert.Equal(TeamGovernanceChangeOutcome.Invalid, result.Outcome);
        Assert.Contains("must be a whole number from", result.Refusal);
        Assert.Equal(0, ChangeRows());
    }

    [Fact]
    public void UnknownRuleNames_AreInvalid()
    {
        Assert.Equal(TeamGovernanceChangeOutcome.Invalid,
            _teams.ChangeTeamGovernance(_team, Owner, Ask(review: new() { ["codex"] = true }), Library).Outcome);
        Assert.Equal(TeamGovernanceChangeOutcome.Invalid,
            _teams.ChangeTeamGovernance(_team, Owner, Ask(agents: new() { ["gemini"] = true }), Library).Outcome);
        Assert.Equal(TeamGovernanceChangeOutcome.Invalid,
            _teams.ChangeTeamGovernance(_team, Owner, Ask(limits: new() { ["tokens"] = 5 }), Library).Outcome);
        Assert.Equal(0, ChangeRows());
    }

    [Fact]
    public void AChangeThatLosesARace_WithASecondGatewayProcess_IsAConflict_AndSavesNothingOfItsOwn()
    {
        Done(Ask(review: new() { ["noSelfMerge"] = true }));
        var before = ChangeRows();
        _governance.BeforeSaveForTests = id =>
        {
            // A second process saves first: its write bumps the version this change read.
            using var other = _db.CreateUnscopedContext();
            var row = other.TeamGovernance.Single(g => g.TeamId == id);
            row.Version += 1;
            other.SaveChanges();
        };

        var result = _teams.ChangeTeamGovernance(_team, Manager, Ask(agents: new() { ["codex"] = false }), Library);

        Assert.Equal(TeamGovernanceChangeOutcome.Conflict, result.Outcome);
        Assert.Equal(TeamGovernanceStore.ChangedAtTheSameMoment, result.Refusal);
        _governance.BeforeSaveForTests = null;
        Assert.True(View(Owner).Agents.Single(a => a.Id == "codex").On);
        Assert.Equal(before, ChangeRows());
    }

    [Fact]
    public void TheRecord_NamesAFormerMember_WithoutTheirAddress()
    {
        Done(Ask(review: new() { ["noSelfMerge"] = true }), Manager);
        Assert.True(_teams.RemoveMember(_team, Manager).IsDone);

        Assert.StartsWith(TeamLibraryEndpoints.FormerMember + " switched on", View(Owner).Changes.Single().Sentence);
    }

    private void Done(TeamGovernanceChangeRequest request, string caller = Owner) =>
        Assert.Equal(TeamGovernanceChangeOutcome.Done, _teams.ChangeTeamGovernance(_team, caller, request, Library).Outcome);

    // ---- The request body -------------------------------------------------------------------------------------------

    [Fact]
    public void ParseChange_ReadsEveryPart_AndANullLimitMeansNoLimit()
    {
        using var doc = JsonDocument.Parse("""
            {"review": {"noSelfMerge": true}, "agents": {"codex": false},
             "items": [{"kind": "Skill", "id": "x", "level": "Required"}], "limits": {"sessionsAtOnce": 8, "agentHoursPerWeek": null}}
            """);

        var (request, invalid) = TeamGovernanceEndpoints.ParseChange(doc.RootElement);

        Assert.Null(invalid);
        Assert.True(request!.Review["noSelfMerge"]);
        Assert.False(request.Agents["codex"]);
        Assert.Equal(new TeamGovernanceItemChange("Skill", "x", "Required"), Assert.Single(request.Items));
        Assert.Equal(8, request.Limits["sessionsAtOnce"]);
        Assert.True(request.Limits.ContainsKey("agentHoursPerWeek"));
        Assert.Null(request.Limits["agentHoursPerWeek"]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"review": {"noSelfMerge": "yes"}}""")]
    [InlineData("""{"limits": {"sessionsAtOnce": 8.5}}""")]
    [InlineData("""{"limits": {"sessionsAtOnce": "8"}}""")]
    [InlineData("""{"items": [{"kind": "Skill", "id": "x"}]}""")]
    [InlineData("""{"items": {"kind": "Skill"}}""")]
    [InlineData("""{"owner": "me"}""")]
    public void ParseChange_RefusesAPartOfTheWrongShape_RatherThanSkippingIt(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var (request, invalid) = TeamGovernanceEndpoints.ParseChange(doc.RootElement);

        Assert.Null(request);
        Assert.False(string.IsNullOrWhiteSpace(invalid));
    }

    // ---- The endpoint declarations ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", TeamAction.SeeTeamGovernance)]
    [InlineData("PUT", TeamAction.ChangeTeamGovernance)]
    public void TheGovernanceRoute_StatesItsActionInTheEndpointList(string method, TeamAction action)
    {
        var rule = TeamEndpointRules.All
            .Where(r => r.Covers(method, TeamGovernanceEndpoints.GovernancePath))
            .OrderByDescending(r => r.Prefix.Length)
            .First();

        Assert.Equal(action, rule.Action);
        Assert.Equal(TeamFrom.RouteTeamId, rule.TeamFrom);
    }
}
