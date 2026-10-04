using System.Net;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// Team invitations by email that expire (devthrottle_internal#2301), over a real, throwaway, fully migrated Gateway
/// database. The six tests the issue names come first, then the decided pieces - the bill gate and the seat sync -
/// then every public method. The clock is injected; nothing sleeps. The website is a recording HTTP handler; nothing
/// leaves the process. In the log-capture collection because some tests read what was logged (review F2).
/// </summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class TeamInvitationTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";
    private const string Newcomer = "sub-newcomer";
    private const string Token = "test-gateway-service-token";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly RecordingWebsite _website = new();
    private readonly TeamRegistry _teams;
    private readonly TeamSeatSync _seatSync;
    private DateTime _now = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
    private readonly string _team;

    public TeamInvitationTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        using (var ctx = _db.CreateUnscopedContext())
        {
            // The website owns this table and creates it; the Gateway's migrations never do. Created here exactly as
            // the #2299 tests create it.
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS team_entitlements (" +
                "team_id TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, seats INTEGER NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, livemode INTEGER NULL, updated_at TEXT NULL)");
        }
        _seatSync = new TeamSeatSync(new EntitlementRegistry(_db, requireLivemode: false),
            new TeamSeatSyncClient(new HttpClient(_website), "https://website.test"), () => Token);
        _teams = new TeamRegistry(_db, _tenants, () => _now, _seatSync);

        _tenants.MintOrLookupBySubject(Owner, "owner@acme.example");
        _tenants.MintOrLookupBySubject(Manager, "manager@acme.example");
        _tenants.MintOrLookupBySubject(Developer, "developer@acme.example");
        _tenants.MintOrLookupBySubject(Collaborator, "collaborator@client.example");
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _teams.AddMember(_team, Manager, TeamRole.Manager);
        _teams.AddMember(_team, Developer, TeamRole.Developer);
        _teams.AddMember(_team, Collaborator, TeamRole.Collaborator);
        _teams.SeatSyncsSettled().GetAwaiter().GetResult();
        StartBill(_team, "active", seats: 3);
        _website.Calls.Clear();
    }

    public void Dispose() => _harness.Dispose();

    // ---- The six tests from devthrottle_internal#2301 -----------------------------------------------------------

    [Fact]
    public void Issue2301Test1_OwnerInvitesEveryRole_ManagerOnlyDeveloperAndCollaborator_DeveloperAndCollaboratorNobody()
    {
        Assert.True(Invite(Owner, "m@x.example", TeamRole.Manager).Outcome == TeamInvitationOutcome.Done);
        Assert.True(Invite(Owner, "d@x.example", TeamRole.Developer).Outcome == TeamInvitationOutcome.Done);
        Assert.True(Invite(Owner, "c@x.example", TeamRole.Collaborator).Outcome == TeamInvitationOutcome.Done);

        var managerAsManager = Invite(Manager, "m2@x.example", TeamRole.Manager);
        Assert.Equal(TeamInvitationOutcome.Forbidden, managerAsManager.Outcome);
        Assert.Equal(TeamInvitationRefusals.OnlyOwnerInvitesManager, managerAsManager.Refusal);
        Assert.Equal(TeamInvitationOutcome.Done, Invite(Manager, "d2@x.example", TeamRole.Developer).Outcome);
        Assert.Equal(TeamInvitationOutcome.Done, Invite(Manager, "c2@x.example", TeamRole.Collaborator).Outcome);

        foreach (var nobody in new[] { Developer, Collaborator })
        foreach (var role in new[] { TeamRole.Manager, TeamRole.Developer, TeamRole.Collaborator })
        {
            var refused = Invite(nobody, $"{role}@y.example", role);
            Assert.Equal(TeamInvitationOutcome.Forbidden, refused.Outcome);
            Assert.Equal(TeamInvitationRefusals.NotAllowedToInvite, refused.Refusal);
        }

        // Nobody, not even the Owner, can invite an Owner.
        Assert.Equal(TeamInvitationRefusals.InviteOwner, Invite(Owner, "o@x.example", TeamRole.Owner).Refusal);
        Assert.Equal(5, CountInvitations());
    }

    [Fact]
    public void Issue2301Test2_Accepting_CreatesTheMemberWithTheInvitedRole_Once_AndASecondAcceptIsRefused()
    {
        _tenants.MintOrLookupBySubject(Newcomer, "anna@devthrottle.example");
        var token = TokenOf(Invite(Owner, "anna@devthrottle.example", TeamRole.Developer));

        var accepted = _teams.AcceptInvitation(token, Newcomer);

        Assert.Equal(TeamInvitationOutcome.Done, accepted.Outcome);
        Assert.Equal(TeamInvitationStates.Accepted, accepted.Invitation!.State);
        Assert.Equal(TeamRole.Developer, _teams.RoleOf(_team, Newcomer));

        var again = _teams.AcceptInvitation(token, Newcomer);
        Assert.Equal(TeamInvitationOutcome.Refused, again.Outcome);
        Assert.Equal(TeamInvitationRefusals.AlreadyAccepted, again.Refusal);
        // A different account holding the same link is refused the same way - the invitation is used up.
        var other = _teams.AcceptInvitation(token, "sub-someone-else");
        Assert.Equal(TeamInvitationRefusals.AlreadyAccepted, other.Refusal);
        Assert.Null(_teams.RoleOf(_team, "sub-someone-else"));
        Assert.Equal(5, MemberCount());
    }

    [Fact]
    public void Issue2301Test3_ExpiredCancelledOrDeclined_CannotBeAccepted_AndSaysWhyInPlainWords()
    {
        var expired = TokenOf(Invite(Owner, "late@x.example", TeamRole.Developer));
        var cancelledInvite = Invite(Owner, "cancelled@x.example", TeamRole.Developer);
        var cancelled = TokenOf(cancelledInvite);
        var declined = TokenOf(Invite(Manager, "declined@x.example", TeamRole.Collaborator));
        Assert.Equal(TeamInvitationOutcome.Done, _teams.CancelInvitation(_team, cancelledInvite.Invitation!.Id, Owner).Outcome);
        Assert.Equal(TeamInvitationOutcome.Done, _teams.DeclineInvitation(declined, Newcomer).Outcome);

        _now = _now.AddDays(8);

        var e = _teams.AcceptInvitation(expired, Newcomer);
        Assert.Equal(TeamInvitationOutcome.Refused, e.Outcome);
        Assert.Equal("This invitation has expired. It was sent on 1 Oct 2026 and was good for 7 days. Ask owner@acme.example to send a new one.", e.Refusal);

        var c = _teams.AcceptInvitation(cancelled, Newcomer);
        Assert.Equal("This invitation was cancelled by the team, so it can no longer be accepted. Ask owner@acme.example if you still want to join.", c.Refusal);

        var d = _teams.AcceptInvitation(declined, Newcomer);
        Assert.Equal("This invitation was declined, so it can no longer be accepted. Ask manager@acme.example to send a new one if you want to join.", d.Refusal);

        Assert.Null(_teams.RoleOf(_team, Newcomer));
    }

    [Fact]
    public void Issue2301Test4_AnEmailWithNoAccount_SignsUpAndThenAcceptsFromTheSameLink()
    {
        // Invited before any account exists for the address - the Gateway knows nothing about the person yet.
        var token = TokenOf(Invite(Owner, "brand.new@nowhere.example", TeamRole.Collaborator));
        Assert.Null(_tenants.LookupBySubject(Newcomer));

        // The link is opened later: the person signs up (hosted enrolment mints their account) and the accept page
        // opens on the same link. (The Cockpit side of this round trip - signed out, to sign-up, back to /invite/{token}
        // - is proven in apps/cockpit inviteRoutes.test.tsx.)
        _now = _now.AddHours(3);
        _tenants.MintOrLookupBySubject(Newcomer, "brand.new@nowhere.example");
        var opened = _teams.OpenInvitation(token, Newcomer);
        Assert.True(opened.Invitation!.CanRespond);
        Assert.Equal("brand.new@nowhere.example", opened.Invitation.SignedInAs);

        Assert.Equal(TeamInvitationOutcome.Done, _teams.AcceptInvitation(token, Newcomer).Outcome);
        Assert.Equal(TeamRole.Collaborator, _teams.RoleOf(_team, Newcomer));
    }

    [Theory]
    [InlineData("anna@gmail.com")]
    [InlineData("rob@client.co.uk")]
    [InlineData("mike@northlane.io")]
    [InlineData("first.last+tag@sub.domain.museum")]
    [InlineData("Someone@Example.ORG")]
    [InlineData("x@xn--bcher-kva.example")]
    public void Issue2301Test5_AnyEmailDomain_IsAccepted(string email)
    {
        var result = Invite(Owner, email, TeamRole.Developer);

        Assert.Equal(TeamInvitationOutcome.Done, result.Outcome);
        Assert.Equal(email.ToLowerInvariant(), result.Invitation!.Email);
    }

    [Fact]
    public void Issue2301Test6_AcceptedOnDay7Works_OnDay8RefusedAsExpired()
    {
        _tenants.MintOrLookupBySubject(Newcomer, "n@x.example");
        var onDay7 = TokenOf(Invite(Owner, "day7@x.example", TeamRole.Developer));
        var onDay8 = TokenOf(Invite(Owner, "day8@x.example", TeamRole.Developer));
        var sent = _now;

        // Day 1 is the day it was sent; day 7 is the last day. The last minute of day 7 still works.
        _now = sent + TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1);
        Assert.Equal(TeamInvitationOutcome.Done, _teams.AcceptInvitation(onDay7, Newcomer).Outcome);

        _now = sent + TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1);
        var late = _teams.AcceptInvitation(onDay8, "sub-late");
        Assert.Equal(TeamInvitationOutcome.Refused, late.Outcome);
        Assert.StartsWith("This invitation has expired.", late.Refusal);
        Assert.Equal(TeamInvitationStates.Expired, _teams.ListInvitations(_team, Owner)!.Single(i => i.Email == "day8@x.example").State);
    }

    // ---- The bill gate (seam section 3, step 5) ------------------------------------------------------------------

    [Fact]
    public void CreateInvitation_TeamWithNoBill_IsRefusedWithThePlainWordsReason()
    {
        var unbilled = _teams.CreateTeam(Owner, "No bill yet").Team!.TeamId;

        var result = _teams.CreateInvitation(unbilled, Owner, "a@x.example", TeamRole.Developer);

        Assert.Equal(TeamInvitationOutcome.Refused, result.Outcome);
        Assert.Equal("The team's bill has not started - the Owner finishes billing first.", result.Refusal);
        Assert.Equal(0, CountInvitations(unbilled));
    }

    [Theory]
    [InlineData("canceled", TeamInvitationOutcome.Refused, TeamInvitationRefusals.BillCancelled)]
    [InlineData("incomplete", TeamInvitationOutcome.Refused, TeamInvitationRefusals.BillNotStarted)]
    [InlineData("past_due", TeamInvitationOutcome.Done, null)]
    [InlineData("active", TeamInvitationOutcome.Done, null)]
    public void CreateInvitation_ByBillStatus_OnlyAStartedBillAllowsIt(string status, TeamInvitationOutcome expected, string? refusal)
    {
        var team = _teams.CreateTeam(Owner, "Billing " + status).Team!.TeamId;
        StartBill(team, status, seats: 1);

        var result = _teams.CreateInvitation(team, Owner, "a@x.example", TeamRole.Developer);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(refusal, result.Refusal);
    }

    [Fact]
    public void CreateInvitation_BillCannotBeRead_IsRefusedAsUnavailableAndStoresNothing()
    {
        var teams = new TeamRegistry(_db, _tenants, () => _now, readTeamBill: _ => new TeamBilledSeats(false, false, null, null));

        var result = teams.CreateInvitation(_team, Owner, "a@x.example", TeamRole.Developer);

        Assert.Equal(TeamInvitationOutcome.Unavailable, result.Outcome);
        Assert.Equal(TeamInvitationRefusals.BillUnreadable, result.Refusal);
        Assert.Equal(0, CountInvitations());
    }

    // ---- Accepting looks at the bill too (Tech Lead ruling on review F5) ------------------------------------------

    [Fact]
    public void AcceptInvitation_BillCancelledSinceItWasSent_IsRefusedInPlainWords_AndNobodyJoins()
    {
        _tenants.MintOrLookupBySubject(Newcomer, "n@x.example");
        var token = TokenOf(Invite(Owner, "n@x.example", TeamRole.Developer));
        StartBill(_team, "canceled", seats: 3);

        var opened = _teams.OpenInvitation(token, Newcomer);
        var accepted = _teams.AcceptInvitation(token, Newcomer);

        Assert.False(opened.Invitation!.CanRespond);
        Assert.Equal(TeamInvitationRefusals.BillStopped, opened.Invitation.Refusal);
        Assert.Equal(TeamInvitationOutcome.Refused, accepted.Outcome);
        Assert.Equal(TeamInvitationRefusals.BillStopped, accepted.Refusal);
        Assert.Null(_teams.RoleOf(_team, Newcomer));
        Assert.Empty(_website.Calls);
    }

    [Fact]
    public void AcceptInvitation_BillPastDue_StillJoins()
    {
        _tenants.MintOrLookupBySubject(Newcomer, "n@x.example");
        var token = TokenOf(Invite(Owner, "n@x.example", TeamRole.Developer));
        StartBill(_team, "past_due", seats: 3);

        Assert.Equal(TeamInvitationOutcome.Done, _teams.AcceptInvitation(token, Newcomer).Outcome);
        Assert.Equal(TeamRole.Developer, _teams.RoleOf(_team, Newcomer));
    }

    [Fact]
    public void AcceptInvitation_BillCannotBeRead_IsUnavailable_AndNobodyJoins()
    {
        var token = TokenOf(Invite(Owner, "n@x.example", TeamRole.Developer));
        var teams = new TeamRegistry(_db, _tenants, () => _now, readTeamBill: _ => new TeamBilledSeats(false, false, null, null));

        var accepted = teams.AcceptInvitation(token, Newcomer);

        Assert.Equal(TeamInvitationOutcome.Unavailable, accepted.Outcome);
        Assert.Equal(TeamInvitationRefusals.BillUnreadableOnAccept, accepted.Refusal);
        Assert.Null(_teams.RoleOf(_team, Newcomer));
    }

    // ---- The log holds the kind of a refusal, never its sentence (review F2) --------------------------------------

    [Fact]
    public void RefusedAnswers_LogTheKind_NeverTheSentence_SoNoAddressReachesTheLog()
    {
        var cancelled = Invite(Owner, "cancelled.person@x.example", TeamRole.Developer);
        var cancelledToken = TokenOf(cancelled);
        _teams.CancelInvitation(_team, cancelled.Invitation!.Id, Owner);
        var declinedToken = TokenOf(Invite(Manager, "declined.person@x.example", TeamRole.Developer));
        _teams.DeclineInvitation(declinedToken, Newcomer);
        var expiredToken = TokenOf(Invite(Owner, "expired.person@x.example", TeamRole.Collaborator));
        _now = _now.AddDays(8);

        IReadOnlyList<string> lines;
        using (var log = FileLog.RedirectForTests())
        {
            foreach (var token in new[] { cancelledToken, declinedToken, expiredToken })
            {
                Assert.Equal(TeamInvitationOutcome.Refused, _teams.AcceptInvitation(token, Newcomer).Outcome);
                Assert.Equal(TeamInvitationOutcome.Refused, _teams.DeclineInvitation(token, Newcomer).Outcome);
                Assert.False(_teams.OpenInvitation(token, Newcomer).Invitation!.CanRespond);
            }
            Assert.Equal(TeamInvitationRefusals.AlreadyAMember, Invite(Owner, "developer@acme.example", TeamRole.Developer).Refusal);
            Assert.Equal(TeamInvitationRefusals.BadEmail, Invite(Owner, "not-an-address", TeamRole.Developer).Refusal);
            lines = log.DrainAndReadLines();
        }

        // The sentences shown to the person name the inviter by address ...
        Assert.Contains("owner@acme.example", _teams.AcceptInvitation(cancelledToken, Newcomer).Refusal);
        // ... and the log records only what kind of refusal it was.
        foreach (var kind in new[] { "kind=cancelled", "kind=declined", "kind=expired", "kind=already-a-member", "kind=bad-email" })
            Assert.Contains(lines, l => l.Contains(kind, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("refusal=expired", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains('@'));
        Assert.DoesNotContain(lines, l => l.Contains("not-an-address", StringComparison.Ordinal));
    }

    // ---- The seat sync (seam section 4) --------------------------------------------------------------------------

    [Fact]
    public async Task SeatSync_AcceptRemoveAndRoleChange_EachCallSyncOnceWithTheTeamId()
    {
        _tenants.MintOrLookupBySubject(Newcomer, "n@x.example");
        var token = TokenOf(Invite(Owner, "n@x.example", TeamRole.Developer));
        Assert.Empty(_website.Calls);

        _teams.AcceptInvitation(token, Newcomer);
        await _teams.SeatSyncsSettled();
        AssertOneSyncFor(_team);

        _teams.ChangeRole(_team, Newcomer, TeamRole.Collaborator);
        await _teams.SeatSyncsSettled();
        AssertOneSyncFor(_team);

        _teams.RemoveMember(_team, Newcomer);
        await _teams.SeatSyncsSettled();
        AssertOneSyncFor(_team);
    }

    [Fact]
    public async Task SeatSync_SendingResendingCancellingAndDeclining_NeverCallSync()
    {
        var a = Invite(Owner, "a@x.example", TeamRole.Developer);
        var b = Invite(Owner, "b@x.example", TeamRole.Developer);
        var c = Invite(Owner, "c@x.example", TeamRole.Developer);
        _teams.ResendInvitation(_team, a.Invitation!.Id, Owner);
        _teams.CancelInvitation(_team, b.Invitation!.Id, Owner);
        _teams.DeclineInvitation(TokenOf(c), Newcomer);
        _teams.OpenInvitation(TokenOf(a), Newcomer);
        await _teams.SeatSyncsSettled();

        Assert.Empty(_website.Calls);
    }

    [Fact]
    public async Task SeatSync_AFailedCall_IsRetriedByConvergence_AndConvergenceStopsOnceTheCountsMatch()
    {
        _tenants.MintOrLookupBySubject(Newcomer, "n@x.example");
        var token = TokenOf(Invite(Owner, "n@x.example", TeamRole.Developer));
        _website.Next.Enqueue(HttpStatusCode.ServiceUnavailable);

        _teams.AcceptInvitation(token, Newcomer);
        await _teams.SeatSyncsSettled();
        AssertOneSyncFor(_team);   // the call after the accept - which failed

        // The bill still says 3 seats; the Gateway now counts 4 paid members (Owner, Manager, two Developers).
        var convergence = new TeamSeatConvergence(_db, _seatSync);
        Assert.Equal(4, convergence.PaidMemberCounts()[_team]);
        Assert.Equal(1, await convergence.RunOnceAsync());
        AssertOneSyncFor(_team);   // the retry

        // The website billed the new count and its webhook wrote it to the row: the next pass calls nothing.
        StartBill(_team, "active", seats: 4);
        Assert.Equal(0, await convergence.RunOnceAsync());
        Assert.Empty(_website.Calls);
    }

    [Fact]
    public async Task SeatSync_NoSeatSyncOnThisGateway_StillCommitsTheMembership()
    {
        var teams = new TeamRegistry(_db, _tenants, () => _now);
        _tenants.MintOrLookupBySubject(Newcomer, "n@x.example");
        var token = TokenOf(teams.CreateInvitation(_team, Owner, "n@x.example", TeamRole.Developer));

        Assert.Equal(TeamInvitationOutcome.Done, teams.AcceptInvitation(token, Newcomer).Outcome);
        await teams.SeatSyncsSettled();

        Assert.Equal(TeamRole.Developer, teams.RoleOf(_team, Newcomer));
        Assert.Empty(_website.Calls);
    }

    [Fact]
    public async Task SeatConvergence_TeamWithNoBill_IsSkippedWithoutACall()
    {
        _teams.CreateTeam(Owner, "Checkout not finished");
        await _teams.SeatSyncsSettled();

        var convergence = new TeamSeatConvergence(_db, _seatSync);
        StartBill(_team, "active", seats: 3);

        Assert.Equal(0, await convergence.RunOnceAsync());
        Assert.Empty(_website.Calls);
    }

    // ---- Every public method ---------------------------------------------------------------------------------------

    [Fact]
    public void CreateInvitation_NotAnAddress_IsRefusedAndStoresNothing()
    {
        foreach (var bad in new[] { "", "   ", "anna", "anna@", "@x.example", "anna@localhost", "a b@x.example", "Anna <anna@x.example>", "a@@x.example" })
            Assert.Equal(TeamInvitationRefusals.BadEmail, Invite(Owner, bad, TeamRole.Developer).Refusal);
        Assert.Equal(0, CountInvitations());
    }

    [Fact]
    public void CreateInvitation_AddressAlreadyWaiting_IsRefused_ButAnExpiredOneCanBeInvitedAgain()
    {
        Invite(Owner, "anna@x.example", TeamRole.Developer);
        Assert.Equal(TeamInvitationRefusals.AlreadyInvited, Invite(Manager, "ANNA@x.example", TeamRole.Collaborator).Refusal);

        _now = _now.AddDays(8);
        Assert.Equal(TeamInvitationOutcome.Done, Invite(Owner, "anna@x.example", TeamRole.Developer).Outcome);
    }

    [Fact]
    public void CreateInvitation_AddressOfAnExistingMember_IsRefused()
    {
        Assert.Equal(TeamInvitationRefusals.AlreadyAMember, Invite(Owner, "Developer@Acme.example", TeamRole.Manager).Refusal);
    }

    [Fact]
    public void CreateInvitation_NotAMemberOrNoSuchTeam_IsNotFound()
    {
        Assert.Equal(TeamInvitationOutcome.NotFound, _teams.CreateInvitation(_team, "sub-outsider", "a@x.example", TeamRole.Developer).Outcome);
        Assert.Equal(TeamInvitationOutcome.NotFound, _teams.CreateInvitation("no-such-team", Owner, "a@x.example", TeamRole.Developer).Outcome);
    }

    [Fact]
    public void CreateInvitation_StoresSentStateSevenDaysAndAnUnguessableToken()
    {
        var result = Invite(Manager, "anna@x.example", TeamRole.Developer);

        var view = result.Invitation!;
        Assert.Equal(TeamInvitationStates.Sent, view.State);
        Assert.Equal(_now, view.SentAtUtc);
        Assert.Equal(_now.AddDays(7), view.ExpiresAtUtc);
        Assert.Equal("manager@acme.example", view.InvitedBy);
        Assert.Equal("owner@acme.example", view.PaidBy);
        var token = TokenOf(result);
        Assert.True(token.Length >= 43);
        Assert.NotEqual(token, TokenOf(Invite(Manager, "rob@x.example", TeamRole.Developer)));
    }

    [Fact]
    public void CreateInvitation_StoresOnlyTheHashOfTheLinksSecret()
    {
        var result = Invite(Owner, "anna@x.example", TeamRole.Developer);
        var token = TokenOf(result);

        using var ctx = _db.CreateUnscopedContext();
        var row = ctx.TeamInvitations.AsNoTracking().Single(i => i.Id == result.Invitation!.Id);
        Assert.Equal(TeamInvitationRules.HashAcceptToken(token), row.AcceptTokenHash);
        Assert.Equal(64, row.AcceptTokenHash.Length);
        // The secret appears nowhere in the stored row.
        var stored = string.Join("|", row.Id, row.TeamId, row.Email, row.State, row.InvitedBySubject, row.AcceptTokenHash);
        Assert.DoesNotContain(token, stored);
    }

    [Fact]
    public void AcceptInvitation_RecordsWhichAccountUsedTheLink_ForTheOwnerToSee()
    {
        _tenants.MintOrLookupBySubject(Newcomer, "personal.address@elsewhere.example");
        var token = TokenOf(Invite(Owner, "work.address@acme.example", TeamRole.Developer));

        _teams.AcceptInvitation(token, Newcomer);

        var listed = _teams.ListInvitations(_team, Owner)!.Single();
        Assert.Equal("work.address@acme.example", listed.Email);
        Assert.Equal("personal.address@elsewhere.example", listed.AcceptedBy);
    }

    [Fact]
    public void ListInvitations_OwnerAndManagerSeeThem_DeveloperSeesNone_OutsiderIsNotFound()
    {
        Invite(Owner, "a@x.example", TeamRole.Developer);
        _now = _now.AddMinutes(5);
        Invite(Manager, "b@x.example", TeamRole.Collaborator);

        var owners = _teams.ListInvitations(_team, Owner)!;
        Assert.Equal(new[] { "b@x.example", "a@x.example" }, owners.Select(i => i.Email));
        Assert.Equal(2, _teams.ListInvitations(_team, Manager)!.Count);
        Assert.Empty(_teams.ListInvitations(_team, Developer)!);
        Assert.Null(_teams.ListInvitations(_team, "sub-outsider"));
    }

    [Fact]
    public void ResendInvitation_StartsANewSevenDays_AndOnlyTheNewestLinkWorks()
    {
        var first = Invite(Owner, "anna@x.example", TeamRole.Developer);
        var oldToken = TokenOf(first);
        _now = _now.AddDays(10);   // long expired

        var resent = _teams.ResendInvitation(_team, first.Invitation!.Id, Owner);

        Assert.Equal(TeamInvitationOutcome.Done, resent.Outcome);
        Assert.Equal(TeamInvitationStates.Sent, resent.Invitation!.State);
        Assert.Equal(_now.AddDays(7), resent.Invitation.ExpiresAtUtc);
        Assert.Equal(TeamInvitationOutcome.NotFound, _teams.OpenInvitation(oldToken, Newcomer).Outcome);
        Assert.True(_teams.OpenInvitation(TokenOf(resent), Newcomer).Invitation!.CanRespond);
    }

    [Fact]
    public void ResendInvitation_AnExpiredOneSinceReplaced_IsRefused_SoOneAddressNeverHoldsTwoLiveLinks()
    {
        var old = Invite(Owner, "anna@x.example", TeamRole.Developer).Invitation!.Id;
        _now = _now.AddDays(8);
        TokenOf(Invite(Owner, "anna@x.example", TeamRole.Developer));   // expired, so invited again: allowed

        var resent = _teams.ResendInvitation(_team, old, Owner);

        Assert.Equal(TeamInvitationOutcome.Refused, resent.Outcome);
        Assert.Equal(TeamInvitationRefusals.AlreadyInvited, resent.Refusal);
        Assert.Null(resent.AcceptToken);
    }

    [Fact]
    public void ResendInvitation_AnExpiredOneWhosePersonHasSinceJoined_IsRefused()
    {
        _tenants.MintOrLookupBySubject(Newcomer, "anna@x.example");
        var old = Invite(Owner, "anna@x.example", TeamRole.Developer).Invitation!.Id;
        _now = _now.AddDays(8);
        Assert.Equal(TeamInvitationOutcome.Done, _teams.AcceptInvitation(TokenOf(Invite(Owner, "anna@x.example", TeamRole.Developer)), Newcomer).Outcome);

        var resent = _teams.ResendInvitation(_team, old, Owner);

        Assert.Equal(TeamInvitationOutcome.Refused, resent.Outcome);
        Assert.Equal(TeamInvitationRefusals.AlreadyAMember, resent.Refusal);
    }

    [Fact]
    public void ResendAndCancel_AManagerCannotTouchAManagerInvitation_ADeveloperNothing()
    {
        var managerInvite = Invite(Owner, "m@x.example", TeamRole.Manager).Invitation!.Id;
        var devInvite = Invite(Owner, "d@x.example", TeamRole.Developer).Invitation!.Id;

        Assert.Equal(TeamInvitationRefusals.OnlyOwnerInvitesManager, _teams.ResendInvitation(_team, managerInvite, Manager).Refusal);
        Assert.Equal(TeamInvitationRefusals.OnlyOwnerInvitesManager, _teams.CancelInvitation(_team, managerInvite, Manager).Refusal);
        Assert.Equal(TeamInvitationRefusals.NotAllowedToInvite, _teams.CancelInvitation(_team, devInvite, Developer).Refusal);
        Assert.Equal(TeamInvitationOutcome.Done, _teams.CancelInvitation(_team, devInvite, Manager).Outcome);
        Assert.Equal(TeamInvitationOutcome.NotFound, _teams.CancelInvitation(_team, "no-such-invitation", Owner).Outcome);
    }

    [Fact]
    public void ResendAndCancel_AnAnsweredInvitation_IsRefused()
    {
        var invite = Invite(Owner, "anna@x.example", TeamRole.Developer);
        _teams.DeclineInvitation(TokenOf(invite), Newcomer);

        Assert.Equal(TeamInvitationRefusals.NoLongerWaiting, _teams.ResendInvitation(_team, invite.Invitation!.Id, Owner).Refusal);
        Assert.Equal(TeamInvitationRefusals.NoLongerWaiting, _teams.CancelInvitation(_team, invite.Invitation.Id, Owner).Refusal);
    }

    [Fact]
    public void OpenInvitation_SaysWhoInvitedWhichTeamWhoPaysAndWhoIsSignedIn()
    {
        _tenants.MintOrLookupBySubject(Newcomer, "anna@devthrottle.example");
        var token = TokenOf(Invite(Manager, "anna@devthrottle.example", TeamRole.Developer));

        var view = _teams.OpenInvitation(token, Newcomer).Invitation!;

        Assert.Equal("Acme", view.TeamName);
        Assert.Equal("manager@acme.example", view.InvitedBy);
        Assert.Equal("owner@acme.example", view.PaidBy);
        Assert.Equal(TeamRole.Developer, view.Role);
        Assert.Equal("anna@devthrottle.example", view.SignedInAs);
        Assert.True(view.CanRespond);
        Assert.Null(view.Refusal);
    }

    [Fact]
    public void OpenInvitation_ACollaboratorSeatIsFree_AndAMemberCannotUseIt()
    {
        var token = TokenOf(Invite(Owner, "c9@x.example", TeamRole.Collaborator));

        var asMember = _teams.OpenInvitation(token, Developer).Invitation!;

        Assert.Null(asMember.PaidBy);
        Assert.False(asMember.CanRespond);
        Assert.Equal(TeamInvitationRefusals.CallerAlreadyMember, asMember.Refusal);
        Assert.Equal(TeamInvitationRefusals.CallerAlreadyMember, _teams.AcceptInvitation(token, Developer).Refusal);
        Assert.Equal(TeamRole.Developer, _teams.RoleOf(_team, Developer));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public void OpenAcceptAndDecline_UnknownToken_AreNotFound(string token)
    {
        Assert.Equal(TeamInvitationOutcome.NotFound, _teams.OpenInvitation(token, Newcomer).Outcome);
        Assert.Equal(TeamInvitationOutcome.NotFound, _teams.AcceptInvitation(token, Newcomer).Outcome);
        Assert.Equal(TeamInvitationOutcome.NotFound, _teams.DeclineInvitation(token, Newcomer).Outcome);
    }

    [Fact]
    public void DeclineInvitation_MarksItDeclined_AndASecondAnswerIsRefused()
    {
        var token = TokenOf(Invite(Owner, "anna@x.example", TeamRole.Developer));

        Assert.Equal(TeamInvitationStates.Declined, _teams.DeclineInvitation(token, Newcomer).Invitation!.State);
        Assert.Equal(TeamInvitationOutcome.Refused, _teams.DeclineInvitation(token, Newcomer).Outcome);
        Assert.Null(_teams.RoleOf(_team, Newcomer));
    }

    [Fact]
    public void InviteOptions_TheOwnerMayChooseEveryRole_AManagerNotManager_ADeveloperNothing()
    {
        var owner = _teams.InviteOptions(_team, Owner)!;
        Assert.All(owner.Roles, r => Assert.True(r.Allowed));
        Assert.Null(owner.Blocked);
        Assert.Equal("Runs sessions on their own computer. A paid seat on owner@acme.example's bill.",
            owner.Roles.Single(r => r.Role == TeamRole.Developer).Hint);

        var manager = _teams.InviteOptions(_team, Manager)!;
        var managerRow = manager.Roles.Single(r => r.Role == TeamRole.Manager);
        Assert.False(managerRow.Allowed);
        Assert.Equal("Only the Owner can invite a Manager.", managerRow.Hint);

        var developer = _teams.InviteOptions(_team, Developer)!;
        Assert.All(developer.Roles, r => Assert.False(r.Allowed));
        Assert.Equal(TeamInvitationRefusals.NotAllowedToInvite, developer.Blocked);

        Assert.Null(_teams.InviteOptions(_team, "sub-outsider"));
    }

    [Fact]
    public void InviteOptions_NoBill_IsBlockedWithTheBillReason()
    {
        var unbilled = _teams.CreateTeam(Owner, "No bill").Team!.TeamId;

        Assert.Equal(TeamInvitationRefusals.BillNotStarted, _teams.InviteOptions(unbilled, Owner)!.Blocked);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private TeamInvitationResult Invite(string inviter, string email, TeamRole role) =>
        _teams.CreateInvitation(_team, inviter, email, role);

    // The link's secret is handed back by a create or a resend and nowhere else: only its hash is stored.
    private static string TokenOf(TeamInvitationResult result)
    {
        Assert.Equal(TeamInvitationOutcome.Done, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.AcceptToken));
        return result.AcceptToken!;
    }

    private int CountInvitations(string? team = null)
    {
        using var ctx = _db.CreateUnscopedContext();
        var id = team ?? _team;
        return ctx.TeamInvitations.Count(i => i.TeamId == id);
    }

    private int MemberCount()
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.TeamMembers.Count(m => m.TeamId == _team);
    }

    private void StartBill(string teamId, string status, int seats)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("DELETE FROM team_entitlements WHERE team_id = {0}", teamId);
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO team_entitlements (team_id, status, seats, livemode) VALUES ({0}, {1}, {2}, 1)", teamId, status, seats);
    }

    private void AssertOneSyncFor(string teamId)
    {
        var call = Assert.Single(_website.Calls);
        Assert.Equal("https://website.test/api/v1/teams/sync-seats", call.Uri);
        Assert.Equal(teamId, JsonNode.Parse(call.Body)!["team_id"]!.GetValue<string>());
        Assert.Single(JsonNode.Parse(call.Body)!.AsObject());
        _website.Calls.Clear();
    }

    /// <summary>The website's seat-sync route: records every call and answers with the next queued status (200 when
    /// none is queued), the way #2299's route answers.</summary>
    private sealed class RecordingWebsite : HttpMessageHandler
    {
        public List<(string Uri, string Body)> Calls { get; } = new();
        public Queue<HttpStatusCode> Next { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((request.RequestUri!.ToString(), body));
            var status = Next.Count > 0 ? Next.Dequeue() : HttpStatusCode.OK;
            return new HttpResponseMessage(status) { Content = new StringContent(status == HttpStatusCode.OK
                ? "{\"data\":{\"changed\":true,\"seats\":4,\"stripe_quantity\":4}}"
                : "{\"error\":{\"code\":\"unavailable\",\"message\":\"down\"}}") };
        }
    }
}
