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
/// THE 80% WARNING COVERS BOTH WINDOWS AGAIN, FOR FAULTS ONLY (mission "Licenses - Loud When Stranded", step 2) - the
/// readiness check raising "cannot-switch-at-80" over the real license store, the real sign-in service, the real approver
/// check and the real license alarm on a throwaway SQLite file. The owner email and the push are recorders; the Directors
/// are connected and take a command without answering; the clock is the test's own.
///
/// The picture is the 24-25 October 2026 one in small: machine-a and machine-b are on account A. Account B has room and is
/// the best account with room; account C has room too, with more weekly use. machine-b holds a sign-in for B; machine-a
/// holds none unless a test says so. No machine is set to approve sign-ins for B, so the Gateway cannot sign machine-a in
/// by itself - as on the day. What these hold down: weekly at 80% warns, and so does the five-hour window; a machine ready
/// for the SECOND account with room is covered and warns about nothing; a stale machine is never warned about; the
/// hold-down; the warning closes when the fault ends; one machine gets one alert at a time (a fresh 100% is one push per
/// machine; 80% then 97% is two, in order); and the #4249 boundary - usage alone, at any level, raises nothing.
/// </summary>
public sealed class LicenseCannotSwitchAlarmTests : IDisposable
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
    private readonly LicenseAlarmService _alarms;
    private readonly List<LicenseOwnerEmail> _emails = new();
    private readonly List<LicenseAlarmOpened> _pushed = new();
    private readonly TaskCompletionSource<DirectorCommandResult?> _neverAnswers = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime _now = T0;
    private long _sequence;

    // How the machines report: the use of account A in each window, and what each machine holds.
    private double _aFiveHour = 30;
    private double _aWeekly = 85;
    private bool _machineAHoldsB;
    private bool _machineAHoldsC;
    private bool _machineBHoldsB = true;
    private bool _machineBHoldsC = true;
    // machine-a has left account A: it reports account B as its active account and holds nothing else.
    private bool _machineAOnB;
    // machine-a stops reporting: only machine-b's reports arrive.
    private bool _machineASilent;

    public LicenseCannotSwitchAlarmTests()
    {
        _db = _harness.Open();
        _store = new LicenseStore(_db);
        _reads = new LicenseReadModel(_store);
        _log = new LicenseSignInLog(_db);
        _signIns = new LicenseSignInService(_log, _reads, _directors.IsConnected, _directors.SendAsync, _directors.EnterScope,
            () => _now, CancellationToken.None);
        _approverReadiness = new LicenseApproverReadiness(new LicenseApproverStore(new TenantSettingsStore(_db)), _reads,
            _directors.IsConnected, () => _now);
        _alarms = new LicenseAlarmService(_db, Mail, Push, () => _now);
        _directors.Answer = _ => _neverAnswers.Task;
    }

    public void Dispose()
    {
        _neverAnswers.TrySetResult(null);
        _signIns.WhenIdleAsync().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    // ----- helpers -----

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

    // Both machines report now, as the fields above say, and their Directors are connected. B and C both have room, B
    // with the lower weekly use, so B is the best account with room.
    private void Report()
    {
        if (!_machineASilent)
        {
            var a = new List<LicenseHolding>
            {
                _machineAOnB ? Holding(B, true, 5, 2, readAt: _now) : Holding(A, true, _aFiveHour, _aWeekly, readAt: _now),
            };
            if (_machineAHoldsB && !_machineAOnB)
                a.Add(Holding(B, false, 5, 2, readAt: _now));
            if (_machineAHoldsC && !_machineAOnB)
                a.Add(Holding(C, false, 5, 40, readAt: _now));
            _store.ApplyReport(One, DirA, ++_sequence, LicenseTestData.Report(MachineA, _now, a.ToArray()), _now, TimeSpan.Zero);
        }
        var b = new List<LicenseHolding> { Holding(A, true, _aFiveHour, _aWeekly, readAt: _now) };
        if (_machineBHoldsB)
            b.Add(Holding(B, false, 5, 2, readAt: _now));
        if (_machineBHoldsC)
            b.Add(Holding(C, false, 5, 40, readAt: _now));
        _store.ApplyReport(One, DirB, ++_sequence, LicenseTestData.Report(MachineB, _now, b.ToArray()), _now, TimeSpan.Zero);
        _directors.Connect(One, DirA);
        _directors.Connect(One, DirB);
    }

    private Task<LicenseReadinessTick> Tick()
        => new LicenseReadinessService(_store, new LicenseSnapshotSource(_store, _reads), _log, _signIns, _approverReadiness.WhyNotReady,
            _directors.IsConnected, _alarms, () => _now).TickAsync(One, CancellationToken.None);

    private void Later(int minutes)
    {
        _now = T0.AddMinutes(minutes);
        Report();
    }

    private List<LicenseAlarmEntity> Warnings() => _alarms.ListOpen(One).Where(a => a.Kind == LicenseCannotSwitchAlarm.Kind.Key).ToList();

    private List<LicenseAlarmEntity> Stranded() => _alarms.ListOpen(One).Where(a => a.Kind == LicenseStrandedAlarm.Kind.Key).ToList();

    private IReadOnlyList<LicenseAlarmDto> BannerLines() => LicenseAlarmFold.Banner(_alarms.ListOpen(One), _now).Alarms;

    private void AssertNothingRaised(LicenseReadinessTick tick)
    {
        Assert.Equal((0, 0, 0), (tick.Alarms!.Opened, tick.Alarms.StillOpen, tick.Alarms.Closed));
        Assert.Empty(_alarms.ListOpen(One));
        Assert.Empty(_emails);
        Assert.Empty(_pushed);
    }

    // ----- it warns, for either window -----

    // The 24-25 October case: weekly at 80% and more, machine-a ready for no account with room, and no automatic fix. One
    // warning: one email, one push and one banner line, naming the machine, the account it is on, the account it needs and
    // the one thing to do.
    [Fact]
    public async Task Tick_WeeklyAt80_AMachineReadyForNoAccountWithRoom_RaisesOneWarning()
    {
        _aWeekly = 80;
        Report();

        var tick = await Tick();

        Assert.Equal((1, 0, 0), (tick.Alarms!.Opened, tick.Alarms.StillOpen, tick.Alarms.Closed));
        var alarm = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal((LicenseCannotSwitchAlarm.Kind.Key, MachineA, A), (alarm.Kind, alarm.MachineName, alarm.AccountEmail));
        Assert.Equal("machine-a is on account-a@example.com, which is at 80% of its weekly limit, and cannot switch to account-b@example.com, "
                     + "the account with room it would go to, because it holds no sign-in for account-b@example.com.", alarm.Problem);
        Assert.Equal(NoApprover, alarm.Action);
        var push = Assert.Single(_pushed);
        Assert.Equal((LicenseCannotSwitchAlarm.Kind.Key, MachineA), (push.Kind.Key, push.MachineName));
        var email = Assert.Single(_emails);
        Assert.Contains(MachineA, email.Subject + email.BodyText);
        Assert.Contains(alarm.Problem, email.BodyText);
        Assert.Contains(NoApprover, email.BodyText);
        var line = Assert.Single(BannerLines());
        Assert.Equal((LicenseCannotSwitchAlarm.Kind.Key, MachineA, A), (line.Kind, line.MachineName, line.AccountEmail));
    }

    // The five-hour window counts the same: at 80% of it, with the week low, the same warning.
    [Fact]
    public async Task Tick_FiveHourAt80_AMachineReadyForNoAccountWithRoom_RaisesTheWarning()
    {
        _aFiveHour = 80;
        _aWeekly = 20;
        Report();

        await Tick();

        var alarm = Assert.Single(Warnings());
        Assert.Equal(MachineA, alarm.MachineName);
        Assert.StartsWith("machine-a is on account-a@example.com, which is at 80% of its 5-hour window, and cannot switch to account-b@example.com",
            alarm.Problem);
        Assert.Single(_pushed);
    }

    // Both windows high: one warning naming both, not two.
    [Fact]
    public async Task Tick_BothWindowsAt80_RaisesOneWarningNamingBoth()
    {
        _aFiveHour = 90;
        _aWeekly = 85;
        Report();

        await Tick();

        Assert.Contains("which is at 90% of its 5-hour window and 85% of its weekly limit,", Assert.Single(Warnings()).Problem);
        Assert.Single(_pushed);
    }

    // Under 80% of both windows: no warning, however unready the machine.
    [Fact]
    public async Task Tick_Under80_AMachineReadyForNoAccountWithRoom_RaisesNothing()
    {
        _aFiveHour = 79.9;
        _aWeekly = 79.9;
        Report();

        AssertNothingRaised(await Tick());
    }

    // ----- what is not a fault -----

    // Readiness counts a machine as covered when it is ready for ANY account with room. machine-a holds no sign-in for B,
    // the best account with room, but holds one for C, which has room too - so the switch would move it to C. It is ready,
    // for C; no sign-in is started for it; nothing is raised.
    [Fact]
    public async Task Tick_AMachineReadyOnlyForTheSecondAccountWithRoom_IsCovered_AndRaisesNothing()
    {
        _machineAHoldsC = true;
        Report();

        var tick = await Tick();

        var a = Assert.Single(tick.Judged, j => j.MachineName == MachineA);
        Assert.Equal((LicenseReadinessStates.Ready, C, "Ready for account-c@example.com."), (a.State, a.TargetAccountEmail, a.Text));
        Assert.Empty(tick.Started);
        AssertNothingRaised(tick);
    }

    // A stale machine is never stranded and never warned about: its holdings are not known to be current. machine-a stops
    // reporting while machine-b keeps the account's reading fresh at 85%.
    [Fact]
    public async Task Tick_AStaleMachine_IsNeverWarnedAbout()
    {
        Report();
        _machineASilent = true;
        _now = T0 + LicenseContract.MachineStaleAfter + TimeSpan.FromMinutes(1);
        Report();

        var tick = await Tick();

        AssertNothingRaised(tick);
        Assert.DoesNotContain(_pushed, p => p.MachineName == MachineA);
    }

    // No account has room: that is no-room, status on the Licenses page at 80%, never this warning.
    [Fact]
    public async Task Tick_NoAccountHasRoom_RaisesNoWarning()
    {
        _machineBHoldsB = false;
        Report();
        // B and C at the threshold: neither has room.
        _store.ApplyReport(One, DirB, ++_sequence, LicenseTestData.Report(MachineB, _now, Holding(A, true, _aFiveHour, _aWeekly, readAt: _now),
            Holding(B, false, 5, 98, readAt: _now), Holding(C, false, 5, 99, readAt: _now)), _now, TimeSpan.Zero);

        var tick = await Tick();

        Assert.All(tick.Judged, j => Assert.Equal(LicenseReadinessStates.NoRoom, j.State));
        AssertNothingRaised(tick);
    }

    // Issue #4249, the boundary: usage alone at any level - 80%, 97%, 100%, of either window - with every machine ready for
    // an account with room, raises nothing at all. Here machine-a is ready for B, or only for C.
    [Theory]
    [InlineData(80, 20, false)]
    [InlineData(20, 80, false)]
    [InlineData(97, 20, false)]
    [InlineData(20, 97, false)]
    [InlineData(100, 100, false)]
    [InlineData(80, 80, true)]
    [InlineData(20, 100, true)]
    public async Task Tick_UsageAlone_AtAnyLevel_WithEveryMachineReady_RaisesNothing(double fiveHour, double weekly, bool onlyForC)
    {
        _aFiveHour = fiveHour;
        _aWeekly = weekly;
        _machineAHoldsB = !onlyForC;
        _machineAHoldsC = onlyForC;
        Report();

        AssertNothingRaised(await Tick());
    }

    // ----- it stays, it closes, it is held down -----

    // The next check sends nothing more; the open warning stays.
    [Fact]
    public async Task Tick_TheNextCheck_KeepsTheWarningOpen_AndSendsNothingMore()
    {
        Report();
        await Tick();
        Later(1);

        var tick = await Tick();

        Assert.Equal((0, 1, 0), (tick.Alarms!.Opened, tick.Alarms.StillOpen, tick.Alarms.Closed));
        Assert.Single(_emails);
        Assert.Single(_pushed);
    }

    // The fault ends when the machine becomes ready: the warning closes by itself.
    [Fact]
    public async Task Tick_TheMachineBecomesReady_TheWarningCloses()
    {
        Report();
        await Tick();

        _machineAHoldsB = true;
        Later(3);
        var tick = await Tick();

        Assert.Equal(1, tick.Alarms!.Closed);
        Assert.Empty(_alarms.ListOpen(One));
    }

    // The fault ends when the account reads fresh under 80% again (its window reset): the warning closes.
    [Fact]
    public async Task Tick_TheAccountFallsUnder80_TheWarningCloses()
    {
        Report();
        await Tick();

        _aWeekly = 3;
        Later(3);
        var tick = await Tick();

        Assert.Equal(1, tick.Alarms!.Closed);
        Assert.Empty(_alarms.ListOpen(One));
    }

    // The fault ends when the machine moves off the account: the warning closes.
    [Fact]
    public async Task Tick_TheMachineLeavesTheAccount_TheWarningCloses()
    {
        Report();
        await Tick();

        _machineAOnB = true;
        Later(3);
        var tick = await Tick();

        Assert.Equal(1, tick.Alarms!.Closed);
        Assert.Empty(_alarms.ListOpen(One));
    }

    // While the account's reading is stale the warning cannot be judged, so it stays open: a line that vanished because a
    // number is not known for a while would read as fixed.
    [Fact]
    public async Task Tick_AnOpenWarning_StaysOpenWhileTheReadingIsStale()
    {
        Report();
        await Tick();
        var opened = Assert.Single(Warnings());

        _now = T0.AddMinutes(20); // nobody has reported since: account A's reading is stale

        var tick = await Tick();
        Assert.Equal((0, 1, 0), (tick.Alarms!.Opened, tick.Alarms.StillOpen, tick.Alarms.Closed));
        Assert.Equal(opened.Id, Assert.Single(Warnings()).Id);
        Assert.Single(_emails);
    }

    // The six-hour hold-down: a warning that closes and comes back within six hours opens its line again but tells nobody;
    // after six hours it tells the owner again.
    [Fact]
    public async Task Tick_AWarningThatComesBackWithinTheHoldDown_TellsNobody_AndAfterItTellsTheOwnerAgain()
    {
        Report();
        await Tick();
        _machineAHoldsB = true;
        Later(5);
        await Tick();
        Assert.Empty(_alarms.ListOpen(One));

        _machineAHoldsB = false;
        Later(60);
        var back = await Tick();

        Assert.Equal((1, 1), (back.Alarms!.Opened, back.Alarms.Held));
        Assert.Single(BannerLines());
        Assert.Single(_emails);
        Assert.Single(_pushed);

        _machineAHoldsB = true;
        Later(70);
        await Tick();
        _machineAHoldsB = false;
        Later(70 + (int)LicenseAlarmService.HoldDown.TotalMinutes + 1);
        await Tick();

        Assert.Single(Warnings());
        Assert.Equal(2, _emails.Count);
        Assert.Equal(2, _pushed.Count);
    }

    // ----- one machine, one fault, one alert -----

    // A fresh picture at 100%: the warning and the stranded alarm are both true for each machine that cannot switch. Each
    // gets exactly ONE push - the stranded one - and the banner one line each; no warning is open.
    [Fact]
    public async Task Tick_AFreshPictureAt100_IsOnePushPerStrandedMachine_NotTwo()
    {
        _aWeekly = 100;
        _machineBHoldsB = false;
        _machineBHoldsC = false;
        Report();
        // A third machine on account B, which has room: B's reading reaches the Gateway while neither machine on A holds it.
        _store.ApplyReport(One, "dir-machine-c", ++_sequence, LicenseTestData.Report("machine-c", _now, Holding(B, true, 5, 2, readAt: _now)),
            _now, TimeSpan.Zero);

        await Tick();

        Assert.Empty(Warnings());
        Assert.Equal(new[] { MachineA, MachineB }, Stranded().Select(a => a.MachineName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(new[] { MachineA, MachineB }, _pushed.Select(p => p.MachineName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(_pushed, p => Assert.Equal(LicenseStrandedAlarm.Kind.Key, p.Kind.Key));
        Assert.Equal(2, BannerLines().Count);
        Assert.Single(_emails); // both alarms due in one check go out in one email
    }

    // D1's progression on the weekly window: at 85% the warning, at 97% the stranded alarm - the "again". Two pushes and two
    // emails in all, in that order, and one banner line throughout.
    [Fact]
    public async Task Tick_From80To97OfTheWeek_TheWarningThenTheStrandedAlarm_TwoPushesInOrder()
    {
        Report();
        await Tick();
        Assert.Single(BannerLines());

        _aWeekly = 97;
        Later(30);
        var tick = await Tick();

        Assert.Equal((1, 1), (tick.Alarms!.Closed, tick.StrandedAlarms!.Opened));
        var line = Assert.Single(BannerLines());
        Assert.Equal(LicenseStrandedAlarm.Kind.Key, line.Kind);
        Assert.Equal(new[] { LicenseCannotSwitchAlarm.Kind.Key, LicenseStrandedAlarm.Kind.Key }, _pushed.Select(p => p.Kind.Key).ToArray());
        Assert.Equal(2, _emails.Count);
    }

    // ----- the retired kind -----

    // "not-ready-five-hour" is retired: an alarm of it left open by an older Gateway closes on the first check, and none is
    // raised again - the warning is the one kind for this fault.
    [Fact]
    public async Task Tick_AnOpenAlarmOfTheRetiredFiveHourKind_Closes_AndOnlyTheWarningIsRaised()
    {
        _aFiveHour = 85;
        Report();
        var machineA = _reads.Read(One, _now).Machines.Single(m => m.MachineName == MachineA);
        using (var ctx = _db.CreateContext(One))
        {
            ctx.LicenseAlarms.Add(new LicenseAlarmEntity
            {
                Kind = LicenseReadinessFold.RetiredFiveHourAlarmKind.Key, MachineId = machineA.MachineId, MachineName = MachineA,
                AccountEmail = A, Problem = "An older Gateway's five-hour alarm.", Action = "Nothing.", OpenedAtUtc = T0.AddMinutes(-30),
                TenantId = Assert.IsType<string>(ctx.ActiveTenant),
            });
            ctx.SaveChanges();
        }

        await Tick();

        Assert.Equal(new[] { LicenseCannotSwitchAlarm.Kind.Key }, _alarms.ListOpen(One).Select(a => a.Kind).ToArray());
    }
}
