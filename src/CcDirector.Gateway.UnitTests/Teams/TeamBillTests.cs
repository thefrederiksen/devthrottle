using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// THE TEAM BILL WITHOUT STRIPE (Teams v1). The owner's ruling, 7 Oct 2026: "we're doing everything except actually
/// charging, but we are pretending we're charging." Over a real, throwaway, fully migrated Gateway database with an
/// injected clock:
///  - each of the Owner's four actions - start the plan, renew now, auto-renew on and off, cancel - and what each leaves
///    on the bill and in its history;
///  - every other role refused where it must be: a Manager sees the bill and changes nothing, a Developer and a
///    Collaborator neither see nor change it, a stranger is told there is no such team;
///  - the renewal pass: an auto-renewing bill rolls to the next month (one history line a month), any other ends;
///  - the readers of the bill: paid features and invitations follow the Gateway's own bill, and stop once it has ended;
///  - every amount is real and every charge is US$0.00, and no sentence says "free".
/// </summary>
public sealed class TeamBillTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";
    private const string Stranger = "sub-stranger";
    private const string Newcomer = "sub-newcomer";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamBillStore _bills;
    private readonly TeamRegistry _teams;
    private readonly EntitlementRegistry _entitlements;
    private DateTime _now = new(2026, 10, 7, 9, 30, 0, DateTimeKind.Utc);
    private readonly string _team;

    public TeamBillTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _bills = new TeamBillStore(_db, () => _now);
        // As the hosted Gateway builds it: live money required of personal rows, and the invitation gate reading the
        // SAME one reader of the team's bill.
        _entitlements = new EntitlementRegistry(_db, requireLivemode: true);
        _teams = new TeamRegistry(_db, _tenants, () => _now, bills: _bills, readTeamBill: _entitlements.ReadTeamBill);

        foreach (var (subject, email) in new[]
                 {
                     (Owner, "soren@acme.example"), (Manager, "priya@acme.example"), (Developer, "rob@acme.example"),
                     (Collaborator, "mike@client.example"), (Stranger, "stranger@else.example"), (Newcomer, "anna@acme.example"),
                 })
            _tenants.MintOrLookupBySubject(subject, email);
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Collaborator, TeamRole.Collaborator).IsDone);
    }

    public void Dispose() => _harness.Dispose();

    // ---- The Owner's actions ----------------------------------------------------------------------------------------

    [Fact]
    public void StartTeamPlan_ByTheOwner_IsActiveForOneMonth_AutoRenewOn_AtThePaidSeats_AndChargesNothing()
    {
        Assert.False(_entitlements.ReadTeamBill(_team).HasBill);

        Assert.Equal(TeamBillChangeOutcome.Done, _teams.StartTeamPlan(_team, Owner).Outcome);

        var bill = Bill();
        Assert.Equal(EntitlementRegistry.StatusActive, bill.Status);
        Assert.Equal(3, bill.Seats);   // the Owner, the Manager and the Developer; never the Collaborator
        Assert.Equal(4900, bill.PricePerSeatCents);
        Assert.Equal(_now, bill.CurrentPeriodStartUtc);
        Assert.Equal(_now.AddMonths(1), bill.CurrentPeriodEndUtc);
        Assert.True(bill.AutoRenew);

        var line = Assert.Single(History());
        Assert.Equal(TeamBillStore.ReasonStarted, line.Reason);
        Assert.Equal(3, line.Seats);
        Assert.Equal(3 * 4900, line.AmountCents);
        Assert.Equal(0, line.ChargedCents);
        Assert.Equal(_now, line.PeriodStartUtc);
        Assert.Equal(_now.AddMonths(1), line.PeriodEndUtc);
    }

    [Fact]
    public void StartTeamPlan_WhenItIsRunning_IsRefused_AndWhenItHasEnded_SaysRenewInstead()
    {
        _teams.StartTeamPlan(_team, Owner);

        var again = _teams.StartTeamPlan(_team, Owner);
        Assert.Equal(TeamBillChangeOutcome.Refused, again.Outcome);
        Assert.Equal(TeamBillRefusals.AlreadyStarted, again.Refusal);

        EndThePlan();
        Assert.Equal(TeamBillRefusals.RenewInstead, _teams.StartTeamPlan(_team, Owner).Refusal);
        Assert.Single(History());
    }

    [Fact]
    public void Cancel_KeepsThePlanActiveToThePeriodEnd_ThenTheRenewalPassEndsIt()
    {
        _teams.StartTeamPlan(_team, Owner);
        var end = Bill().CurrentPeriodEndUtc;

        Assert.Equal(TeamBillChangeOutcome.Done, _teams.CancelTeamPlan(_team, Owner).Outcome);

        // Still running: paid features and invitations carry on to the end of the period.
        Assert.Equal(EntitlementRegistry.StatusActive, Bill().Status);
        Assert.False(Bill().AutoRenew);
        Assert.Equal(EntitlementOutcome.Entitled, _entitlements.EvaluateTeam(_team, _now).Outcome);
        Assert.Equal(TeamInvitationOutcome.Done, _teams.CreateInvitation(_team, Owner, "early@acme.example", TeamRole.Developer).Outcome);
        Assert.Equal(TeamBillStates.Ending, Page(Owner).Bill!.State);

        // A minute before the end the pass does nothing; at the end it ends the plan.
        _now = end.AddMinutes(-1);
        Assert.Equal(new TeamBillRenewalSummary(0, 0, 0, 0, 0), _bills.RenewDue());
        _now = end;
        Assert.Equal(new TeamBillRenewalSummary(1, 0, 1, 0, 0), _bills.RenewDue());

        Assert.Equal(EntitlementRegistry.StatusCanceled, Bill().Status);
        Assert.Single(History());   // an ended period is not charged again
        Assert.Equal(TeamBillStates.Ended, Page(Owner).Bill!.State);
    }

    [Fact]
    public void Cancel_AnEndingPlan_ChangesNothing_AnEndedOne_IsRefused_ANeverStartedOne_IsRefused()
    {
        Assert.Equal(TeamBillRefusals.NotStarted, _teams.CancelTeamPlan(_team, Owner).Refusal);

        _teams.StartTeamPlan(_team, Owner);
        _teams.CancelTeamPlan(_team, Owner);
        var ending = Bill();
        _now = _now.AddHours(1);
        Assert.Equal(TeamBillChangeOutcome.Done, _teams.CancelTeamPlan(_team, Owner).Outcome);
        Assert.Equal(ending.UpdatedAtUtc, Bill().UpdatedAtUtc);

        EndThePlan();
        Assert.Equal(TeamBillRefusals.AlreadyEnded, _teams.CancelTeamPlan(_team, Owner).Refusal);
    }

    [Fact]
    public void AutoRenew_OffMakesThePlanEnding_OnMakesItRenewAgain_AndOnAnEndedPlanIsRefused()
    {
        Assert.Equal(TeamBillRefusals.NotStarted, _teams.SetTeamPlanAutoRenew(_team, Owner, on: false).Refusal);
        _teams.StartTeamPlan(_team, Owner);

        Assert.Equal(TeamBillChangeOutcome.Done, _teams.SetTeamPlanAutoRenew(_team, Owner, on: false).Outcome);
        Assert.False(Bill().AutoRenew);
        Assert.Equal(TeamBillStates.Ending, Page(Owner).Bill!.State);

        Assert.Equal(TeamBillChangeOutcome.Done, _teams.SetTeamPlanAutoRenew(_team, Owner, on: true).Outcome);
        Assert.True(Bill().AutoRenew);
        Assert.Equal(TeamBillStates.Active, Page(Owner).Bill!.State);

        _teams.SetTeamPlanAutoRenew(_team, Owner, on: false);
        EndThePlan();
        Assert.Equal(TeamBillRefusals.EndedRenewInstead, _teams.SetTeamPlanAutoRenew(_team, Owner, on: true).Refusal);
        Assert.Equal(EntitlementRegistry.StatusCanceled, Bill().Status);
    }

    [Fact]
    public void RenewNow_AnEndedPlan_IsActiveForANewMonthFromNow_AutoRenewOn_WithAHistoryLine()
    {
        _teams.StartTeamPlan(_team, Owner);
        EndThePlan();
        Assert.Equal(EntitlementOutcome.NotEntitled, _entitlements.EvaluateTeam(_team, _now).Outcome);
        _now = _now.AddDays(3);

        Assert.Equal(TeamBillChangeOutcome.Done, _teams.RenewTeamPlan(_team, Owner).Outcome);

        var bill = Bill();
        Assert.Equal(EntitlementRegistry.StatusActive, bill.Status);
        Assert.True(bill.AutoRenew);
        Assert.Equal(_now, bill.CurrentPeriodStartUtc);
        Assert.Equal(_now.AddMonths(1), bill.CurrentPeriodEndUtc);
        var line = History()[0];
        Assert.Equal(TeamBillStore.ReasonRenewed, line.Reason);
        Assert.Equal(0, line.ChargedCents);
        Assert.Equal(2, History().Count);
        Assert.Equal(EntitlementOutcome.Entitled, _entitlements.EvaluateTeam(_team, _now).Outcome);
    }

    [Fact]
    public void RenewNow_IsOfferedOnlyOnceThePlanHasEnded_AnEndingPlanGoesBackByTheSwitch_KeepingItsPeriod_WithNoNewLine()
    {
        Assert.Equal(TeamBillRefusals.NotStarted, _teams.RenewTeamPlan(_team, Owner).Refusal);

        _teams.StartTeamPlan(_team, Owner);
        var renews = _teams.RenewTeamPlan(_team, Owner);
        Assert.Equal(TeamBillChangeOutcome.Refused, renews.Outcome);
        Assert.Equal(TeamBillRefusals.AlreadyRenews(TeamBillStore.Day(_now.AddMonths(1))), renews.Refusal);

        // Ending (the Delivery Lead's ruling, 7 Oct 2026): Renew is neither offered nor accepted - a renewal would begin a
        // second month overlapping the one running, two charge lines for one month of service.
        _teams.CancelTeamPlan(_team, Owner);
        var period = (Bill().CurrentPeriodStartUtc, Bill().CurrentPeriodEndUtc);
        _now = _now.AddDays(10);
        var ending = Page(Owner).Bill!;
        Assert.Equal(TeamBillStates.Ending, ending.State);
        Assert.False(ending.CanRenew);
        Assert.Null(ending.Checkout);
        Assert.True(ending.CanSetAutoRenew);
        var refused = _teams.RenewTeamPlan(_team, Owner);
        Assert.Equal(TeamBillChangeOutcome.Refused, refused.Outcome);
        Assert.Equal(TeamBillRefusals.StillRunning(TeamBillStore.Day(period.CurrentPeriodEndUtc)), refused.Refusal);
        Assert.Single(History());

        // The way back for an ending plan is the switch: the same period, no new line.
        Assert.Equal(TeamBillChangeOutcome.Done, _teams.SetTeamPlanAutoRenew(_team, Owner, on: true).Outcome);
        Assert.True(Bill().AutoRenew);
        Assert.Equal(period, (Bill().CurrentPeriodStartUtc, Bill().CurrentPeriodEndUtc));
        Assert.Single(History());

        // Once it has ended, Renew is offered, with its checkout.
        EndThePlan();
        var ended = Page(Owner).Bill!;
        Assert.True(ended.CanRenew);
        Assert.Equal("Renew now", ended.Checkout!.ConfirmLabel);
    }

    [Fact]
    public void PeriodEnd_IsAnchoredToThePlansStart_SoTheBillingDaySurvivesAShortMonth()
    {
        var jan31 = new DateTime(2027, 1, 31, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2027, 2, 28, 9, 0, 0, DateTimeKind.Utc), TeamBillStore.PeriodEnd(jan31, jan31));
        Assert.Equal(new DateTime(2027, 3, 31, 9, 0, 0, DateTimeKind.Utc), TeamBillStore.PeriodEnd(jan31, new DateTime(2027, 2, 28, 9, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2027, 4, 30, 9, 0, 0, DateTimeKind.Utc), TeamBillStore.PeriodEnd(jan31, new DateTime(2027, 3, 31, 9, 0, 0, DateTimeKind.Utc)));
        Assert.Throws<ArgumentException>(() => TeamBillStore.PeriodEnd(jan31, jan31.AddDays(-1)));
    }

    [Fact]
    public void RenewalPass_APlanStartedOnThe31st_RenewsOnThe31stAgainAfterFebruary()
    {
        _now = new DateTime(2027, 1, 31, 9, 0, 0, DateTimeKind.Utc);
        _teams.StartTeamPlan(_team, Owner);
        Assert.Equal(new DateTime(2027, 2, 28, 9, 0, 0, DateTimeKind.Utc), Bill().CurrentPeriodEndUtc);

        _now = new DateTime(2027, 4, 1, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new TeamBillRenewalSummary(1, 1, 0, 2, 0), _bills.RenewDue());

        Assert.Equal(new[]
        {
            new DateTime(2027, 3, 31, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 2, 28, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 31, 9, 0, 0, DateTimeKind.Utc),
        }, History().Select(h => h.PeriodStartUtc));
        Assert.Equal(new DateTime(2027, 4, 30, 9, 0, 0, DateTimeKind.Utc), Bill().CurrentPeriodEndUtc);
    }

    // ---- Every other role is refused where it must be ----------------------------------------------------------------

    public static IEnumerable<object[]> NotTheOwner() =>
        from who in new[] { Manager, Developer, Collaborator }
        from action in new[] { "start", "renew", "auto-renew-off", "auto-renew-on", "cancel" }
        select new object[] { who, action };

    [Theory]
    [MemberData(nameof(NotTheOwner))]
    public void BillActions_ByAnyoneButTheOwner_AreForbidden_AndChangeNothing(string who, string action)
    {
        // A bill in each state the action could act on: none for start, an ending one for renew, a renewing one otherwise.
        if (action != "start")
            _teams.StartTeamPlan(_team, Owner);
        if (action is "renew" or "auto-renew-on")
            _teams.CancelTeamPlan(_team, Owner);
        var before = _bills.Find(_team);
        _now = _now.AddMinutes(5);

        var result = Act(action, who);

        Assert.Equal(TeamBillChangeOutcome.Forbidden, result.Outcome);
        Assert.Contains("change the billing", result.Refusal);
        var after = _bills.Find(_team);
        if (before is null)
            Assert.Null(after);
        else
            Assert.Equal(EntitlementRegistry.TeamBillFingerprint(before), EntitlementRegistry.TeamBillFingerprint(after!));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("renew")]
    [InlineData("auto-renew-off")]
    [InlineData("cancel")]
    public void BillActions_ByAStranger_AreNotFound(string action)
    {
        var result = Act(action, Stranger);

        Assert.Equal(TeamBillChangeOutcome.NotFound, result.Outcome);
        Assert.Null(_bills.Find(_team));
    }

    [Fact]
    public void TheBill_TheOwnerSeesItAndMayChangeIt_AManagerSeesItReadOnly_ADeveloperAndCollaboratorDoNot()
    {
        _teams.StartTeamPlan(_team, Owner);

        var owner = Page(Owner).Bill!;
        Assert.True(owner.CanChange);
        Assert.True(owner.CanSetAutoRenew);
        Assert.True(owner.CanCancel);

        var manager = Page(Manager).Bill!;
        Assert.Equal(owner.StatusLine, manager.StatusLine);
        Assert.Equal(owner.AmountLine, manager.AmountLine);
        Assert.Equal(owner.History, manager.History);
        Assert.False(manager.CanChange);
        Assert.False(manager.CanStart || manager.CanRenew || manager.CanSetAutoRenew || manager.CanCancel);
        Assert.Null(manager.Checkout);
        Assert.Null(manager.CancelWarning);
        Assert.Equal(TeamRegistry.OwnerChangesTheBill, manager.Note);
        Assert.Null(owner.Note);
        Assert.Contains(TeamBillStore.Day(Bill().CurrentPeriodEndUtc), owner.CancelWarning);
        // The period end is worded here, the same day the status line names: a client formatting the date itself showed
        // the day before in a time zone behind UTC ("Renews on 7 Nov" over "Period ends 6 Nov").
        Assert.Equal(TeamBillStore.Day(Bill().CurrentPeriodEndUtc), owner.PeriodEnd);
        Assert.Contains(owner.PeriodEnd!, owner.StatusLine);
        Assert.Equal(owner.PeriodEnd, manager.PeriodEnd);

        // A Developer has the Team page and no Billing section on it; the bill on its own is refused.
        Assert.Null(Page(Developer).Bill);
        var developer = _teams.DescribeTeamBill(_team, Developer);
        Assert.Equal(TeamBillViewOutcome.Forbidden, developer.Outcome);
        Assert.Contains("see the team's bill", developer.Refusal);

        // A Collaborator has no Team page at all, and the bill on its own is refused.
        Assert.Equal(TeamPageOutcome.Forbidden, _teams.DescribeTeamPage(_team, Collaborator).Outcome);
        Assert.Equal(TeamBillViewOutcome.Forbidden, _teams.DescribeTeamBill(_team, Collaborator).Outcome);
        Assert.Equal(TeamBillViewOutcome.NotFound, _teams.DescribeTeamBill(_team, Stranger).Outcome);
    }

    // ---- The renewal pass --------------------------------------------------------------------------------------------

    [Fact]
    public void RenewalPass_AnAutoRenewingPlan_RollsToTheNextMonth_AtTheSeatCountNow_AndWritesAHistoryLine()
    {
        _teams.StartTeamPlan(_team, Owner);
        var first = Bill();
        Assert.True(_teams.AddMember(_team, Newcomer, TeamRole.Developer).IsDone);   // a fourth paid seat, mid-month
        _now = first.CurrentPeriodEndUtc.AddMinutes(2);

        Assert.Equal(new TeamBillRenewalSummary(1, 1, 0, 1, 0), _bills.RenewDue());

        var bill = Bill();
        Assert.Equal(EntitlementRegistry.StatusActive, bill.Status);
        Assert.Equal(first.CurrentPeriodEndUtc, bill.CurrentPeriodStartUtc);
        Assert.Equal(first.CurrentPeriodEndUtc.AddMonths(1), bill.CurrentPeriodEndUtc);
        Assert.Equal(4, bill.Seats);
        var line = History()[0];
        Assert.Equal(TeamBillStore.ReasonAutoRenewed, line.Reason);
        Assert.Equal(4, line.Seats);
        Assert.Equal(4 * 4900, line.AmountCents);
        Assert.Equal(0, line.ChargedCents);
        Assert.Equal(2, History().Count);

        // The next pass, before the new period ends, does nothing.
        Assert.Equal(new TeamBillRenewalSummary(0, 0, 0, 0, 0), _bills.RenewDue());
    }

    [Fact]
    public void RenewalPass_AGatewayDownForMonths_WritesOneLineForEachMonthMissed()
    {
        _teams.StartTeamPlan(_team, Owner);
        var start = Bill().CurrentPeriodStartUtc;
        _now = start.AddMonths(3).AddDays(1);

        Assert.Equal(new TeamBillRenewalSummary(1, 1, 0, 3, 0), _bills.RenewDue());

        Assert.Equal(start.AddMonths(3), Bill().CurrentPeriodStartUtc);
        Assert.Equal(start.AddMonths(4), Bill().CurrentPeriodEndUtc);
        Assert.Equal(new[] { start.AddMonths(3), start.AddMonths(2), start.AddMonths(1), start },
            History().Select(h => h.PeriodStartUtc));
    }

    [Fact]
    public void RenewalPass_ABillAnotherProcessChangedFirst_IsLeftAsThatProcessSavedIt()
    {
        // Two Gateway processes on one database (a deploy). Just before this pass saves its roll, the other process's
        // Owner switches auto-renew off. The bill's version has moved, so the database refuses this pass's stale write:
        // the bill is not rolled over the other writer's change, and no history line is written for it.
        _teams.StartTeamPlan(_team, Owner);
        var first = Bill();
        _now = first.CurrentPeriodEndUtc.AddMinutes(2);
        var otherProcess = new TeamBillStore(_db, () => _now);
        _bills.BeforeRenewalSaveForTests = _ => otherProcess.SetAutoRenew(_team, on: false);

        Assert.Equal(new TeamBillRenewalSummary(1, 0, 0, 0, 1), _bills.RenewDue());

        var bill = Bill();
        Assert.False(bill.AutoRenew);
        Assert.Equal(first.CurrentPeriodEndUtc, bill.CurrentPeriodEndUtc);
        Assert.Single(History());

        // The next pass ends it, as the other writer asked.
        _bills.BeforeRenewalSaveForTests = null;
        Assert.Equal(new TeamBillRenewalSummary(1, 0, 1, 0, 0), _bills.RenewDue());
        Assert.Equal(EntitlementRegistry.StatusCanceled, Bill().Status);
    }

    [Fact]
    public void RenewalPass_APeriodAnotherProcessAlreadyRecorded_IsNeverRecordedTwice()
    {
        // The other process has already written this period's history line. The database holds one line per team and
        // period start, so this pass's line is refused and the history shows the period once.
        _teams.StartTeamPlan(_team, Owner);
        var first = Bill();
        _now = first.CurrentPeriodEndUtc.AddMinutes(2);
        _bills.BeforeRenewalSaveForTests = teamId =>
        {
            using var ctx = _db.CreateUnscopedContext();
            ctx.TeamBillCharges.Add(new TeamBillChargeEntity
            {
                Id = Guid.NewGuid().ToString(), TeamId = teamId,
                PeriodStartUtc = first.CurrentPeriodEndUtc, PeriodEndUtc = first.CurrentPeriodEndUtc.AddMonths(1),
                Seats = 3, PricePerSeatCents = 4900, AmountCents = 3 * 4900, ChargedCents = 0,
                Reason = TeamBillStore.ReasonAutoRenewed, CreatedAtUtc = _now,
            });
            ctx.SaveChanges();
        };

        Assert.Equal(new TeamBillRenewalSummary(1, 0, 0, 0, 1), _bills.RenewDue());

        Assert.Single(History(), h => h.PeriodStartUtc == first.CurrentPeriodEndUtc);
        Assert.Equal(2, History().Count);
    }

    [Fact]
    public void RenewalPass_TheTimersEntryPoint_RunsThePass_AndNeverThrows()
    {
        _teams.StartTeamPlan(_team, Owner);
        _teams.CancelTeamPlan(_team, Owner);
        _now = Bill().CurrentPeriodEndUtc;
        var renewal = new TeamBillRenewal(_bills);

        Assert.Equal(1, renewal.RunOnce()!.Ended);

        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE team_bills");
        renewal.RunSafe();   // a failing pass is logged, never thrown into the timer
        Assert.Throws<ArgumentNullException>(() => new TeamBillRenewal(null!));
    }

    // ---- The readers -------------------------------------------------------------------------------------------------

    [Fact]
    public void Readers_FollowTheGatewaysOwnBill_InvitationsOpenWhileItRuns_AndCloseOnceItHasEnded()
    {
        Assert.Equal(TeamInvitationRefusals.BillNotStarted,
            _teams.CreateInvitation(_team, Owner, "before@acme.example", TeamRole.Developer).Refusal);

        _teams.StartTeamPlan(_team, Owner);
        Assert.Equal(EntitlementOutcome.Entitled, _entitlements.EvaluateTeam(_team, _now).Outcome);
        Assert.Equal(EntitlementRegistry.TierTeam, _entitlements.EvaluateTeam(_team, _now).Tier);

        // Cancelled, and an invitation sent on the plan's last day - still running, so it goes out.
        _teams.CancelTeamPlan(_team, Owner);
        var end = Bill().CurrentPeriodEndUtc;
        _now = end.AddDays(-1);
        var waiting = _teams.CreateInvitation(_team, Owner, "anna@acme.example", TeamRole.Developer);
        Assert.Equal(TeamInvitationOutcome.Done, waiting.Outcome);

        _now = end;
        _bills.RenewDue();

        Assert.Equal(EntitlementOutcome.NotEntitled, _entitlements.EvaluateTeam(_team, _now).Outcome);
        Assert.Equal(TeamInvitationRefusals.BillCancelled,
            _teams.CreateInvitation(_team, Owner, "after@acme.example", TeamRole.Developer).Refusal);
        Assert.Equal(TeamInvitationRefusals.BillStopped, _teams.AcceptInvitation(waiting.AcceptToken!, Newcomer).Refusal);
        Assert.Null(_teams.RoleOf(_team, Newcomer));
    }

    // ---- What the Billing section says --------------------------------------------------------------------------------

    [Fact]
    public void BillingSection_EveryStateSaysNoCharge_ShowsTheRealAmount_AndNeverSaysFree()
    {
        var views = new List<TeamBillView> { Page(Owner).Bill! };      // not started
        _teams.StartTeamPlan(_team, Owner);
        views.Add(Page(Owner).Bill!);                                   // active
        _teams.CancelTeamPlan(_team, Owner);
        views.Add(Page(Owner).Bill!);                                   // ending
        EndThePlan();
        views.Add(Page(Owner).Bill!);                                   // ended
        views.Add(Page(Manager).Bill!);                                 // read-only

        Assert.Equal(new[] { TeamBillStates.NotStarted, TeamBillStates.Active, TeamBillStates.Ending, TeamBillStates.Ended, TeamBillStates.Ended },
            views.Select(v => v.State));
        foreach (var view in views)
        {
            Assert.Equal("No charge", view.ChargeLine);
            Assert.Equal("US$49.00 x 3 paid seats = US$147.00 a month", view.AmountLine);
            foreach (var text in Words(view))
                Assert.DoesNotContain("free", text, StringComparison.OrdinalIgnoreCase);
        }

        var started = views[0];
        Assert.True(started.CanStart);
        Assert.Equal("Start the team plan", started.Checkout!.Title);
        Assert.Equal("Total: US$147.00 a month", started.Checkout.TotalLine);
        Assert.StartsWith("No charge", started.Checkout.ChargeLine);
        Assert.Equal("Charged: US$0.00", views[3].History.Single().Charged);
        Assert.Equal("US$49.00 x 3 paid seats = US$147.00", views[3].History.Single().Amount);
        Assert.Equal("Renew the team plan", views[3].Checkout!.Title);
    }

    [Theory]
    [InlineData(0, "US$0.00")]
    [InlineData(4900, "US$49.00")]
    [InlineData(147000, "US$1,470.00")]
    public void Money_IsUsDollarsWithCents(int cents, string expected)
    {
        Assert.Equal(expected, TeamRegistry.Money(cents));
    }

    // ---- The routes, as a client receives them ------------------------------------------------------------------------

    [Fact]
    public async Task BillRoutes_OwnerStarts200_ManagerStarts403_StrangerReads404_DeveloperReads403()
    {
        var (managerStatus, managerBody) = await RenderAsync(TeamEndpoints.AnswerBillChange(_teams.StartTeamPlan(_team, Manager), "start"));
        Assert.Equal(403, managerStatus);
        Assert.Contains("Manager", managerBody.GetProperty("error").GetString());

        var (ownerStatus, _) = await RenderAsync(TeamEndpoints.AnswerBillChange(_teams.StartTeamPlan(_team, Owner), "start"));
        Assert.Equal(200, ownerStatus);

        var (againStatus, againBody) = await RenderAsync(TeamEndpoints.AnswerBillChange(_teams.StartTeamPlan(_team, Owner), "start"));
        Assert.Equal(409, againStatus);
        Assert.Equal(TeamBillRefusals.AlreadyStarted, againBody.GetProperty("error").GetString());

        var (readStatus, bill) = await RenderAsync(TeamEndpoints.ReadBill(_teams, Manager, _team));
        Assert.Equal(200, readStatus);
        Assert.Equal("active", bill.GetProperty("state").GetString());
        Assert.Equal("No charge", bill.GetProperty("chargeLine").GetString());
        Assert.False(bill.GetProperty("canChange").GetBoolean());
        Assert.Equal("Charged: US$0.00", bill.GetProperty("history")[0].GetProperty("charged").GetString());

        Assert.Equal(403, (await RenderAsync(TeamEndpoints.ReadBill(_teams, Developer, _team))).Status);
        Assert.Equal(404, (await RenderAsync(TeamEndpoints.ReadBill(_teams, Stranger, _team))).Status);

        var (pageStatus, page) = await RenderAsync(TeamEndpoints.TeamPage(_teams, Owner, _team));
        Assert.Equal(200, pageStatus);
        Assert.True(page.GetProperty("bill").GetProperty("canChange").GetBoolean());
        var (_, developerPage) = await RenderAsync(TeamEndpoints.TeamPage(_teams, Developer, _team));
        Assert.Equal(JsonValueKind.Null, developerPage.GetProperty("bill").ValueKind);
    }

    [Fact]
    public void EndpointRules_ReadingTheBillIsSeeing_EveryChangeIsTheOwnersBillingRow()
    {
        Assert.Equal(TeamAction.SeeTeamBill, TeamEndpointRules.Find("GET", TeamEndpoints.BillPath)!.Action);
        foreach (var (method, path) in new[] { ("POST", "/start"), ("POST", "/renew"), ("POST", "/cancel"), ("PUT", "/auto-renew") })
            Assert.Equal(TeamAction.BillingRenameOrDeleteTeam, TeamEndpointRules.Find(method, TeamEndpoints.BillPath + path)!.Action);
        Assert.Equal(TeamGrant.Yes, TeamPermissions.Grant(TeamRole.Manager, TeamAction.SeeTeamBill));
        Assert.Equal(TeamGrant.No, TeamPermissions.Grant(TeamRole.Manager, TeamAction.BillingRenameOrDeleteTeam));
        Assert.Equal(TeamGrant.No, TeamPermissions.Grant(TeamRole.Developer, TeamAction.SeeTeamBill));
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private TeamBillChangeResult Act(string action, string who) => action switch
    {
        "start" => _teams.StartTeamPlan(_team, who),
        "renew" => _teams.RenewTeamPlan(_team, who),
        "auto-renew-off" => _teams.SetTeamPlanAutoRenew(_team, who, on: false),
        "auto-renew-on" => _teams.SetTeamPlanAutoRenew(_team, who, on: true),
        "cancel" => _teams.CancelTeamPlan(_team, who),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>Cancel the running plan and let the renewal pass end it at its period end.</summary>
    private void EndThePlan()
    {
        if (Bill().AutoRenew)
            _teams.CancelTeamPlan(_team, Owner);
        _now = Bill().CurrentPeriodEndUtc;
        _bills.RenewDue();
        Assert.Equal(EntitlementRegistry.StatusCanceled, Bill().Status);
    }

    private TeamBillEntity Bill() => _bills.Find(_team) ?? throw new InvalidOperationException("The team has no bill.");

    private IReadOnlyList<TeamBillChargeEntity> History() => _bills.History(_team);

    private TeamPage Page(string caller)
    {
        var result = _teams.DescribeTeamPage(_team, caller);
        Assert.Equal(TeamPageOutcome.Found, result.Outcome);
        return result.Page!;
    }

    private static IEnumerable<string> Words(TeamBillView v)
    {
        yield return v.StatusLabel;
        yield return v.StatusLine;
        yield return v.SeatsLine;
        yield return v.PriceLine;
        yield return v.AmountLine;
        yield return v.ChargeLine;
        if (v.Checkout is { } c)
        {
            yield return c.Title;
            yield return c.SeatsLine;
            yield return c.PriceLine;
            yield return c.TotalLine;
            yield return c.ChargeLine;
            yield return c.PeriodLine;
            yield return c.ConfirmLabel;
        }
        if (v.Note is { } note)
            yield return note;
        if (v.CancelWarning is { } warning)
            yield return warning;
        foreach (var h in v.History)
        {
            yield return h.Period;
            yield return h.Amount;
            yield return h.Charged;
            yield return h.Reason;
        }
    }

    private static async Task<(int Status, JsonElement Body)> RenderAsync(IResult result)
    {
        var provider = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = provider };
        using var ms = new MemoryStream();
        ctx.Response.Body = ms;
        await result.ExecuteAsync(ctx);
        ms.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ms);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }
}
