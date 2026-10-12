using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE SNAPSHOT SOURCE over the real store on a throwaway SQLite file (License Switching): the engine's picture is
/// built from accepted reports, the open switch rows and the bars in force - so the minute check, Switch now, the
/// forecast and the Licenses page all judge the same facts. The open "no account has room" events are passed in.
/// </summary>
public sealed class LicenseSnapshotSourceTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _source;

    private static readonly TenantId TenantA = new("acct-a");
    private static readonly TenantId TenantB = new("acct-b");
    private static readonly IReadOnlyList<LicenseExhaustion> NoExhaustions = Array.Empty<LicenseExhaustion>();

    public LicenseSnapshotSourceTests()
    {
        _store = new LicenseStore(_harness.Open());
        _source = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
    }

    public void Dispose() => _harness.Dispose();

    private Guid Apply(TenantId tenant, LicenseUsageReport report, long seq = 1, DateTime? at = null)
        => _store.ApplyReport(tenant, "dir-1", seq, report, at ?? T0, TimeSpan.Zero).MachineId;

    [Fact]
    public void Read_MachinesAndReadings_ComeFromTheLatestReports()
    {
        var expires = T0.AddDays(9);
        var spare = Holding(B, false, 4, 22, readAt: T0.AddMinutes(-8), readError: LicenseReadErrors.RateLimited);
        spare.SignInExpiresAtUtc = expires;
        var active = Holding(A, true, 96, 61);
        active.SignInExpiresAtUtc = T0.AddDays(20);
        Apply(TenantA, Report("the-windows-machine", active, spare));

        var snapshot = _source.Read(TenantA, T0, NoExhaustions);

        Assert.Equal(T0, snapshot.NowUtc);
        var machine = Assert.Single(snapshot.Machines);
        Assert.Equal(("the-windows-machine", "owner", A), (machine.MachineName, machine.OsUser, machine.ActiveAccountEmail));
        Assert.Equal(T0, machine.LastReportReceivedAtUtc);
        Assert.Equal(T0.AddDays(20), machine.ActiveSignInExpiresAtUtc);
        Assert.False(machine.HasOpenSwitch);
        Assert.Null(machine.SwitchInProgressId);
        Assert.Equal(new LicenseSpare(B, LicenseReadErrors.RateLimited, expires), Assert.Single(machine.Spares));

        var a = snapshot.Accounts.Single(r => r.AccountEmail == A);
        Assert.Equal((96d, 61d, true), (a.FiveHour!.Utilization, a.SevenDay!.Utilization, a.ReadWhileActive));
        Assert.Equal(T0, a.ReadAtUtc);
        var b = snapshot.Accounts.Single(r => r.AccountEmail == B);
        Assert.Equal((4d, 22d, false), (b.FiveHour!.Utilization, b.SevenDay!.Utilization, b.ReadWhileActive));
        Assert.Equal(T0.AddMinutes(-8), b.ReadAtUtc);
        Assert.Equal(T0.AddHours(2), b.FiveHour.ResetsAtUtc);
    }

    [Fact]
    public void Read_AnAccountNeverRead_HasNoWindowsAndNoReadTime_SoTheEngineReadsItAsStale()
    {
        var never = Holding(B, false);
        never.FiveHour = null;
        never.SevenDay = null;
        never.ReadAtUtc = null;
        Apply(TenantA, Report("m1", Holding(A, true), never));

        var reading = _source.Read(TenantA, T0, NoExhaustions).Accounts.Single(r => r.AccountEmail == B);

        Assert.Null(reading.FiveHour);
        Assert.Null(reading.SevenDay);
        Assert.Null(reading.ReadAtUtc);
        Assert.True(LicenseDecisionEngine.IsReadingStale(reading, T0));
    }

    [Fact]
    public void Read_AMachineWithAnOpenSwitchRow_HasAnOpenSwitch_UntilTheRowIsSettled()
    {
        var first = Apply(TenantA, Report("m1", Holding(A, true), Holding(B, false)));
        Apply(TenantA, Report("m2", Holding(A, true), Holding(B, false)));
        var row = _store.CreatePendingSwitch(TenantA, first, A, B, LicenseSwitchReasons.Manual, T0)!;

        var open = _source.ReadPicture(TenantA, T0, NoExhaustions);

        Assert.True(open.Snapshot.Machines.Single(m => m.MachineName == "m1").HasOpenSwitch);
        Assert.False(open.Snapshot.Machines.Single(m => m.MachineName == "m2").HasOpenSwitch);
        Assert.Equal(row.SwitchId, Assert.Single(open.OpenSwitches).SwitchId);

        Assert.True(_store.TryMark(TenantA, row.SwitchId!, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorError, "It failed.", T0));
        var settled = _source.ReadPicture(TenantA, T0, NoExhaustions);

        Assert.False(settled.Snapshot.Machines.Single(m => m.MachineName == "m1").HasOpenSwitch);
        Assert.Empty(settled.OpenSwitches);
    }

    [Fact]
    public void Read_ASwitchInProgressTheMachineReports_IsCarried()
    {
        var report = Report("m1", Holding(A, true), Holding(B, false));
        report.SwitchInProgressId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40";
        Apply(TenantA, report);

        Assert.Equal("6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40", _source.Read(TenantA, T0, NoExhaustions).Machines.Single().SwitchInProgressId);
    }

    [Fact]
    public void Read_OnlyTheBarsStillInForce_AreCarried()
    {
        Apply(TenantA, Report("m1", Holding(A, true), Holding(B, false)));
        _store.Bar(TenantA, A, LicenseWindows.FiveHour, T0.AddHours(1), T0.AddHours(-1));
        _store.Bar(TenantA, B, LicenseWindows.SevenDay, T0, T0.AddHours(-1)); // ends exactly now: no longer in force

        var bar = Assert.Single(_source.Read(TenantA, T0, NoExhaustions).Bars);

        Assert.Equal(new LicenseBar(A, LicenseWindowNames.FiveHour, T0.AddHours(1)), bar);
    }

    [Fact]
    public void Read_TheOpenExhaustionsPassedIn_AreTheSnapshots()
    {
        Apply(TenantA, Report("m1", Holding(A, true)));
        var events = new[] { new LicenseExhaustion(A, T0.AddMinutes(-3), T0.AddHours(2)) };

        Assert.Same(events, _source.Read(TenantA, T0, events).OpenExhaustions);
    }

    [Fact]
    public void Read_OneAccount_NeverSeesAnothers()
    {
        var mine = Apply(TenantA, Report("m1", Holding(A, true), Holding(B, false)));
        Apply(TenantB, Report("their-machine", Holding(C, true)));
        _store.CreatePendingSwitch(TenantA, mine, A, B, LicenseSwitchReasons.Manual, T0);
        _store.Bar(TenantA, A, LicenseWindows.FiveHour, T0.AddHours(1), T0);

        var theirs = _source.ReadPicture(TenantB, T0, NoExhaustions);

        Assert.Equal(new[] { C }, theirs.Snapshot.Accounts.Select(a => a.AccountEmail));
        Assert.Equal("their-machine", Assert.Single(theirs.Snapshot.Machines).MachineName);
        Assert.False(theirs.Snapshot.Machines[0].HasOpenSwitch);
        Assert.Empty(theirs.Snapshot.Bars);
        Assert.Empty(theirs.OpenSwitches);
    }

    [Fact]
    public void Read_AnAccountThatNeverReported_IsAnEmptySnapshot()
    {
        var snapshot = _source.Read(TenantA, T0, NoExhaustions);

        Assert.Empty(snapshot.Accounts);
        Assert.Empty(snapshot.Machines);
        Assert.Empty(snapshot.Bars);
    }

    [Fact]
    public void Read_TheEngineDecidesOnIt_SwitchNowPicksTheSpareWithRoom()
    {
        Apply(TenantA, Report("m1", Holding(A, true, 98, 61), Holding(B, false, 4, 22)));

        var verdict = LicenseDecisionEngine.DecideManual(_source.Read(TenantA, T0, NoExhaustions), A, null);

        Assert.True(verdict.Accepted);
        Assert.Equal((A, B, "m1"), (verdict.Switches[0].FromAccountEmail, verdict.Switches[0].TargetAccountEmail, verdict.Switches[0].MachineName));
    }
}
