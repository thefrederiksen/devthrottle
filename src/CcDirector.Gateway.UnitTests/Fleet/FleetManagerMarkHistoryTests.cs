using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// The history of which sessions an account has marked as its Fleet Manager (the Fleet Manager mission, step 3),
/// against a real database file: only ever added to, one row per session, partitioned by account.
/// </summary>
public sealed class FleetManagerMarkHistoryTests : IDisposable
{
    private static readonly TenantId TenantA = new("acct-marks-a");
    private static readonly TenantId TenantB = new("acct-marks-b");
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
    private const string First = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string Second = "aaaaaaaa-0000-4000-8000-000000000002";

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Record_KeepsEverySessionEverMarked_OldestFirst_AndSurvivesARestart()
    {
        var history = new FleetManagerMarkHistory(_harness.Open());

        history.Record(TenantA, First, Now);
        history.Record(TenantA, Second.ToUpperInvariant(), Now.AddHours(1));
        var again = history.Record(TenantA, First, Now.AddHours(2));

        Assert.Equal(Now, again.FirstMarkedAtUtc);
        Assert.Equal(Now.AddHours(2), again.LastMarkedAtUtc);

        var reopened = new FleetManagerMarkHistory(_harness.Open()).List(TenantA);
        Assert.Equal(new[] { First, Second }, reopened.Select(m => m.SessionId));
        Assert.Empty(reopened.Skip(2));
    }

    [Fact]
    public void List_AnotherAccount_SeesNoneOfIt()
    {
        var history = new FleetManagerMarkHistory(_harness.Open());

        history.Record(TenantA, First, Now);

        Assert.Empty(history.List(TenantB));
    }

    [Fact]
    public void Record_NotASessionId_IsRefused()
    {
        var history = new FleetManagerMarkHistory(_harness.Open());

        Assert.Throws<ArgumentException>(() => history.Record(TenantA, "fleet-manager", Now));
        Assert.Empty(history.List(TenantA));
    }
}
