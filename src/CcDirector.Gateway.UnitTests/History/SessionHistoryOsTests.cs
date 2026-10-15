using System;
using System.Linq;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.UnitTests.History;

/// <summary>
/// A session's history row records the operating system its Director reported (the Systems tab of Your Throttle).
/// Known is never overwritten by unknown, the same rule the row's machine name and version follow.
/// </summary>
public sealed class SessionHistoryOsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static SessionDto Session() => new() { SessionId = "s-1", Name = "one" };

    private string? StoredOs(Data.GatewayDatabase db)
    {
        using var ctx = db.CreateContext();
        return ctx.SessionHistory.Single(e => e.SessionId == "s-1").Os;
    }

    [Fact]
    public void UpsertLive_DirectorReportedASystem_RecordsIt()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);

        store.UpsertLive("dir-1", Session(), Now, new DirectorFacts("devlinux", "2.21.0", Os: "linux"));

        Assert.Equal("linux", StoredOs(db));
    }

    [Fact]
    public void UpsertLive_LaterWriteWithNoSystem_KeepsTheRecordedOne()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);

        store.UpsertLive("dir-1", Session(), Now, new DirectorFacts("devlinux", "2.21.0", Os: "linux"));
        store.UpsertLive("dir-1", Session(), Now.AddMinutes(5), new DirectorFacts("devlinux", "2.21.0"));

        Assert.Equal("linux", StoredOs(db));
    }

    [Fact]
    public void UpsertLive_OlderDirectorReportsNoSystem_RecordsNull()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);

        store.UpsertLive("dir-1", Session(), Now, new DirectorFacts("old-box", "2.1.0"));

        Assert.Null(StoredOs(db));
    }
}
