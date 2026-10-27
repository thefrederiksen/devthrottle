using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE APPROVER ALARM (License Switching phase 6, Track C), over the real license alarm service, the real approver
/// store and the real Licenses store on a throwaway SQLite file. What these hold down: an approver that cannot approve
/// opens exactly one alarm, with the readiness sentences, and pushes and emails once; it stays open quietly while that
/// stays true and closes by itself when it stops being true or the approver is removed; an approver nobody checks
/// alarms once its 26 hours pass; an account with no approver raises nothing; a check report raises the alarm without
/// the hub call waiting for it; and one tenant never raises or closes another's.
/// </summary>
public sealed class LicenseApproverAlarmTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");
    private const string MachineA = "machine-a";
    private const string DirectorA = "dir-a";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _licenses;
    private readonly LicenseApproverStore _approvers;
    private readonly LicenseAlarmService _alarms;
    private readonly LicenseApproverAlarm _alarm;
    private readonly List<(TenantId Tenant, LicenseOwnerEmail Email)> _emails = new();
    private readonly List<(TenantId Tenant, LicenseAlarmOpened Alarm)> _pushes = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logged = new();
    private DateTime _now = T0;
    private long _sequence;
    // The Directors connected now, by identifier; none unless a test connects one.
    private readonly HashSet<string> _connected = new();

    public LicenseApproverAlarmTests()
    {
        _db = _harness.Open();
        _licenses = new LicenseStore(_db);
        _approvers = new LicenseApproverStore(new TenantSettingsStore(_db));
        _alarms = new LicenseAlarmService(_db, Mail, Push, () => _now);
        _alarm = new LicenseApproverAlarm(_approvers, new LicenseReadModel(_licenses), _alarms, (_, directorId) => _connected.Contains(directorId), _ => new NoScope(), () => _now,
            CancellationToken.None, _logged.Enqueue);
        _licenses.ApplyReport(One, DirectorA, ++_sequence, Report(MachineA, _now, Holding(A, true)), _now, TimeSpan.Zero);
    }

    public void Dispose() => _harness.Dispose();

    private sealed class NoScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private Task<LicenseOwnerEmailResult> Mail(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
    {
        _emails.Add((tenant, email));
        return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
    }

    // A push the test can hold open, to stop a raise part way.
    private TaskCompletionSource? _holdPush;
    private readonly TaskCompletionSource _pushStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task Push(TenantId tenant, IReadOnlyList<LicenseAlarmOpened> opened, CancellationToken ct)
    {
        foreach (var alarm in opened)
            _pushes.Add((tenant, alarm));
        _pushStarted.TrySetResult();
        return _holdPush?.Task ?? Task.CompletedTask;
    }

    /// <summary>Account A's approver on machine A, profile "work", as its last check left it.</summary>
    private void ApproverOfA(string? state, DateTime? checkedAt, string machine = MachineA, TenantId? tenant = null)
        => ApproverOf(A, state, checkedAt, machine, tenant);

    private void ApproverOf(string account, string? state, DateTime? checkedAt, string machine = MachineA, TenantId? tenant = null,
        string? detail = null)
        => _approvers.Change(tenant ?? One, records =>
        {
            var next = records.Approvers.Where(a => a.AccountEmail != account).ToList();
            next.Add(new LicenseApprover(account, machine, "owner", "work", LicenseApproverSources.Registered, state, checkedAt,
                state == LicenseApproverStates.CanApprove ? account : null, detail, "Work Chrome"));
            return ((IReadOnlyList<LicenseApprover>?)next, 0);
        }, _now);

    private Task<LicenseAlarmRaised> Raise(TenantId? tenant = null) => _alarm.RaiseAsync(tenant ?? One, CancellationToken.None);

    // ----- step 4: an account a machine needs has no approver at all (mission "Licenses - Loud When Stranded") -----
    //
    // On 26 October 2026 this alarm handed "no approver at all" to readiness, readiness started no sign-in and raised
    // nothing, and nobody was told. These run the real readiness check over the real store: machine A is on account A,
    // machine B on account B, both well under any limit - a fault, never usage - and neither holds a sign-in for the
    // other's account.

    private const string MachineB = "machine-b";
    private const string DirectorB = "dir-b";

    private LicenseApproverAlarm WithReadiness(Func<LicenseApproverNeeds, LicenseApproverNeeds>? change = null)
    {
        _licenses.ApplyReport(One, DirectorB, ++_sequence, Report(MachineB, _now, Holding(B, true, readAt: _now)), _now, TimeSpan.Zero);
        _licenses.ApplyReport(One, DirectorA, ++_sequence, Report(MachineA, _now, Holding(A, true, readAt: _now)), _now, TimeSpan.Zero);
        _connected.Add(DirectorA);
        _connected.Add(DirectorB);
        var readiness = LicenseReadinessTestKit.ForReading(_db, _licenses, (_, directorId) => _connected.Contains(directorId), () => _now);
        return new LicenseApproverAlarm(_approvers, new LicenseReadModel(_licenses), _alarms, (_, directorId) => _connected.Contains(directorId),
            _ => new NoScope(), () => _now, CancellationToken.None, _logged.Enqueue,
            judge: tenant => change is null ? readiness.JudgeNow(tenant) : change(readiness.JudgeNow(tenant)));
    }

    [Fact]
    public async Task NoApproverAtAll_AMachineNeedsTheAccount_OpensOneAlarm_NamingTheMachineTheAccountAndWhatToDo()
    {
        var alarm = WithReadiness();

        var raised = await alarm.RaiseAsync(One, CancellationToken.None);

        Assert.Equal(2, raised.Opened);
        var forB = Assert.Single(_alarms.ListOpen(One), a => a.AccountEmail == B);
        Assert.Equal(LicenseApproverAlarm.Kind.Key, forB.Kind);
        Assert.Equal("No machine is set to approve sign-ins for account-b@example.com. machine-a cannot be signed in to it, so cannot switch to it when it must.",
            forB.Problem);
        Assert.Equal("Set an approver for account-b@example.com on the Licenses page.", forB.Action);
        Assert.Equal(2, _pushes.Count);
    }

    [Fact]
    public async Task NoApproverAtAll_TheApproverChecksFailed_TheAlarmSaysWhyInTheDirectorsWords()
    {
        var alarm = WithReadiness();
        // SOREN_NORTH's 26 October report: its own profiles, checked with no account, all failed. No record names them.
        _approvers.RecordChecks(One, MachineA, "owner", new[]
        {
            new LicenseApproverCheckRow
            {
                BrowserProfile = "mindzie", BrowserProfileName = "mindzie", State = LicenseApproverStates.CheckFailed, CheckedAtUtc = _now,
                Detail = "The check could not run: the browser profile could not be started: CC_DIRECTOR_ID is not set.",
            },
        }, _now);

        await alarm.RaiseAsync(One, CancellationToken.None);

        var forB = Assert.Single(_alarms.ListOpen(One), a => a.AccountEmail == B);
        Assert.EndsWith("The approver check of browser profile \"mindzie\" on machine-a could not run at 2026-10-11 03:12 UTC: The check could not run: "
                        + "the browser profile could not be started: CC_DIRECTOR_ID is not set.", forB.Problem);
    }

    [Fact]
    public async Task ABrokenApprover_TwoMachinesNeedIt_WithAFullFailedCheckDetail_TheAlarmStillNamesBothMachines()
    {
        // The 26 October shape: the approver's check failed with the longest detail a row takes, and two machines need
        // its account. Naming the account a second time took that sentence past 400 and dropped both machines' names.
        // The second machine's name is long enough that the old sentence passes 400 here too (checked below).
        const string machineC = "machine-c-of-the-night-factory";
        var alarm = WithReadiness();
        _licenses.ApplyReport(One, "dir-c", ++_sequence, Report(machineC, _now, Holding(A, true, readAt: _now)), _now, TimeSpan.Zero);
        _connected.Add("dir-c");
        ApproverOf(B, LicenseApproverStates.CheckFailed, _now, MachineB, detail: new string('d', 199) + ".");

        await alarm.RaiseAsync(One, CancellationToken.None);

        var forB = Assert.Single(_alarms.ListOpen(One), a => a.AccountEmail == B);
        Assert.StartsWith("The approver for account-b@example.com (browser profile \"Work Chrome\" on machine-b)", forB.Problem);
        const string clause = " machine-a and " + machineC + " cannot be signed in to it until it can.";
        Assert.EndsWith(new string('d', 199) + "." + clause, forB.Problem);
        Assert.True(forB.Problem.Length <= LicenseAlarmService.MaxSentenceLength);
        // The shape the test is for: the sentence that named the account a second time is past the limit.
        var healthText = forB.Problem[..^clause.Length];
        Assert.True((healthText + " machine-a and " + machineC + " cannot be signed in to " + B + " until it can.").Length
                    > LicenseAlarmService.MaxSentenceLength);
        Assert.DoesNotContain(_logged, l => l.Contains("[LicenseApproverAlarm] Broken:"));
    }

    [Fact]
    public async Task ABrokenApprover_SoManyMachinesNeedItThatTheirNamesDoNotFit_NamesTheApproverAndLogsTheMachinesItLeftOut()
    {
        var alarm = WithReadiness();
        foreach (var n in Enumerable.Range(1, 12))
        {
            _licenses.ApplyReport(One, $"dir-{n}", ++_sequence, Report($"machine-with-a-long-name-{n}", _now, Holding(A, true, readAt: _now)), _now, TimeSpan.Zero);
            _connected.Add($"dir-{n}");
        }
        ApproverOf(B, LicenseApproverStates.SignedOut, _now, MachineB);

        await alarm.RaiseAsync(One, CancellationToken.None);

        var forB = Assert.Single(_alarms.ListOpen(One), a => a.AccountEmail == B);
        Assert.DoesNotContain("cannot be signed in", forB.Problem);
        Assert.Contains(_logged, l => l.Contains("[LicenseApproverAlarm] Broken: the alarm of account=account-b@example.com")
                                      && l.Contains("but not the 13 machines that need it"));
    }

    [Fact]
    public void TheLicensesPage_AMachineWhoseApproverCheckFailed_ItsReadinessLineSaysWhy_AndNoOtherMachinesLineDoes()
    {
        // The page leg of "the reason is never discarded": the overview route's folds, over the real store and the real
        // readiness check, with a failed check on machine A's own browser profile and none on machine B.
        WithReadiness();
        _approvers.RecordChecks(One, MachineA, "owner", new[]
        {
            new LicenseApproverCheckRow
            {
                BrowserProfile = "mindzie", BrowserProfileName = "mindzie", State = LicenseApproverStates.CheckFailed, CheckedAtUtc = _now,
                Detail = "The check could not run: the browser profile could not be started: CC_DIRECTOR_ID is not set.",
            },
        }, _now);
        var readiness = LicenseReadinessTestKit.ForReading(_db, _licenses, (_, directorId) => _connected.Contains(directorId), () => _now);
        var picture = new LicenseSnapshotSource(_licenses, new LicenseReadModel(_licenses)).ReadPicture(One, _now, Array.Empty<LicenseExhaustion>());
        var overview = LicenseDisplayFold.Overview(picture);

        LicenseReadinessFold.Apply(overview, readiness.Judge(One, picture, _now), _approvers.ListFailedChecksQuietly(One), _now);

        var a = overview.Machines.Single(m => m.MachineName == MachineA);
        var b = overview.Machines.Single(m => m.MachineName == MachineB);
        Assert.EndsWith(" The approver check of browser profile \"mindzie\" on machine-a could not run at 2026-10-11 03:12 UTC: The check could not run: "
                        + "the browser profile could not be started: CC_DIRECTOR_ID is not set.", a.ReadinessText);
        Assert.NotNull(b.ReadinessText);
        Assert.DoesNotContain("approver check", b.ReadinessText);
    }

    [Fact]
    public void FailedChecksText_ForOneMachine_CountsOnlyThatMachineAndUsersChecks_AndNamesTheLatest()
    {
        var failed = new[]
        {
            new LicenseApproverFailedCheck(MachineA, "owner", "work", "Work Chrome", _now.AddHours(-2), "Older."),
            new LicenseApproverFailedCheck("MACHINE-A", "OWNER", "mindzie", null, _now.AddHours(-1), "Latest on machine-a."),
            new LicenseApproverFailedCheck(MachineA, "someone-else", "home", null, _now, "Another user."),
            new LicenseApproverFailedCheck(MachineB, "owner", "work", null, _now, "Another machine."),
            new LicenseApproverFailedCheck(MachineA, "owner", "stale", null, _now - LicenseApproverContract.NotCheckedAfter - TimeSpan.FromMinutes(1), "Too old."),
        };

        var text = LicenseApproverReadiness.FailedChecksText(failed, _now, MachineA, "owner");

        Assert.Equal("2 approver checks could not run, the latest of browser profile \"mindzie\" on MACHINE-A at 2026-10-11 02:12 UTC: Latest on machine-a.",
            text);
        Assert.Null(LicenseApproverReadiness.FailedChecksText(failed, _now, "machine-z", "owner"));
    }

    [Fact]
    public async Task NoApproverAtAll_ClosesWhenAnApproverThatCanApproveIsSet_AndComingBackWithinTheHoldDownTellsNobody()
    {
        var alarm = WithReadiness();
        await alarm.RaiseAsync(One, CancellationToken.None);
        var pushes = _pushes.Count;

        ApproverOf(B, LicenseApproverStates.CanApprove, _now, MachineB);
        ApproverOf(A, LicenseApproverStates.CanApprove, _now, MachineA);
        var closed = await alarm.RaiseAsync(One, CancellationToken.None);
        Assert.Equal(2, closed.Closed);
        Assert.Empty(_alarms.ListOpen(One));

        // The approver of B is removed an hour later: the same fault, inside six hours - the line comes back, nobody is told.
        _now = _now.AddHours(1);
        _licenses.ApplyReport(One, DirectorB, ++_sequence, Report(MachineB, _now, Holding(B, true, readAt: _now)), _now, TimeSpan.Zero);
        _licenses.ApplyReport(One, DirectorA, ++_sequence, Report(MachineA, _now, Holding(A, true, readAt: _now)), _now, TimeSpan.Zero);
        Assert.True(_approvers.Remove(One, B, _now));
        await alarm.RaiseAsync(One, CancellationToken.None);
        Assert.Single(_alarms.ListOpen(One), a => a.AccountEmail == B);
        Assert.Equal(pushes, _pushes.Count);
    }

    [Fact]
    public async Task AMachineToldByItsOwnStrandedOrEightyAlarm_TheApproverAlarmStandsDownForThatAccount()
    {
        // Machine A's own line (stranded, or warned at 80%) already says the approver's sentence: one cause, one alert.
        // Machine B is not told by its own alarm, so account A's alarm, which only machine B needs, still opens.
        Guid machineA = Guid.Empty;
        var alarm = WithReadiness(needs =>
        {
            machineA = needs.Judged.Single(j => j.MachineName == MachineA).MachineId;
            return needs with { ToldByTheirOwnAlarm = new HashSet<Guid> { machineA } };
        });

        await alarm.RaiseAsync(One, CancellationToken.None);

        Assert.Equal(new[] { A }, _alarms.ListOpen(One).Select(a => a.AccountEmail));
    }

    [Fact]
    public async Task NoApproverAtAll_NoMachineNeedsTheAccount_RaisesNothing_AtAnyUse()
    {
        // Every machine holds a sign-in for the other's account, so neither needs an approver: use alone, even at 100%,
        // is never an alarm here (issue #4249).
        _licenses.ApplyReport(One, DirectorB, ++_sequence,
            Report(MachineB, _now, Holding(B, true, fiveHour: 100, sevenDay: 100, readAt: _now), Holding(A, false, readAt: _now)), _now, TimeSpan.Zero);
        _licenses.ApplyReport(One, DirectorA, ++_sequence,
            Report(MachineA, _now, Holding(A, true, fiveHour: 99, sevenDay: 99, readAt: _now), Holding(B, false, readAt: _now)), _now, TimeSpan.Zero);
        var readiness = LicenseReadinessTestKit.ForReading(_db, _licenses, (_, _) => true, () => _now);
        var alarm = new LicenseApproverAlarm(_approvers, new LicenseReadModel(_licenses), _alarms, (_, _) => true, _ => new NoScope(), () => _now,
            CancellationToken.None, _logged.Enqueue, judge: readiness.JudgeNow);

        var raised = await alarm.RaiseAsync(One, CancellationToken.None);

        Assert.Equal(0, raised.Opened);
        Assert.Empty(_pushes);
    }

    [Fact]
    public async Task ACheckReport_KeepsAFailedCheckOfAProfileNoRecordNames_AndAWorkingCheckClearsIt()
    {
        // From 19 to 26 October every one of SOREN_NORTH's checks was answered "nothing to store" and lost.
        var registration = new LicenseApproverRegistration(_approvers, new LicenseReadModel(_licenses), () => _now);
        var failed = new LicenseApproverCheckReport
        {
            OffersApprovers = true,
            Rows = [new LicenseApproverCheckRow { BrowserProfile = "mindzie", State = LicenseApproverStates.CheckFailed, CheckedAtUtc = _now, Detail = "Why it failed." }],
        };

        var answer = registration.Report(One, DirectorA, failed);

        Assert.Equal(LicenseApproverCheckOutcomes.NothingToStore, Assert.Single(answer.Rows).Outcome);
        var kept = Assert.Single(_approvers.ListFailedChecksQuietly(One));
        Assert.Equal((MachineA, "mindzie", "Why it failed."), (kept.MachineName, kept.BrowserProfile, kept.Detail));

        registration.Report(One, DirectorA, new LicenseApproverCheckReport
        {
            OffersApprovers = true,
            Rows = [new LicenseApproverCheckRow { BrowserProfile = "mindzie", State = LicenseApproverStates.SignedOut, CheckedAtUtc = _now }],
        });
        Assert.Empty(_approvers.ListFailedChecksQuietly(One));
    }

    [Fact]
    public async Task AnApproverThatNeedsAFreshSignIn_OpensOneAlarm_WithTheReadinessSentences_AndPushesAndEmailsOnce()
    {
        ApproverOfA(LicenseApproverStates.NeedsFreshSignIn, _now);

        var raised = await Raise();

        Assert.Equal(1, raised.Opened);
        var open = Assert.Single(_alarms.ListOpen(One));
        // About the account alone: the sentence names the machine.
        Assert.Equal((LicenseApproverAlarm.Kind.Key, Guid.Empty, "", A), (open.Kind, open.MachineId, open.MachineName, open.AccountEmail));
        Assert.Equal("The approver for account-a@example.com (browser profile \"Work Chrome\" on machine-a) needs a fresh claude.ai sign-in on the approval page.",
            open.Problem);
        Assert.Equal("Open that browser on machine-a and sign in to claude.ai once.", open.Action);
        Assert.Single(_pushes);
        Assert.Single(_emails);
    }

    [Fact]
    public async Task RaisedAgainWhileStillTrue_StaysOpenQuietly_AndClosesWhenTheApproverCanApproveAgain()
    {
        ApproverOfA(LicenseApproverStates.SignedOut, _now);
        await Raise();

        var again = await Raise();
        Assert.Equal((0, 1, 0), (again.Opened, again.StillOpen, again.Closed));
        Assert.Single(_pushes);

        ApproverOfA(LicenseApproverStates.CanApprove, _now);
        var healed = await Raise();
        Assert.Equal(1, healed.Closed);
        Assert.Empty(_alarms.ListOpen(One));
    }

    [Fact]
    public async Task AnApproverTheOwnerRemoves_ClosesItsAlarm()
    {
        ApproverOfA(LicenseApproverStates.WrongAccount, _now);
        await Raise();

        Assert.True(_approvers.Remove(One, A, _now));
        await Raise();

        Assert.Empty(_alarms.ListOpen(One));
    }

    [Fact]
    public async Task AHealthyApproverThatNobodyChecks_AlarmsOnceItsCheckIsOver26HoursOld()
    {
        ApproverOfA(LicenseApproverStates.CanApprove, _now);
        await Raise();
        Assert.Empty(_alarms.ListOpen(One));

        _now = T0 + LicenseApproverContract.NotCheckedAfter + TimeSpan.FromSeconds(1);
        await Raise();

        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.StartsWith("The approver for account-a@example.com (browser profile \"Work Chrome\" on machine-a) was last checked", open.Problem);
        Assert.Equal("Start the Director on machine-a; it checks this approver within 10 minutes of connecting.", open.Action);
    }

    [Fact]
    public async Task ANeverCheckedApprover_WhoseDirectorIsConnected_AlarmSaysTheCheckIsComing_NeverToStartTheDirector()
    {
        // The owner has just set the approver, and machine-a's Director is running and connected: the alarm is true (the
        // record cannot approve yet) and its one thing to do is not to start a Director that is already running.
        _connected.Add(DirectorA);
        ApproverOfA(null, null);

        await Raise();

        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.EndsWith("has never been checked.", open.Problem);
        Assert.Equal("Nothing to do: the Director on machine-a is connected and checks it within 10 minutes.", open.Action);
    }

    [Fact]
    public async Task AnApproverOnAMachineThatHasNeverReported_AlarmsAboutTheAccount_AndStillNamesTheMachine()
    {
        ApproverOfA(null, null, machine: "machine-z");

        await Raise();

        var open = Assert.Single(_alarms.ListOpen(One));
        // Stored as "about no one machine": the empty identifier and name.
        Assert.Equal((Guid.Empty, "", A), (open.MachineId, open.MachineName, open.AccountEmail));
        Assert.Contains("on machine-z", open.Problem);
    }

    [Fact]
    public async Task AnApproversMachineReportingForTheFirstTime_KeepsTheSameAlarm_NoSecondPushOrEmail()
    {
        ApproverOfA(null, null, machine: "machine-z");
        await Raise();
        var first = Assert.Single(_alarms.ListOpen(One)).Id;

        // machine-z's Director starts and reports, before its first approver check.
        _licenses.ApplyReport(One, "dir-z", ++_sequence, Report("machine-z", _now), _now, TimeSpan.Zero);
        var raised = await Raise();

        Assert.Equal((0, 1, 0), (raised.Opened, raised.StillOpen, raised.Closed));
        Assert.Equal(first, Assert.Single(_alarms.ListOpen(One)).Id);
        Assert.Single(_pushes);
        Assert.Single(_emails);
    }

    [Fact]
    public async Task AnApproverWhoseSentenceIsTooLongForAnAlarm_CostsOnlyItsOwnAlarm_TheOthersStillOpen()
    {
        // A 300-character account: its "could not be checked" sentence with a 200-character detail is past 400.
        var longAccount = new string('x', 288) + "@example.com";
        ApproverOf(longAccount, LicenseApproverStates.CheckFailed, _now, detail: new string('d', 199) + ".");
        ApproverOfA(LicenseApproverStates.SignedOut, _now);

        await Raise();

        Assert.Equal(A, Assert.Single(_alarms.ListOpen(One)).AccountEmail);
        Assert.Contains(_logged, l => l.Contains($"REFUSED the alarm of account={longAccount}"));
    }

    [Fact]
    public async Task AnApproverWithALongDetailAndALongAccount_WithinTheLimit_StillOpens()
    {
        // The review's case: a 200-character detail and a 100-character account still fit one alarm sentence.
        var account = new string('y', 88) + "@example.com";
        ApproverOf(account, LicenseApproverStates.CheckFailed, _now, detail: new string('d', 199) + ".");

        await Raise();

        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal(account, open.AccountEmail);
        Assert.True(open.Problem.Length <= LicenseAlarmService.MaxSentenceLength);
    }

    [Fact]
    public async Task TwoRaisesOfOneTenant_RunOneAtATime_SoTheSecondReadsTheRecordsAsTheyAreWhenItsTurnComes()
    {
        ApproverOfA(LicenseApproverStates.SignedOut, _now);
        _holdPush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Raise();
        await _pushStarted.Task;

        // While the first raise is still inside (its push held), the approver reports healthy and a second raise starts,
        // then the approver signs out again. Reading before its turn, the second would carry the healthy read and close
        // an alarm that is true again; waiting its turn, it reads signed out and leaves the alarm open.
        ApproverOfA(LicenseApproverStates.CanApprove, _now);
        var second = Raise();
        Assert.False(second.IsCompleted);
        ApproverOfA(LicenseApproverStates.SignedOut, _now);

        _holdPush.SetResult();
        await first;
        var raised = await second;

        Assert.Equal((0, 1, 0), (raised.Opened, raised.StillOpen, raised.Closed));
        Assert.Single(_alarms.ListOpen(One));
    }

    [Fact]
    public async Task AMinuteRaiseWithNothingToSay_WritesNoLogLine()
    {
        await Raise();
        ApproverOfA(LicenseApproverStates.CanApprove, _now);
        await Raise();

        Assert.Empty(_logged);
    }

    [Fact]
    public async Task AnAccountWithNoApprover_RaisesNothing()
    {
        var raised = await Raise();

        Assert.Equal((0, 0, 0), (raised.Opened, raised.StillOpen, raised.Closed));
        Assert.Empty(_alarms.ListOpen(One));
        Assert.Empty(_pushes);
    }

    [Fact]
    public async Task OneTenantsRaise_NeverOpensOrClosesAnotherTenantsAlarm()
    {
        ApproverOfA(LicenseApproverStates.SignedOut, _now);
        await Raise(One);

        await Raise(Two);

        Assert.Single(_alarms.ListOpen(One));
        Assert.Empty(_alarms.ListOpen(Two));
    }

    [Fact]
    public async Task ACheckReport_RaisesTheAlarm_AfterTheRecordIsWritten_WithoutTheReportWaitingForIt()
    {
        var registration = new LicenseApproverRegistration(_approvers, new LicenseReadModel(_licenses), () => _now, _alarm.RaiseSoon);
        Assert.True(_approvers.Set(One, A, MachineA, "owner", "work", _now).Accepted);

        var answer = registration.Report(One, DirectorA, new LicenseApproverCheckReport
        {
            Rows = [new LicenseApproverCheckRow { BrowserProfile = "work", AccountEmail = A, State = LicenseApproverStates.SignedOut, CheckedAtUtc = _now }],
        });
        await _alarm.WhenIdleAsync();

        Assert.Equal(LicenseApproverCheckOutcomes.HealthUpdated, Assert.Single(answer.Rows).Outcome);
        Assert.Equal("The approver for account-a@example.com (browser profile \"work\" on machine-a) is not signed in to claude.ai.",
            Assert.Single(_alarms.ListOpen(One)).Problem);
    }

    [Fact]
    public void AReportRefusedWhole_RaisesNothing_AndADecidedReportRaisesOnce()
    {
        var raises = new List<TenantId>();
        var registration = new LicenseApproverRegistration(_approvers, new LicenseReadModel(_licenses), () => _now, raises.Add);

        Assert.Throws<ArgumentException>(() => registration.Report(One, DirectorA, null));
        Assert.Empty(raises);

        registration.Report(One, DirectorA, new LicenseApproverCheckReport());
        Assert.Equal(new[] { One }, raises);
    }
}
