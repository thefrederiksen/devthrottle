using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// The machine's active account taken from a switch answer's read-back (contract section 3.5), and never over newer
/// facts: an answer is taken only when it finished after the machine's last accepted report, judged on the Gateway's
/// clock, and a REPLAYED answer - a stored answer to an earlier send - is refused once a later report exists.
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
    public void ApplySwitchReadBack_AnswerFinishedAfterTheLastReport_TakesTheAccountOnMachineAndHoldings()
    {
        var (store, machineId) = MachineOnA();

        var taken = store.ApplySwitchReadBack(Tenant, machineId, Answer(T0.AddSeconds(30)));

        Assert.True(taken);
        Assert.Equal((B, B), Active(store));
    }

    [Fact]
    public void ApplySwitchReadBack_AnswerOlderThanTheLastReport_IsRefused()
    {
        var (store, machineId) = MachineOnA();

        var taken = store.ApplySwitchReadBack(Tenant, machineId, Answer(T0.AddSeconds(-30)));

        Assert.False(taken);
        Assert.Equal((A, A), Active(store));
    }

    [Fact]
    public void ApplySwitchReadBack_ReplayedAnswerWhenALaterReportExists_NeverOverwritesIt()
    {
        // The switch ran at T0 - 1 minute; the machine has reported since (at T0, still on A - for example a person
        // switched it back by hand). A resend after a Gateway restart gets the STORED answer back, Replayed = true.
        var (store, machineId) = MachineOnA();

        var taken = store.ApplySwitchReadBack(Tenant, machineId, Answer(T0.AddMinutes(-1), replayed: true));

        Assert.False(taken);
        Assert.Equal((A, A), Active(store));
    }

    [Fact]
    public void ApplySwitchReadBack_DirectorClockBehind_JudgesTheFinishOnTheGatewayClock()
    {
        // The Director's clock runs 5 minutes behind: its report was judged with a +5 minute shift. An answer stamped
        // 4 minutes before T0 on its clock finished 1 minute AFTER T0 on the Gateway's, so it is newer than the report.
        var (store, machineId) = MachineOnA(readAtShift: TimeSpan.FromMinutes(5));

        var taken = store.ApplySwitchReadBack(Tenant, machineId, Answer(T0.AddMinutes(-4)));

        Assert.True(taken);
    }

    [Fact]
    public void ApplySwitchReadBack_NoAccountReadBack_TakesNothing()
    {
        var (store, machineId) = MachineOnA();
        var answer = Answer(T0.AddSeconds(30));
        answer.ActiveAccountEmailAfter = "";

        Assert.False(store.ApplySwitchReadBack(Tenant, machineId, answer));
        Assert.Equal(A, Active(store).Machine);
    }
}
