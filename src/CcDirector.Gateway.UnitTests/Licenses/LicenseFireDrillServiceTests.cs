using System.Text.Json;
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
/// THE DAILY LICENSE FIRE DRILL (mission "Licenses - Always Ready, Loud When Not", step 7, issue 4698). Over the real
/// license store, the real switch service, the real alarm service and the real tenant settings on a throwaway SQLite
/// file, with a fake Director that answers each switch the way the test says, and a clock that is the test's own.
/// The four verdicts the issue asks for - all held; one hole; the switch fails on the way out; the switch fails on the
/// way back - are the first four tests.
/// </summary>
public sealed class LicenseFireDrillServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly TenantId Tenant = new("acct-a");

    private const string Windows = "the-windows-machine";
    private const string Mac = "the-mac";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly LicenseAlarmService _alarms;
    private readonly LicenseFireDrillLog _log;
    private readonly TenantSettingsStore _settings;
    private readonly LicenseFireDrillService _drill;
    private readonly List<LicenseSwitchRequest> _sent = new();
    private readonly List<LicenseAlarmOpened> _pushed = new();
    // How the fake Director answers the n-th switch it is sent (0-based); a switch with no entry is switched and verified.
    private readonly Dictionary<int, Func<LicenseSwitchRequest, LicenseSwitchResult?>> _answers = new();
    private bool _offline;
    private DateTime _now = T0;
    private long _sequence;

    public LicenseFireDrillServiceTests()
    {
        _db = _harness.Open();
        _store = new LicenseStore(_db);
        _snapshots = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
        var switches = new LicenseSwitchService(_store, _snapshots, Send,
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)), () => _now);
        _alarms = new LicenseAlarmService(_db,
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            (_, opened, _) => { lock (_pushed) _pushed.AddRange(opened); return Task.CompletedTask; },
            () => _now);
        _log = new LicenseFireDrillLog(_db);
        _settings = new TenantSettingsStore(_db);
        _drill = new LicenseFireDrillService(_store, _snapshots, switches, _alarms, _log, _settings, () => _now);
    }

    public void Dispose() => _harness.Dispose();

    // ----- the fake Director -----

    private Task<DirectorCommandResult?> Send(string directorId, DirectorCommand command, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<LicenseSwitchRequest>(command.PayloadJson, Web)
            ?? throw new InvalidOperationException("The command carried no switch request.");
        if (_offline)
            return Task.FromResult<DirectorCommandResult?>(null);
        int n;
        lock (_sent) { n = _sent.Count; _sent.Add(request); }
        var answer = _answers.TryGetValue(n, out var make) ? make(request) : Switched(request);
        return Task.FromResult<DirectorCommandResult?>(answer is null
            ? DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "no answer in time")
            : DirectorCommandResult.Success(JsonSerializer.Serialize(answer, Web)));
    }

    private LicenseSwitchResult Switched(LicenseSwitchRequest request, bool verified = true) => new()
    {
        SwitchId = request.SwitchId,
        Outcome = LicenseSwitchOutcomes.Switched,
        Message = "The Director switched.",
        PreviousActiveAccountEmail = request.ExpectedActiveAccountEmail,
        ActiveAccountEmailAfter = request.TargetAccountEmail,
        VerifiedByUsageRead = verified,
        StartedAtUtc = _now,
        FinishedAtUtc = _now,
    };

    private LicenseSwitchResult Failed(LicenseSwitchRequest request) => new()
    {
        SwitchId = request.SwitchId,
        Outcome = LicenseSwitchOutcomes.Failed,
        ReasonCode = LicenseSwitchReasonCodes.SpareNotUsable,
        Message = "The spare for the target answered unauthorized on its usage read.",
        PreviousActiveAccountEmail = request.ExpectedActiveAccountEmail,
        ActiveAccountEmailAfter = request.ExpectedActiveAccountEmail,
        StartedAtUtc = _now,
        FinishedAtUtc = _now,
    };

    // ----- the machines -----

    private static LicenseHolding Fresh(string account, bool active, DateTime readAt)
    {
        var holding = Holding(account, active, readAt: readAt);
        holding.SignInExpiresAtUtc = readAt.AddDays(20);
        return holding;
    }

    private void Reports(string machine, params LicenseHolding[] holdings)
        => _store.ApplyReport(Tenant, "dir-" + machine, ++_sequence, Report(machine, _now, holdings), _now, TimeSpan.Zero);

    /// <summary>The Windows machine on A holding a fresh B, and the Mac on A holding a fresh B.</summary>
    private void BothMachinesReady()
    {
        Reports(Windows, Fresh(A, true, _now), Fresh(B, false, _now));
        Reports(Mac, Fresh(A, true, _now), Fresh(B, false, _now));
    }

    private Guid MachineId(string name)
        => _snapshots.ReadPicture(Tenant, _now, Array.Empty<LicenseExhaustion>()).Overview.Machines.Single(m => m.MachineName == name).MachineId;

    private async Task ChooseWindows()
        => Assert.Null(await _drill.SetDrillMachineAsync(Tenant, MachineId(Windows), CancellationToken.None));

    private List<LicenseAlarmEntity> OpenAlarms()
        => _alarms.ListOpen(Tenant).Where(a => a.Kind == LicenseFireDrillFold.AlarmKind.Key).ToList();

    private LicenseSwitchLogEntity LatestDrillRow() => _log.Newest(Tenant, 1).Single();

    // ----- the four verdicts of the issue -----

    [Fact]
    public async Task RunAsync_EveryMachineHoldsAFreshSignIn_AndTheSwitchGoesOutAndBack_Passes()
    {
        BothMachinesReady();
        await ChooseWindows();

        var result = await _drill.RunAsync(Tenant, CancellationToken.None);

        Assert.True(result!.Passed);
        Assert.Equal(new[] { (A, B), (B, A) }, _sent.Select(s => (s.ExpectedActiveAccountEmail, s.TargetAccountEmail)));
        var row = LatestDrillRow();
        Assert.Equal((LicenseLogStates.Passed, (string?)null, A, B), (row.State, row.ReasonCode, row.FromAccountEmail, row.ToAccountEmail));
        Assert.Equal($"Passed: every machine holds a fresh sign-in for every paid account, verified by a usage read, and {Windows} "
                     + $"switched to {B} and back to {A}, each verified by a usage read.", row.Message);
        Assert.Empty(OpenAlarms());
        // Both switches are rows of the Licenses log, each verified, and the machine ends where it started.
        var switches = _store.ListLog(Tenant, T0.AddDays(-1)).Where(r => r.Kind == LicenseLogKinds.Switch).ToList();
        Assert.Equal(2, switches.Count);
        Assert.All(switches, s => Assert.Equal((LicenseLogStates.Switched, (bool?)true), (s.State, s.VerifiedByUsageRead)));
        Assert.Equal(A, _snapshots.ReadPicture(Tenant, _now, Array.Empty<LicenseExhaustion>()).Overview.Machines
            .Single(m => m.MachineName == Windows).ActiveAccountEmail);
    }

    [Fact]
    public async Task RunAsync_OneMachineHoldsNoSignInForAPaidAccount_FailsOnThatHole_AndRaisesItsAlarm()
    {
        Reports(Windows, Fresh(A, true, _now), Fresh(B, false, _now));
        Reports(Mac, Fresh(A, true, _now));
        await ChooseWindows();

        var result = await _drill.RunAsync(Tenant, CancellationToken.None);

        Assert.False(result!.Passed);
        Assert.Null(result.Failure);                 // the drill machine still switched out and back
        Assert.Equal(2, _sent.Count);
        var hole = Assert.Single(result.Holes);
        Assert.Equal((Mac, B), (hole.Label, hole.AccountEmail));
        var row = LatestDrillRow();
        Assert.Equal((LicenseLogStates.Failed, LicenseFireDrillLog.FailedReason), (row.State, row.ReasonCode));
        Assert.Equal($"Failed: 1 hole. {Windows} switched to {B} and back, each verified by a usage read. {Mac}: holds no sign-in for {B}.", row.Message);
        var alarm = Assert.Single(OpenAlarms());
        Assert.Equal((Mac, B), (alarm.MachineName, alarm.AccountEmail));
        Assert.Equal($"The daily license drill found a hole. {Mac}: holds no sign-in for {B}.", alarm.Problem);
        Assert.Single(_pushed);
    }

    [Fact]
    public async Task RunAsync_TheSwitchFailsOnTheWayOut_NamesThatStep_AndNeverSendsTheWayBack()
    {
        BothMachinesReady();
        await ChooseWindows();
        _answers[0] = Failed;

        var result = await _drill.RunAsync(Tenant, CancellationToken.None);

        Assert.False(result!.Passed);
        Assert.Empty(result.Holes);
        Assert.Equal(LicenseFireDrillFold.StepOut, result.Failure!.Step);
        Assert.Single(_sent);
        Assert.Equal($"Failed: 0 holes. The drill switch failed at {LicenseFireDrillFold.StepOut}: "
                     + "The spare for the target answered unauthorized on its usage read.", LatestDrillRow().Message);
        var alarm = Assert.Single(OpenAlarms());
        Assert.Equal((Windows, ""), (alarm.MachineName, alarm.AccountEmail ?? ""));
        Assert.Contains("the switch out to the spare failed", alarm.Problem);
    }

    [Fact]
    public async Task RunAsync_TheSwitchFailsOnTheWayBack_NamesThatStep()
    {
        BothMachinesReady();
        await ChooseWindows();
        _answers[1] = Failed;

        var result = await _drill.RunAsync(Tenant, CancellationToken.None);

        Assert.False(result!.Passed);
        Assert.Equal(LicenseFireDrillFold.StepBack, result.Failure!.Step);
        Assert.Equal(2, _sent.Count);
        Assert.StartsWith($"Failed: 0 holes. The drill switch failed at {LicenseFireDrillFold.StepBack}:", LatestDrillRow().Message);
        Assert.Contains("the switch back failed", Assert.Single(OpenAlarms()).Problem);
    }

    // ----- what "fresh, verified by a usage read" means -----

    [Fact]
    public async Task RunAsync_ASpareThatHasNotAnsweredAUsageReadRecently_IsAHole_EvenThoughItIsHeld()
    {
        Reports(Windows, Fresh(A, true, _now), Fresh(B, false, _now));
        Reports(Mac, Fresh(A, true, _now), Fresh(B, false, _now.AddHours(-2)));
        await ChooseWindows();

        var result = await _drill.RunAsync(Tenant, CancellationToken.None);

        var hole = Assert.Single(result!.Holes);
        Assert.Equal($"{Mac}: its sign-in for {B} has not answered a usage read recently.", hole.Text);
    }

    [Fact]
    public async Task RunAsync_ASignInWhoseLastUsageReadFailed_IsAHole_WithTheReason()
    {
        Reports(Windows, Fresh(A, true, _now), Fresh(B, false, _now));
        var failing = Fresh(B, false, _now);
        failing.ReadError = LicenseReadErrors.Network;
        Reports(Mac, Fresh(A, true, _now), failing);
        await ChooseWindows();

        var result = await _drill.RunAsync(Tenant, CancellationToken.None);

        Assert.Equal($"{Mac}: its sign-in for {B} failed its last usage read: the machine could not reach the provider.",
            Assert.Single(result!.Holes).Text);
    }

    [Fact]
    public async Task RunAsync_ASwitchThatWasNotVerifiedByAUsageRead_FailsTheDrill()
    {
        BothMachinesReady();
        await ChooseWindows();
        _answers[0] = r => Switched(r, verified: false);

        var result = await _drill.RunAsync(Tenant, CancellationToken.None);

        Assert.Equal(LicenseFireDrillFold.StepOut, result!.Failure!.Step);
        Assert.Equal($"it switched, and the sign-in for {B} did not answer a usage read", result.Failure.Why);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task RunAsync_TheDrillMachineHoldsNoSpareWithRoom_FailsAtChoosingOne_AndSendsNothing()
    {
        Reports(Windows, Fresh(A, true, _now), Holding(B, false, 10, 99, _now));
        Reports(Mac, Fresh(A, true, _now), Fresh(B, false, _now));
        await ChooseWindows();

        var result = await _drill.RunAsync(Tenant, CancellationToken.None);

        Assert.Equal(LicenseFireDrillFold.StepChooseSpare, result!.Failure!.Step);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task RunAsync_TheDirectorIsNotConnected_FailsOnTheWayOut_AndLeavesNoSwitchWaitingToBeSentLater()
    {
        BothMachinesReady();
        await ChooseWindows();
        _offline = true;

        var result = await _drill.RunAsync(Tenant, CancellationToken.None);

        Assert.Equal(LicenseFireDrillFold.StepOut, result!.Failure!.Step);
        var row = Assert.Single(_store.ListLog(Tenant, T0.AddDays(-1)), r => r.Kind == LicenseLogKinds.Switch);
        Assert.Equal((LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorOffline), (row.State, row.ReasonCode));
        Assert.Empty(_store.ListOpenSwitches(Tenant));
    }

    // ----- once a day, and the alarms follow the newest drill -----

    [Fact]
    public async Task RunIfDueAsync_RunsOnceADay_AndARestartDoesNotRunItAgain()
    {
        BothMachinesReady();
        await ChooseWindows();

        Assert.NotNull(await _drill.RunIfDueAsync(Tenant, CancellationToken.None));
        _now = _now.AddHours(23);
        BothMachinesReady();
        var restarted = new LicenseFireDrillService(_store, _snapshots,
            new LicenseSwitchService(_store, _snapshots, Send, (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)), () => _now),
            _alarms, _log, _settings, () => _now);
        Assert.Null(await restarted.RunIfDueAsync(Tenant, CancellationToken.None));
        _now = _now.AddHours(1).AddMinutes(1);
        BothMachinesReady();
        Assert.NotNull(await restarted.RunIfDueAsync(Tenant, CancellationToken.None));

        Assert.Equal(2, _log.Newest(Tenant, 10).Count);
        Assert.Equal(4, _sent.Count);
    }

    [Fact]
    public async Task RunIfDueAsync_ADrillLeftOpenByAGatewayThatStopped_IsFinishedAsInterrupted_AndTheDrillRunsAtOnce()
    {
        BothMachinesReady();
        await ChooseWindows();
        var left = _log.Start(Tenant, MachineId(Windows), Windows, "owner", "dir-" + Windows, _now.AddMinutes(-5));

        var result = await _drill.RunIfDueAsync(Tenant, CancellationToken.None);

        Assert.True(result!.Passed);
        var rows = _log.Newest(Tenant, 10);
        var interrupted = rows.Single(r => r.Id == left.Id);
        Assert.Equal((LicenseLogStates.Failed, LicenseFireDrillLog.InterruptedReason), (interrupted.State, interrupted.ReasonCode));
        Assert.Equal(LicenseLogStates.Passed, rows.First().State);
    }

    [Fact]
    public async Task RunIfDueAsync_NoDrillMachineChosen_RunsNothing_AndTheReportSaysItIsNotSetUp()
    {
        BothMachinesReady();

        Assert.Null(await _drill.RunIfDueAsync(Tenant, CancellationToken.None));

        Assert.Empty(_sent);
        Assert.Empty(_log.Newest(Tenant, 10));
        var line = _drill.ReportLine(Tenant)!;
        Assert.False(line.Passed);
        Assert.Equal("The license fire drill is not set up: no drill machine is chosen, so it does not run.", line.Text);
    }

    [Fact]
    public async Task RunAsync_AHoleFixedBeforeTheNextDrill_ClosesItsAlarm()
    {
        Reports(Windows, Fresh(A, true, _now), Fresh(B, false, _now));
        Reports(Mac, Fresh(A, true, _now));
        await ChooseWindows();
        await _drill.RunAsync(Tenant, CancellationToken.None);
        Assert.Single(OpenAlarms());

        _now = _now.AddDays(1);
        BothMachinesReady();
        var result = await _drill.RunAsync(Tenant, CancellationToken.None);

        Assert.True(result!.Passed);
        Assert.Empty(OpenAlarms());
    }

    [Fact]
    public async Task SetDrillMachineAsync_AMachineNotOnTheLicensesPage_IsRefused_AndClearingClosesTheDrillsAlarms()
    {
        Reports(Windows, Fresh(A, true, _now), Fresh(B, false, _now));
        Reports(Mac, Fresh(A, true, _now));
        Assert.Equal("That machine is not on the Licenses page.", await _drill.SetDrillMachineAsync(Tenant, Guid.NewGuid(), CancellationToken.None));
        await ChooseWindows();
        await _drill.RunAsync(Tenant, CancellationToken.None);
        Assert.Single(OpenAlarms());

        Assert.Null(await _drill.SetDrillMachineAsync(Tenant, null, CancellationToken.None));

        Assert.Null(_drill.DrillMachine(Tenant));
        Assert.Empty(OpenAlarms());
    }

    // ----- the daily report's line -----

    [Fact]
    public async Task ReportLine_AfterThreePassesInARow_SaysPassedAndCountsThem()
    {
        BothMachinesReady();
        await ChooseWindows();
        for (var day = 0; day < 3; day++)
        {
            await _drill.RunAsync(Tenant, CancellationToken.None);
            _now = _now.AddDays(1);
            BothMachinesReady();
        }
        _now = _now.AddDays(-1).AddHours(2);

        var line = _drill.ReportLine(Tenant)!;

        Assert.Equal(MorningAttentionTypes.LicenseFireDrill, line.Type);
        Assert.True(line.Passed);
        Assert.Equal(3, line.PassedInARow);
        Assert.StartsWith($"License fire drill, {_now.AddHours(-2):yyyy-MM-dd HH:mm} UTC, on {Windows}: Passed:", line.Text);
        Assert.EndsWith("3 drills passed in a row.", line.Text);
    }

    [Fact]
    public async Task ReportLine_TheNewestDrillIsOlderThanADay_SaysItHasNotRun()
    {
        BothMachinesReady();
        await ChooseWindows();
        await _drill.RunAsync(Tenant, CancellationToken.None);
        _now = _now.AddHours(27);

        var line = _drill.ReportLine(Tenant)!;

        Assert.False(line.Passed);
        Assert.Equal($"The license fire drill has not run since {T0:yyyy-MM-dd HH:mm} UTC.", line.Text);
    }

    [Fact]
    public void ReportLine_NoMachineReports_IsNoLine()
        => Assert.Null(_drill.ReportLine(Tenant));

    // ----- the words fit -----

    [Fact]
    public void Message_ManyHoles_FitsTheLogRow_AndCountsWhatItCouldNotList()
    {
        var holes = Enumerable.Range(0, 40)
            .Select(i => new LicenseFireDrillHole(Guid.NewGuid(), $"machine-{i}", B, $"machine-{i}: holds no sign-in for {B}."))
            .ToList();
        var result = new LicenseFireDrillResult(Guid.NewGuid(), Windows, holes, null, A, B);

        var message = LicenseFireDrillFold.Message(result);

        Assert.True(message.Length <= LicenseStore.MaxMessageLength);
        Assert.StartsWith("Failed: 40 holes.", message);
        Assert.Contains("more holes; the coverage grid on the Licenses page shows them all.", message);
    }

    [Fact]
    public void Conditions_NamesTooLongForAnAlarm_StayWithinTheSentenceLimit()
    {
        var label = new string('m', 300);
        var holes = new[] { new LicenseFireDrillHole(Guid.NewGuid(), label, new string('a', 300) + "@example.com", $"{label}: holds no sign-in.") };
        var result = new LicenseFireDrillResult(Guid.NewGuid(), label, holes,
            new LicenseFireDrillFailure(LicenseFireDrillFold.StepOut, new string('w', 400)), A, B);

        var conditions = LicenseFireDrillFold.Conditions(result);

        Assert.Equal(2, conditions.Count);
        Assert.All(conditions, c => Assert.True(c.Problem.Length <= LicenseAlarmService.MaxSentenceLength && c.Action.Length <= LicenseAlarmService.MaxSentenceLength));
    }
}
