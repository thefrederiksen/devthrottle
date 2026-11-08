using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// <c>GET /fleet/counts</c> (Web UI Polish phase 3): the calling account's Directors and sessions, for the phone's
/// About page, which showed "Not reported" because the public <c>/healthz</c> has no account to count. Driven through
/// the handler with a real Director registry, a real pushed-session store and the real hosted tenant boundary, with
/// the caller's credential stamped on the request the way the authentication middleware stamps it.
/// </summary>
public sealed class FleetCountsEndpointsTests : IDisposable
{
    private static readonly TenantId AccountA = new("acct-counts-a");
    private static readonly TenantId AccountB = new("acct-counts-b");
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-fleet-counts-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _pushed = new();
    private readonly HostedTenantBoundary _hosted = new(new AsyncLocalTenantContext(), new DeviceRegistry(), hosted: true);

    public FleetCountsEndpointsTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private void Director(TenantId tenant, string directorId, int sessions)
    {
        _registry.RegisterFromStream(directorId, "PC-" + directorId, "user", "1.0", pid: 1, startedAt: Now, tenant: tenant);
        _pushed.RegisterConnection(tenant, directorId, "conn-" + directorId);
        var rows = Enumerable.Range(0, sessions)
            .Select(i => new SessionDto { SessionId = Guid.NewGuid().ToString(), DirectorId = directorId })
            .ToArray();
        Assert.True(_pushed.ApplySnapshot(tenant, directorId, "conn-" + directorId, 1, rows));
    }

    /// <summary>A Director that registered but never pushed a roster: it is counted, its sessions are not.</summary>
    private void DirectorWithNoRoster(TenantId tenant, string directorId)
        => _registry.RegisterFromStream(directorId, "PC-" + directorId, "user", "1.0", pid: 1, startedAt: Now, tenant: tenant);

    private static DefaultHttpContext SignedInPhone(TenantId tenant)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "device-key";
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "mobile";
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("phone-" + tenant.Value, tenant.Value, "mobile", "active");
        return ctx;
    }

    private IResult Get(HttpContext ctx) => FleetCountsEndpoints.Handle(ctx, _registry, _pushed, StaleAfter, _hosted);

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    [Fact]
    public void Handle_SignedInPhone_AnswersItsAccountsDirectorsAndSessions()
    {
        // Arrange
        Director(AccountA, "dir-a1", sessions: 3);
        Director(AccountA, "dir-a2", sessions: 2);
        DirectorWithNoRoster(AccountA, "dir-a3");

        // Act
        var result = Get(SignedInPhone(AccountA));

        // Assert
        Assert.Equal(200, Status(result));
        var counts = Assert.IsType<JsonHttpResult<FleetCountsDto>>(result).Value!;
        Assert.Equal(3, counts.Directors);
        Assert.Equal(5, counts.Sessions);
    }

    [Fact]
    public void Handle_TwoAccounts_EachSeesOnlyItsOwnCounts()
    {
        // Arrange
        Director(AccountA, "dir-a1", sessions: 4);
        Director(AccountB, "dir-b1", sessions: 1);
        Director(AccountB, "dir-b2", sessions: 6);

        // Act
        var a = Assert.IsType<JsonHttpResult<FleetCountsDto>>(Get(SignedInPhone(AccountA))).Value!;
        var b = Assert.IsType<JsonHttpResult<FleetCountsDto>>(Get(SignedInPhone(AccountB))).Value!;

        // Assert
        Assert.Equal((1, 4), (a.Directors, a.Sessions));
        Assert.Equal((2, 7), (b.Directors, b.Sessions));
    }

    [Fact]
    public void Handle_RequestWithNoAccount_IsRefusedWith403()
    {
        // Arrange: Directors exist, so a fleet-wide total WOULD be a number - the refusal must not depend on an empty fleet.
        Director(AccountA, "dir-a1", sessions: 2);

        // Act
        var result = Get(new DefaultHttpContext());

        // Assert
        Assert.Equal(403, Status(result));
    }

    [Fact]
    public void Handle_NoBoundary_IsRefusedWith403()
    {
        Director(AccountA, "dir-a1", sessions: 2);

        var result = FleetCountsEndpoints.Handle(SignedInPhone(AccountA), _registry, _pushed, StaleAfter, tenantBoundary: null);

        Assert.Equal(403, Status(result));
    }

    [Fact]
    public void For_LocalTenant_CountsWhatSelfHostHealthzReports()
    {
        // The self-host /healthz reads the same method for the Local tenant, so the two routes cannot count differently.
        Director(TenantId.Local, "dir-local", sessions: 2);
        Director(AccountA, "dir-a1", sessions: 9);

        var counts = FleetCounts.For(_registry, _pushed, TenantId.Local, StaleAfter);

        Assert.Equal((1, 2), (counts.Directors, counts.Sessions));
    }

    [Fact]
    public void SessionKeyGuard_FleetCountsRead_IsAllowedLikeTheDirectorsList()
    {
        Assert.True(SessionKeyGuard.Check("GET", "/fleet/counts").Allowed);
        Assert.True(SessionKeyGuard.Check("GET", "/directors").Allowed);
    }
}
