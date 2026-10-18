using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// FORGET THIS MACHINE (License Switching phase 6, Track B, the Tech Lead's decision on finding 3 of the review of
/// pull request 4172). A machine that is gone or renamed stops reporting and would stay on the page, with its open
/// problems, for ever; the owner forgets it. The handler runs over the real store on a throwaway SQLite file, and the
/// page is read back through the real overview route, so the button and the route are held to the same verdict.
/// </summary>
public sealed class LicenseForgetMachineTests : IDisposable
{
    private static readonly TenantId TenantOne = new("acct-one");
    private static readonly TenantId TenantTwo = new("acct-two");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly LicenseSignInLog _signIns;
    private readonly LicenseUsageIngest _ingest;
    private DateTime _now = T0;
    private long _sequence;

    // Past the dead limit: the machine's last report is too old, so it is "Not reporting".
    private static readonly DateTime Silent = T0 + LicenseStore.SilentAfter + TimeSpan.FromMinutes(1);

    public LicenseForgetMachineTests()
    {
        _db = _harness.Open();
        _store = new LicenseStore(_db);
        _snapshots = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
        _signIns = new LicenseSignInLog(_db);
        _ingest = new LicenseUsageIngest(_store);
        _ingest.RegisterConnection(TenantOne, "dir-1", "conn-1");
    }

    public void Dispose() => _harness.Dispose();

    // ---- plumbing ----

    private static DefaultHttpContext Context()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static (int Status, JsonElement Json) Read(DefaultHttpContext ctx, IResult result)
    {
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        var text = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        return (ctx.Response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private LicenseApproverStore Approvers() => new(new CcDirector.Gateway.Settings.TenantSettingsStore(_db));

    private LicenseReadinessService Readiness() => LicenseReadinessTestKit.ForReading(_db, _store, (_, _) => false, () => _now);

    private JsonElement Machines(TenantId tenant)
    {
        var ctx = Context();
        var (status, json) = Read(ctx, LicenseEndpoints.Overview(ctx, _ => tenant, _snapshots, () => _now, Approvers(), _signIns, (_, _) => false, Readiness()));
        Assert.Equal(200, status);
        return json.GetProperty("machines");
    }

    private (int Status, JsonElement Json) Forget(TenantId tenant, string? machineId, IResult? notTheOwner = null)
    {
        var ctx = Context();
        return Read(ctx, LicenseEndpoints.ForgetMachine(ctx, _ => notTheOwner, _ => tenant, _snapshots, () => _now, Approvers(), _signIns,
            (_, _) => false, Readiness(), _store, new LicenseForgetMachineRequest { MachineId = machineId }));
    }

    private Guid Apply(TenantId tenant, LicenseUsageReport report, DateTime? at = null)
        => _store.ApplyReport(tenant, "dir-1", ++_sequence, report, at ?? T0, TimeSpan.Zero).MachineId;

    private static LicenseUsageReport Good(string machine = "machine-a") => Report(machine, Holding(A, true, 40, 30), Holding(B, false, 5, 5));

    private LicenseMachineView View(TenantId tenant, Guid id) => new LicenseReadModel(_store).Read(tenant, _now).Machines.Single(m => m.MachineId == id);

    // ---- the tests ----

    [Fact]
    public void ASilentMachine_IsOfferedForgetting_AndForgettingItTakesItOffThePage_EndsItsSilence_AndKeepsTheLog()
    {
        var id = Apply(TenantOne, Good());
        _now = Silent;
        var silence = _store.OpenSilence(TenantOne, View(TenantOne, id), _now)!;

        var offered = Machines(TenantOne)[0];
        Assert.True(offered.GetProperty("canForget").GetBoolean());
        Assert.Equal("Take machine-a off this page, with what it holds, and end its open problems and their alarms. "
                     + "For a machine that is gone or renamed; if it reports again, it comes back.", offered.GetProperty("forgetText").GetString());

        var (status, json) = Forget(TenantOne, id.ToString("N"));

        Assert.Equal(200, status);
        Assert.Equal("machine-a was forgotten: it is off the Licenses page, with what it held, and its open problems are over. "
                     + "If it reports again, it comes back.", json.GetProperty("text").GetString());
        var entry = json.GetProperty("entry");
        Assert.Equal(("forgotten", "Machine forgotten", id.ToString("N"), "machine-a"),
            (entry.GetProperty("kind").GetString(), entry.GetProperty("kindText").GetString(), entry.GetProperty("machineId").GetString(), entry.GetProperty("label").GetString()));
        Assert.Equal(0, Machines(TenantOne).GetArrayLength());
        Assert.Empty(_store.ListOpenMachineHealth(TenantOne));
        var log = _store.ListLog(TenantOne, T0.AddDays(-1));
        Assert.Equal(LicenseLogStates.Ended, Assert.Single(log, r => r.Id == silence.Id).State);
        Assert.Single(log, r => r.Kind == LicenseLogKinds.Forgotten);
        using var ctx = _db.CreateContext(TenantOne);
        Assert.Empty(ctx.LicenseHoldings.Where(h => h.MachineId == id));
    }

    [Fact]
    public void AForgottenMachineThatReportsAgain_ComesBack()
    {
        var id = Apply(TenantOne, Good());
        _now = Silent;
        Assert.Equal(200, Forget(TenantOne, id.ToString("N")).Status);

        Apply(TenantOne, Good(), _now);

        var back = Assert.Single(Machines(TenantOne).EnumerateArray());
        Assert.Equal(("machine-a", "reporting"), (back.GetProperty("machineName").GetString(), back.GetProperty("status").GetString()));
        Assert.False(back.GetProperty("canForget").GetBoolean());
    }

    [Fact]
    public void AReportingMachine_IsNotOffered_AndTheRouteRefusesItWithTheSameSentence()
    {
        var id = Apply(TenantOne, Good());

        var machine = Machines(TenantOne)[0];
        Assert.False(machine.GetProperty("canForget").GetBoolean());
        const string why = "machine-a is reporting, so it is not forgotten: only a machine that has stopped reporting, or whose reports are refused, can be.";
        Assert.Equal(why, machine.GetProperty("forgetText").GetString());

        var (status, json) = Forget(TenantOne, id.ToString("N"));

        Assert.Equal(409, status);
        Assert.Equal((LicenseForgetRefusals.NotOffered, why), (json.GetProperty("code").GetString(), json.GetProperty("error").GetString()));
        Assert.Equal(1, Machines(TenantOne).GetArrayLength());
    }

    private LicenseSwitchService Switches() => new(_store, _snapshots,
        (_, _, _) => Task.FromResult<DirectorCommandResult?>(null),
        (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
        () => _now);

    [Fact]
    public async Task AMachineThatDiedWithASwitchUnanswered_IsOffered_AndForgettingEndsTheSwitchAsFailed()
    {
        // The review of pull request 4183, finding 1: the switch was sent, no answer came, and the machine never
        // reported again. The minute check settles such a switch only from a later report, so it stays open for ever.
        var id = Apply(TenantOne, Good());
        var row = _store.CreatePendingSwitch(TenantOne, id, A, B, LicenseSwitchReasons.Manual, T0)!;
        Assert.True(_store.TryMark(TenantOne, row.SwitchId!, LicenseLogStates.Unknown, null, "Sent; no answer came back.", T0));
        var switches = Switches();
        for (var day = 1; day <= 3; day++)
        {
            _now = T0.AddDays(day);
            await switches.TickAsync(TenantOne, CancellationToken.None);
        }
        Assert.True(LicenseLogStates.IsOpen(_store.FindSwitch(TenantOne, row.SwitchId!)!.State));

        var machine = Machines(TenantOne)[0];
        Assert.Equal("not-reporting", machine.GetProperty("status").GetString());
        Assert.True(machine.GetProperty("canForget").GetBoolean());
        Assert.Equal(200, Forget(TenantOne, id.ToString("N")).Status);

        var ended = _store.FindSwitch(TenantOne, row.SwitchId!)!;
        Assert.Equal((LicenseLogStates.Failed, LicenseGatewayReasonCodes.MachineForgotten), (ended.State, ended.ReasonCode));
        Assert.Equal("Ended without an answer: machine-a was forgotten before this switch was over.", ended.Message);
        // The log says it in words, never as the code word (the review of pull request 4183, round two, finding 2).
        var entry = Assert.Single(LicenseDisplayFold.LogEntries(new[] { ended }, Array.Empty<(string, string)>()));
        Assert.Equal("The machine was forgotten, so this switch can never be answered.", entry.ReasonText);
        Assert.NotNull(ended.FinishedAtUtc);
        // No open row names the machine row that is gone, so the minute check goes on without a word.
        await switches.TickAsync(TenantOne, CancellationToken.None);
        Assert.Equal(0, Machines(TenantOne).GetArrayLength());
    }

    [Fact]
    public void TheStoreRefusesAMachineThatReportedAgain_AfterThePageWasRead_AndChangesNothing()
    {
        // The review of pull request 4183, round two, finding 1: the route read the page while the machine was silent,
        // and it reported again before the store's save.
        var id = Apply(TenantOne, Good());
        var row = _store.CreatePendingSwitch(TenantOne, id, A, B, LicenseSwitchReasons.Manual, T0)!;
        Assert.True(_store.TryMark(TenantOne, row.SwitchId!, LicenseLogStates.Unknown, null, "Sent; no answer came back.", T0));
        Assert.True(Approvers().Set(TenantOne, A, "machine-a", "owner", "profile-a", T0).Accepted);
        _now = Silent;
        Apply(TenantOne, Good(), _now);

        var result = _store.ForgetMachine(TenantOne, id, "machine-a", _now, new[] { A });

        Assert.Equal((null, LicenseForgetRefusals.NotOffered), (result.Row, result.RefusedBecause));
        Assert.True(LicenseLogStates.IsOpen(_store.FindSwitch(TenantOne, row.SwitchId!)!.State));
        Assert.Equal(1, Machines(TenantOne).GetArrayLength());
        Assert.Single(Approvers().List(TenantOne));
    }

    [Fact]
    public void AMachineThatDiedReportingASwitchInProgress_IsOffered_AndForgotten()
    {
        // The review of pull request 4183, finding 1: its last accepted report said a switch was running, and no
        // later report can ever clear that.
        var report = Good();
        report.SwitchInProgressId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40";
        var id = Apply(TenantOne, report);
        _now = Silent;

        var machine = Machines(TenantOne)[0];
        Assert.Equal(JsonValueKind.Object, machine.GetProperty("switchInProgress").ValueKind);
        Assert.True(machine.GetProperty("canForget").GetBoolean());

        Assert.Equal(200, Forget(TenantOne, id.ToString("N")).Status);
        Assert.Equal(0, Machines(TenantOne).GetArrayLength());
    }

    [Fact]
    public void AMachineThatApprovesSignIns_TakesItsApproversWithIt_AndSaysSo()
    {
        // The review of pull request 4183, finding 3: the approver record named the machine and outlived it.
        var id = Apply(TenantOne, Good());
        Assert.True(Approvers().Set(TenantOne, A, "machine-a", "owner", "profile-a", T0).Accepted);
        Assert.True(Approvers().Set(TenantOne, B, "machine-b", "owner", "profile-b", T0).Accepted);
        _now = Silent;

        var (status, json) = Forget(TenantOne, id.ToString("N"));

        Assert.Equal(200, status);
        Assert.Equal("machine-a was forgotten: it is off the Licenses page, with what it held, and its open problems are over. "
                     + "It approved the sign-ins of account-a@example.com; that approver is removed too. If it reports again, it comes back.",
            json.GetProperty("text").GetString());
        // Only the approver on that machine goes, and it leaves no removal record: a machine that comes back may
        // register it again.
        Assert.Equal(new[] { B }, Approvers().List(TenantOne).Select(a => a.AccountEmail));
        Assert.Empty(Approvers().ListRemovals(TenantOne));
    }

    [Fact]
    public void ASilentMachineWithASignInUnderWay_IsNotOffered_AndTheStoreRefusesItToo()
    {
        var id = Apply(TenantOne, Good());
        _signIns.CreatePending(TenantOne, View(TenantOne, id), C, LicenseSignInReasons.Manual, "Signing machine-a in to a spare.", T0);
        _now = Silent;

        var machine = Machines(TenantOne)[0];
        Assert.False(machine.GetProperty("canForget").GetBoolean());
        Assert.Equal("A sign-in on machine-a has not finished; it can be forgotten once it has.", machine.GetProperty("forgetText").GetString());

        var result = _store.ForgetMachine(TenantOne, id, "machine-a", _now);
        Assert.Equal((null, LicenseForgetRefusals.SignInOpen), (result.Row, result.RefusedBecause));
        Assert.Equal(1, Machines(TenantOne).GetArrayLength());
    }

    [Fact]
    public void AMachineWhoseReportsWereNeverAccepted_IsForgottenUnderItsStandInRow_AndItsRefusalEnds()
    {
        Assert.Equal(LicenseIngestOutcome.Malformed,
            _ingest.Ingest(TenantOne, "dir-1", "conn-1", ++_sequence, Report("machine-a", Holding(A, true), Holding(A, false)), _now));
        var standIn = Machines(TenantOne)[0];
        Assert.Equal("report-refused", standIn.GetProperty("status").GetString());
        Assert.True(standIn.GetProperty("canForget").GetBoolean());

        var (status, json) = Forget(TenantOne, standIn.GetProperty("machineId").GetString());

        Assert.Equal(200, status);
        Assert.Equal(standIn.GetProperty("machineId").GetString(), json.GetProperty("entry").GetProperty("machineId").GetString());
        Assert.Equal(0, Machines(TenantOne).GetArrayLength());
        Assert.Empty(_store.ListOpenMachineHealth(TenantOne));
    }

    [Fact]
    public void ARefusedMachineWithARow_IsForgotten_AndItsRefusalEnds()
    {
        Apply(TenantOne, Good());
        Assert.Equal(LicenseIngestOutcome.Malformed,
            _ingest.Ingest(TenantOne, "dir-1", "conn-1", ++_sequence, Report("machine-a", Holding(A, true), Holding(A, false)), _now));
        var machine = Machines(TenantOne)[0];
        Assert.True(machine.GetProperty("canForget").GetBoolean());

        Assert.Equal(200, Forget(TenantOne, machine.GetProperty("machineId").GetString()).Status);

        Assert.Equal(0, Machines(TenantOne).GetArrayLength());
        Assert.Empty(_store.ListOpenMachineHealth(TenantOne));
    }

    private (LicenseMachineHealthAlarm Alarm, LicenseAlarmService Alarms) HealthAlarm()
    {
        var alarms = new LicenseAlarmService(_db,
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            (_, _, _) => Task.CompletedTask,
            () => _now);
        return (new LicenseMachineHealthAlarm(_store, new LicenseReadModel(_store), alarms, () => _now), alarms);
    }

    [Fact]
    public async Task ForgettingASilentMachine_ClosesItsAlarm_OnTheNextRaise()
    {
        // The reason this exists (the review of pull request 4172, finding 3): a machine that is gone kept its
        // "Machine stopped reporting" alarm for ever.
        var (alarm, alarms) = HealthAlarm();
        var id = Apply(TenantOne, Good());
        _now = Silent;
        _store.OpenSilence(TenantOne, View(TenantOne, id), _now);
        await alarm.RaiseAsync(TenantOne, CancellationToken.None);
        Assert.Single(alarms.ListOpen(TenantOne), a => a.Kind == LicenseMachineHealthAlarm.Silent.Key);

        Assert.Equal(200, Forget(TenantOne, id.ToString("N")).Status);
        await alarm.RaiseAsync(TenantOne, CancellationToken.None);

        Assert.Empty(alarms.ListOpen(TenantOne));
    }

    [Fact]
    public async Task ForgettingARefusedMachineThatNeverReportedWell_ClosesItsAlarm_OnTheNextRaise()
    {
        var (alarm, alarms) = HealthAlarm();
        Assert.Equal(LicenseIngestOutcome.Malformed,
            _ingest.Ingest(TenantOne, "dir-1", "conn-1", ++_sequence, Report("machine-a", Holding(A, true), Holding(A, false)), _now));
        await alarm.RaiseAsync(TenantOne, CancellationToken.None);
        Assert.Single(alarms.ListOpen(TenantOne), a => a.Kind == LicenseMachineHealthAlarm.Refused.Key);

        Assert.Equal(200, Forget(TenantOne, Machines(TenantOne)[0].GetProperty("machineId").GetString()).Status);
        await alarm.RaiseAsync(TenantOne, CancellationToken.None);

        Assert.Empty(alarms.ListOpen(TenantOne));
    }

    [Fact]
    public void OneAccount_CannotForgetAnotherAccountsMachine()
    {
        var id = Apply(TenantOne, Good());
        _now = Silent;

        var (status, json) = Forget(TenantTwo, id.ToString("N"));

        Assert.Equal(404, status);
        Assert.Equal(LicenseForgetRefusals.NotFound, json.GetProperty("code").GetString());
        Assert.Equal(1, Machines(TenantOne).GetArrayLength());
        Assert.Null(_store.ForgetMachine(TenantTwo, id, "machine-a", _now).Row);
        Assert.Equal(1, Machines(TenantOne).GetArrayLength());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-id")]
    public void AMachineIdThatIsNotOne_IsRefusedBeforeAnythingIsRead(string? machineId)
    {
        var (status, json) = Forget(TenantOne, machineId);

        Assert.Equal(400, status);
        Assert.Equal("invalid_machine", json.GetProperty("code").GetString());
    }

    [Fact]
    public void ACallerThatIsNotTheOwner_IsRefused_AndNothingChanges()
    {
        var id = Apply(TenantOne, Good());
        _now = Silent;

        var (status, _) = Forget(TenantOne, id.ToString("N"), Results.Json(new { error = "owner_only" }, statusCode: 403));

        Assert.Equal(403, status);
        Assert.Equal(1, Machines(TenantOne).GetArrayLength());
    }
}
