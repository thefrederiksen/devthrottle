using System;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Throttle;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Throttle;

/// <summary>
/// The reader's half of the Systems split, against a real database: a session row's own reported system wins, a
/// row written before its Director reported one takes the system ANOTHER row of the same machine reported (so the
/// tab answers the whole retention window the day the report begins), and a machine nobody has reported for is
/// Unknown - never guessed. Each tenant's rows answer only that tenant.
/// </summary>
public sealed class ThrottleLedgerReaderSystemsTests : IDisposable
{
    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");
    private static readonly DateTime From = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static void Seed(GatewayDatabase db, TenantId tenant, string sessionId, string? machine, string? os, DateTime lastSeen, int turns)
    {
        using var ctx = db.CreateContext(tenant);
        ctx.SessionHistory.Add(new SessionHistoryEntity
        {
            TenantId = tenant.Value, SessionId = sessionId, DirectorId = "dir-" + machine, MachineName = machine, Os = os,
            OriginKind = "human", StartedAtUtc = lastSeen.AddHours(-1), LastSeenUtc = lastSeen,
        });
        for (var i = 0; i < turns; i++)
        {
            ctx.ActivityEvents.Add(new ActivityEventEntity
            {
                TenantId = tenant.Value, EventId = Guid.NewGuid(), DirectorSequence = i, DirectorId = "dir-" + machine,
                SessionId = sessionId, EventType = ThrottleDefinition.TurnSubmitted, Cause = "test",
                OccurredUtc = From.AddHours(2 + i), RecordedUtc = From.AddHours(2 + i), InputOrigin = "voice/desktop",
            });
        }
        ctx.SaveChanges();
    }

    [Fact]
    public void Compute_RowWithoutASystem_TakesTheSystemItsMachineReportedOnAnotherRow()
    {
        var db = _h.Open(new AsyncLocalTenantContext());
        Seed(db, Alice, "old-session", "devlinux", null, From.AddDays(1), turns: 2);
        // Reported AFTER the window by a newer Director on the same machine - and still answers the old row.
        Seed(db, Alice, "new-session", "devlinux", "linux", To.AddDays(1), turns: 0);

        var figure = new ThrottleLedgerReader(db).Compute(Alice, From, To);

        var linux = Assert.Single(figure.Systems);
        Assert.Equal("linux", linux.Os);
        Assert.Equal(2, linux.Turns);
        Assert.Equal("devlinux", Assert.Single(linux.Machines).MachineName);
    }

    [Fact]
    public void Compute_MachineNobodyReportedFor_IsUnknown()
    {
        var db = _h.Open(new AsyncLocalTenantContext());
        Seed(db, Alice, "s1", "old-box", null, From.AddDays(1), turns: 1);

        var figure = new ThrottleLedgerReader(db).Compute(Alice, From, To);

        Assert.Equal("", Assert.Single(figure.Systems).Os);
        Assert.Equal(1, figure.SystemsSummary.UnknownTurns);
    }

    [Fact]
    public void Compute_SameMachineNameInAnotherTenant_DoesNotLendItsSystem()
    {
        var db = _h.Open(new AsyncLocalTenantContext());
        Seed(db, Alice, "s1", "box", null, From.AddDays(1), turns: 1);
        Seed(db, Bob, "s2", "box", "windows", From.AddDays(1), turns: 1);

        var alice = new ThrottleLedgerReader(db).Compute(Alice, From, To);

        Assert.Equal("", Assert.Single(alice.Systems).Os);
    }

    [Fact]
    public void Compute_RowsOwnReport_WinsOverItsMachinesNewestReport()
    {
        var db = _h.Open(new AsyncLocalTenantContext());
        Seed(db, Alice, "s1", "box", "windows", From.AddDays(1), turns: 1);
        Seed(db, Alice, "s2", "box", "linux", To.AddDays(1), turns: 0);

        var figure = new ThrottleLedgerReader(db).Compute(Alice, From, To);

        Assert.Equal("windows", Assert.Single(figure.Systems).Os);
    }
}
