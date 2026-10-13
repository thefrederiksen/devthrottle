using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// The machine's active account taken from a switch answer's read-back (contract section 3.5), and never over newer
/// facts. Newer is judged on the Gateway's own clock only: an answer to a command just sent is taken unless the machine
/// has reported since the Gateway sent it, and a REPLAYED answer - a stored answer to an earlier send - is refused
/// once the machine has reported after the switch row was written. The Director's clock decides nothing.
/// </summary>
public sealed class LicenseSwitchReadBackTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId Tenant = new("acct-a");

    private static LicenseSwitchResult Answer(DateTime finishedAt, bool replayed = false) => new()
    {
        SwitchId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40",
        Outcome = LicenseSwitchOutcomes.Switched,
        PreviousActiveAccountEmail = A,
        ActiveAccountEmailAfter = B,
        StartedAtUtc = finishedAt.AddSeconds(-2),
        FinishedAtUtc = finishedAt,
        Replayed = replayed,
    };

    // A machine on A holding a spare for B, whose last report the Gateway received at T0.
    private (LicenseStore Store, Guid MachineId) MachineOnA(TimeSpan? readAtShift = null)
    {
        var store = new LicenseStore(_harness.Open());
        var applied = store.ApplyReport(Tenant, "dir-1", 1, Report("the-windows-machine", Holding(A, true), Holding(B, false)),
            T0, readAtShift ?? TimeSpan.Zero);
        return (store, applied.MachineId);
    }

    private static (string? Machine, string? ActiveHolding) Active(LicenseStore store)
    {
        var machine = Assert.Single(new LicenseReadModel(store).Read(Tenant, T0).Machines);
        return (machine.ActiveAccountEmail, Assert.Single(machine.Holdings, h => h.IsActive).AccountEmail);
    }

    [Fact]
    public void ApplySwitchReadBack_NoReportSinceTheCommandWasSent_TakesTheAccountOnMachineAndHoldings()
    {
        // The machine last reported at T0; the Gateway sent the command 20 seconds later.
        var (store, machineId) = MachineOnA();

        var taken = store.ApplySwitchReadBack(Tenant, machineId, Answer(T0.AddSeconds(30)), sentAtUtc: T0.AddSeconds(20));

        Assert.True(taken);
        Assert.Equal((B, B), Active(store));
    }

    [Fact]
    public void ApplySwitchReadBack_TheMachineReportedSinceTheCommandWasSent_IsRefused()
    {
        // The command went out 30 seconds before the report the Gateway holds: that report was written after the
        // switch, so it is the newer fact, whatever the answer says.
        var (store, machineId) = MachineOnA();

        var taken = store.ApplySwitchReadBack(Tenant, machineId, Answer(T0.AddSeconds(30)), sentAtUtc: T0.AddSeconds(-30));

        Assert.False(taken);
        Assert.Equal((A, A), Active(store));
    }

    [Theory]
    [InlineData(-90)] // the Director's clock is 90 seconds behind: its answer is stamped before the machine's last report
    [InlineData(90)]  // and 90 seconds ahead
    public void ApplySwitchReadBack_AFreshAnswer_IsJudgedByTheGatewaysSendTime_NotTheDirectorsClock(int directorClockSeconds)
    {
        // A skew under two minutes is not measured, so nothing corrects the answer's own times. The machine reported
        // at T0, the Gateway sent the command 20 seconds later, and the switch finished 2 seconds after that.
        var (store, machineId) = MachineOnA();
        var finishedOnTheDirectorsClock = T0.AddSeconds(22 + directorClockSeconds);

        var taken = store.ApplySwitchReadBack(Tenant, machineId, Answer(finishedOnTheDirectorsClock), sentAtUtc: T0.AddSeconds(20));

        Assert.True(taken);
        Assert.Equal((B, B), Active(store));
    }

    [Fact]
    public void ApplySwitchReadBack_ReplayedAnswerWhenALaterReportExists_NeverOverwritesIt()
    {
        // The switch ran at T0 - 1 minute; the machine has reported since (at T0, still on A - for example a person
        // switched it back by hand). A resend after a Gateway restart gets the STORED answer back, Replayed = true.
        var (store, machineId) = MachineOnA();

        var taken = store.ApplySwitchReadBack(Tenant, machineId, Answer(T0.AddMinutes(-1), replayed: true), sentAtUtc: T0.AddMinutes(5));

        Assert.False(taken);
        Assert.Equal((A, A), Active(store));
    }

    // A machine on A whose switch row was written a minute before T0, and which reported again at T0, still on A.
    private (LicenseStore Store, Guid MachineId, string SwitchId) MachineThatReportedAfterItsSwitchRowWasWritten()
    {
        var store = new LicenseStore(_harness.Open());
        var machineId = store.ApplyReport(Tenant, "dir-1", 1, Report("the-windows-machine", Holding(A, true), Holding(B, false)),
            T0.AddMinutes(-2), TimeSpan.Zero).MachineId;
        var row = store.CreatePendingSwitch(Tenant, machineId, A, B, LicenseSwitchReasons.Threshold, T0.AddMinutes(-1))
                  ?? throw new InvalidOperationException("The row was not written.");
        store.ApplyReport(Tenant, "dir-1", 2, Report("the-windows-machine", Holding(A, true), Holding(B, false)), T0, TimeSpan.Zero);
        return (store, machineId, row.SwitchId ?? throw new InvalidOperationException("The row has no switch identifier."));
    }

    [Fact]
    public void ApplySwitchReadBack_ReplayedAnswerFromADirectorWhoseClockIsAhead_IsNotTakenOnceTheMachineHasReportedSinceTheRowWasWritten()
    {
        // The Director's clock runs 90 seconds ahead - under the two minutes at which a skew is measured, so nothing
        // corrects it. The switch ran 30 seconds BEFORE the report the Gateway holds, and is stamped 60 seconds
        // after it. Judged by the Director's clock the stored answer looks newer than the report; it is not.
        var (store, machineId, switchId) = MachineThatReportedAfterItsSwitchRowWasWritten();
        var answer = Answer(T0.AddSeconds(60), replayed: true);
        answer.SwitchId = switchId;

        // Sent after the report: for an answer that is NOT replayed this send time would let it through.
        var taken = store.ApplySwitchReadBack(Tenant, machineId, answer, sentAtUtc: T0.AddSeconds(50));

        Assert.False(taken);
        Assert.Equal((A, A), Active(store));
    }

    [Fact]
    public void ApplySwitchReadBack_ReplayedAnswerWhenTheMachineHasNotReportedSinceTheRowWasWritten_IsTaken()
    {
        var (store, machineId) = MachineOnA();
        var row = store.CreatePendingSwitch(Tenant, machineId, A, B, LicenseSwitchReasons.Threshold, T0.AddSeconds(10))
                  ?? throw new InvalidOperationException("The row was not written.");
        var answer = Answer(T0.AddSeconds(30), replayed: true);
        answer.SwitchId = row.SwitchId ?? throw new InvalidOperationException("The row has no switch identifier.");

        var taken = store.ApplySwitchReadBack(Tenant, machineId, answer, sentAtUtc: T0.AddSeconds(20));

        Assert.True(taken);
        Assert.Equal((B, B), Active(store));
    }

    [Fact]
    public void ApplySwitchReadBack_NoAccountReadBack_TakesNothing()
    {
        var (store, machineId) = MachineOnA();
        var answer = Answer(T0.AddSeconds(30));
        answer.ActiveAccountEmailAfter = "";

        Assert.False(store.ApplySwitchReadBack(Tenant, machineId, answer, sentAtUtc: T0.AddSeconds(20)));
        Assert.Equal(A, Active(store).Machine);
    }
}
