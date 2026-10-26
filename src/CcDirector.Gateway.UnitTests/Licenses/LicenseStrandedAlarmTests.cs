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
/// A STRANDED MACHINE IS LOUD ONCE (mission "Licenses - Loud When Stranded", step 1) - the readiness check raising the
/// stranded alarm over the real license store and the real license alarm on a throwaway SQLite file. The owner email and
/// the push are recorders; the Directors never answer; the clock is the test's own.
///
/// The picture is the 26 October 2026 one in small: machine-a and machine-b are on account A, which is at 100% of its
/// weekly limit and 30% of its five-hour window (so the five-hour readiness alarm stays out of it). Account B has room.
/// machine-b holds a sign-in for B; machine-a does not. What these hold down: one email, one push and one banner line
/// when it opens, naming the machine, the account it is stuck on, the account it needs and the one thing to do; nothing
/// more on the next check; each of the three closes; a Gateway that restarts sends nothing again; the no-room case
/// unchanged; and the #4249 boundary - an account at its limit with every machine ready raises nothing.
/// </summary>
public sealed class LicenseStrandedAlarmTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");

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
    private readonly List<LicenseOwnerEmail> _emails = new();
    private readonly List<LicenseAlarmOpened> _pushed = new();
    private readonly TaskCompletionSource<DirectorCommandResult?> _neverAnswers = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LicenseAlarmService _alarms;
    private DateTime _now = T0;
    private long _sequence;

    // How the machines report: the use of account A, and of account B.
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
        _alarms = NewAlarmService();
        // The Director takes a sign-in command and never answers: the automatic sign-in stays open, and changes nothing.
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
            _machineAOnB ? Holding(B, true, 5, _bWeekly, readAt: _now) : Holding(A, true, 30, _aWeekly, readAt: _now),
        };
        if (_machineAHoldsB && !_machineAOnB)
            a.Add(Holding(B, false, 5, _bWeekly, readAt: _now));
        _store.ApplyReport(One, DirA, ++_sequence, Report(MachineA, _now, a.ToArray()), _now, TimeSpan.Zero);
        _store.ApplyReport(One, DirB, ++_sequence,
            Report(MachineB, _now, Holding(A, true, 30, _aWeekly, readAt: _now), Holding(B, false, 5, _bWeekly, readAt: _now)),
            _now, TimeSpan.Zero);
        _directors.Connect(One, DirA);
        _directors.Connect(One, DirB);
    }

    /// <summary>One readiness check, by a service built afresh over the same database and alarm service.</summary>
    private Task<LicenseReadinessTick> Tick()
        => new LicenseReadinessService(_store, new LicenseSnapshotSource(_store, _reads), _log, _signIns, (_, _) => null,
            _directors.IsConnected, _alarms, () => _now).TickAsync(One, CancellationToken.None);

    private List<LicenseAlarmEntity> Stranded() => _alarms.ListOpen(One).Where(a => a.Kind == LicenseStrandedAlarm.Kind.Key).ToList();

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

    // ----- it opens, once -----

    // The 26 October case: one alarm, for machine-a only, saying what is wrong and the one thing to do.
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
        Assert.Equal("Sign machine-a in to account-b@example.com from the Licenses page; it then switches off account-a@example.com by itself.",
            alarm.Action);
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
    [Fact]
    public async Task Tick_AtTheLimit_EveryMachineReady_RaisesNothing()
    {
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
