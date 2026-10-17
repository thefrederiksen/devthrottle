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
/// READINESS ON EVERY MINUTE, FIXED AT ONCE, LOUD AT 80% (License Switching phase 6, Track A) - over the real license
/// store, the real sign-in service and the real license alarm on a throwaway SQLite file. The Directors are fakes that
/// answer as each test says; the owner email and the push are recorders; the clock is the test's own.
///
/// The picture in every test: machine-a and machine-b are on account A. Both read account B, which has room, so B is the
/// account they would switch to next - except that machine-a holds no sign-in for it (the 16 October picture), while
/// machine-b does. What these hold down: every readiness sentence word for word; one sign-in per gap and none while it
/// runs; the retry waits, each one, read from the Licenses log so they survive a restart; no sign-in without an approver
/// that can approve; under 80% no alarm but the fixing runs; at 80% one alarm, kept, then closed by itself; and nothing
/// at all for a tenant whose machines never reported.
/// </summary>
public sealed class LicenseReadinessServiceTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");

    private const string MachineA = "machine-a";
    private const string MachineB = "machine-b";
    private const string DirA = "dir-machine-a";
    private const string DirB = "dir-machine-b";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _store;
    private readonly LicenseReadModel _reads;
    private readonly LicenseSignInLog _log;
    private readonly FakeLicenseDirectors _directors = new();
    private readonly LicenseSignInService _signIns;
    private readonly LicenseAlarmService _alarms;
    private readonly LicenseApproverReadiness _approverReadiness;
    private readonly List<(TenantId Tenant, LicenseOwnerEmail Email)> _emails = new();
    private readonly List<LicenseAlarmOpened> _pushed = new();
    private readonly TaskCompletionSource<DirectorCommandResult?> _neverAnswers = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Track C's one approver check, as the host passes it; a test that is not about the approver sets "can approve".
    private Func<TenantId, string, string?> _approverWhyNot = (_, _) => null;
    // True once a test's Director answers at once; then a check waits for the sign-ins it started to record their answer.
    private bool _directorAnswers;
    private DateTime _now = T0;
    private long _sequence;

    // How each machine reports: the use of account A in each window, and what machine-a holds for account B.
    private double _aFiveHour = 50;
    private double _aWeekly = 50;
    private double _bWeekly = 10;
    private bool _machineAHoldsB;
    private DateTime? _machineABExpires;
    private string? _machineABReadError;
    // machine-a has left account A: it reports account B as its active account and holds nothing else.
    private bool _machineAOnB;

    public LicenseReadinessServiceTests()
    {
        _db = _harness.Open();
        _store = new LicenseStore(_db);
        _reads = new LicenseReadModel(_store);
        _log = new LicenseSignInLog(_db);
        _signIns = new LicenseSignInService(_log, _reads, _directors.IsConnected, _directors.SendAsync, _directors.EnterScope,
            () => _now, CancellationToken.None);
        _alarms = new LicenseAlarmService(_db, Mail, Push, () => _now);
        _approverReadiness = new LicenseApproverReadiness(new LicenseApproverStore(new TenantSettingsStore(_db)), _reads, () => _now);
        // By default the Director takes the command and never answers: the sign-in stays open.
        _directors.Answer = _ => _neverAnswers.Task;
    }

    public void Dispose()
    {
        // A sign-in still waiting for its Director ends before the database goes.
        _neverAnswers.TrySetResult(null);
        _signIns.WhenIdleAsync().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    // ----- helpers -----

    private Task<LicenseOwnerEmailResult> Mail(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
    {
        _emails.Add((tenant, email));
        return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
    }

    private Task Push(TenantId tenant, IReadOnlyList<LicenseAlarmOpened> opened, CancellationToken ct)
    {
        _pushed.AddRange(opened);
        return Task.CompletedTask;
    }

    /// <summary>A new service over the same database: what a Gateway that restarted would build.</summary>
    private LicenseReadinessService Service()
        => new(_store, new LicenseSnapshotSource(_store, _reads), _log, _signIns, (tenant, account) => _approverWhyNot(tenant, account),
            _directors.IsConnected, _alarms, () => _now);

    /// <summary>Both machines report now, as the fields above say, and their Directors are connected.</summary>
    private void BothReport(TenantId? tenant = null)
    {
        var t = tenant ?? One;
        var a = new List<LicenseHolding> { _machineAOnB ? Holding(B, true, 5, _bWeekly, readAt: _now) : Holding(A, true, _aFiveHour, _aWeekly, readAt: _now) };
        if (_machineAHoldsB && !_machineAOnB)
        {
            var b = Holding(B, false, 5, _bWeekly, readAt: _now, readError: _machineABReadError);
            b.SignInExpiresAtUtc = _machineABExpires;
            a.Add(b);
        }
        _store.ApplyReport(t, DirA, ++_sequence, Report(MachineA, _now, a.ToArray()), _now, TimeSpan.Zero);
        _store.ApplyReport(t, DirB, ++_sequence,
            Report(MachineB, _now, Holding(A, true, _aFiveHour, _aWeekly, readAt: _now), Holding(B, false, 5, _bWeekly, readAt: _now)),
            _now, TimeSpan.Zero);
        _directors.Connect(t, DirA);
        _directors.Connect(t, DirB);
    }

    private async Task<LicenseReadinessTick> Tick(TenantId? tenant = null)
    {
        var tick = await Service().TickAsync(tenant ?? One, CancellationToken.None);
        if (_directorAnswers)
            await _signIns.WhenIdleAsync();
        return tick;
    }

    private static LicenseMachineReadiness Of(LicenseReadinessTick tick, string machine)
        => Assert.Single(tick.Judged, j => j.MachineName == machine);

    // The sign-in's command is sent from the sign-in service's own task: waited for, never read at once.
    private Task<FakeLicenseDirectors.Sent> TheSignInSent() => _directors.WaitFor(s => s.Verb == LicenseContract.SignInVerb);

    private List<FakeLicenseDirectors.Sent> SignInsSent() => _directors.Of(LicenseContract.SignInVerb);

    private List<LicenseSwitchLogEntity> SignInRows() => _log.ListSince(One, T0.AddDays(-1)).OrderBy(r => r.CreatedAtUtc).ToList();

    private void TheDirectorFails(string message = "The approval in the browser did not complete.")
    {
        _directorAnswers = true;
        _directors.AnswerWith(sent => FakeLicenseDirectors.Ok(new LicenseSignInResult
        {
            SignInId = ((LicenseSignInRequest)sent.Payload).SignInId,
            Outcome = LicenseSignInOutcomes.Failed,
            ReasonCode = LicenseRenewalErrors.ApprovalFailed,
            Message = message,
            FinishedAtUtc = _now,
        }));
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

    // ----- the readiness of each machine, word for word -----

    // The machine that holds the sign-in for the account it would switch to next is ready, in those words.
    [Fact]
    public async Task Judge_AMachineHoldingTheNextAccount_IsReady()
    {
        BothReport();

        var b = Of(await Tick(), MachineB);

        Assert.Equal((LicenseReadinessStates.Ready, B, "Ready for account-b@example.com.", LicenseTones.Good),
            (b.State, b.TargetAccountEmail, b.Text, b.Tone));
    }

    // The 16 October gap: no sign-in for the account it would switch to next. A sign-in is started at once, so the
    // sentence of the check that found it says so.
    [Fact]
    public async Task Judge_HoldsNoSignIn_IsNotReady_AndASignInIsStarted()
    {
        BothReport();

        var a = Of(await Tick(), MachineA);

        Assert.Equal((LicenseReadinessStates.NotReady, B, LicenseReadinessFixes.StartNow, LicenseSignInReasons.AutomaticSpare, LicenseTones.Warning),
            (a.State, a.TargetAccountEmail, a.Fix, a.SignInReason, a.Tone));
        Assert.Equal("Not ready: holds no sign-in for account-b@example.com; DevThrottle is starting a sign-in to account-b@example.com now.", a.Text);
    }

    // A sign-in that expires before a switch could finish is not ready; the fix is a renewal, not a second spare.
    [Fact]
    public async Task Judge_SpareExpiresTooSoon_IsNotReady_NamesTheExpiry_AndARenewalIsStarted()
    {
        _machineAHoldsB = true;
        _machineABExpires = T0.AddMinutes(5);
        BothReport();

        var tick = await Tick();
        var a = Of(tick, MachineA);

        Assert.Equal((LicenseReadinessFixes.StartNow, LicenseSignInReasons.AutomaticRenewal), (a.Fix, a.SignInReason));
        Assert.Equal("Not ready: holds a sign-in for account-b@example.com that expires at 2026-10-11 03:17 UTC, before a switch could finish; "
                     + "DevThrottle is starting a sign-in to account-b@example.com now.", a.Text);
        var sent = (LicenseSignInRequest)(await TheSignInSent()).Payload;
        Assert.Equal((B, LicenseSignInReasons.AutomaticRenewal), (sent.AccountEmail, sent.Reason));
        Assert.Single(SignInsSent());
    }

    // No approver that can approve: nothing is started, and the reason is Track C's own sentence.
    [Fact]
    public async Task Judge_NoApprover_StartsNothing_AndTheReasonIsTheApproversSentence()
    {
        _approverWhyNot = _approverReadiness.WhyNotReady;
        BothReport();

        var tick = await Tick();
        var a = Of(tick, MachineA);

        Assert.Equal(LicenseReadinessFixes.ApproverNotReady, a.Fix);
        Assert.Equal("Not ready: holds no sign-in for account-b@example.com. No machine is set to approve sign-ins for account-b@example.com. "
                     + "Set an approver for account-b@example.com on the Licenses page.", a.Text);
        Assert.Empty(tick.Started);
        Assert.Empty(SignInsSent());
        Assert.Empty(SignInRows());
    }

    // A machine whose Director is not connected cannot be signed in: nothing is started, and the sentence says why.
    [Fact]
    public async Task Judge_DirectorOffline_StartsNothing_AndSaysSo()
    {
        BothReport();
        _directors.Disconnect(One, DirA);

        var tick = await Tick();
        var a = Of(tick, MachineA);

        Assert.Equal(LicenseReadinessFixes.DirectorOffline, a.Fix);
        Assert.Equal("Not ready: holds no sign-in for account-b@example.com, and the Director on machine-a is not connected, so it cannot be signed in yet.", a.Text);
        Assert.Empty(SignInsSent());
        Assert.Empty(SignInRows());
    }

    // The check after the one that started a sign-in finds it running, and says when it started.
    [Fact]
    public async Task Judge_ASignInRunning_SaysSigningItInNow()
    {
        BothReport();
        await Tick();
        _now = T0.AddMinutes(1);
        BothReport();

        var a = Of(await Tick(), MachineA);

        Assert.Equal(LicenseReadinessFixes.SigningIn, a.Fix);
        Assert.Equal("Not ready: holds no sign-in for account-b@example.com; a sign-in to account-b@example.com is running, started at 03:12 UTC.", a.Text);
    }

    // The last sign-in failed: the sentence gives when, the Director's own reason, and when it is tried again.
    [Fact]
    public async Task Judge_LastSignInFailed_SaysWhenWhyAndWhenItIsTriedAgain()
    {
        TheDirectorFails();
        BothReport();
        await Tick();
        _now = T0.AddMinutes(1);
        BothReport();

        var a = Of(await Tick(), MachineA);

        Assert.Equal((LicenseReadinessFixes.RetryLater, T0.AddMinutes(2)), (a.Fix, a.NextAttemptAtUtc));
        Assert.Equal("Not ready: holds no sign-in for account-b@example.com; the last sign-in failed at 03:12 UTC "
                     + "(The approval in the browser did not complete); trying again at 03:14 UTC.", a.Text);
    }

    // A sign-in that ended well a moment ago is given the machine's next report, rather than a second sign-in.
    [Fact]
    public async Task Judge_SignedInAMomentAgo_WaitsForTheReport_AndStartsNoSecondSignIn()
    {
        TheDirectorAdds();
        BothReport();
        await Tick();
        _now = T0.AddMinutes(1);
        BothReport(); // the report does not show it yet

        var tick = await Tick();
        var a = Of(tick, MachineA);

        Assert.Equal(LicenseReadinessFixes.AwaitingReport, a.Fix);
        Assert.Equal("Not ready: holds no sign-in for account-b@example.com; it was signed in at 03:12 UTC, and its next report should show it.", a.Text);
        Assert.Empty(tick.Started);
        Assert.Single(SignInsSent());
    }

    // No account has room: its own state, not a readiness gap - nothing is started and nothing is raised.
    [Fact]
    public async Task Judge_NoOtherAccountHasRoom_IsItsOwnState_NotAGap()
    {
        _aWeekly = 90;
        _bWeekly = 98;
        BothReport();

        var tick = await Tick();

        foreach (var machine in new[] { MachineA, MachineB })
        {
            var m = Of(tick, machine);
            Assert.Equal((LicenseReadinessStates.NoRoom, null, "No other account has room for it to switch to.", LicenseTones.Neutral),
                (m.State, m.TargetAccountEmail, m.Text, m.Tone));
            Assert.False(m.IsGap);
        }
        Assert.Empty(SignInsSent());
        Assert.Empty(_alarms.ListOpen(One));
    }

    // ----- one sign-in per gap -----

    // A gap starts exactly one sign-in, recorded in the Licenses log as automatic; the next check, while it is still
    // running, starts none.
    [Fact]
    public async Task Tick_AGap_StartsExactlyOneSignIn_AndNoneWhileItRuns()
    {
        BothReport();

        var first = await Tick();
        _now = T0.AddMinutes(1);
        BothReport();
        var second = await Tick();

        Assert.Single(first.Started);
        Assert.Empty(second.Started);
        var sent = await TheSignInSent();
        Assert.Single(SignInsSent());
        Assert.Equal((DirA, B, LicenseSignInReasons.AutomaticSpare),
            (sent.DirectorId, ((LicenseSignInRequest)sent.Payload).AccountEmail, ((LicenseSignInRequest)sent.Payload).Reason));
        var row = Assert.Single(SignInRows());
        Assert.Equal((MachineA, B, LicenseSignInReasons.AutomaticSpare, LicenseLogStates.Pending), (row.MachineName, row.ToAccountEmail, row.Reason, row.State));
        Assert.Equal("Started automatically: machine-a holds no sign-in for account-b@example.com, the account it would switch to next, "
                     + "so the Director on machine-a was asked to sign in to it as a new spare.", row.Message);
        // The log shows it was automatic, in words, for as long as the row is kept.
        Assert.Equal("Automatic: a new spare", LicenseSignInFold.LogEntry(row, MachineA).WhyText);
    }

    // What the check relies on: the sign-in service itself refuses a second sign-in for the same machine and account
    // while one is open, however it was asked for.
    [Fact]
    public void Start_AnAutomaticSignInWhileOneIsRunning_IsRefused_AndWritesNothing()
    {
        BothReport();
        var machineId = _reads.Read(One, _now).Machines.Single(m => m.MachineName == MachineA).MachineId.ToString("N");

        var first = _signIns.Start(One, B, machineId, LicenseSignInReasons.AutomaticSpare);
        var second = _signIns.Start(One, B, machineId, LicenseSignInReasons.AutomaticSpare);
        var byHand = _signIns.Start(One, B, machineId, LicenseSignInReasons.Manual);

        Assert.True(first.Accepted);
        Assert.Equal((false, "sign_in_running"), (second.Accepted, second.RefusalWord));
        Assert.Equal((false, "sign_in_running"), (byHand.Accepted, byHand.RefusalWord));
        Assert.Single(SignInRows());
    }

    // ----- the retry waits -----

    // Every attempt fails. Each wait of the schedule - 2, 5, 15 and 30 minutes, then hourly - is checked one second
    // before it ends (no sign-in) and when it ends (one sign-in). Each check is made by a NEW service over the same
    // database, as a Gateway that restarted would be, so the wait is read from the log and survives a restart.
    [Fact]
    public async Task Tick_FailedAttempts_AreRetriedOnlyAfterEachWaitOfTheSchedule_EvenAcrossARestart()
    {
        TheDirectorFails();
        BothReport();
        await Tick();
        Assert.Single(SignInsSent());
        var lastAttempt = T0;
        var waits = new[] { 2, 5, 15, 30, 60, 60 };

        for (var i = 0; i < waits.Length; i++)
        {
            var due = lastAttempt.AddMinutes(waits[i]);
            _now = due.AddSeconds(-1);
            BothReport();
            var early = await Tick();
            Assert.Empty(early.Started);
            Assert.Equal((LicenseReadinessFixes.RetryLater, due), (Of(early, MachineA).Fix, Of(early, MachineA).NextAttemptAtUtc));

            _now = due;
            BothReport();
            var onTime = await Tick();
            Assert.Single(onTime.Started);
            Assert.Equal(i + 2, SignInsSent().Count);
            lastAttempt = due;
        }
        // At most one at a time: every attempt ended before the next was started.
        Assert.All(SignInRows(), r => Assert.Equal(LicenseSignInOutcomes.Failed, r.State));
        Assert.Equal(waits.Length + 1, SignInRows().Count);
    }

    [Fact]
    public void RetryWait_IsTheDocumentedSchedule()
    {
        Assert.Equal(new[] { 2.0, 5, 15, 30, 60, 60, 60 },
            Enumerable.Range(1, 7).Select(n => LicenseReadinessFold.RetryWait(n).TotalMinutes));
        Assert.Throws<ArgumentOutOfRangeException>(() => LicenseReadinessFold.RetryWait(0));
    }

    // ----- the 80% alarm -----

    // Under 80%: not ready, no alarm - and the fixing still runs.
    [Fact]
    public async Task Tick_Under80_NotReady_NoAlarm_ButASignInIsStarted()
    {
        _aFiveHour = 79.9;
        _aWeekly = 79.9;
        BothReport();

        var tick = await Tick();

        Assert.True(Of(tick, MachineA).IsGap);
        Assert.Single(tick.Started);
        Assert.Empty(_alarms.ListOpen(One));
        Assert.Empty(_emails);
        Assert.Empty(_pushed);
    }

    // At 80% of the weekly limit and not ready: one alarm opens, naming the machine, the account in use, the account it
    // would switch to and why, and the one thing to do. The next check raises no second alarm and sends nothing more.
    [Fact]
    public async Task Tick_At80OfTheWeeklyLimit_NotReady_OpensOneAlarm_AndTheNextTickNoSecond()
    {
        _aWeekly = 80;
        BothReport();

        var first = await Tick();
        _now = T0.AddMinutes(1);
        BothReport();
        var second = await Tick();

        var alarm = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal(("not-ready-at-80", MachineA, A), (alarm.Kind, alarm.MachineName, alarm.AccountEmail));
        Assert.Equal("machine-a is not ready to switch off account-a@example.com, which is at 80% of its weekly limit: "
                     + "account-b@example.com is the account it would switch to next, and it holds no sign-in for account-b@example.com.", alarm.Problem);
        Assert.Equal("Nothing to do yet: DevThrottle is signing it in; if this stays, sign machine-a in to account-b@example.com from the Licenses page.",
            alarm.Action);
        Assert.Equal((1, 0), (first.Alarms!.Opened, first.Alarms.StillOpen));
        Assert.Equal((0, 1, 0), (second.Alarms!.Opened, second.Alarms.StillOpen, second.Alarms.Closed));
        Assert.Single(_emails);
        Assert.Equal(MachineA, Assert.Single(_pushed).MachineName);
    }

    // The 5-hour window counts too: either window at 80% is enough.
    [Fact]
    public async Task Tick_At80OfTheFiveHourWindow_NotReady_OpensTheAlarm()
    {
        _aFiveHour = 85.5;
        BothReport();

        await Tick();

        Assert.Equal("machine-a is not ready to switch off account-a@example.com, which is at 85.5% of its 5-hour window: "
                     + "account-b@example.com is the account it would switch to next, and it holds no sign-in for account-b@example.com.",
            Assert.Single(_alarms.ListOpen(One)).Problem);
    }

    // The one thing to do follows the reason: start the Director; or the approver's own sentence.
    [Fact]
    public async Task Tick_At80_TheActionIsTheOneThingToDo_ForEachReason()
    {
        _aWeekly = 90;
        BothReport();
        _directors.Disconnect(One, DirA);
        await Tick();
        Assert.Equal("Start the Director on machine-a.", Assert.Single(_alarms.ListOpen(One)).Action);

        _directors.Connect(One, DirA);
        _approverWhyNot = _approverReadiness.WhyNotReady;
        _now = T0.AddMinutes(1);
        BothReport();
        await Tick();
        Assert.Equal("No machine is set to approve sign-ins for account-b@example.com. Set an approver for account-b@example.com on the Licenses page.",
            Assert.Single(_alarms.ListOpen(One)).Action);
    }

    // The machine becomes ready: the alarm closes by itself.
    [Fact]
    public async Task Tick_TheMachineBecomesReady_TheAlarmCloses()
    {
        _aWeekly = 85;
        BothReport();
        await Tick();
        Assert.Single(_alarms.ListOpen(One));

        _machineAHoldsB = true;
        _now = T0.AddMinutes(3);
        BothReport();
        var tick = await Tick();

        Assert.Equal(LicenseReadinessStates.Ready, Of(tick, MachineA).State);
        Assert.Equal(1, tick.Alarms!.Closed);
        Assert.Empty(_alarms.ListOpen(One));
    }

    // The account falls back under 80% (its window reset): the alarm closes, though the machine is still not ready.
    [Fact]
    public async Task Tick_TheAccountFallsUnder80_TheAlarmCloses()
    {
        _aWeekly = 85;
        BothReport();
        await Tick();
        Assert.Single(_alarms.ListOpen(One));

        _aWeekly = 3;
        _now = T0.AddMinutes(3);
        BothReport();
        var tick = await Tick();

        Assert.True(Of(tick, MachineA).IsGap);
        Assert.Equal(1, tick.Alarms!.Closed);
        Assert.Empty(_alarms.ListOpen(One));
    }

    // A reading that is no longer fresh does not raise the alarm: 80% is judged on a fresh reading only.
    [Fact]
    public async Task Tick_At80OnAStaleReading_RaisesNoAlarm()
    {
        _aWeekly = 90;
        BothReport();
        _now = T0.AddMinutes(20); // account A's active reading is stale after 6 minutes; B's spare reading is still fresh

        await Tick();

        Assert.Empty(_alarms.ListOpen(One));
    }

    // Review finding 2 (the Tech Lead's ruling): an open alarm stays open while the reading is stale - the banner going
    // away would read as fixed - with no second email; a fresh reading under 80% then closes it.
    [Fact]
    public async Task Tick_AnOpenAlarm_StaysOpenWhileTheReadingIsStale_AndClosesOnAFreshReadingUnder80()
    {
        _aWeekly = 85;
        BothReport();
        await Tick();
        var opened = Assert.Single(_alarms.ListOpen(One));

        _now = T0.AddMinutes(20); // no report since: account A's reading is stale, B's spare reading still fresh
        var stale = await Tick();

        var still = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal(opened.Id, still.Id);
        Assert.Equal((0, 1, 0), (stale.Alarms!.Opened, stale.Alarms.StillOpen, stale.Alarms.Closed));
        Assert.Equal("machine-a is not ready to switch off account-a@example.com, which was at or above 80% at its last fresh reading: "
                     + "account-b@example.com is the account it would switch to next, and it holds no sign-in for account-b@example.com.", still.Problem);
        Assert.Single(_emails);

        _aWeekly = 40;
        _now = T0.AddMinutes(21);
        BothReport();
        var fresh = await Tick();

        Assert.Equal(1, fresh.Alarms!.Closed);
        Assert.Empty(_alarms.ListOpen(One));
    }

    // Review finding 2: the machine leaving the account closes its alarm, whatever its new account reads.
    [Fact]
    public async Task Tick_TheMachineLeavesTheAccount_TheAlarmCloses()
    {
        _aWeekly = 85;
        BothReport();
        await Tick();
        Assert.Single(_alarms.ListOpen(One));

        _machineAOnB = true;
        _now = T0.AddMinutes(3);
        BothReport();
        var tick = await Tick();

        Assert.Equal(1, tick.Alarms!.Closed);
        Assert.Empty(_alarms.ListOpen(One));
    }

    // Review finding 1: after a failed sign-in the alarm says so - when, the Director's reason, when it is tried again -
    // and never "DevThrottle is signing it in" while nothing is running. The open alarm's words are brought up to date
    // with no second email.
    [Fact]
    public async Task Tick_At80_AfterAFailedSignIn_TheAlarmSaysItFailed_AndWhenItIsTriedAgain()
    {
        TheDirectorFails();
        _aWeekly = 85;
        BothReport();
        await Tick();
        _now = T0.AddMinutes(1);
        BothReport();

        await Tick();

        Assert.Equal("The last sign-in failed at 03:12 UTC (The approval in the browser did not complete); DevThrottle tries again at 03:14 UTC. "
                     + "If this stays, sign machine-a in to account-b@example.com from the Licenses page.", Assert.Single(_alarms.ListOpen(One)).Action);
        Assert.Single(_emails);
    }

    // Review finding 1: a sign-in that ended well and is not reported yet is said as such.
    [Fact]
    public async Task Tick_At80_AfterASignInEndedWell_TheAlarmSaysItWaitsForTheReport()
    {
        TheDirectorAdds();
        _aWeekly = 85;
        BothReport();
        await Tick();
        _now = T0.AddMinutes(1);
        BothReport();

        await Tick();

        Assert.Equal("It was signed in at 03:12 UTC and its next report should show it; if this stays, sign machine-a in to "
                     + "account-b@example.com from the Licenses page.", Assert.Single(_alarms.ListOpen(One)).Action);
    }

    // Review finding 3: "a sign-in is running" is the same read Start refuses by - every open row, however old - so a
    // row open for more than a day still reads as running, and nothing claims to start one.
    [Fact]
    public async Task Judge_ASignInOpenForMoreThanADay_StillReadsAsRunning()
    {
        BothReport();
        var machine = _reads.Read(One, _now).Machines.Single(m => m.MachineName == MachineA);
        _log.CreatePending(One, machine, B, LicenseSignInReasons.AutomaticSpare, "An old sign-in.", T0.AddDays(-2));

        var tick = await Tick();
        var a = Of(tick, MachineA);

        Assert.Equal(LicenseReadinessFixes.SigningIn, a.Fix);
        Assert.Equal("Not ready: holds no sign-in for account-b@example.com; a sign-in to account-b@example.com is running, started at 03:12 UTC.", a.Text);
        Assert.Empty(tick.Started);
    }

    // Review note 4, a ceiling: sign-ins that end well but never show in the machine's reports are not repeated every
    // ten minutes. After the second one in an hour the next waits an hour after the last, and the sentence says why.
    [Fact]
    public async Task Tick_SignInsThatEndWellButNeverShow_AreRepeatedOnlyHourlyAfterTheSecond()
    {
        TheDirectorAdds();
        BothReport();
        await Tick();                       // the first, at 03:12
        _now = T0.AddMinutes(10);
        BothReport();
        Assert.Single((await Tick()).Started); // its report did not show it after ten minutes: the second, at 03:22

        _now = T0.AddMinutes(20);
        BothReport();
        var waiting = await Tick();
        var a = Of(waiting, MachineA);
        Assert.Empty(waiting.Started);
        Assert.Equal((LicenseReadinessFixes.SignedInNotShown, T0.AddMinutes(70)), (a.Fix, a.NextAttemptAtUtc));
        Assert.Equal("Not ready: holds no sign-in for account-b@example.com; it was signed in 2 times within an hour and its reports still "
                     + "do not show it; trying again at 04:22 UTC.", a.Text);
        Assert.Equal("It was signed in to account-b@example.com 2 times within an hour and its reports still do not show it; DevThrottle "
                     + "tries again at 04:22 UTC. Check that the Director on machine-a reports the sign-ins it holds.", a.AlarmAction);

        _now = T0.AddMinutes(70).AddSeconds(-1);
        BothReport();
        Assert.Empty((await Tick()).Started);
        _now = T0.AddMinutes(70);
        BothReport();
        Assert.Single((await Tick()).Started); // the third, at 04:22

        // It stays hourly: after the third, the next is an hour after it, at 05:22.
        _now = T0.AddMinutes(130).AddSeconds(-1);
        BothReport();
        Assert.Equal(LicenseReadinessFixes.SignedInNotShown, Of(await Tick(), MachineA).Fix);
        _now = T0.AddMinutes(130);
        BothReport();
        Assert.Single((await Tick()).Started);
        Assert.Equal(4, SignInsSent().Count);
    }

    // Review note 7: a refused sign-in is not ready, in the one definition's own words, and is fixed by a renewal.
    [Fact]
    public async Task Judge_ASignInThatWasRefused_IsNotReady_AndARenewalIsStarted()
    {
        _machineAHoldsB = true;
        _machineABReadError = LicenseReadErrors.Unauthorized;
        BothReport();

        var a = Of(await Tick(), MachineA);

        Assert.Equal((LicenseReadinessFixes.StartNow, LicenseSignInReasons.AutomaticRenewal), (a.Fix, a.SignInReason));
        Assert.Equal("Not ready: holds a sign-in for account-b@example.com that was refused; DevThrottle is starting a sign-in to "
                     + "account-b@example.com now.", a.Text);
    }

    // Review note 7: the approver is asked only for an account some machine is not ready for, and once per check.
    [Fact]
    public async Task Judge_TheApproverIsAskedOnlyForAnAccountAMachineIsNotReadyFor_OncePerCheck()
    {
        var asked = new List<string>();
        _approverWhyNot = (_, account) => { asked.Add(account); return null; };
        _machineAHoldsB = true;
        BothReport();
        await Tick();
        Assert.Empty(asked); // every machine ready

        _machineAHoldsB = false;
        _now = T0.AddMinutes(1);
        BothReport();
        await Tick();
        Assert.Equal(new[] { B }, asked);
    }

    // ----- only where automation is switched on -----

    // A tenant none of whose machines has ever reported (license switching is not switched on there): nothing is read,
    // no sign-in is started, and no alarm is raised - while the tenant that does report is checked as usual.
    [Fact]
    public async Task Tick_ATenantWithoutAutomation_StartsNoSignIn_AndRaisesNoAlarm()
    {
        _aWeekly = 90;
        BothReport(One);

        var other = await Tick(Two);

        Assert.Same(LicenseReadinessTick.NotRun, other);
        Assert.Empty(SignInsSent());
        Assert.Empty(_alarms.ListOpen(Two));
        Assert.Empty(_alarms.ListOpen(One)); // One was not checked by that call either
    }
}
