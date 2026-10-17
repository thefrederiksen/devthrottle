using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// A MACHINE THAT SAYS IT CANNOT SWITCH (License Switching, contract section 10.1). On 16 October 2026 the Gateway sent a
/// switch to a machine whose license tool was missing for a minute, and it failed; nothing had told the Gateway. Now
/// the report says so, and these hold what the Gateway does with it: the machine is shown so, it is not sent a switch
/// and the skip names why, the Licenses log gets one entry when it starts and one when it ends, a report saying the
/// tool is back gets the machine switched on the next check - and a Director too old to say anything is treated
/// exactly as before. The facts go in as usage reports applied to the real store on a throwaway SQLite file and come
/// back through <see cref="LicenseSnapshotSource"/>, so this watches the store, the read model, the snapshot, the fold
/// and the engine together.
/// </summary>
public sealed class LicenseCannotSwitchTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _source;

    private static readonly TenantId Tenant = new("acct-a");
    private const string ToolMissingText = "Cannot switch: the DevThrottle tool is missing from its Python tool environment";

    public LicenseCannotSwitchTests()
    {
        _store = new LicenseStore(_harness.Open());
        _source = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
    }

    public void Dispose() => _harness.Dispose();

    private void Apply(LicenseUsageReport report, long seq, DateTime at)
        => _store.ApplyReport(Tenant, "dir-" + report.MachineName, seq, report, at, TimeSpan.Zero);

    private LicensePicture Picture(DateTime now) => _source.ReadPicture(Tenant, now, Array.Empty<LicenseExhaustion>());

    private LicenseMachineDto MachineDto(string name, DateTime now) =>
        LicenseDisplayFold.Overview(Picture(now)).Machines.Single(m => m.MachineName == name);

    /// <summary>A machine on account A, which is tired (98% on the five-hour window), holding a spare for B, which has room.</summary>
    private static LicenseUsageReport OnTiredA(string machine, DateTime at, bool? canSwitch, string? reason = null)
    {
        var report = Report(machine, at, Holding(A, true, fiveHour: 98, readAt: at), Holding(B, false, fiveHour: 5, sevenDay: 5, readAt: at));
        report.CanSwitch = canSwitch;
        report.CannotSwitchReason = reason;
        return report;
    }

    [Fact]
    public void AMachineThatCannotSwitch_IsShownSo_WithWhy()
    {
        // The status is folded on the Gateway and stamped on the machine, in words the page shows as they are.
        Apply(OnTiredA("m1", T0, canSwitch: false, LicenseCannotSwitchReasons.ToolMissing), 1, T0);

        var machine = MachineDto("m1", T0);

        Assert.Equal(LicenseMachineStatuses.CannotSwitch, machine.Status);
        Assert.Equal(ToolMissingText, machine.StatusText);
        Assert.Equal(LicenseTones.Warning, machine.StatusTone);
    }

    [Fact]
    public void AMachineThatCannotSwitch_IsNotSentOne_AndTheSkipNamesWhy_WhileTheOthersAreSwitched()
    {
        // Two machines on the tired account; one says it cannot switch. The other is switched; this one is left out
        // with the reason, and it takes no part in choosing the target.
        Apply(OnTiredA("m1", T0, canSwitch: false, LicenseCannotSwitchReasons.ToolMissing), 1, T0);
        Apply(OnTiredA("m2", T0, canSwitch: true), 1, T0);

        var decisions = LicenseDecisionEngine.Decide(Picture(T0).Snapshot);

        var sent = Assert.Single(decisions.Switches);
        Assert.Equal(("m2", B), (sent.MachineName, sent.TargetAccountEmail));
        var skipped = Assert.Single(decisions.Skipped);
        Assert.Equal("m1", skipped.MachineName);
        Assert.Equal("it reports it cannot run a switch: the DevThrottle tool is missing from its Python tool environment", skipped.Why);
        Assert.Empty(decisions.Emails);
    }

    [Fact]
    public void TheOnlyMachineCannotSwitch_NothingIsSent_AndNoRoomIsNotClaimed()
    {
        // B has room, so "no account had room" would be a false email. The machine is simply skipped.
        Apply(OnTiredA("m1", T0, canSwitch: false, LicenseCannotSwitchReasons.ToolMissing), 1, T0);

        var decisions = LicenseDecisionEngine.Decide(Picture(T0).Snapshot);

        Assert.Empty(decisions.Switches);
        Assert.Empty(decisions.Emails);
        Assert.Empty(decisions.OpenedExhaustions);
        Assert.Single(decisions.Skipped);
    }

    [Fact]
    public void TheLog_GetsOneEntryWhenItStarts_AndOneWhenItEnds_NeverOnePerReport()
    {
        Apply(OnTiredA("m1", T0, canSwitch: true), 1, T0);
        Apply(OnTiredA("m1", T0.AddMinutes(1), canSwitch: false, LicenseCannotSwitchReasons.ToolMissing), 2, T0.AddMinutes(1));
        Apply(OnTiredA("m1", T0.AddMinutes(3), canSwitch: false, LicenseCannotSwitchReasons.ToolMissing), 3, T0.AddMinutes(3));
        Apply(OnTiredA("m1", T0.AddMinutes(5), canSwitch: false, LicenseCannotSwitchReasons.ToolMissing), 4, T0.AddMinutes(5));
        Apply(OnTiredA("m1", T0.AddMinutes(6), canSwitch: true), 5, T0.AddMinutes(6));
        Apply(OnTiredA("m1", T0.AddMinutes(8), canSwitch: true), 6, T0.AddMinutes(8));

        var rows = _store.ListLog(Tenant, T0.AddDays(-1)).OrderBy(r => r.CreatedAtUtc).ToList();
        var entries = LicenseDisplayFold.LogEntries(rows, new[] { ("m1", "owner") });

        Assert.Equal(new[] { LicenseLogKinds.CannotSwitch, LicenseLogKinds.CanSwitchAgain }, rows.Select(r => r.Kind));
        Assert.Equal(new[] { T0.AddMinutes(1), T0.AddMinutes(6) }, rows.Select(r => DateTime.SpecifyKind(r.CreatedAtUtc, DateTimeKind.Utc)));
        Assert.Equal(new[] { "Cannot switch", "Can switch again" }, entries.Select(e => e.KindText));
        Assert.Equal("m1 cannot be switched: the DevThrottle tool is missing from its Python tool environment.", entries[0].Summary);
        Assert.Equal("The DevThrottle tool is missing from the machine's Python tool environment.", entries[0].ReasonText);
        Assert.Equal("m1 can be switched again.", entries[1].Summary);
    }

    [Fact]
    public void AReportSayingTheToolIsBack_GetsTheMachineSwitchedOnTheNextCheck()
    {
        // Left behind while it could not switch; when its next report says it can, it is a machine on a tired account
        // like any other and the next minute check sends it its switch. No second retry mechanism is involved.
        Apply(OnTiredA("m1", T0, canSwitch: false, LicenseCannotSwitchReasons.ToolMissing), 1, T0);
        Assert.Empty(LicenseDecisionEngine.Decide(Picture(T0).Snapshot).Switches);

        var back = T0.AddMinutes(1);
        Apply(OnTiredA("m1", back, canSwitch: true), 2, back);
        var decisions = LicenseDecisionEngine.Decide(Picture(back).Snapshot);

        var sent = Assert.Single(decisions.Switches);
        Assert.Equal(("m1", A, B), (sent.MachineName, sent.FromAccountEmail, sent.TargetAccountEmail));
        Assert.Equal(LicenseMachineStatuses.Reporting, MachineDto("m1", back).Status);
    }

    [Fact]
    public void ADirectorThatSendsNoField_BehavesExactlyAsBefore()
    {
        // A Director older than the field sends CanSwitch null: "this Director does not say". It is shown as reporting,
        // it is switched, and nothing is written to the log about it.
        Apply(OnTiredA("m1", T0, canSwitch: null), 1, T0);

        Assert.Equal((LicenseMachineStatuses.Reporting, "Reporting"), (MachineDto("m1", T0).Status, MachineDto("m1", T0).StatusText));
        Assert.Equal("m1", Assert.Single(LicenseDecisionEngine.Decide(Picture(T0).Snapshot).Switches).MachineName);
        Assert.Empty(_store.ListLog(Tenant, T0.AddDays(-1)));
    }

    [Fact]
    public void ADirectorThatStopsSaying_IsTreatedAsBefore_ButTheLogDoesNotClaimItCanSwitchAgain()
    {
        // A machine said it cannot switch, then an older Director took over and says nothing. The machine goes back to
        // today's rule (it is switched), but no "can switch again" entry is written: nothing said it can.
        Apply(OnTiredA("m1", T0, canSwitch: false, LicenseCannotSwitchReasons.ToolMissing), 1, T0);
        var later = T0.AddMinutes(1);
        Apply(OnTiredA("m1", later, canSwitch: null), 2, later);

        Assert.Single(LicenseDecisionEngine.Decide(Picture(later).Snapshot).Switches);
        Assert.Equal(new[] { LicenseLogKinds.CannotSwitch }, _store.ListLog(Tenant, T0.AddDays(-1)).Select(r => r.Kind));
    }

    [Theory]
    [InlineData("a-word-from-a-newer-director")]
    [InlineData(null)]
    public void AReasonThisGatewayDoesNotKnow_IsStoredAsNotKnown_AndTheMachineIsStillNotSentASwitch(string? word)
    {
        // The word is never stored as given - it could be anything - but that the machine cannot switch is kept.
        Apply(OnTiredA("m1", T0, canSwitch: false, word), 1, T0);

        var machine = Assert.Single(Picture(T0).Snapshot.Machines);
        Assert.Equal(LicenseStore.CannotSwitchReasonNotKnown, machine.CannotSwitchReason);
        Assert.Equal("Cannot switch: its Director says it cannot run a switch, for a reason this Gateway does not know",
            MachineDto("m1", T0).StatusText);
        Assert.Empty(LicenseDecisionEngine.Decide(Picture(T0).Snapshot).Switches);

        // The log row's reason is a sentence too, never the stored code word (review of #4115, observation 2).
        var row = Assert.Single(_store.ListLog(Tenant, T0.AddDays(-1)));
        Assert.Equal("The machine's Director says it cannot run a switch, for a reason this Gateway does not know.",
            LicenseDisplayFold.ReasonText(row.ReasonCode));
    }
}
