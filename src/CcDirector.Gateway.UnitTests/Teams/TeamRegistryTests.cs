using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// Teams and their members (devthrottle_internal#2300) over a real, throwaway, fully migrated Gateway database.
/// The four tests the issue names are the first four facts; the rest pin every public method.
/// </summary>
public sealed class TeamRegistryTests : IDisposable
{
    private const string Alice = "sub-alice";
    private const string Bob = "sub-bob";
    private const string Carol = "sub-carol";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;

    public TeamRegistryTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
    }

    public void Dispose() => _harness.Dispose();

    // ---- The four tests from devthrottle_internal#2300 ----------------------------------------------------------

    [Fact]
    public void Issue2300Test1_NewAccountHasNoTeam_AndCreatingOneMakesItOwnerAndChangesNothingElse()
    {
        // A new account, exactly as hosted enrolment mints it today.
        var personal = _tenants.MintOrLookupBySubject(Alice, "alice@example.com");
        Assert.Empty(_teams.ListTeamsFor(Alice));
        var before = Snapshot(personal);

        var created = _teams.CreateTeam(Alice, "Acme");

        // The creator is the team's Owner, and the team is a tenant of its own - never the personal one.
        var team = Assert.IsType<TeamSummary>(created.Team);
        Assert.Equal(TeamRole.Owner, team.Role);
        Assert.Equal(TeamRole.Owner, _teams.RoleOf(team.TeamId, Alice));
        Assert.NotEqual(personal.Value, team.TeamId);

        // Nothing else changed for the account's existing work: the same personal tenant resolves, by every
        // read that exists today, and its row is untouched.
        Assert.Equal(personal, _tenants.MintOrLookupBySubject(Alice, "alice@example.com"));
        Assert.Equal(personal, _tenants.LookupBySubject(Alice));
        Assert.Equal(Alice, _tenants.SubjectForTenant(personal));
        Assert.Equal(before, Snapshot(personal));
        // The team is not an account: it never appears in the accounts list the daily report and the
        // administrator lookups read.
        Assert.DoesNotContain(_tenants.ListAll(), r => r.TenantId == team.TeamId);
        Assert.Single(_tenants.ListAll());
    }

    [Fact]
    public void Issue2300Test2_MemberOfTeamA_CanNeverReadTeamBsMembersOrData()
    {
        _tenants.MintOrLookupBySubject(Alice, "alice@example.com");
        _tenants.MintOrLookupBySubject(Bob, "bob@example.com");
        var teamA = _teams.CreateTeam(Alice, "Team A").Team!;
        var teamB = _teams.CreateTeam(Bob, "Team B").Team!;

        // Members: Alice reads A's list, and is told B does not exist for her - the same answer as a team that
        // does not exist at all.
        Assert.Equal(TeamMembersOutcome.Found, _teams.ListMembers(teamA.TeamId, Alice).Outcome);
        var refused = _teams.ListMembers(teamB.TeamId, Alice);
        Assert.Equal(TeamMembersOutcome.NotFound, refused.Outcome);
        Assert.Null(refused.Team);
        Assert.Empty(refused.Members);
        Assert.Equal(TeamMembersOutcome.NotFound, _teams.ListMembers(Guid.NewGuid().ToString(), Alice).Outcome);
        Assert.Null(_teams.RoleOf(teamB.TeamId, Alice));
        Assert.DoesNotContain(_teams.ListTeamsFor(Alice), t => t.TeamId == teamB.TeamId);

        // Data: a row written in team B's tenant is invisible from team A's tenant (the tenant filter), and the
        // other way round.
        var inB = new CronJobStore(_harness.Open(new FixedTenantContext(teamB.Tenant)), _harness.LegacyPath("b.json"));
        var inA = new CronJobStore(_harness.Open(new FixedTenantContext(teamA.Tenant)), _harness.LegacyPath("a.json"));
        var jobB = inB.Create(Job("team-b-job"));
        var jobA = inA.Create(Job("team-a-job"));

        Assert.Null(inA.Get(jobB.Id));
        Assert.Equal(new[] { "team-a-job" }, inA.ListAll().Select(j => j.Name));
        Assert.Null(inB.Get(jobA.Id));
        Assert.Equal(new[] { "team-b-job" }, inB.ListAll().Select(j => j.Name));
    }

    [Fact]
    public void Issue2300Test3_OneAccountInTwoTeams_GetsTheRightRoleInEach()
    {
        var teamA = _teams.CreateTeam(Alice, "Team A").Team!;
        var teamB = _teams.CreateTeam(Bob, "Team B").Team!;
        Assert.True(_teams.AddMember(teamA.TeamId, Carol, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(teamB.TeamId, Carol, TeamRole.Developer).IsDone);

        Assert.Equal(TeamRole.Manager, _teams.RoleOf(teamA.TeamId, Carol));
        Assert.Equal(TeamRole.Developer, _teams.RoleOf(teamB.TeamId, Carol));

        var hers = _teams.ListTeamsFor(Carol).ToDictionary(t => t.TeamId, t => t.Role);
        Assert.Equal(2, hers.Count);
        Assert.Equal(TeamRole.Manager, hers[teamA.TeamId]);
        Assert.Equal(TeamRole.Developer, hers[teamB.TeamId]);

        // Changing her role in one team leaves the other alone.
        Assert.True(_teams.ChangeRole(teamB.TeamId, Carol, TeamRole.Collaborator).IsDone);
        Assert.Equal(TeamRole.Manager, _teams.RoleOf(teamA.TeamId, Carol));
        Assert.Equal(TeamRole.Collaborator, _teams.RoleOf(teamB.TeamId, Carol));
    }

    [Fact]
    public void Issue2300Test4_EveryTeamHasExactlyOneOwner_RemovingOrDemotingItIsRefused()
    {
        var team = _teams.CreateTeam(Alice, "Acme").Team!;
        _teams.AddMember(team.TeamId, Bob, TeamRole.Manager);

        var removed = _teams.RemoveMember(team.TeamId, Alice);
        Assert.False(removed.IsDone);
        Assert.Equal(TeamRefusals.RemoveOwner, removed.Refusal);

        var demoted = _teams.ChangeRole(team.TeamId, Alice, TeamRole.Manager);
        Assert.False(demoted.IsDone);
        Assert.Equal(TeamRefusals.DemoteOwner, demoted.Refusal);

        var promoted = _teams.ChangeRole(team.TeamId, Bob, TeamRole.Owner);
        Assert.False(promoted.IsDone);
        Assert.Equal(TeamRefusals.SecondOwner, promoted.Refusal);

        var addedOwner = _teams.AddMember(team.TeamId, Carol, TeamRole.Owner);
        Assert.False(addedOwner.IsDone);
        Assert.Equal(TeamRefusals.SecondOwner, addedOwner.Refusal);

        // After every refused attempt: exactly one Owner, and it is still the creator.
        Assert.Equal(new[] { Alice }, OwnersOf(team.TeamId));
        Assert.Equal(TeamRole.Manager, _teams.RoleOf(team.TeamId, Bob));
        Assert.Null(_teams.RoleOf(team.TeamId, Carol));
    }

    [Fact]
    public void Issue2300Test4_TheDatabaseItselfRefusesASecondOwner()
    {
        // The registry is not the only guard: a write that went around it is refused by the filtered unique index.
        var team = _teams.CreateTeam(Alice, "Acme").Team!;

        using var ctx = _db.CreateUnscopedContext();
        ctx.TeamMembers.Add(new TeamMemberEntity
        {
            TeamId = team.TeamId, AccountSubject = Bob, Role = TeamRole.Owner, JoinedAtUtc = DateTime.UtcNow,
        });
        Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
        Assert.Equal(new[] { Alice }, OwnersOf(team.TeamId));
    }

    [Fact]
    public void Roles_AreStoredAsTheFourLowerCaseWords_TheWebsiteReadsByName()
    {
        var team = _teams.CreateTeam(Alice, "Acme").Team!;
        _teams.AddMember(team.TeamId, Bob, TeamRole.Developer);

        using var ctx = _db.CreateUnscopedContext();
        var connection = ctx.Database.GetDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT role FROM team_members ORDER BY role";
        using var reader = command.ExecuteReader();
        var stored = new List<string>();
        while (reader.Read()) stored.Add(reader.GetString(0));

        Assert.Equal(new[] { "developer", "owner" }, stored);
    }

    // ---- CreateTeam ------------------------------------------------------------------------------------------

    [Fact]
    public void CreateTeam_ValidName_StoresTheTrimmedNameAndOneOwnerMember()
    {
        var created = _teams.CreateTeam(Alice, "  Acme Platform  ");

        Assert.Null(created.Refusal);
        var team = created.Team!;
        Assert.Equal("Acme Platform", team.Name);
        Assert.Equal(1, team.MemberCount);
        Assert.True(Guid.TryParse(team.TeamId, out _));
        Assert.Equal(new TenantId(team.TeamId), team.Tenant);
        var members = _teams.ListMembers(team.TeamId, Alice).Members;
        Assert.Equal(TeamRole.Owner, Assert.Single(members).Role);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Line one\nLine two")]
    [InlineData("Tab\there")]
    public void CreateTeam_UnusableName_IsRefusedAndCreatesNothing(string? name)
    {
        var created = _teams.CreateTeam(Alice, name);

        Assert.Null(created.Team);
        Assert.False(string.IsNullOrWhiteSpace(created.Refusal));
        Assert.Empty(_teams.ListTeamsFor(Alice));
    }

    [Fact]
    public void CreateTeam_NameAtTheLimit_IsAccepted_AndOneOverIsRefused()
    {
        Assert.NotNull(_teams.CreateTeam(Alice, new string('a', TeamRegistry.MaxNameLength)).Team);

        var tooLong = _teams.CreateTeam(Alice, new string('a', TeamRegistry.MaxNameLength + 1));
        Assert.Null(tooLong.Team);
        Assert.Contains(TeamRegistry.MaxNameLength.ToString(), tooLong.Refusal);
    }

    [Fact]
    public void CreateTeam_BlankSubject_Throws()
    {
        Assert.Throws<ArgumentException>(() => _teams.CreateTeam(" ", "Acme"));
    }

    [Fact]
    public void CreateTeam_TwiceBySameAccount_MakesTwoTeamsBothOwned()
    {
        var first = _teams.CreateTeam(Alice, "One").Team!;
        var second = _teams.CreateTeam(Alice, "Two").Team!;

        Assert.NotEqual(first.TeamId, second.TeamId);
        Assert.All(_teams.ListTeamsFor(Alice), t => Assert.Equal(TeamRole.Owner, t.Role));
        Assert.Equal(2, _teams.ListTeamsFor(Alice).Count);
    }

    [Fact]
    public void CreateTeam_NewTeam_JoinsTheTenantCensusAtOnce()
    {
        // The census is held between sweeps; a team created after it was read must still be swept, or the rows
        // the team writes would never be retained, expired or settled.
        var personal = _tenants.MintOrLookupBySubject(Alice, "alice@example.com");
        Assert.Equal(new[] { personal }, _tenants.AllTenantIds());

        var team = _teams.CreateTeam(Alice, "Acme").Team!;

        Assert.Equal(
            new[] { personal, team.Tenant }.OrderBy(t => t.Value),
            _tenants.AllTenantIds().OrderBy(t => t.Value));
    }

    // ---- ListTeamsFor ----------------------------------------------------------------------------------------

    [Fact]
    public void ListTeamsFor_AccountInNoTeam_IsEmpty()
    {
        _teams.CreateTeam(Bob, "Not Alice's");

        Assert.Empty(_teams.ListTeamsFor(Alice));
    }

    [Fact]
    public void ListTeamsFor_SeveralTeams_OrderedByNameWithMemberCounts()
    {
        var zed = _teams.CreateTeam(Alice, "zed").Team!;
        var acme = _teams.CreateTeam(Bob, "Acme").Team!;
        _teams.AddMember(acme.TeamId, Alice, TeamRole.Collaborator);
        _teams.AddMember(acme.TeamId, Carol, TeamRole.Developer);

        var list = _teams.ListTeamsFor(Alice);

        Assert.Equal(new[] { "Acme", "zed" }, list.Select(t => t.Name));
        Assert.Equal(3, list[0].MemberCount);
        Assert.Equal(TeamRole.Collaborator, list[0].Role);
        Assert.Equal(1, list[1].MemberCount);
        Assert.Equal(zed.TeamId, list[1].TeamId);
    }

    [Fact]
    public void ListTeamsFor_BlankSubject_Throws()
    {
        Assert.Throws<ArgumentException>(() => _teams.ListTeamsFor(""));
    }

    // ---- RoleOf ----------------------------------------------------------------------------------------------

    [Fact]
    public void RoleOf_MemberAndNonMember_RoleOrNull()
    {
        var team = _teams.CreateTeam(Alice, "Acme").Team!;
        _teams.AddMember(team.TeamId, Bob, TeamRole.Developer);

        Assert.Equal(TeamRole.Owner, _teams.RoleOf(team.TeamId, Alice));
        Assert.Equal(TeamRole.Developer, _teams.RoleOf(team.TeamId, Bob));
        Assert.Null(_teams.RoleOf(team.TeamId, Carol));
        Assert.Null(_teams.RoleOf("", Alice));
    }

    // ---- ListMembers -----------------------------------------------------------------------------------------

    [Fact]
    public void ListMembers_ForAMember_ListsEveryoneOwnerFirstWithTheirEmails()
    {
        _tenants.MintOrLookupBySubject(Alice, "alice@example.com");
        _tenants.MintOrLookupBySubject(Bob, "bob@example.com");
        var team = _teams.CreateTeam(Alice, "Acme").Team!;
        _teams.AddMember(team.TeamId, Bob, TeamRole.Collaborator);
        _teams.AddMember(team.TeamId, Carol, TeamRole.Manager);   // Carol has no personal tenant row: no email

        var result = _teams.ListMembers(team.TeamId, Bob);

        Assert.Equal(TeamMembersOutcome.Found, result.Outcome);
        Assert.Equal(TeamRole.Collaborator, result.Team!.Role);   // the CALLER's role
        Assert.Equal(3, result.Team.MemberCount);
        Assert.Equal(new[] { TeamRole.Owner, TeamRole.Manager, TeamRole.Collaborator }, result.Members.Select(m => m.Role));
        Assert.Equal(new[] { "alice@example.com", null, "bob@example.com" }, result.Members.Select(m => m.Email));
    }

    [Fact]
    public void ListMembers_BlankTeamId_IsNotFound()
    {
        Assert.Equal(TeamMembersOutcome.NotFound, _teams.ListMembers(" ", Alice).Outcome);
    }

    // ---- AddMember -------------------------------------------------------------------------------------------

    [Fact]
    public void AddMember_NoSuchTeam_IsRefused()
    {
        var result = _teams.AddMember(Guid.NewGuid().ToString(), Bob, TeamRole.Developer);

        Assert.False(result.IsDone);
        Assert.Equal(TeamRefusals.NoSuchTeam, result.Refusal);
    }

    [Fact]
    public void AddMember_AlreadyAMember_IsRefusedAndKeepsTheRole()
    {
        var team = _teams.CreateTeam(Alice, "Acme").Team!;
        _teams.AddMember(team.TeamId, Bob, TeamRole.Developer);

        var again = _teams.AddMember(team.TeamId, Bob, TeamRole.Manager);

        Assert.False(again.IsDone);
        Assert.Equal(TeamRefusals.AlreadyAMember, again.Refusal);
        Assert.Equal(TeamRole.Developer, _teams.RoleOf(team.TeamId, Bob));
    }

    [Fact]
    public void AddMember_UndefinedRole_Throws()
    {
        var team = _teams.CreateTeam(Alice, "Acme").Team!;

        Assert.Throws<ArgumentOutOfRangeException>(() => _teams.AddMember(team.TeamId, Bob, (TeamRole)7));
    }

    // ---- ChangeRole ------------------------------------------------------------------------------------------

    [Fact]
    public void ChangeRole_NotAMember_IsRefused()
    {
        var team = _teams.CreateTeam(Alice, "Acme").Team!;

        var result = _teams.ChangeRole(team.TeamId, Bob, TeamRole.Developer);

        Assert.False(result.IsDone);
        Assert.Equal(TeamRefusals.NotAMember, result.Refusal);
    }

    [Fact]
    public void ChangeRole_SameRole_IsDoneAndUnchanged()
    {
        var team = _teams.CreateTeam(Alice, "Acme").Team!;
        _teams.AddMember(team.TeamId, Bob, TeamRole.Developer);

        Assert.True(_teams.ChangeRole(team.TeamId, Bob, TeamRole.Developer).IsDone);
        Assert.Equal(TeamRole.Developer, _teams.RoleOf(team.TeamId, Bob));
    }

    // ---- RemoveMember ----------------------------------------------------------------------------------------

    [Fact]
    public void RemoveMember_ANonOwner_IsRemoved()
    {
        var team = _teams.CreateTeam(Alice, "Acme").Team!;
        _teams.AddMember(team.TeamId, Bob, TeamRole.Developer);

        Assert.True(_teams.RemoveMember(team.TeamId, Bob).IsDone);
        Assert.Null(_teams.RoleOf(team.TeamId, Bob));
        Assert.Empty(_teams.ListTeamsFor(Bob));
    }

    [Fact]
    public void RemoveMember_NotAMember_IsRefused()
    {
        var team = _teams.CreateTeam(Alice, "Acme").Team!;

        var result = _teams.RemoveMember(team.TeamId, Bob);

        Assert.False(result.IsDone);
        Assert.Equal(TeamRefusals.NotAMember, result.Refusal);
    }

    // ---- NameRefusal -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Acme", true)]
    [InlineData(" Acme ", true)]
    [InlineData("", false)]
    [InlineData("a\u0007b", false)]
    public void NameRefusal_EachName_RefusesOnlyTheUnusable(string name, bool usable)
    {
        Assert.Equal(usable, TeamRegistry.NameRefusal(name) is null);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private string[] OwnersOf(string teamId)
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.Role == TeamRole.Owner)
            .Select(m => m.AccountSubject)
            .ToArray();
    }

    private (string Id, string Subject, string? Email, DateTime Created) Snapshot(TenantId personal)
    {
        using var ctx = _db.CreateUnscopedContext();
        var row = ctx.Tenants.AsNoTracking().Single(t => t.Id == personal.Value);
        return (row.Id, row.AccountSubject, row.Email, row.CreatedAtUtc);
    }

    private static CcDirector.Gateway.Contracts.CronJobDto Job(string name) => new()
    {
        Name = name,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CcDirector.Gateway.Contracts.CronJobTarget { Machine = "m" },
        Action = new CcDirector.Gateway.Contracts.CronJobAction { RepoPath = @"D:\r", Seed = "/x" },
    };
}
