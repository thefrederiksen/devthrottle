using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE LICENSE STORE over the real EF store on a throwaway SQLite file (License Switching, contract sections 2, 4, 5).
/// What these hold down: a report replaces the machine's holdings exactly; a holding that appears or leaves writes a
/// spare row (but a machine's first report writes none); machine identity ignores case; one account never sees
/// another's; a switch row is settled once and never again; a bar holds until its window resets.
/// </summary>
public sealed class LicenseStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId TenantA = new("acct-a");
    private static readonly TenantId TenantB = new("acct-b");

    private LicenseStore Open() => new(_harness.Open());

    private static LicenseReportApplied Apply(LicenseStore store, TenantId tenant, LicenseUsageReport report, long seq = 1, DateTime? at = null)
        => store.ApplyReport(tenant, "dir-1", seq, report, at ?? T0, TimeSpan.Zero);

    [Fact]
    public void ApplyReport_FirstReport_StoresMachineAccountsAndHoldings_AndLogsNoSpareRows()
    {
        var store = Open();

        var applied = Apply(store, TenantA, Report("the-windows-machine", Holding(A, true, 96), Holding(B, false, 4)));

        Assert.True(applied.FirstReport);
        Assert.Empty(applied.Added);
        var overview = new LicenseReadModel(store).Read(TenantA, T0);
        var machine = Assert.Single(overview.Machines);
        Assert.Equal((A, "dir-1"), (machine.ActiveAccountEmail, machine.DirectorId));
        Assert.Equal(new[] { B }, machine.Spares);
        Assert.Equal(new[] { A, B }, overview.Accounts.Select(a => a.AccountEmail));
        Assert.Equal(96, overview.Accounts[0].Freshest!.FiveHourUtilization);
        Assert.Empty(store.ListLog(TenantA, T0.AddDays(-1)));
    }

    [Fact]
    public void ApplyReport_SpareAddedAndRemoved_WritesOneRowEach()
    {
        var store = Open();
        Apply(store, TenantA, Report("m1", Holding(A, true), Holding(B, false)));

        var applied = Apply(store, TenantA, Report("m1", Holding(A, true), Holding(C, false)), seq: 2, at: T0.AddMinutes(2));

        Assert.Equal(new[] { C }, applied.Added);
        Assert.Equal(new[] { B }, applied.Removed);
        var log = store.ListLog(TenantA, T0);
        Assert.Equal(2, log.Count);
        var added = Assert.Single(log, r => r.Kind == LicenseLogKinds.SpareAdded);
        var removed = Assert.Single(log, r => r.Kind == LicenseLogKinds.SpareRemoved);
        Assert.Equal((C, ""), (added.ToAccountEmail, added.FromAccountEmail));
        Assert.Equal((B, ""), (removed.FromAccountEmail, removed.ToAccountEmail));
        Assert.All(log, r => Assert.Equal(LicenseLogStates.Recorded, r.State));
        Assert.Equal(new[] { C }, new LicenseReadModel(store).Read(TenantA, T0.AddMinutes(2)).Machines[0].Spares);
    }

    [Fact]
    public void ApplyReport_ActiveAndSpareTradePlaces_LogsNothing_AndMovesTheActiveAccount()
    {
        var store = Open();
        Apply(store, TenantA, Report("m1", Holding(A, true), Holding(B, false)));

        var applied = Apply(store, TenantA, Report("m1", Holding(B, true), Holding(A, false)), seq: 2);

        Assert.Empty(applied.Added);
        Assert.Empty(applied.Removed);
        var machine = new LicenseReadModel(store).Read(TenantA, T0).Machines[0];
        Assert.Equal(B, machine.ActiveAccountEmail);
        Assert.Equal(new[] { A }, machine.Spares);
    }

    [Fact]
    public void ApplyReport_MachineNameInAnotherCase_IsTheSameMachine()
    {
        var store = Open();
        Apply(store, TenantA, Report("The-Windows-Machine", Holding(A, true)));

        Apply(store, TenantA, new LicenseUsageReport
        {
            MachineName = "THE-WINDOWS-MACHINE", OsUser = "OWNER", SentAtUtc = T0, Trigger = "interval",
            Holdings = { Holding("Account-A@Example.com", true) },
        }, seq: 2);

        var overview = new LicenseReadModel(store).Read(TenantA, T0);
        Assert.Single(overview.Machines);
        Assert.Equal(A, Assert.Single(overview.Accounts).AccountEmail);
    }

    [Fact]
    public void ApplyReport_OneTenant_IsInvisibleToAnother()
    {
        var store = Open();
        Apply(store, TenantA, Report("m1", Holding(A, true)));
        Apply(store, TenantB, Report("m1", Holding(A, true, 50)));

        var a = new LicenseReadModel(store).Read(TenantA, T0);
        var b = new LicenseReadModel(store).Read(TenantB, T0);

        Assert.Equal(10, Assert.Single(a.Accounts).Freshest!.FiveHourUtilization);
        Assert.Equal(50, Assert.Single(b.Accounts).Freshest!.FiveHourUtilization);
        Assert.Single(a.Machines);
        Assert.Single(b.Machines);
    }

    [Fact]
    public void ApplyReport_ReadAtShift_IsStoredAsTheJudgedReadTime()
    {
        var store = Open();

        store.ApplyReport(TenantA, "dir-1", 1, Report("m1", Holding(A, true, readAt: T0.AddMinutes(-1))), T0, TimeSpan.FromHours(1));

        var reading = new LicenseReadModel(store).Read(TenantA, T0).Accounts[0].Freshest!;
        Assert.Equal(T0.AddMinutes(59), reading.JudgedReadAtUtc);
    }

    [Fact]
    public void ApplyReport_KeepsTheSignInTimes_AndFoldsTheExpiryVerdict()
    {
        var store = Open();
        var active = Holding(A, true);
        active.SignedInAtUtc = T0.AddDays(-28);
        active.SignInExpiresAtUtc = T0.AddDays(2);

        Apply(store, TenantA, Report("m1", active, Holding(B, false)));

        var holdings = new LicenseReadModel(store).Read(TenantA, T0).Machines[0].Holdings;
        Assert.Equal((T0.AddDays(-28), T0.AddDays(2), LicenseSignInExpiry.ExpiresSoon),
            (holdings[0].SignedInAtUtc!.Value, holdings[0].SignInExpiresAtUtc!.Value, holdings[0].ExpiryVerdict));
        Assert.Equal(LicenseSignInExpiry.NotKnown, holdings[1].ExpiryVerdict);
    }

    [Fact]
    public void PendingSwitch_IsSettledOnce_AndAFinalRowNeverChangesAgain()
    {
        var store = Open();
        var machineId = Apply(store, TenantA, Report("m1", Holding(A, true), Holding(B, false))).MachineId;

        var row = store.CreatePendingSwitch(TenantA, machineId, A, B, LicenseSwitchReasons.Threshold, T0)!;
        Assert.Equal(LicenseLogStates.Pending, row.State);
        Assert.Equal(T0 + LicenseContract.SwitchNotAfter, row.NotAfterUtc);
        Assert.Matches(LicenseContract.SwitchIdShape, row.SwitchId!);

        Assert.True(store.RecordSend(TenantA, row.SwitchId!, "dir-1", T0));
        Assert.True(store.TryMark(TenantA, row.SwitchId!, LicenseLogStates.Unknown, null, "No answer within 60 seconds.", T0.AddMinutes(1)));
        var result = new LicenseSwitchResult
        {
            SwitchId = row.SwitchId!, Outcome = LicenseSwitchOutcomes.Switched, PreviousActiveAccountEmail = A,
            ActiveAccountEmailAfter = B, VerifiedByUsageRead = true, StartedAtUtc = T0, FinishedAtUtc = T0.AddSeconds(2),
            ExchangeMilliseconds = 41, Replayed = true,
        };
        Assert.True(store.TrySettle(TenantA, result, T0.AddMinutes(2)));
        Assert.False(store.TryMark(TenantA, row.SwitchId!, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorError, "late", T0.AddMinutes(3)));

        var after = store.FindSwitch(TenantA, row.SwitchId)!;
        Assert.Equal((LicenseLogStates.Switched, B, 41, 1), (after.State, after.ActiveAccountEmailAfter, after.ExchangeMilliseconds, after.SendCount));
        Assert.True(after.Replayed);
        Assert.Empty(store.ListOpenSwitches(TenantA));
        Assert.Null(store.FindSwitch(TenantB, row.SwitchId));
    }

    [Fact]
    public void Bar_HoldsUntilTheWindowResets_AndBarringAgainMovesIt()
    {
        var store = Open();

        store.Bar(TenantA, A, LicenseWindows.FiveHour, T0.AddHours(2), T0);
        store.Bar(TenantA, A, LicenseWindows.FiveHour, T0.AddHours(5), T0.AddHours(1));

        var bar = Assert.Single(store.ListActiveBars(TenantA, T0.AddHours(3)));
        Assert.Equal(T0.AddHours(5), bar.UntilUtc);
        Assert.Empty(store.ListActiveBars(TenantA, T0.AddHours(5)));
        Assert.Empty(store.ListActiveBars(TenantB, T0));
    }
}
