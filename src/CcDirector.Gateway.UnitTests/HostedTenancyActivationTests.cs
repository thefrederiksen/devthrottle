using System;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The hosted per-account activation (Hosted Multi-Tenancy increment 1). These prove the isolation the whole
/// increment exists for: two DIFFERENT accounts each resolve to their OWN tenant from their AUTHENTICATED
/// device key, and neither can see the other's data - at the EF store level (the query filter) and at the
/// tunnel level (the tenant-keyed pushed-session store). The revert-prove is explicit: reverting the
/// resolution-from-the-authenticated-device-key makes two accounts share one tenant and the isolation
/// assertions go RED.
/// </summary>
public sealed class HostedTenancyActivationTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"htact-{Guid.NewGuid():N}");

    public HostedTenancyActivationTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ---- The boundary: resolve a tenant from the AUTHENTICATED device key -------------------------------

    [Fact]
    public void Boundary_Hosted_ResolvesEachDeviceKeyToItsBoundTenant()
    {
        var ambient = new AsyncLocalTenantContext();
        var devices = _harness.OpenDevices();
        var boundary = new HostedTenantBoundary(ambient, devices);
        Assert.True(boundary.IsHosted);

        var keyA = devices.Register("dev-a", "MA").DeviceKey;
        devices.SetAccountBinding("dev-a", "sub-alice", "tenant-alice");

        Assert.Equal("tenant-alice", boundary.ResolveForDeviceKey(keyA)!.Value.Value);
    }

    [Fact]
    public void Boundary_Hosted_DeviceKeyWithNoBoundTenant_ResolvesNull_DenyByDefault()
    {
        var ambient = new AsyncLocalTenantContext();
        var devices = _harness.OpenDevices();
        var boundary = new HostedTenantBoundary(ambient, devices);

        // A registered-but-unbound device (e.g. a self-host-shaped enrollment) has no tenant -> a DENY, never
        // a fall-back to local or SYSTEM.
        var key = devices.Register("dev-x", "MX").DeviceKey;
        Assert.Null(boundary.ResolveForDeviceKey(key));
        // An unknown / blank key likewise resolves to nothing.
        Assert.Null(boundary.ResolveForDeviceKey("not-a-key"));
        Assert.Null(boundary.ResolveForDeviceKey(null));
    }

    [Fact]
    public void Boundary_SelfHost_EveryAuthenticatedCallerIsLocal()
    {
        // Self-host: the context is the SingleTenantContext, so the boundary is inert and resolves Local.
        var devices = _harness.OpenDevices();
        var boundary = new HostedTenantBoundary(new SingleTenantContext(), devices);

        Assert.False(boundary.IsHosted);
        Assert.True(boundary.ResolveForDeviceKey("anything")!.Value.IsLocal);
    }

    // ---- EF store isolation: two accounts, cross-read returns nothing -----------------------------------

    [Fact]
    public void TwoAccounts_WriteUnderTheirResolvedTenant_AndCannotSeeEachOther()
    {
        var ambient = new AsyncLocalTenantContext();
        var db = _harness.Open(ambient);
        var tenants = new TenantRegistry(db);
        var devices = _harness.OpenDevices();
        var boundary = new HostedTenantBoundary(ambient, devices);

        // Two accounts enroll: two subjects -> two DISTINCT tenant ids -> two devices, each bound to its tenant.
        var tenantA = tenants.MintOrLookupBySubject("sub-alice", "alice@example.com");
        var tenantB = tenants.MintOrLookupBySubject("sub-bob", "bob@example.com");
        Assert.NotEqual(tenantA.Value, tenantB.Value);

        var keyA = devices.Register("dev-a", "MA").DeviceKey;
        var keyB = devices.Register("dev-b", "MB").DeviceKey;
        devices.SetAccountBinding("dev-a", "sub-alice", tenantA.Value);
        devices.SetAccountBinding("dev-b", "sub-bob", tenantB.Value);

        // Each account writes a row under the tenant resolved from its AUTHENTICATED device key - the exact
        // production resolution the tunnel Hello and the HTTP middleware use.
        WriteNote(db, boundary, keyA, "mission-a", "alice's why");
        WriteNote(db, boundary, keyB, "mission-b", "bob's why");

        // Cross-read: under A's tenant, ONLY A's row is visible; under B's tenant, ONLY B's. This is the
        // isolation the increment exists for - reverting HostedTenantBoundary.ResolveForDeviceKey to a fixed
        // tenant makes both accounts share one tenant and these assertions fail.
        Assert.Equal(new[] { "mission-a" }, ReadKeys(db, boundary, keyA));
        Assert.Equal(new[] { "mission-b" }, ReadKeys(db, boundary, keyB));
    }

    private static void WriteNote(CcDirector.Gateway.Data.GatewayDatabase db, HostedTenantBoundary boundary, string deviceKey,
        string key, string why)
    {
        var tenant = boundary.ResolveForDeviceKey(deviceKey);
        Assert.NotNull(tenant);
        using (boundary.EnterScope(tenant!.Value))
        using (var ctx = db.CreateContext())
        {
            // Stamp the tenant exactly as the stores do (TenantId = ctx.ActiveTenant), driven by the scope.
            ctx.MissionNotes.Add(new MissionNoteEntity
            {
                Key = key,
                Mission = key,
                Why = why,
                UpdatedAtUtc = DateTime.UtcNow,
                TenantId = ctx.ActiveTenant!,
            });
            ctx.SaveChanges();
        }
    }

    private static string[] ReadKeys(CcDirector.Gateway.Data.GatewayDatabase db, HostedTenantBoundary boundary, string deviceKey)
    {
        var tenant = boundary.ResolveForDeviceKey(deviceKey);
        using (boundary.EnterScope(tenant!.Value))
        using (var ctx = db.CreateContext())
        {
            return ctx.MissionNotes.Select(n => n.Key).OrderBy(k => k).ToArray();
        }
    }

    // ---- Tunnel isolation: Hello binds the tenant from the authenticated key ----------------------------

    [Fact]
    public void DirectorHub_Hello_BindsTheTenantFromTheAuthenticatedKey_AndPushesIsolate()
    {
        var ambient = new AsyncLocalTenantContext();
        var devices = _harness.OpenDevices();
        var boundary = new HostedTenantBoundary(ambient, devices);
        var store = new PushedSessionStore(() => DateTime.UtcNow);

        var tenantA = new TenantId(Guid.NewGuid().ToString());
        var tenantB = new TenantId(Guid.NewGuid().ToString());
        var keyA = devices.Register("dev-a", "MA").DeviceKey;
        var keyB = devices.Register("dev-b", "MB").DeviceKey;
        devices.SetAccountBinding("dev-a", "sub-alice", tenantA.Value);
        devices.SetAccountBinding("dev-b", "sub-bob", tenantB.Value);

        // Director A: authenticated with key A -> Hello binds tenant A -> its pushed session lands in tenant A.
        var hubA = NewHub("conn-a", keyA, boundary, store);
        hubA.Hello(new DirectorStreamHello { DirectorId = "dir-a", Version = "t" });
        hubA.PushDelta(1, new SessionDto { SessionId = "sess-a" });

        var hubB = NewHub("conn-b", keyB, boundary, store);
        hubB.Hello(new DirectorStreamHello { DirectorId = "dir-b", Version = "t" });
        hubB.PushDelta(1, new SessionDto { SessionId = "sess-b" });

        // The tenant-keyed store isolates: tenant A sees only sess-a, tenant B only sess-b. Reverting the hub's
        // ResolveConnectionTenant to a fixed tenant makes both bind the same tenant and this fails.
        Assert.Equal(new[] { "sess-a" }, SessionIds(store, tenantA));
        Assert.Equal(new[] { "sess-b" }, SessionIds(store, tenantB));
    }

    [Fact]
    public void DirectorHub_Hello_Hosted_UnboundDeviceKey_IsDenied()
    {
        var ambient = new AsyncLocalTenantContext();
        var devices = _harness.OpenDevices();
        var boundary = new HostedTenantBoundary(ambient, devices);
        var store = new PushedSessionStore(() => DateTime.UtcNow);

        // An authenticated device key with NO tenant binding must be denied (deny-by-default), not defaulted.
        var key = devices.Register("dev-x", "MX").DeviceKey;
        var (hub, ctx) = NewHubWithContext("conn-x", key, boundary, store);

        hub.Hello(new DirectorStreamHello { DirectorId = "dir-x", Version = "t" });

        Assert.True(ctx.Aborted);
    }

    private DirectorHub NewHub(string connId, string deviceKey, HostedTenantBoundary boundary, PushedSessionStore store)
        => NewHubWithContext(connId, deviceKey, boundary, store).hub;

    private (DirectorHub hub, FakeHubCtx ctx) NewHubWithContext(
        string connId, string deviceKey, HostedTenantBoundary boundary, PushedSessionStore store)
    {
        var http = new DefaultHttpContext();
        var tenant = boundary.ResolveForDeviceKey(deviceKey);
        http.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity(
            "test-device",
            tenant?.Value,
            DeviceRegistry.DefaultDeviceType,
            DeviceRegistry.StatusActive);
        var ctx = new FakeHubCtx(connId, http);
        var registry = new DirectorRegistry(_tempDir);
        var inputStats = new GatewayInputStatsAggregator(Path.Combine(_tempDir, $"stats-{Guid.NewGuid():N}.db"));
        var hub = new DirectorHub(store, registry, InputStatsHandle.Available(inputStats), new GatewayStreamRegistry(), tenantBoundary: boundary)
        {
            Context = ctx,
        };
        return (hub, ctx);
    }

    private static string[] SessionIds(PushedSessionStore store, TenantId tenant)
        => store.SnapshotFresh(tenant, TimeSpan.FromMinutes(5)).Select(x => x.Session.SessionId).OrderBy(s => s).ToArray();

    private sealed class FakeHubCtx : HubCallerContext
    {
        public FakeHubCtx(string connectionId, HttpContext http)
        {
            ConnectionId = connectionId;
            Features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(new HttpContextFeatureImpl { HttpContext = http });
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override System.Threading.CancellationToken ConnectionAborted => System.Threading.CancellationToken.None;

        public bool Aborted { get; private set; }
        public override void Abort() => Aborted = true;

        private sealed class HttpContextFeatureImpl : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; }
        }
    }
}
