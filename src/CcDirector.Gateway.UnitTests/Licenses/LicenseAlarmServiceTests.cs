using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE LICENSE ALARM (License Switching phase 6) over the real EF store on a throwaway SQLite file, with the owner email
/// and the push replaced by recorders. What these hold down: a new alarm opens, writes a log row and sends one email and
/// one push; raising it again sends nothing; an alarm no longer raised closes by itself and logs it, and raising it later
/// is a new alarm with a new email; a failed email is tried again later and sent once; one kind never closes another;
/// two tenants never see each other's alarms; a badly formed alarm is refused.
/// </summary>
public sealed class LicenseAlarmServiceTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");
    private static readonly LicenseAlarmKind NotReady = new("not-ready-five-hour", "Not ready to switch");
    private static readonly LicenseAlarmKind Stopped = new("stopped-reporting", "Stopped reporting");
    private static readonly Guid MachineA = Guid.Parse("0f8e6a1c-2b3d-4e5f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid MachineB = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly List<(TenantId Tenant, LicenseOwnerEmail Email)> _sent = new();
    private readonly List<(TenantId Tenant, LicenseAlarmOpened Alarm)> _pushed = new();
    private Func<LicenseOwnerEmailResult> _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null);
    private bool _pushThrows;
    private DateTime _now = T0;
    private readonly LicenseAlarmService _alarms;

    public LicenseAlarmServiceTests()
    {
        _db = _harness.Open();
        _alarms = new LicenseAlarmService(_db, Mail, Push, () => _now);
    }

    public void Dispose() => _harness.Dispose();

    [Theory]
    [InlineData("machine-silent")]
    [InlineData("no-room-at-80")]
    [InlineData("not-ready-at-80")]
    public async Task RetiredKind_StoredBeforeDeployment_ClosesBeforeReadingOrSending(string kind)
    {
        var identifiers = new Dictionary<TenantId, Guid>();
        foreach (var tenant in new[] { One, Two })
        {
            using var ctx = _db.CreateContext(tenant);
            Assert.NotNull(ctx.ActiveTenant);
            var row = new CcDirector.Gateway.Data.Entities.LicenseAlarmEntity
            {
                Kind = kind, MachineId = MachineA, MachineName = "machine-a", AccountEmail = "alice@example.com",
                Problem = "An old condition.", Action = "An old action.", OpenedAtUtc = T0,
                TenantId = ctx.ActiveTenant,
            };
            ctx.LicenseAlarms.Add(row);
            ctx.SaveChanges();
            identifiers[tenant] = row.Id;
        }

        Assert.Equal(0, await _alarms.SendDueEmailAsync(One, CancellationToken.None));
        Assert.Empty(_alarms.ListOpen(One));
        using (var ctx = _db.CreateContext(One))
            Assert.Equal(T0, ctx.LicenseAlarms.Single(a => a.Id == identifiers[One]).ClosedAtUtc);
        using (var ctx = _db.CreateContext(Two))
            Assert.Null(ctx.LicenseAlarms.Single(a => a.Id == identifiers[Two]).ClosedAtUtc);

        Assert.Empty(_alarms.ListOpen(Two));
        using (var ctx = _db.CreateContext(Two))
            Assert.Equal(T0, ctx.LicenseAlarms.Single(a => a.Id == identifiers[Two]).ClosedAtUtc);
        var raised = await Raise(One, new LicenseAlarmKind(kind, "Retired condition"), OnMachineA());
        Assert.Equal(new LicenseAlarmRaised(0, 0, 0), raised);
        Assert.Empty(_pushed);
        Assert.Empty(_sent);
    }

    private Task<LicenseOwnerEmailResult> Mail(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
    {
        _sent.Add((tenant, email));
        return Task.FromResult(_mailAnswers());
    }

    private Task Push(TenantId tenant, IReadOnlyList<LicenseAlarmOpened> opened, CancellationToken ct)
    {
        if (_pushThrows)
            throw new InvalidOperationException("the push service is down");
        foreach (var alarm in opened)
            _pushed.Add((tenant, alarm));
        return Task.CompletedTask;
    }

    private static LicenseAlarmCondition OnMachineA(string problem = "machine-a holds no spare that could take it off alice@example.com.")
        => new(MachineA, "machine-a", "alice@example.com", problem, "Sign machine-a in to a second account.");

    private static LicenseAlarmCondition OnMachineB()
        => new(MachineB, "machine-b", "alice@example.com", "machine-b holds no spare that could take it off alice@example.com.",
            "Sign machine-b in to a second account.");

    private Task<LicenseAlarmRaised> Raise(TenantId tenant, LicenseAlarmKind kind, params LicenseAlarmCondition[] trueNow)
        => _alarms.RaiseAsync(tenant, kind, trueNow, CancellationToken.None);

    private List<CcDirector.Gateway.Data.Entities.LicenseSwitchLogEntity> AlarmLog(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.LicenseSwitchLog.AsNoTracking()
            .Where(r => r.Kind == LicenseLogKinds.AlarmOpened || r.Kind == LicenseLogKinds.AlarmClosed)
            .ToList().OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Kind, StringComparer.Ordinal).ToList();
    }

    // Raising a new alarm: it is open, the Licenses log has one "opened" row, and the owner got one email and the
    // phones one push.
    [Fact]
    public async Task RaiseAsync_NewAlarm_OpensWritesLogRowSendsOneEmailAndOnePush()
    {
        var raised = await Raise(One, NotReady, OnMachineA());

        Assert.Equal(new LicenseAlarmRaised(1, 0, 0), raised);
        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal(("not-ready-five-hour", MachineA, "machine-a", "alice@example.com"), (open.Kind, open.MachineId, open.MachineName, open.AccountEmail));
        Assert.Equal(T0, open.OpenedAtUtc);
        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, open.EmailState);
        var log = Assert.Single(AlarmLog(One));
        Assert.Equal(LicenseLogKinds.AlarmOpened, log.Kind);
        Assert.Equal("Alarm opened. machine-a holds no spare that could take it off alice@example.com. Sign machine-a in to a second account.", log.Message);
        Assert.Single(_sent);
        var pushed = Assert.Single(_pushed);
        Assert.Equal((One, open.Id, "machine-a"), (pushed.Tenant, pushed.Alarm.AlarmId, pushed.Alarm.MachineName));
    }

    // The same alarm raised again on the next tick is the same alarm: nothing more is sent, and only its words move.
    [Fact]
    public async Task RaiseAsync_SameAlarmAgain_SendsNothingMore_AndUpdatesItsWords()
    {
        await Raise(One, NotReady, OnMachineA());
        _now = T0.AddMinutes(1);

        var raised = await Raise(One, NotReady, OnMachineA("machine-a still holds no spare that could take it off alice@example.com."));

        Assert.Equal(new LicenseAlarmRaised(0, 1, 0), raised);
        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal(T0, open.OpenedAtUtc);
        Assert.Equal("machine-a still holds no spare that could take it off alice@example.com.", open.Problem);
        Assert.Single(_sent);
        Assert.Single(_pushed);
        Assert.Single(AlarmLog(One));
    }

    // An alarm no longer raised closes by itself and writes a "closed" row; raised after the hold-down, it is a NEW alarm
    // with its own email and push.
    [Fact]
    public async Task RaiseAsync_AlarmNoLongerRaised_ClosesAndLogs_ThenRaisedLaterOpensANewAlarm()
    {
        await Raise(One, NotReady, OnMachineA());
        var first = Assert.Single(_alarms.ListOpen(One)).Id;
        _now = T0.AddMinutes(5);

        var cleared = await Raise(One, NotReady);

        Assert.Equal(new LicenseAlarmRaised(0, 0, 1), cleared);
        Assert.Empty(_alarms.ListOpen(One));
        var closedRow = AlarmLog(One).Last();
        Assert.Equal(LicenseLogKinds.AlarmClosed, closedRow.Kind);
        Assert.Equal("Alarm closed, because this is no longer true: machine-a holds no spare that could take it off alice@example.com.", closedRow.Message);
        Assert.Equal(_now, closedRow.CreatedAtUtc);

        _now = T0.AddMinutes(5) + LicenseAlarmService.HoldDown;
        await Raise(One, NotReady, OnMachineA());

        var second = Assert.Single(_alarms.ListOpen(One));
        Assert.NotEqual(first, second.Id);
        Assert.Equal(_now, second.OpenedAtUtc);
        Assert.Equal(2, _sent.Count);
        Assert.Equal(2, _pushed.Count);
        Assert.Equal(new[] { LicenseLogKinds.AlarmOpened, LicenseLogKinds.AlarmClosed, LicenseLogKinds.AlarmOpened },
            AlarmLog(One).Select(r => r.Kind));
    }

    // A failed email is not marked sent; it is tried again on a later check once its wait has passed (one minute), sent
    // once, and never again.
    [Fact]
    public async Task SendDueEmailAsync_FailedEmail_IsRetriedOnALaterTick_AndSentOnce()
    {
        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, "the DevThrottle account service could not be reached");
        await Raise(One, NotReady, OnMachineA());
        Assert.Single(_sent);
        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailFailed, Assert.Single(_alarms.ListOpen(One)).EmailState);

        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null);
        _now = T0.AddSeconds(30);
        Assert.Equal(0, await _alarms.SendDueEmailAsync(One, CancellationToken.None)); // inside the one-minute wait
        Assert.Single(_sent);

        _now = T0.AddMinutes(1);
        Assert.Equal(1, await _alarms.SendDueEmailAsync(One, CancellationToken.None));
        Assert.Equal(2, _sent.Count);
        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal((LicenseGatewayReasonCodes.OwnerEmailed, (DateTime?)T0.AddMinutes(1)), (open.EmailState, open.EmailSentAtUtc));

        _now = T0.AddHours(3);
        Assert.Equal(0, await _alarms.SendDueEmailAsync(One, CancellationToken.None));
        await Raise(One, NotReady, OnMachineA());
        Assert.Equal(2, _sent.Count);
        Assert.Single(_pushed);
    }

    // A sender that throws has sent nothing: it is a failed send, tried again later, and the raise itself still lands.
    [Fact]
    public async Task RaiseAsync_SenderThrows_AlarmStillOpens_AndTheEmailIsOwed()
    {
        _mailAnswers = () => throw new HttpRequestException("no route to the mail service");

        await Raise(One, NotReady, OnMachineA());

        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailFailed, Assert.Single(_alarms.ListOpen(One)).EmailState);
    }

    // A push that fails must not fail the alarm it announces: it is open, logged and emailed.
    [Fact]
    public async Task RaiseAsync_PushFails_AlarmStillOpensAndIsEmailed()
    {
        _pushThrows = true;

        await Raise(One, NotReady, OnMachineA());

        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, Assert.Single(_alarms.ListOpen(One)).EmailState);
        Assert.Single(_sent);
    }

    // Two alarms opening in one raise: one email that carries both, and one push for each.
    [Fact]
    public async Task RaiseAsync_TwoNewAlarmsAtOnce_OneEmailCarriesBoth_AndEachIsPushed()
    {
        await Raise(One, NotReady, OnMachineA(), OnMachineB());

        var email = Assert.Single(_sent).Email;
        Assert.Equal("DevThrottle license alarm: 2 problems with your licenses need you", email.Subject);
        Assert.Contains("machine-a holds no spare", email.BodyText);
        Assert.Contains("machine-b holds no spare", email.BodyText);
        Assert.Equal(new[] { "machine-a", "machine-b" }, _pushed.Select(p => p.Alarm.MachineName).OrderBy(n => n, StringComparer.Ordinal));
    }

    // A check speaks only for its own kind: clearing one kind never closes another kind's alarm on the same machine.
    [Fact]
    public async Task RaiseAsync_OneKindCleared_NeverClosesAnotherKind()
    {
        await Raise(One, NotReady, OnMachineA());
        await Raise(One, Stopped, new LicenseAlarmCondition(MachineA, "machine-a", null,
            "machine-a has not reported for 20 minutes.", "Check that the Director on machine-a is running."));

        await Raise(One, NotReady);

        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal("stopped-reporting", open.Kind);
        Assert.Equal("", open.AccountEmail);
    }

    // Two tenants never see each other's alarms, and one tenant's clear never closes the other's.
    [Fact]
    public async Task RaiseAsync_TwoTenants_NeverSeeEachOthersAlarms()
    {
        await Raise(One, NotReady, OnMachineA());
        await Raise(Two, NotReady, OnMachineB());

        Assert.Equal("machine-a", Assert.Single(_alarms.ListOpen(One)).MachineName);
        Assert.Equal("machine-b", Assert.Single(_alarms.ListOpen(Two)).MachineName);

        await Raise(Two, NotReady);

        Assert.Single(_alarms.ListOpen(One));
        Assert.Empty(_alarms.ListOpen(Two));
        Assert.Equal(new[] { One, Two }, _sent.Select(s => s.Tenant));
        Assert.Single(AlarmLog(One));
    }

    // The identity is the machine and the account: an email address in another case is the same alarm.
    [Fact]
    public async Task RaiseAsync_AccountInAnotherCase_IsTheSameAlarm()
    {
        await Raise(One, NotReady, OnMachineA());

        var raised = await Raise(One, NotReady, OnMachineA() with { AccountEmail = "Alice@Example.com " });

        Assert.Equal(new LicenseAlarmRaised(0, 1, 0), raised);
    }

    // What a raiser passes is checked, and a bad one changes nothing.
    [Theory]
    [InlineData("no full stop", "Do this.")]
    [InlineData("A problem.", "")]
    [InlineData("Two\nlines.", "Do this.")]
    public async Task RaiseAsync_SentenceNotWellFormed_IsRefused(string problem, string action)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Raise(One, NotReady, new LicenseAlarmCondition(MachineA, "machine-a", null, problem, action)));
        Assert.Empty(_alarms.ListOpen(One));
    }

    [Fact]
    public async Task RaiseAsync_TwoConditionsWithOneIdentity_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Raise(One, NotReady, OnMachineA(), OnMachineA()));
        Assert.Empty(_alarms.ListOpen(One));
    }

    [Fact]
    public async Task RaiseAsync_MachineIdWithoutName_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Raise(One, NotReady, new LicenseAlarmCondition(MachineA, null, null, "A problem.", "Do this.")));
    }

    [Theory]
    [InlineData("Not-Ready")]
    [InlineData("not ready")]
    [InlineData("")]
    public void LicenseAlarmKind_KeyNotWellFormed_IsRefused(string key)
        => Assert.Throws<ArgumentException>(() => new LicenseAlarmKind(key, "Not ready to switch"));

    // A full stop hidden behind a trailing space is still a full stop (review finding 3).
    [Fact]
    public void LicenseAlarmKind_TitleEndingInAFullStopAndASpace_IsRefused()
        => Assert.Throws<ArgumentException>(() => new LicenseAlarmKind("not-ready-at-80", "Not ready. "));

    // The minute check retries a failed email for every tenant, each in its own scope, through TickEveryTenantAsync -
    // the method the Gateway's once-a-minute license check calls (review finding 4).
    [Fact]
    public async Task TickEveryTenantAsync_RetriesEachTenantsFailedEmail_InThatTenantsScope()
    {
        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, "the DevThrottle account service could not be reached");
        await Raise(One, NotReady, OnMachineA());
        await Raise(Two, NotReady, OnMachineB());
        Assert.Equal(new[] { One, Two }, _sent.Select(s => s.Tenant));

        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null);
        _now = T0.AddMinutes(1);
        await _alarms.TickEveryTenantAsync(new ListedTenants(One, null, Two), CancellationToken.None);

        // One more email for each tenant (the pass with no tenant in scope checks nothing), each now marked sent.
        Assert.Equal(new[] { One, Two, One, Two }, _sent.Select(s => s.Tenant));
        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, Assert.Single(_alarms.ListOpen(One)).EmailState);
        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, Assert.Single(_alarms.ListOpen(Two)).EmailState);

        _now = T0.AddHours(2);
        await _alarms.TickEveryTenantAsync(new ListedTenants(One, Two), CancellationToken.None);
        Assert.Equal(4, _sent.Count);
    }

    /// <summary>A tenant pass over a fixed list: each pass runs with that tenant as the one in scope.</summary>
    private sealed class ListedTenants(params TenantId?[] tenants) : CcDirector.Gateway.Tenancy.ITenantPass
    {
        public TenantId? Current { get; private set; }

        public void ForEachTenant(Action pass) => throw new NotSupportedException();

        public async Task ForEachTenantAsync(Func<Task> pass, CancellationToken ct = default)
        {
            foreach (var tenant in tenants)
            {
                Current = tenant;
                await pass();
            }
            Current = null;
        }
    }

    // ----- an alarm that comes and goes tells the owner once (the hold-down) -----

    private async Task OpenThenClose(TimeSpan openFor, LicenseAlarmCondition? condition = null)
    {
        await Raise(One, NotReady, condition ?? OnMachineA());
        _now += openFor;
        await Raise(One, NotReady);
    }

    // The machine that drops off for a few minutes and comes back: the second opening is a real alarm - its row, its
    // banner line, its "opened" log row - but no email and no push go out, because the owner was told minutes ago.
    [Fact]
    public async Task RaiseAsync_ReopenedWithinTheHoldDown_ShowsButSendsNoEmailAndNoPush()
    {
        await OpenThenClose(TimeSpan.FromMinutes(15));
        Assert.Single(_sent);
        Assert.Single(_pushed);
        _now += TimeSpan.FromMinutes(20);

        var raised = await Raise(One, NotReady, OnMachineA());

        Assert.Equal(new LicenseAlarmRaised(1, 0, 0, 1), raised);
        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal(_now, open.OpenedAtUtc);
        Assert.Equal(LicenseGatewayReasonCodes.HeldCameBack, open.EmailState);
        Assert.Single(_sent);
        Assert.Single(_pushed);
        var reopened = AlarmLog(One).Last();
        Assert.Equal(LicenseLogKinds.AlarmOpened, reopened.Kind);
        Assert.Equal("Alarm opened again within 6 hours of closing, so no email or push was sent. machine-a holds no spare that "
                     + "could take it off alice@example.com. Sign machine-a in to a second account.", reopened.Message);
        // The minute check owes it nothing either.
        _now += TimeSpan.FromMinutes(5);
        Assert.Equal(0, await _alarms.SendDueEmailAsync(One, CancellationToken.None));
        Assert.Single(_sent);
    }

    // Five openings in three hours, as on 18 October 2026: one email, one push, and every opening is still shown and
    // logged. The hold-down is measured from the LAST close, so the run is held for as long as it keeps coming back.
    [Fact]
    public async Task RaiseAsync_FiveOpeningsInThreeHours_TellTheOwnerOnce()
    {
        for (var i = 0; i < 5; i++)
        {
            await OpenThenClose(TimeSpan.FromMinutes(15));
            _now += TimeSpan.FromMinutes(20);
        }

        Assert.Single(_sent);
        Assert.Single(_pushed);
        Assert.Equal(10, AlarmLog(One).Count);
    }

    // After the hold-down a reopening is news again: email and push.
    [Fact]
    public async Task RaiseAsync_ReopenedAfterTheHoldDown_SendsEmailAndPushAgain()
    {
        await OpenThenClose(TimeSpan.FromMinutes(15));
        _now += LicenseAlarmService.HoldDown + TimeSpan.FromSeconds(1);

        await Raise(One, NotReady, OnMachineA());

        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, Assert.Single(_alarms.ListOpen(One)).EmailState);
        Assert.Equal(2, _sent.Count);
        Assert.Equal(2, _pushed.Count);
    }

    // The hold-down is per identity: machine-b's first alarm is told even while machine-a's return is held, and each
    // machine is told exactly once.
    [Fact]
    public async Task RaiseAsync_TwoMachines_EachToldOnce()
    {
        await OpenThenClose(TimeSpan.FromMinutes(15));
        _now += TimeSpan.FromMinutes(5);

        var raised = await Raise(One, NotReady, OnMachineA(), OnMachineB());

        Assert.Equal(new LicenseAlarmRaised(2, 0, 0, 1), raised);
        Assert.Equal(2, _sent.Count);
        Assert.Contains("machine-b", _sent[1].Email.Subject);
        Assert.Equal(new[] { "machine-a", "machine-b" }, _pushed.Select(p => p.Alarm.MachineName));
        var states = _alarms.ListOpen(One).ToDictionary(a => a.MachineName, a => a.EmailState);
        Assert.Equal(LicenseGatewayReasonCodes.HeldCameBack, states["machine-a"]);
        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, states["machine-b"]);
    }

    // The held decision is read from the stored rows: a NEW service on the same database - a Gateway that restarted
    // between the close and the reopen - still holds it.
    [Fact]
    public async Task RaiseAsync_GatewayRestartsBetweenCloseAndReopen_StillHeld()
    {
        await OpenThenClose(TimeSpan.FromMinutes(15));
        _now += TimeSpan.FromMinutes(20);
        var restarted = new LicenseAlarmService(_db, Mail, Push, () => _now);

        var raised = await restarted.RaiseAsync(One, NotReady, new[] { OnMachineA() }, CancellationToken.None);

        Assert.Equal(1, raised.Held);
        Assert.Single(_sent);
        Assert.Single(_pushed);
    }

    // The owner was never told of an alarm that closed with its email still owed, so its return is told, not held.
    [Fact]
    public async Task RaiseAsync_LastOneClosedWithItsEmailOwed_ReopeningIsTold()
    {
        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, "the DevThrottle account service could not be reached");
        await OpenThenClose(TimeSpan.FromMinutes(2));
        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null);
        _now += TimeSpan.FromMinutes(5);

        await Raise(One, NotReady, OnMachineA());

        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, Assert.Single(_alarms.ListOpen(One)).EmailState);
        Assert.Equal(2, _sent.Count);
    }

    // The banner's count: 1 for an alarm that told the owner, then one more for each return in the run - read from the
    // stored rows, so a restarted service counts the same. A return after the hold-down starts again at 1.
    [Fact]
    public async Task TimesInARow_CountsEachReturnInTheRun_AndStartsAgainAfterTheHoldDown()
    {
        await Raise(One, NotReady, OnMachineA());
        Assert.Equal(1, _alarms.TimesInARow(One, _alarms.ListOpen(One)).Values.Single());
        _now += TimeSpan.FromMinutes(15);
        await Raise(One, NotReady);
        _now += TimeSpan.FromMinutes(20);
        await OpenThenClose(TimeSpan.FromMinutes(15));
        _now += TimeSpan.FromMinutes(20);
        await Raise(One, NotReady, OnMachineA());

        var restarted = new LicenseAlarmService(_db, Mail, Push, () => _now);
        var open = restarted.ListOpen(One);
        Assert.Equal(3, restarted.TimesInARow(One, open)[Assert.Single(open).Id]);

        _now += TimeSpan.FromMinutes(15);
        await Raise(One, NotReady);
        _now += LicenseAlarmService.HoldDown + TimeSpan.FromMinutes(1);
        await Raise(One, NotReady, OnMachineA());
        open = _alarms.ListOpen(One);
        Assert.Equal(1, _alarms.TimesInARow(One, open)[Assert.Single(open).Id]);
        Assert.Equal(2, _sent.Count);
    }

    // Every kind is held, not only one: the hold-down is in the one mechanism.
    [Fact]
    public async Task RaiseAsync_AnotherKindComingAndGoing_IsHeldToo()
    {
        var silent = new LicenseAlarmCondition(MachineA, "machine-a", null, "machine-a has not reported for 20 minutes.",
            "Check that the Director on machine-a is running.");
        await Raise(One, Stopped, silent);
        _now += TimeSpan.FromMinutes(15);
        await Raise(One, Stopped);
        _now += TimeSpan.FromMinutes(20);

        Assert.Equal(1, (await Raise(One, Stopped, silent)).Held);
        Assert.Single(_sent);
        Assert.Single(_pushed);
    }

    // The title is what a locked screen shows: never an email address.
    [Fact]
    public void LicenseAlarmKind_TitleWithAnEmailAddress_IsRefused()
        => Assert.Throws<ArgumentException>(() => new LicenseAlarmKind("not-ready-at-80", "alice@example.com is not ready"));
}
