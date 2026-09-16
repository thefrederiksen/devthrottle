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

    /// <summary>
    /// BOUNDED. Marking more sessions than the cap keeps only the most recently marked ones - a session marked
    /// long ago and marked again counts as recent - and another account's history is not pruned by this one's.
    /// </summary>
    [Fact]
    public void Record_BeyondTheCap_KeepsOnlyTheMostRecentlyMarked()
    {
        var history = new FleetManagerMarkHistory(_harness.Open());
        history.Record(TenantB, First, Now);
        var sessions = Enumerable.Range(1, FleetManagerMarkHistory.MaxRememberedPerAccount + 5)
            .Select(i => $"bbbbbbbb-0000-4000-8000-{i:D12}")
            .ToList();
        for (var i = 0; i < sessions.Count; i++)
            history.Record(TenantA, sessions[i], Now.AddMinutes(i));
        // The oldest one still kept is marked again, and then one more new session is marked.
        history.Record(TenantA, sessions[5], Now.AddHours(5));
        const string Newest = "cccccccc-0000-4000-8000-000000000001";
        history.Record(TenantA, Newest, Now.AddHours(6));

        var kept = history.List(TenantA).Select(m => m.SessionId).ToList();

        // The first five went when the cap was passed; the re-marked one was kept as recent, so the next oldest
        // went instead.
        Assert.Equal(FleetManagerMarkHistory.MaxRememberedPerAccount, kept.Count);
        Assert.Equal(new[] { sessions[5] }.Concat(sessions.Skip(7)).Append(Newest), kept);
        Assert.Equal(new[] { First }, history.List(TenantB).Select(m => m.SessionId));
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
