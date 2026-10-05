using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// Requests to a team's Owner and Managers (devthrottle_internal#2308), over a real, throwaway, fully migrated Gateway
/// database with a real team of five - one member per role and a second Collaborator - a second team, and a stranger.
/// </summary>
public sealed class TeamRequestStoreTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";
    private const string OtherCollaborator = "sub-collaborator-2";
    private const string Stranger = "sub-stranger";
    private const string OtherTeamOwner = "sub-other-owner";

    private static readonly DateTime T0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly TeamRequestStore _store;
    private readonly string _team;
    private readonly string _otherTeam;
    private DateTime _now = T0;

    public TeamRequestStoreTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        foreach (var (subject, email) in new[]
                 {
                     (Owner, "olivia@example.com"), (Manager, "mark@example.com"), (Developer, "dev@example.com"),
                     (Collaborator, "carla@client.example"), (OtherCollaborator, "colin@client.example"),
                     (Stranger, "stranger@example.com"), (OtherTeamOwner, "other@example.com"),
                 })
            _tenants.MintOrLookupBySubject(subject, email);
        _teams = new TeamRegistry(_db, _tenants);
        _store = new TeamRequestStore(_db, _teams, () => _now);

        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Collaborator, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_team, OtherCollaborator, TeamRole.Collaborator).IsDone);
        _otherTeam = _teams.CreateTeam(OtherTeamOwner, "Other").Team!.TeamId;
    }

    public void Dispose() => _harness.Dispose();

    private TeamRequestView Sent(string sender = Collaborator, string text = "Please add a dark mode")
    {
        var result = _store.Send(_team, sender, text);
        Assert.Equal(TeamRequestOutcome.Done, result.Outcome);
        return result.Request!;
    }

    private int StoredRequests(string teamId)
    {
        using var ctx = _db.CreateContext(new TenantId(teamId));
        return ctx.TeamRequests.Count();
    }

    private (string State, int Steps) Stored(string requestId)
    {
        var id = Guid.Parse(requestId);
        using var ctx = _db.CreateContext(new TenantId(_team));
        var row = ctx.TeamRequests.Single(r => r.Id == id);
        return (row.State, ctx.TeamRequestChanges.Count(s => s.RequestId == id));
    }

    // ---- Send ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Owner)]
    [InlineData(Manager)]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void Send_AnyMember_StoresItSentWithTheSenderStampedAndOneTrailStep(string sender)
    {
        var request = Sent(sender, "  Please add a dark mode  ");

        Assert.Equal("Please add a dark mode", request.Text);
        Assert.Equal((TeamRequestStates.Sent, "Sent"), (request.State, request.StateLabel));
        Assert.True(request.IsYours);
        Assert.Equal("You", request.SentBy);
        Assert.Equal(T0, request.SentAtUtc);
        var step = Assert.Single(request.Trail);
        Assert.Equal(("Sent by You", "You", T0, (string?)null), (step.Sentence, step.By, step.AtUtc, step.Reason));
        Assert.Equal((TeamRequestStates.Sent, 1), Stored(request.Id));
    }

    [Fact]
    public void Send_NotAMember_IsNoSuchTeamAndStoresNothing()
    {
        var result = _store.Send(_team, Stranger, "Let me in");

        Assert.Equal(TeamRequestOutcome.NoSuchTeam, result.Outcome);
        Assert.Equal(0, StoredRequests(_team));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Send_NoText_IsInvalidAndStoresNothing(string? text)
    {
        var result = _store.Send(_team, Collaborator, text);

        Assert.Equal(TeamRequestOutcome.Invalid, result.Outcome);
        Assert.Equal("Write your request before sending it.", result.Refusal);
        Assert.Equal(0, StoredRequests(_team));
    }

    [Fact]
    public void Send_TooLong_IsInvalidAndStoresNothing()
    {
        var result = _store.Send(_team, Collaborator, new string('x', TeamRequestStates.MaxTextLength + 1));

        Assert.Equal(TeamRequestOutcome.Invalid, result.Outcome);
        Assert.Equal(0, StoredRequests(_team));
    }

    [Fact]
    public void Send_BlankSubject_Throws()
    {
        Assert.Throws<ArgumentException>(() => _store.Send(_team, " ", "x"));
    }

    // ---- Test 1 of #2308: visible to the Owner and Managers of that team only -------------------------------------

    [Theory]
    [InlineData(Owner)]
    [InlineData(Manager)]
    public void ListForTeam_OwnerOrManager_SeesEveryRequestNewestFirstWithWhoSentIt(string reader)
    {
        var first = Sent(Collaborator, "First");
        _now = T0.AddMinutes(1);
        var second = Sent(OtherCollaborator, "Second");

        var list = _store.ListForTeam(_team, reader);

        Assert.Equal(TeamRequestOutcome.Done, list.Outcome);
        Assert.Equal(new[] { second.Id, first.Id }, list.Requests.Select(r => r.Id));
        Assert.Equal(new[] { "colin@client.example", "carla@client.example" }, list.Requests.Select(r => r.SentBy));
        Assert.All(list.Requests, r => Assert.False(r.IsYours));
    }

    [Theory]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void ListForTeam_DeveloperOrCollaborator_IsRefusedInTheTablesWords(string reader)
    {
        Sent(OtherCollaborator);

        var list = _store.ListForTeam(_team, reader);

        Assert.Equal(TeamRequestOutcome.Refused, list.Outcome);
        Assert.Empty(list.Requests);
        Assert.Contains("may not read the team's Requests list", list.Refusal);
    }

    [Theory]
    [InlineData(Stranger)]
    [InlineData(OtherTeamOwner)]
    public void ListForTeam_NotAMember_IsNoSuchTeam(string reader)
    {
        Sent();

        var list = _store.ListForTeam(_team, reader);

        Assert.Equal(TeamRequestOutcome.NoSuchTeam, list.Outcome);
        Assert.Empty(list.Requests);
    }

    [Fact]
    public void ListMine_ACollaborator_SeesOnlyTheirOwnRequests()
    {
        var mine = Sent(Collaborator, "Mine");
        Sent(OtherCollaborator, "Theirs");
        Sent(Manager, "The manager's");

        var list = _store.ListMine(_team, Collaborator);

        Assert.Equal(TeamRequestOutcome.Done, list.Outcome);
        var only = Assert.Single(list.Requests);
        Assert.Equal((mine.Id, "Mine"), (only.Id, only.Text));
    }

    [Fact]
    public void ListMine_NotAMember_IsNoSuchTeam()
    {
        Assert.Equal(TeamRequestOutcome.NoSuchTeam, _store.ListMine(_team, Stranger).Outcome);
    }

    [Fact]
    public void ListMine_NothingSent_IsAnEmptyList()
    {
        var list = _store.ListMine(_team, Collaborator);
        Assert.Equal(TeamRequestOutcome.Done, list.Outcome);
        Assert.Empty(list.Requests);
    }

    // ---- Test 2 of #2308: each state change is shown to the sender, with the reason for Not doing this ------------

    [Fact]
    public void Decide_AcceptThenDone_TheSenderSeesEachStepWithWhoAndWhen()
    {
        var request = Sent();
        _now = T0.AddHours(1);
        Assert.Equal(TeamRequestOutcome.Done, _store.Decide(_team, request.Id, Manager, TeamRequestDecision.Accept, null).Outcome);
        _now = T0.AddHours(2);
        Assert.Equal(TeamRequestOutcome.Done, _store.Decide(_team, request.Id, Owner, TeamRequestDecision.MarkDone, null).Outcome);

        var seen = Assert.Single(_store.ListMine(_team, Collaborator).Requests);

        Assert.Equal(("done", "Done"), (seen.State, seen.StateLabel));
        Assert.Equal(T0.AddHours(2), seen.UpdatedAtUtc);
        Assert.Equal(new[] { "Sent by You", "Accepted by mark@example.com", "Marked Done by olivia@example.com" },
            seen.Trail.Select(s => s.Sentence));
        Assert.Equal(new[] { T0, T0.AddHours(1), T0.AddHours(2) }, seen.Trail.Select(s => s.AtUtc));
    }

    [Fact]
    public void Decide_NotDoingThisWithAReason_TheSenderSeesThePersonAndTheReason()
    {
        var request = Sent();
        _now = T0.AddMinutes(5);

        var result = _store.Decide(_team, request.Id, Manager, TeamRequestDecision.Decline, "  Out of scope for this project  ");

        Assert.Equal(TeamRequestOutcome.Done, result.Outcome);
        var seen = Assert.Single(_store.ListMine(_team, Collaborator).Requests);
        Assert.Equal(("declined", "Not doing this"), (seen.State, seen.StateLabel));
        var last = seen.Trail[^1];
        Assert.Equal(("Not doing this - mark@example.com", "mark@example.com", "Out of scope for this project", T0.AddMinutes(5)),
            (last.Sentence, last.By, last.Reason, last.AtUtc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Decide_NotDoingThisWithoutAReason_IsInvalidAndNothingChanges(string? reason)
    {
        var request = Sent();

        var result = _store.Decide(_team, request.Id, Owner, TeamRequestDecision.Decline, reason);

        Assert.Equal(TeamRequestOutcome.Invalid, result.Outcome);
        Assert.Equal((TeamRequestStates.Sent, 1), Stored(request.Id));
    }

    // ---- Test 4 of #2308: a Developer cannot accept or reject a request -------------------------------------------

    [Theory]
    [InlineData(Developer, TeamRequestDecision.Accept)]
    [InlineData(Developer, TeamRequestDecision.Decline)]
    [InlineData(Developer, TeamRequestDecision.MarkDone)]
    [InlineData(Collaborator, TeamRequestDecision.Accept)]
    [InlineData(Collaborator, TeamRequestDecision.Decline)]
    [InlineData(Collaborator, TeamRequestDecision.MarkDone)]
    public void Decide_DeveloperOrCollaborator_IsRefusedAndNothingChanges(string caller, TeamRequestDecision decision)
    {
        var request = Sent(OtherCollaborator);

        var result = _store.Decide(_team, request.Id, caller, decision, "a reason");

        Assert.Equal(TeamRequestOutcome.Refused, result.Outcome);
        Assert.Equal((TeamRequestStates.Sent, 1), Stored(request.Id));
    }

    [Fact]
    public void Decide_NotAMember_IsNoSuchTeamAndNothingChanges()
    {
        var request = Sent();

        Assert.Equal(TeamRequestOutcome.NoSuchTeam, _store.Decide(_team, request.Id, Stranger, TeamRequestDecision.Accept, null).Outcome);
        Assert.Equal((TeamRequestStates.Sent, 1), Stored(request.Id));
    }

    // ---- Tenant isolation: a request of another team -------------------------------------------------------------

    [Fact]
    public void Decide_ARequestOfAnotherTeam_ThroughTheCallersOwnTeam_IsNoSuchRequestAndNothingChanges()
    {
        var request = Sent();

        // The other team's Owner decides it through THEIR team, where they may decide: the request is not there.
        var result = _store.Decide(_otherTeam, request.Id, OtherTeamOwner, TeamRequestDecision.Accept, null);

        Assert.Equal(TeamRequestOutcome.NoSuchRequest, result.Outcome);
        Assert.Equal((TeamRequestStates.Sent, 1), Stored(request.Id));
        Assert.Empty(_store.ListForTeam(_otherTeam, OtherTeamOwner).Requests);
    }

    [Fact]
    public void Rows_AreScopedToTheTeamsTenant_AnotherTenantReadsNone()
    {
        Sent();

        using var other = _db.CreateContext(new TenantId(_otherTeam));
        Assert.Empty(other.TeamRequests.ToList());
        Assert.Empty(other.TeamRequestChanges.ToList());
        Assert.Equal(1, StoredRequests(_team));
    }

    // ---- State rules ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TeamRequestDecision.Decline)]
    [InlineData(TeamRequestDecision.MarkDone)]
    public void Decide_AfterAFinalState_IsConflictAndNothingChanges(TeamRequestDecision first)
    {
        var request = Sent();
        Assert.Equal(TeamRequestOutcome.Done, _store.Decide(_team, request.Id, Owner, first, "no").Outcome);

        var again = _store.Decide(_team, request.Id, Manager, TeamRequestDecision.Accept, null);

        Assert.Equal(TeamRequestOutcome.Conflict, again.Outcome);
        Assert.Equal(2, Stored(request.Id).Steps);
    }

    [Fact]
    public void Decide_AcceptTwice_IsConflict()
    {
        var request = Sent();
        _store.Decide(_team, request.Id, Owner, TeamRequestDecision.Accept, null);

        var again = _store.Decide(_team, request.Id, Manager, TeamRequestDecision.Accept, null);

        Assert.Equal((TeamRequestOutcome.Conflict, "This request has already been accepted."), (again.Outcome, again.Refusal));
    }

    [Theory]
    [InlineData("8d7c2b1e-3f4a-4b5c-9d6e-7f8a9b0c1d2e")]
    [InlineData("not-an-id")]
    public void Decide_NoSuchRequest_IsNoSuchRequest(string requestId)
    {
        Assert.Equal(TeamRequestOutcome.NoSuchRequest,
            _store.Decide(_team, requestId, Owner, TeamRequestDecision.Accept, null).Outcome);
    }

    [Fact]
    public void Decide_AReasonOnAccept_IsNotStored()
    {
        var request = Sent();

        var result = _store.Decide(_team, request.Id, Owner, TeamRequestDecision.Accept, "ignored");

        Assert.Null(result.Request!.Trail[^1].Reason);
    }

    // ---- The verdicts the screens render (rule 7) -----------------------------------------------------------------

    [Fact]
    public void Verdicts_ForTheOwner_FollowTheState()
    {
        var request = Sent();
        Assert.Equal((true, true, true), Verdicts(_store.ListForTeam(_team, Owner).Requests.Single()));

        _store.Decide(_team, request.Id, Owner, TeamRequestDecision.Accept, null);
        Assert.Equal((false, true, true), Verdicts(_store.ListForTeam(_team, Owner).Requests.Single()));

        _store.Decide(_team, request.Id, Owner, TeamRequestDecision.MarkDone, null);
        Assert.Equal((false, false, false), Verdicts(_store.ListForTeam(_team, Owner).Requests.Single()));
    }

    [Theory]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void Verdicts_ForASenderWhoMayNotDecide_AreAllFalse(string sender)
    {
        Sent(sender);
        Assert.Equal((false, false, false), Verdicts(_store.ListMine(_team, sender).Requests.Single()));
    }

    [Fact]
    public void Trail_ASenderWhoHasLeftTheTeam_IsNamedAsAFormerMember()
    {
        var request = Sent(OtherCollaborator);
        Assert.True(_teams.RemoveMember(_team, OtherCollaborator).IsDone);

        var seen = _store.ListForTeam(_team, Owner).Requests.Single(r => r.Id == request.Id);

        Assert.Equal(TeamRequestStore.FormerMemberName, seen.SentBy);
        Assert.Equal("Sent by " + TeamRequestStore.FormerMemberName, seen.Trail[0].Sentence);
    }

    private static (bool, bool, bool) Verdicts(TeamRequestView r) => (r.CanAccept, r.CanDecline, r.CanMarkDone);
}
