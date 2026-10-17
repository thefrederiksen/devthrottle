using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE GATEWAY NEVER GOES SILENT ABOUT A MACHINE (License Switching, phase 6). A Director whose every report is thrown
/// away for what it says is shown on the Licenses page as refused, with the reason in words, and is never sent a switch;
/// a machine that stops reporting says since when, and the Licenses log says it once. One log row for each CHANGE, never
/// one for each report. Everything goes in the way it does in production: reports through the real ingest into the
/// real store on a throwaway SQLite file, the page folded from the real snapshot source, the minute check run by the
/// real switch service on the test's own clock.
/// </summary>
public sealed class LicenseRefusedAndSilentMachineTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-a");
    private static readonly TenantId Other = new("acct-b");
    private static readonly DateTime LogStart = T0.AddDays(-1);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseUsageIngest _ingest;
    private readonly LicenseSnapshotSource _source;
    private readonly LicenseSwitchService _service;
    private readonly List<string> _sentTo = new();
    private DateTime _now = T0;
    private long _sequence;

    public LicenseRefusedAndSilentMachineTests()
    {
        _store = new LicenseStore(_harness.Open());
        _ingest = new LicenseUsageIngest(_store);
        _ingest.RegisterConnection(Tenant, "dir-1", "conn-1");
        _source = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
        _service = new LicenseSwitchService(_store, _source,
            (directorId, _, _) => { lock (_sentTo) _sentTo.Add(directorId); return Task.FromResult<DirectorCommandResult?>(null); },
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            () => _now);
    }

    public void Dispose() => _harness.Dispose();

    // ----- the helpers -----

    private LicenseIngestOutcome Push(LicenseUsageReport? report, DateTime? at = null, string conn = "conn-1", long? seq = null, TenantId? tenant = null)
        => _ingest.Ingest(tenant ?? Tenant, "dir-1", conn, seq ?? ++_sequence, report, at ?? _now);

    private static LicenseUsageReport Good(double fiveHour = 10) => Report("machine-a", Holding(A, true, fiveHour), Holding(B, false));

    private static LicenseUsageReport HeldTwice() => Report("machine-a", Holding(A, true), Holding(A, false));

    private LicensePicture Picture(TenantId? tenant = null) => _source.ReadPicture(tenant ?? Tenant, _now, Array.Empty<LicenseExhaustion>());

    private LicenseOverviewResponse Overview(TenantId? tenant = null) => LicenseDisplayFold.Overview(Picture(tenant));

    private LicenseMachineDto TheMachine() => Assert.Single(Overview().Machines);

    private List<LicenseSwitchLogEntity> Log(string kind, TenantId? tenant = null)
        => _store.ListLog(tenant ?? Tenant, LogStart).Where(r => r.Kind == kind).ToList();

    private Task Tick() => _service.TickAsync(Tenant, CancellationToken.None);

    private const string Refused = "Its report was refused: ";
    private const string NotSwitched = ". It is not switched until a report is accepted.";

    /// <summary>One report for each refusal reason, with the reason it must be refused for in words.</summary>
    public static TheoryData<string, string> EveryRefusal()
    {
        var data = new TheoryData<string, string>();
        foreach (var (code, _, reason) in Cases())
            data.Add(code, reason);
        return data;
    }

    private static IEnumerable<(string Code, Func<LicenseUsageReport?> Report, string Reason)> Cases()
    {
        yield return (LicenseReportRefusalCodes.NoReport, () => null, "it sent no report");
        yield return (LicenseReportRefusalCodes.NoMachineName, () => Report("  ", Holding(A, true)), "it does not name its machine");
        yield return (LicenseReportRefusalCodes.NoOsUser, () => { var r = Good(); r.OsUser = null!; return r; },
            "it does not name its operating system user");
        yield return (LicenseReportRefusalCodes.NameTooLong, () => Report(new string('m', 129), Holding(A, true)),
            "its machine name or user name is longer than 128 characters");
        yield return (LicenseReportRefusalCodes.NoHoldingsList, () => { var r = Good(); r.Holdings = null!; return r; },
            "it carries no list of sign-ins");
        yield return (LicenseReportRefusalCodes.TooManyHoldings,
            () => Report("machine-a", Enumerable.Range(0, LicenseUsageIngest.MaxHoldings + 1).Select(i => Holding($"spare-{i}@example.com", i == 0)).ToArray()),
            $"it lists {LicenseUsageIngest.MaxHoldings + 1} sign-ins, and at most {LicenseUsageIngest.MaxHoldings} are taken");
        yield return (LicenseReportRefusalCodes.EmptyHolding, () => Report("machine-a", Holding(A, true), null!),
            "it lists a sign-in that is empty");
        yield return (LicenseReportRefusalCodes.NoAccountEmail, () => Report("machine-a", Holding(A, true), Holding("  ", false)),
            "it lists a sign-in with no account email, or with one longer than 320 characters");
        yield return (LicenseReportRefusalCodes.AccountHeldTwice, HeldTwice,
            $"it holds {A} twice (once in use and once as a spare)");
        yield return (LicenseReportRefusalCodes.MoreThanOneInUse, () => Report("machine-a", Holding(A, true), Holding(B, true)),
            $"it says 2 sign-ins are in use at once ({A} and {B}), and a machine is on one account at a time");
        yield return (LicenseReportRefusalCodes.SwitchIdShape, () => { var r = Good(); r.SwitchInProgressId = "not-a-switch"; return r; },
            "it reports a switch in progress whose identifier is not one the Gateway could have sent");
        yield return (LicenseReportRefusalCodes.UsageNumber, () => Report("machine-a", Holding(A, true, fiveHour: -1)),
            $"it gives a usage number for {A} that is not between 0 and 1000");
        yield return (LicenseReportRefusalCodes.ReadTimeAhead, () => Report("machine-a", Holding(A, true, readAt: T0.AddMinutes(3))),
            $"it gives a read time for {A} more than two minutes later than the time the report was sent");
        yield return (LicenseReportRefusalCodes.ReadTimeTooOld, () => Report("machine-a", Holding(A, true, readAt: T0.AddDays(-3651))),
            $"it gives a read time for {A} more than ten years before the time the report was sent");
    }

    private static LicenseUsageReport? ReportFor(string code) => Cases().Single(c => c.Code == code).Report();

    // ----- a refused report is a visible machine status -----

    [Theory]
    [MemberData(nameof(EveryRefusal))]
    public void ARefusedReport_ShowsTheMachineRefused_WithItsOwnSentence_AndWritesOneLogRow(string code, string reason)
    {
        Assert.Equal(LicenseIngestOutcome.Accepted, Push(Good()));

        Assert.Equal(LicenseIngestOutcome.Malformed, Push(ReportFor(code)));

        var machine = TheMachine();
        Assert.Equal(LicenseMachineStatuses.ReportRefused, machine.Status);
        Assert.Equal("Report refused", machine.StatusText);
        Assert.Equal(LicenseTones.Bad, machine.StatusTone);
        Assert.True(machine.IsStale);
        Assert.StartsWith(Refused + reason + NotSwitched, machine.StatusDetail);
        Assert.Contains("Its Director is still sending reports: 1 report refused since 2026-10-11 03:12 UTC, the latest less than a minute ago.", machine.StatusDetail);
        Assert.DoesNotContain(code, machine.StatusDetail);
        var row = Assert.Single(Log(LicenseLogKinds.ReportRefused));
        Assert.Equal(code, row.ReasonCode);
        Assert.Equal(Refused + reason + NotSwitched, row.Message);
        Assert.Equal(machine.MachineId, row.MachineId.ToString("N"));
    }

    [Fact]
    public void EveryRefusalReason_IsCovered_AndEachHasASentenceOfItsOwn()
    {
        var cases = Cases().ToList();
        Assert.Equal(LicenseReportRefusalCodes.All.OrderBy(c => c), cases.Select(c => c.Code).OrderBy(c => c));
        Assert.Equal(cases.Count, cases.Select(c => c.Reason).Distinct().Count());
        foreach (var (code, report, reason) in cases)
            Assert.Equal(new LicenseReportRefusal(code, reason), LicenseUsageIngest.Malformed(report()));
    }

    [Fact]
    public void TheSameRefusalAgainAndAgain_WritesNoFurtherRow_AndCountsTheReports()
    {
        Push(Good());
        for (var i = 0; i < 30; i++)
        {
            _now = T0.AddMinutes(2 * i);
            Assert.Equal(LicenseIngestOutcome.Malformed, Push(HeldTwice()));
        }

        var row = Assert.Single(Log(LicenseLogKinds.ReportRefused));
        Assert.Equal(30, row.SendCount);
        Assert.Equal(T0.AddMinutes(58), DateTime.SpecifyKind(row.LastSentAtUtc!.Value, DateTimeKind.Utc));
        Assert.Contains("30 reports refused since 2026-10-11 03:12 UTC, the latest less than a minute ago.", TheMachine().StatusDetail);
    }

    [Fact]
    public void ADifferentRefusal_EndsTheFirstRow_AndWritesExactlyOneMore()
    {
        Push(Good());
        Push(HeldTwice());
        Push(HeldTwice());

        Push(ReportFor(LicenseReportRefusalCodes.MoreThanOneInUse));
        Push(ReportFor(LicenseReportRefusalCodes.MoreThanOneInUse));

        var rows = Log(LicenseLogKinds.ReportRefused).OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.State).ToList();
        Assert.Equal(2, rows.Count);
        var ended = Assert.Single(rows, r => r.State == LicenseLogStates.Ended);
        var open = Assert.Single(rows, r => r.State == LicenseLogStates.Open);
        Assert.Equal(LicenseReportRefusalCodes.AccountHeldTwice, ended.ReasonCode);
        Assert.Equal(LicenseReportRefusalCodes.MoreThanOneInUse, open.ReasonCode);
        Assert.Equal(2, open.SendCount);
        Assert.Contains("are in use at once", TheMachine().StatusDetail);
    }

    [Fact]
    public void ARefusedMachine_IsNotSentASwitch_AndTheSkipSaysWhy()
    {
        // The account in use is at its limit and the machine holds a spare with room: it would be switched now.
        Push(Good(fiveHour: 99));
        Assert.Single(LicenseDecisionEngine.Decide(Picture().Snapshot).Switches);

        Push(HeldTwice());

        var decisions = LicenseDecisionEngine.Decide(Picture().Snapshot);
        Assert.Empty(decisions.Switches);
        var skipped = Assert.Single(decisions.Skipped);
        Assert.Equal(LicenseDecisionEngine.ReportsRefusedWhy, skipped.Why);
        var account = Overview().Accounts.Single(a => a.AccountEmail == A);
        Assert.False(account.CanSwitchNow);
        Assert.Contains("machine-a is not switched: its reports are being refused, so what it holds now is not known.", account.SwitchNowText);
    }

    [Fact]
    public async Task TheMinuteCheck_SendsARefusedMachineNoSwitch()
    {
        Push(Good(fiveHour: 99));
        Push(HeldTwice());

        await Tick();

        Assert.Empty(_sentTo);
        Assert.Empty(Log(LicenseLogKinds.Switch));
    }

    [Fact]
    public void AnAcceptedReportAfterRefusals_ClearsTheStatus_AndWritesOneAcceptedAgainRow()
    {
        Push(Good());
        Push(HeldTwice());
        Push(HeldTwice());
        _now = T0.AddMinutes(4);

        Assert.Equal(LicenseIngestOutcome.Accepted, Push(Good()));
        Assert.Equal(LicenseIngestOutcome.Accepted, Push(Good()));

        var machine = TheMachine();
        Assert.Equal(LicenseMachineStatuses.Reporting, machine.Status);
        Assert.Null(machine.StatusDetail);
        Assert.False(machine.IsStale);
        Assert.Equal(LicenseLogStates.Ended, Assert.Single(Log(LicenseLogKinds.ReportRefused)).State);
        var accepted = Assert.Single(Log(LicenseLogKinds.ReportAccepted));
        Assert.Equal("Its reports are accepted again, after 2 reports refused since 2026-10-11 03:12 UTC.", accepted.Message);
        Assert.Equal("Reports accepted", LicenseDisplayFold.LogEntry(accepted).KindText);
    }

    [Fact]
    public void AMachineNeverAccepted_ButRefused_StillAppearsOnThePage()
    {
        Assert.Equal(LicenseIngestOutcome.Malformed, Push(HeldTwice()));

        var overview = Overview();
        Assert.False(overview.IsEmpty);
        var machine = Assert.Single(overview.Machines);
        Assert.Equal("machine-a", machine.Label);
        Assert.Equal(LicenseMachineStatuses.ReportRefused, machine.Status);
        Assert.StartsWith(Refused + $"it holds {A} twice (once in use and once as a spare)" + NotSwitched, machine.StatusDetail);
        Assert.Null(machine.LastReportAtUtc);
        Assert.Equal("No report from it has been accepted yet", machine.LastReportText);
        Assert.Null(machine.Active);
        Assert.Empty(machine.Spares);
        Assert.Equal(LicenseDisplayFold.RefusedMachineId("dir-1"), machine.MachineId);

        // The sign-in verdicts are filled in over the same overview the route answers with, and must not trip on it.
        LicenseSignInFold.Apply(overview, Picture().Overview.Machines, Array.Empty<LicenseApprover>(), Array.Empty<LicenseSwitchLogEntity>(), _ => true);
        Assert.False(machine.CanAddSpare);
        Assert.Equal("A spare can be added to machine-a once a report from it is accepted.", machine.AddSpareText);
    }

    [Fact]
    public void AMachineNeverAccepted_WhoseReportNamesNoMachine_IsNamedByItsDirector()
    {
        Push(ReportFor(LicenseReportRefusalCodes.NoMachineName));

        var machine = TheMachine();
        Assert.Equal("The machine of Director dir-1", machine.Label);
        Assert.StartsWith(Refused + "it does not name its machine" + NotSwitched, machine.StatusDetail);
    }

    [Fact]
    public void AMachineNeverAccepted_ThenAccepted_IsOneMachine_AndSaysItsReportsAreAccepted()
    {
        Push(HeldTwice());

        Push(Good());

        var machine = TheMachine();
        Assert.Equal(LicenseMachineStatuses.Reporting, machine.Status);
        Assert.Single(Log(LicenseLogKinds.ReportAccepted));
    }

    // ----- connection bookkeeping is not a problem with the machine -----

    [Fact]
    public void ConnectionBookkeepingDrops_ChangeNothingOnThePage()
    {
        Push(Good(), seq: 5);

        // A report from a connection that is not the owner, and one with a sequence already sent - each of them a
        // report that would be refused for what it says, had it been judged.
        Assert.Equal(LicenseIngestOutcome.NotActiveConnection, Push(HeldTwice(), conn: "conn-superseded"));
        Assert.Equal(LicenseIngestOutcome.StaleSequence, Push(HeldTwice(), seq: 5));
        Assert.Equal(LicenseIngestOutcome.StaleSequence, Push(Good(), seq: 4));

        var machine = TheMachine();
        Assert.Equal(LicenseMachineStatuses.Reporting, machine.Status);
        Assert.Null(machine.StatusDetail);
        Assert.Empty(Log(LicenseLogKinds.ReportRefused));
        Assert.Empty(_store.ListOpenMachineHealth(Tenant));
    }

    // ----- not reporting since -----

    [Theory]
    [InlineData(6 * 60, LicenseMachineStatuses.Reporting)]          // the stale limit itself is still reporting
    [InlineData(6 * 60 + 1, LicenseMachineStatuses.NotReporting)]
    public void AMachineSilentPastTheStaleLimit_SaysSinceWhen(int secondsSilent, string expected)
    {
        Push(Good());
        _now = T0.AddSeconds(secondsSilent);

        var machine = TheMachine();

        Assert.Equal(expected, machine.Status);
        if (expected == LicenseMachineStatuses.NotReporting)
        {
            Assert.Equal("Not reporting since 2026-10-11 03:12 UTC", machine.StatusText);
            Assert.Equal("No report has come from it for 6 minutes. It is not switched until it reports again.", machine.StatusDetail);
            Assert.Equal(T0, machine.LastReportAtUtc);
        }
    }

    [Fact]
    public async Task AMachineSilentPastTheDeadLimit_WritesOneRow_AndAReportEndsIt_WithOneMore()
    {
        Push(Good());

        _now = T0 + LicenseStore.SilentAfter;
        await Tick();
        Assert.Empty(Log(LicenseLogKinds.MachineSilent));                // the limit itself is not past it

        _now = T0 + LicenseStore.SilentAfter + TimeSpan.FromSeconds(1);
        await Tick();
        _now = _now.AddMinutes(1);
        await Tick();
        _now = _now.AddMinutes(30);
        await Tick();
        var silent = Assert.Single(Log(LicenseLogKinds.MachineSilent));
        Assert.Equal(LicenseLogStates.Open, silent.State);
        Assert.Equal("No report has come from it since 2026-10-11 03:12 UTC, for more than 10 minutes: its Director may have stopped. "
                     + "It is not switched until it reports again.", silent.Message);
        Assert.Equal("Not reporting", LicenseDisplayFold.LogEntry(silent).KindText);
        Assert.True(LicenseDisplayFold.LogEntry(silent).IsOpen);

        // The report clears the status at once; the next minute check ends the row and says so, once.
        Push(Good());
        Assert.Equal(LicenseMachineStatuses.Reporting, TheMachine().Status);
        await Tick();
        await Tick();

        Assert.Equal(LicenseLogStates.Ended, Assert.Single(Log(LicenseLogKinds.MachineSilent)).State);
        var again = Assert.Single(Log(LicenseLogKinds.ReportingAgain));
        Assert.Equal("It is reporting again: a report was accepted at 2026-10-11 03:53 UTC, 41 minutes after the one before it.", again.Message);
        Assert.Empty(_store.ListOpenMachineHealth(Tenant));
    }

    [Fact]
    public async Task ARefusedMachine_IsNotAlsoCalledSilent()
    {
        Push(Good());
        _now = T0.AddMinutes(20);
        Push(HeldTwice());

        await Tick();

        Assert.Empty(Log(LicenseLogKinds.MachineSilent));
        Assert.Equal(LicenseMachineStatuses.ReportRefused, TheMachine().Status);
    }

    // ----- tenant isolation -----

    [Fact]
    public void OneAccountsRefusedMachine_NeverAppearsOnAnotherAccountsPage()
    {
        _ingest.RegisterConnection(Other, "dir-1", "conn-other");
        Push(HeldTwice());                                                // never accepted, in this account
        Assert.Single(Overview().Machines);

        var other = Overview(Other);

        Assert.True(other.IsEmpty);
        Assert.Empty(other.Machines);
        Assert.Empty(_store.ListOpenMachineHealth(Other));
        Assert.Empty(Log(LicenseLogKinds.ReportRefused, Other));

        // And a report the other account's own Director sends is judged on its own: the refusal in this account is
        // not ended by it, and it is not refused.
        Assert.Equal(LicenseIngestOutcome.Accepted, _ingest.Ingest(Other, "dir-1", "conn-other", 1, Good(), _now));
        Assert.Equal(LicenseMachineStatuses.ReportRefused, TheMachine().Status);
        Assert.Equal(LicenseMachineStatuses.Reporting, Assert.Single(Overview(Other).Machines).Status);
        Assert.Empty(Log(LicenseLogKinds.ReportAccepted, Other));
    }
}
