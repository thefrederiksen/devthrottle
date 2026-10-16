using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// The roster lookup a start whose outcome was not known is matched by (follow-up review of 4073, findings 1 and 2):
/// every session of the run's exact name, with a schedule's origin, on the ONE Director the create went to - never the
/// first of the name only, and never a Director on another machine.
/// </summary>
public sealed class ScheduleSessionsByNameTests
{
    private const string Name = "Eric - Mail Desk - 2026-10-16 09:09";

    private static SessionDto Session(string id, string directorId, string name, string? originSurface = SessionOriginSurfaces.Cron) => new()
    {
        SessionId = id,
        DirectorId = directorId,
        Name = name,
        OriginSurface = originSurface,
        Agent = "ClaudeCode",
        RepoPath = @"D:\ReposFred\devthrottle_internal",
        Status = "Running",
        ActivityState = "Working",
        LastActivityAt = new DateTime(2026, 10, 16, 13, 17, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void LastKnownScheduleSessionsByName_TwoSessionsOfOneName_ReturnsBoth_InRosterOrder()
    {
        var store = new PushedSessionStore(() => new DateTime(2026, 10, 16, 13, 17, 0, DateTimeKind.Utc));
        store.RegisterConnection(TenantId.Local, "dir-north", "conn-1");
        Assert.True(store.ApplySnapshot(TenantId.Local, "dir-north", "conn-1", 1, new[]
        {
            Session("sid-1", "dir-north", Name),
            Session("sid-other", "dir-north", "Eric - Mail Desk - 2026-10-16 09:10"),
            Session("sid-2", "dir-north", Name),
        }));

        Assert.Equal(new[] { "sid-1", "sid-2" }, GatewayEndpoints.LastKnownScheduleSessionsByName(store, TenantId.Local, "dir-north", Name));
    }

    [Fact]
    public void LastKnownScheduleSessionsByName_ASessionOfThatNameOnAnotherDirector_IsNotReturned()
    {
        var store = new PushedSessionStore(() => new DateTime(2026, 10, 16, 13, 17, 0, DateTimeKind.Utc));
        store.RegisterConnection(TenantId.Local, "dir-south", "conn-s");
        Assert.True(store.ApplySnapshot(TenantId.Local, "dir-south", "conn-s", 1, new[] { Session("sid-south", "dir-south", Name) }));
        store.RegisterConnection(TenantId.Local, "dir-north", "conn-n");
        Assert.True(store.ApplySnapshot(TenantId.Local, "dir-north", "conn-n", 1, new[] { Session("sid-north", "dir-north", Name) }));

        Assert.Equal(new[] { "sid-north" }, GatewayEndpoints.LastKnownScheduleSessionsByName(store, TenantId.Local, "dir-north", Name));
        Assert.Equal(new[] { "sid-south" }, GatewayEndpoints.LastKnownScheduleSessionsByName(store, TenantId.Local, "dir-south", Name));
    }

    [Fact]
    public void LastKnownScheduleSessionsByName_ASessionOfThatNameNotStartedByASchedule_IsNotReturned()
    {
        var store = new PushedSessionStore(() => new DateTime(2026, 10, 16, 13, 17, 0, DateTimeKind.Utc));
        store.RegisterConnection(TenantId.Local, "dir-north", "conn-1");
        Assert.True(store.ApplySnapshot(TenantId.Local, "dir-north", "conn-1", 1, new[] { Session("sid-by-hand", "dir-north", Name, originSurface: null) }));

        Assert.Empty(GatewayEndpoints.LastKnownScheduleSessionsByName(store, TenantId.Local, "dir-north", Name));
    }

    [Fact]
    public void LastKnownScheduleSessionsByName_NoDirectorOrNoName_ReturnsNothing()
    {
        var store = new PushedSessionStore(() => new DateTime(2026, 10, 16, 13, 17, 0, DateTimeKind.Utc));
        store.RegisterConnection(TenantId.Local, "dir-north", "conn-1");
        Assert.True(store.ApplySnapshot(TenantId.Local, "dir-north", "conn-1", 1, new[] { Session("sid-1", "dir-north", Name) }));

        Assert.Empty(GatewayEndpoints.LastKnownScheduleSessionsByName(store, TenantId.Local, "", Name));
        Assert.Empty(GatewayEndpoints.LastKnownScheduleSessionsByName(store, TenantId.Local, "dir-north", ""));
        Assert.Empty(GatewayEndpoints.LastKnownScheduleSessionsByName(null, TenantId.Local, "dir-north", Name));
    }
}
