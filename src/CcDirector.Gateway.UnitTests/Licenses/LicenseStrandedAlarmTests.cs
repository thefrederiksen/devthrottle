using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// A STRANDED MACHINE IS LOUD ONCE, AND ONLY ABOUT WHAT THE GATEWAY CANNOT REPAIR (mission "Licenses - Loud When
/// Stranded", step 1) - the readiness check raising the stranded alarm over the real license store, the real sign-in
/// service, the real approver check and the real license alarm on a throwaway SQLite file. The owner email and the push
/// are recorders; the Directors answer as each test says; the clock is the test's own.
///
/// The picture is the 26 October 2026 one in small: machine-a and machine-b are on account A, which is at 100% of its
/// weekly limit. Account B has room. machine-b holds a sign-in for B; machine-a does not. No machine is set to approve
/// sign-ins for B, so the Gateway cannot sign machine-a in by itself - on 26 October the approver check was failing. What
/// these hold down: one email, one push and one banner line when it opens, naming the machine, the account it is stuck on,
/// the account it needs and the one thing to do; nothing more on the next check; each of the three closes; a Gateway that
/// restarts sends nothing again; no alarm while the Gateway is signing the machine in itself, and the alarm once that
/// fails (review finding 2); one alarm per machine when the five-hour window is high too, and the 80% warning
/// (step 2's "cannot-switch-at-80", which replaced the five-hour alarm) handing over to this one at 97% (review finding
/// 1); the no-room case unchanged; and the #4249 boundary.
/// </summary>
public sealed class LicenseStrandedAlarmTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");

    private const string MachineA = "machine-a";
    private const string MachineB = "machine-b";
    private const string DirA = "dir-machine-a";
    private const string DirB = "dir-machine-b";

    private const string NoApprover = "No machine is set to approve sign-ins for account-b@example.com. "
                                      + "Set an approver for account-b@example.com on the Licenses page.";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _store;
    private readonly LicenseReadModel _reads;
    private readonly LicenseSignInLog _log;
    private readonly FakeLicenseDirectors _directors = new();
    private readonly LicenseSignInService _signIns;
    private readonly LicenseApproverReadiness _approverReadiness;
    private readonly List<LicenseOwnerEmail> _emails = new();
    private readonly List<LicenseAlarmOpened> _pushed = new();
    private readonly TaskCompletionSource<DirectorCommandResult?> _neverAnswers = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LicenseAlarmService _alarms;
    private DateTime _now = T0;
    private long _sequence;
    // The real approver check: no approver is set for any account, so no automatic sign-in can start.
    private Func<TenantId, string, string?> _approverWhyNot;
    // True once a test's Director answers at once; then a check waits for the sign-ins it started to record their answer.
    private bool _directorAnswers;

    // How the machines report: the use of account A in each window, and of account B.
    private double _aFiveHour = 30;
    private double _aWeekly = 100;
    private double _bWeekly = 2;
    private bool _machineAHoldsB;

    public LicenseStrandedAlarmTests()
    {
        _db = _harness.Open();
        _store = new LicenseStore(_db);
        _reads = new LicenseReadModel(_store);
        _log = new LicenseSignInLog(_db);
        _signIns = new LicenseSignInService(_log, _reads, _directors.IsConnected, _directors.SendAsync, _directors.EnterScope,
            () => _now, CancellationToken.None);
        _approverReadiness = new LicenseApproverReadiness(new LicenseApproverStore(new TenantSettingsStore(_db)), _reads,
            _directors.IsConnected, () => _now);
        _approverWhyNot = _approverReadiness.WhyNotReady;
        _alarms = NewAlarmService();
        // By default a Director takes a sign-in command and never answers.
        _directors.Answer = _ => _neverAnswers.Task;
    }

    public void Dispose()
    {
        _neverAnswers.TrySetResult(null);
        _signIns.WhenIdleAsync().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    // ----- helpers -----

    private LicenseAlarmService NewAlarmService() => new(_db, Mail, Push, () => _now, NeverNight);

    private Task<LicenseOwnerEmailResult> Mail(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
    {
        _emails.Add(email);
        return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
    }

    private Task Push(TenantId tenant, IReadOnlyList<LicenseAlarmOpened> opened, CancellationToken ct)
    {
        _pushed.AddRange(opened);
        return Task.CompletedTask;
    }

    /// <summary>Both machines report now, as the fields above say, and their Directors are connected.</summary>
    private void BothReport()
    {
        var a = new List<LicenseHolding>
        {
            Holding(A, true, _aFiveHour, _aWeekly, readAt: _now),
        };
        if (_machineAHoldsB)
            a.Add(Holding(B, false, 5, _bWeekly, readAt: _now));
        _store.ApplyReport(One, DirA, ++_sequence, Report(MachineA, _now, a.ToArray()), _now, TimeSpan.Zero);
        _store.ApplyReport(One, DirB, ++_sequence,
            Report(MachineB, _now, Holding(A, true, _aFiveHour, _aWeekly, readAt: _now), Holding(B, false, 5, _bWeekly, readAt: _now)),
            _now, TimeSpan.Zero);
        _directors.Connect(One, DirA);
        _directors.Connect(One, DirB);
    }

    /// <summary>One readiness check, by a service built afresh over the same database and alarm service.</summary>
    private async Task<LicenseReadinessTick> Tick()
    {
        var tick = await new LicenseReadinessService(_store, new LicenseSnapshotSource(_store, _reads), _log, _signIns,
            (tenant, account) => _approverWhyNot(tenant, account), _directors.IsConnected, _alarms, new LicenseUsageHistory(), () => _now).TickAsync(One, CancellationToken.None);
        if (_directorAnswers)
            await _signIns.WhenIdleAsync();
        return tick;
    }

    private List<LicenseAlarmEntity> Stranded() => _alarms.ListOpen(One).Where(a => a.Kind == LicenseStrandedAlarm.Kind.Key).ToList();

    private List<LicenseAlarmEntity> Warning() => _alarms.ListOpen(One).Where(a => a.Kind == LicenseCannotSwitchAlarm.Kind.Key).ToList();

    private async Task<LicenseAlarmEntity> OpenTheAlarm()
    {
        BothReport();
        await Tick();
        return Assert.Single(Stranded());
    }

    private void Later(int minutes)
    {
        _now = T0.AddMinutes(minutes);
        BothReport();
    }

    // The Gateway CAN sign machine-a in by itself: the approver can approve, and the Directors are connected.
    private void TheApproverCanApprove() => _approverWhyNot = (_, _) => null;

    private void TheDirectorFails()
    {
        _directorAnswers = true;
        _directors.AnswerWith(sent => FakeLicenseDirectors.Ok(new LicenseSignInResult
        {
            SignInId = ((LicenseSignInRequest)sent.Payload).SignInId,
            Outcome = LicenseSignInOutcomes.Failed,
            ReasonCode = LicenseRenewalErrors.ApprovalFailed,
            Message = "The approval in the browser did not complete.",
            FinishedAtUtc = _now,
        }));
    }

    // A Director that is connected and never answers in time: every command comes back as a timeout.
    private void TheDirectorTimesOut()
    {
        _directorAnswers = true;
        _directors.AnswerWith(_ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "The Director did not answer within 60 seconds."));
    }

    private void TheDirectorAdds()
    {
        _directorAnswers = true;
        _directors.AnswerWith(sent => FakeLicenseDirectors.Ok(new LicenseSignInResult
        {
            SignInId = ((LicenseSignInRequest)sent.Payload).SignInId,
            Outcome = LicenseSignInOutcomes.Added,
            Message = "Signed in.",
            SignedInAtUtc = _now,
            SignInExpiresAtUtc = _now.AddDays(30),
            FinishedAtUtc = _now,
        }));
    }

    // ----- it opens, once -----

    // ----- it closes, by itself, on each of three facts -----

    // ----- review finding 2: loud only about what the Gateway cannot repair -----

    // The Gateway starts its own sign-in on this check: it is repairing the machine, so nothing opens and nobody is told.
    [Fact]
    public async Task Tick_TheGatewayStartsItsOwnSignIn_OpensNoAlarm()
    {
        TheApproverCanApprove();
        BothReport();

        var tick = await Tick();

        Assert.Single(tick.Started);
        Assert.Empty(Stranded());
        Assert.Empty(_alarms.ListOpen(One));
        Assert.Empty(_emails);
        Assert.Empty(_pushed);
    }

    // While that sign-in runs, the next checks stay quiet too.
    [Fact]
    public async Task Tick_WhileTheGatewaysSignInRuns_OpensNoAlarm()
    {
        TheApproverCanApprove();
        BothReport();
        await Tick();

        Later(1);
        var tick = await Tick();

        Assert.Equal(LicenseReadinessFixes.SigningIn, Assert.Single(tick.Judged, j => j.MachineName == MachineA).Fix);
        Assert.Empty(Stranded());
        Assert.Empty(_emails);
    }

    // The sign-in ended well and the machine's next report should show it: still the Gateway's repair, still quiet.
    [Fact]
    public async Task Tick_TheGatewaysSignInEndedWell_AndWaitsForTheReport_OpensNoAlarm()
    {
        TheApproverCanApprove();
        TheDirectorAdds();
        BothReport();
        await Tick();

        Later(1);
        var tick = await Tick();

        Assert.Equal(LicenseReadinessFixes.AwaitingReport, Assert.Single(tick.Judged, j => j.MachineName == MachineA).Fix);
        Assert.Empty(Stranded());
        Assert.Empty(_emails);
    }

    // ----- review finding 1: one machine, one fault, one alarm -----

    // At 97% while the Gateway is signing machine-a in itself: neither the stranded alarm nor the 80% warning opens. Step
    // 1's rule binds the warning too (step 2): no alarm while the Gateway's own sign-in runs. The repair is bounded, so a
    // machine it does not fix is loud once that wait is over (the deaf-Director tests below).
    [Fact]
    public async Task Tick_At97OfTheFiveHourWindow_WhileTheGatewayRepairs_NeitherAlarmOpens()
    {
        TheApproverCanApprove();
        _aFiveHour = 97;
        _aWeekly = 50;
        BothReport();

        var tick = await Tick();

        Assert.Single(tick.Started);
        Assert.Empty(_alarms.ListOpen(One));
        Assert.Empty(_emails);
        Assert.Empty(_pushed);
    }

    // ----- the second review: a Director that is connected and never answers -----

    // ----- what it is not -----

    // No account has room: that is the no-room case, status on the Licenses page as before, and no stranded alarm.
    [Fact]
    public async Task Tick_NoAccountHasRoom_RaisesNoStrandedAlarm_AndTheNoRoomStatusIsUnchanged()
    {
        _bWeekly = 99;
        BothReport();

        var tick = await Tick();

        Assert.Empty(Stranded());
        Assert.All(tick.Judged, j => Assert.Equal(LicenseReadinessStates.NoRoom, j.State));
        Assert.Equal((0, 0, 0), (tick.NoRoomAlarms!.Opened, tick.NoRoomAlarms.StillOpen, tick.NoRoomAlarms.Closed));
        Assert.Empty(_emails);
        Assert.Empty(_pushed);
    }

    // Issue #4249: an account at its limit with every machine on it ready raises nothing new - no alarm, no email, no push.
    [Theory]
    [InlineData(30, 100)]
    [InlineData(97, 50)]
    [InlineData(100, 100)]
    public async Task Tick_AtTheLimit_EveryMachineReady_RaisesNothing(double fiveHour, double weekly)
    {
        _aFiveHour = fiveHour;
        _aWeekly = weekly;
        _machineAHoldsB = true;
        BothReport();

        var tick = await Tick();

        Assert.Equal((0, 0, 0), (tick.StrandedAlarms!.Opened, tick.StrandedAlarms.StillOpen, tick.StrandedAlarms.Closed));
        Assert.Empty(_alarms.ListOpen(One));
        Assert.Empty(_emails);
        Assert.Empty(_pushed);
    }

    // Issue #4249: near the limit, under the threshold, a machine that is not ready raises no stranded alarm.
    [Fact]
    public async Task Tick_NearTheLimit_AMachineNotReady_RaisesNoStrandedAlarm()
    {
        _aWeekly = 96.9;
        BothReport();

        var tick = await Tick();

        Assert.Equal(0, tick.StrandedAlarms!.Opened);
        Assert.Empty(Stranded());
    }
}
