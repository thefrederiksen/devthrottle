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
    // machine-a has left account A: it reports account B as its active account and holds nothing else.
    private bool _machineAOnB;

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

    private LicenseAlarmService NewAlarmService() => new(_db, Mail, Push, () => _now);

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
            _machineAOnB ? Holding(B, true, 5, _bWeekly, readAt: _now) : Holding(A, true, _aFiveHour, _aWeekly, readAt: _now),
        };
        if (_machineAHoldsB && !_machineAOnB)
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
            (tenant, account) => _approverWhyNot(tenant, account), _directors.IsConnected, _alarms, () => _now).TickAsync(One, CancellationToken.None);
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

    // The 26 October case: one alarm, for machine-a only, saying what is wrong and the one thing to do - which is the
    // readiness check's own sentence for why the Gateway cannot sign it in.
    [Fact]
    public async Task Tick_AMachineOnATiredAccountReadyForNoAccountWithRoom_OpensOneAlarmForIt()
    {
        BothReport();

        var tick = await Tick();

        var alarm = Assert.Single(Stranded());
        Assert.Equal((1, 0, 0), (tick.StrandedAlarms!.Opened, tick.StrandedAlarms.StillOpen, tick.StrandedAlarms.Closed));
        Assert.Equal((MachineA, A), (alarm.MachineName, alarm.AccountEmail));
        Assert.Equal("machine-a is stuck on account-a@example.com, which is at 100% of its weekly limit: it cannot switch to "
                     + "account-b@example.com, the account with room it would go to, because it holds no sign-in for account-b@example.com.",
            alarm.Problem);
        Assert.Equal(NoApprover, alarm.Action);
    }

    // One email, one push, one banner line - and the email names the machine, both accounts and the one thing to do.
    [Fact]
    public async Task Tick_TheAlarmOpens_SendsOneEmailOnePushAndOneBannerLine()
    {
        var alarm = await OpenTheAlarm();

        var email = Assert.Single(_emails);
        Assert.Equal("DevThrottle license alarm: a problem on machine-a needs you", email.Subject);
        Assert.Contains(alarm.Problem, email.BodyText);
        Assert.Contains(alarm.Action, email.BodyText);
        var pushed = Assert.Single(_pushed);
        Assert.Equal((MachineA, LicenseStrandedAlarm.Kind.Key), (pushed.MachineName, pushed.Kind.Key));
        Assert.Equal("machine-a: Stuck on a full account. Open Licenses to see what to do.",
            LicenseAlarmFold.PushBody(pushed.MachineName, pushed.Kind));
        var line = Assert.Single(LicenseAlarmFold.Banner(_alarms.ListOpen(One), _now).Alarms);
        Assert.Equal($"{alarm.Problem} {alarm.Action}", line.Text);
    }

    // The machine's Director is not connected, so no sign-in can be started there: no automatic fix, so it opens.
    [Fact]
    public async Task Tick_TheDirectorIsOffline_NoAutomaticFix_OpensTheAlarm_SayingToStartIt()
    {
        TheApproverCanApprove();
        BothReport();
        _directors.Disconnect(One, DirA);

        var tick = await Tick();

        Assert.Empty(tick.Started);
        Assert.Equal("Start the Director on machine-a.", Assert.Single(Stranded()).Action);
    }

    // The next check, and the one after, find it still true: it stays open and sends nothing more.
    [Fact]
    public async Task Tick_TheNextChecks_KeepItOpen_AndSendNothingMore()
    {
        var opened = await OpenTheAlarm();

        Later(1);
        var second = await Tick();
        Later(2);
        await Tick();

        Assert.Equal(opened.Id, Assert.Single(Stranded()).Id);
        Assert.Equal((0, 1, 0), (second.StrandedAlarms!.Opened, second.StrandedAlarms.StillOpen, second.StrandedAlarms.Closed));
        Assert.Single(_emails);
        Assert.Single(_pushed);
    }

    // A Gateway that restarts builds a new alarm service over the same database: the open row is found, nothing is sent.
    [Fact]
    public async Task Tick_AfterAGatewayRestart_TheOpenAlarmIsFound_AndNothingIsSentAgain()
    {
        var opened = await OpenTheAlarm();

        _alarms = NewAlarmService();
        Later(1);
        await Tick();
        await _alarms.SendDueEmailAsync(One, CancellationToken.None);

        Assert.Equal(opened.Id, Assert.Single(Stranded()).Id);
        Assert.Single(_emails);
        Assert.Single(_pushed);
    }

    // ----- it closes, by itself, on each of three facts -----

    // The machine moves off the account (switched by hand, or by the automatic switch): the alarm closes.
    [Fact]
    public async Task Tick_TheMachineMovesOffTheAccount_TheAlarmCloses()
    {
        await OpenTheAlarm();

        _machineAOnB = true;
        Later(3);
        var tick = await Tick();

        Assert.Equal(1, tick.StrandedAlarms!.Closed);
        Assert.Empty(Stranded());
    }

    // The account resets: a fresh reading under the threshold closes it, though the machine is still not ready.
    [Fact]
    public async Task Tick_TheAccountResets_TheAlarmCloses()
    {
        await OpenTheAlarm();

        _aWeekly = 1;
        Later(3);
        var tick = await Tick();

        Assert.Equal(1, tick.StrandedAlarms!.Closed);
        Assert.Empty(Stranded());
    }

    // The account resets while nothing reports: once the tired window's reset time has passed, it is over.
    [Fact]
    public async Task Tick_TheTiredWindowsResetTimePasses_OnAStaleReading_TheAlarmCloses()
    {
        await OpenTheAlarm();

        _now = T0.AddDays(4).AddMinutes(1); // the weekly window resets at T0 + 4 days; nothing has reported since
        var tick = await Tick();

        Assert.Equal(1, tick.StrandedAlarms!.Closed);
        Assert.Empty(Stranded());
    }

    // The machine becomes ready - it now holds a sign-in for B - and so the switch takes it: the alarm closes.
    [Fact]
    public async Task Tick_TheMachineBecomesReady_TheAlarmCloses()
    {
        await OpenTheAlarm();

        _machineAHoldsB = true;
        Later(3);
        var tick = await Tick();

        Assert.Equal(1, tick.StrandedAlarms!.Closed);
        Assert.Empty(Stranded());
        Assert.Single(_emails);
    }

    // Not known is not fixed: while nothing reports, the stale reading and the stale machine keep it open, quietly.
    [Fact]
    public async Task Tick_WhileTheReadingAndTheMachineAreStale_TheAlarmStaysOpen()
    {
        var opened = await OpenTheAlarm();

        _now = T0.AddMinutes(20);
        var tick = await Tick();

        Assert.Equal((0, 1, 0), (tick.StrandedAlarms!.Opened, tick.StrandedAlarms.StillOpen, tick.StrandedAlarms.Closed));
        Assert.Equal(opened.Id, Assert.Single(Stranded()).Id);
        Assert.Single(_emails);
    }

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

    // The Gateway's own sign-in failed: the fix did not work, so the alarm opens, and its action says what failed and when
    // it is tried again - never "sign it in by hand" as if nothing were happening.
    [Fact]
    public async Task Tick_TheGatewaysSignInFailed_OpensTheAlarm_SayingItFailed()
    {
        TheApproverCanApprove();
        TheDirectorFails();
        BothReport();
        await Tick();
        Assert.Empty(Stranded());

        Later(1);
        await Tick();

        var alarm = Assert.Single(Stranded());
        Assert.Equal("The last sign-in failed at 03:12 UTC (The approval in the browser did not complete); DevThrottle tries again at 03:14 UTC. "
                     + "If this stays, sign machine-a in to account-b@example.com from the Licenses page.", alarm.Action);
        Assert.Single(_emails);
        Assert.Single(_pushed);
    }

    // Once open, a later retry is not yet a fix: the alarm stays open while it runs, with no second email.
    [Fact]
    public async Task Tick_AnOpenAlarm_StaysOpenWhileTheGatewayTriesAgain()
    {
        TheApproverCanApprove();
        TheDirectorFails();
        BothReport();
        await Tick();
        Later(1);
        await Tick();
        var opened = Assert.Single(Stranded());

        _directorAnswers = false;
        _directors.Answer = _ => _neverAnswers.Task; // the retry is sent and does not answer: it runs
        Later(3);
        var retry = await Tick();

        Assert.Single(retry.Started);
        Assert.Equal(opened.Id, Assert.Single(Stranded()).Id);
        Assert.Single(_emails);
    }

    // ----- review finding 1: one machine, one fault, one alarm -----

    // A fresh picture at 97% of the five-hour window: the 80% warning and the stranded alarm would both be true for
    // machine-a. Exactly one alarm, one email, one push and one banner line.
    [Fact]
    public async Task Tick_At97OfTheFiveHourWindow_AStrandedMachine_GetsOneAlarm()
    {
        _aFiveHour = 97;
        _aWeekly = 50;
        BothReport();

        await Tick();

        var alarm = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal((LicenseStrandedAlarm.Kind.Key, MachineA), (alarm.Kind, alarm.MachineName));
        Assert.Single(_emails);
        Assert.Single(_pushed);
        Assert.Single(LicenseAlarmFold.Banner(_alarms.ListOpen(One), _now).Alarms);
    }

    // The progression the owner asked for (D1): at 80% of the five-hour window the 80% warning tells him once; at 97%
    // its line closes and the stranded alarm is the "again" - two emails and two pushes in all, in that order, and never
    // two banner lines at once.
    [Fact]
    public async Task Tick_From80To97OfTheFiveHourWindow_TheWarningHandsOverToTheStrandedAlarm()
    {
        _aFiveHour = 85;
        _aWeekly = 50;
        BothReport();
        await Tick();
        Assert.Single(Warning());
        Assert.Empty(Stranded());

        _aFiveHour = 97;
        Later(5);
        var tick = await Tick();

        Assert.Equal((1, 1), (tick.Alarms!.Closed, tick.StrandedAlarms!.Opened));
        Assert.Empty(Warning());
        Assert.Single(Stranded());
        Assert.Single(LicenseAlarmFold.Banner(_alarms.ListOpen(One), _now).Alarms);
        Assert.Equal(new[] { LicenseCannotSwitchAlarm.Kind.Key, LicenseStrandedAlarm.Kind.Key }, _pushed.Select(p => p.Kind.Key).ToArray());
        Assert.Equal(2, _emails.Count);
    }

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

    // machine-a's Director is connected and answers every command with a timeout, as the Mac Mini's did on 26 October
    // (issue #4490). The sign-in the Gateway starts is marked unknown at once; the minute settle asks again every minute
    // and settles nothing. Once it is older than the wait the sign-in service gives a sign-in, it is not running as far as
    // anyone can tell, so it is no repair: the stranded alarm opens, says the sign-in got no answer, and stays open - one
    // email - for days.
    [Fact]
    public async Task Tick_ADirectorThatNeverAnswers_TheSignInIsNoRepair_TheAlarmOpensSayingItGotNoAnswer_AndStays()
    {
        TheApproverCanApprove();
        TheDirectorTimesOut();
        BothReport();
        var first = await Tick();
        Assert.Single(first.Started);
        Assert.Empty(Stranded());

        foreach (var minutes in new[] { 16, 60, 180, 1500, 5000 })
        {
            Later(minutes);
            await _signIns.SettleUnansweredAsync(One, CancellationToken.None);
            await Tick();
            Assert.Equal(LicenseLogStates.Unknown, Assert.Single(_log.ListOpen(One)).State);
            var alarm = Assert.Single(Stranded());
            Assert.Equal("The sign-in to account-b@example.com that DevThrottle sent to machine-a at 03:12 UTC got no answer from "
                         + "the machine, so restart the Director on machine-a.", alarm.Action);
        }
        Assert.Single(_emails);
        Assert.Single(_pushed);
    }

    // The third review: a send that times out, or whose tunnel drops, marks the sign-in unknown at once, while the Director
    // may be running it well. So an unknown sign-in younger than the unanswered wait is still a repair: no alarm at 1
    // minute or at 14, and the alarm - saying it got no answer, which is then true - once it is older than 15.
    [Theory]
    [InlineData(DirectorCommandStatus.Timeout)]
    [InlineData(DirectorCommandStatus.TunnelDropped)]
    public async Task Tick_AnUnknownSignIn_IsARepairUntilTheUnansweredWait_ThenTheAlarmOpens(DirectorCommandStatus status)
    {
        TheApproverCanApprove();
        _directorAnswers = true;
        _directors.AnswerWith(_ => DirectorCommandResult.Fail(status, "The send got no answer."));
        BothReport();
        await Tick();

        foreach (var minutes in new[] { 1, 14 })
        {
            Later(minutes);
            await Tick();
            Assert.Equal(LicenseLogStates.Unknown, Assert.Single(_log.ListOpen(One)).State);
            Assert.Empty(Stranded());
        }
        Assert.Empty(_emails);

        Later(16);
        await Tick();

        Assert.StartsWith("The sign-in to account-b@example.com that DevThrottle sent to machine-a at 03:12 UTC got no answer",
            Assert.Single(Stranded()).Action);
        Assert.Single(_emails);
    }

    // A sign-in still pending, with no answer, for longer than the sign-in service waits before settling one is not running
    // either: the alarm holds back for that wait and opens after it.
    [Fact]
    public async Task Tick_ASignInPendingLongerThanTheUnansweredWait_IsNoRepair_TheAlarmOpens()
    {
        TheApproverCanApprove();
        BothReport();
        await Tick();

        Later(14);
        await Tick();
        Assert.Empty(Stranded());

        Later(16);
        await Tick();

        Assert.Equal(LicenseLogStates.Pending, Assert.Single(_log.ListOpen(One)).State);
        Assert.Single(Stranded());
        Assert.Single(_emails);
    }

    // The 80-to-97 shape with that deaf Director: at 85% nothing opens while the Gateway's sign-in is young; past the
    // unanswered wait the 80% warning opens and says the sign-in got no answer; at 97% that line closes as the stranded
    // alarm opens - the "again". Once the warning is open there is exactly one line on every check, never two, and two
    // pushes in that order.
    [Fact]
    public async Task Tick_From80To97_WithADirectorThatNeverAnswers_OneLineAlways_AndTheStrandedAlarmIsTheAgain()
    {
        TheApproverCanApprove();
        TheDirectorTimesOut();
        _aFiveHour = 85;
        _aWeekly = 50;
        BothReport();
        await Tick();
        Later(5);
        await Tick();
        Assert.Empty(_alarms.ListOpen(One));

        Later(16);
        await _signIns.SettleUnansweredAsync(One, CancellationToken.None);
        await Tick();
        Assert.StartsWith("The sign-in to account-b@example.com that DevThrottle sent to machine-a at 03:12 UTC got no answer",
            Assert.Single(Warning()).Action);

        _aFiveHour = 97;
        foreach (var minutes in new[] { 20, 30, 90 })
        {
            Later(minutes);
            await _signIns.SettleUnansweredAsync(One, CancellationToken.None);
            await Tick();
            var line = Assert.Single(LicenseAlarmFold.Banner(_alarms.ListOpen(One), _now).Alarms);
            Assert.Equal(LicenseStrandedAlarm.Kind.Key, line.Kind);
        }
        Assert.Equal(new[] { LicenseCannotSwitchAlarm.Kind.Key, LicenseStrandedAlarm.Kind.Key }, _pushed.Select(p => p.Kind.Key).ToArray());
        Assert.Equal(2, _emails.Count);
    }

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
