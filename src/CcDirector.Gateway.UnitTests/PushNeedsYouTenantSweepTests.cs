using System.Net;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Push;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Lib.Net.Http.WebPush;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE APP-ICON DOT ON THE HOSTED GATEWAY. The notifier was previously skipped whenever the Gateway was
/// hosted - a bare background timer has no ambient tenant, so it would have failed closed against the
/// tenant-scoped subscriptions store on every tick. Skipped meant a phone talking to the hosted Gateway got
/// no dot when a session needed it, and never got the single falling-edge zero that CLEARS one.
///
/// These tests drive the real <see cref="PushNeedsYouTenantSweep"/> over a HOSTED boundary with two tenants,
/// and pin the two properties that make hosted push safe to have on at all:
///   1. every tenant gets a pass, and each pass pushes to that tenant's OWN devices only;
///   2. the dot decision is per tenant - one tenant reading zero must not clear the dot another tenant just
///      raised, which a single flat decision state would do on every interleaved tick.
/// </summary>
public sealed class PushNeedsYouTenantSweepTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private sealed class FakeSender : IWebPushSender
    {
        public List<(string endpoint, string payload)> Sent { get; } = new();

        public Task SendAsync(StoredPushSubscription subscription, string payloadJson, CancellationToken cancellationToken)
        {
            Sent.Add((subscription.Endpoint, payloadJson));
            return Task.CompletedTask;
        }
    }

    private static int CountIn(string payload)
        => JsonDocument.Parse(payload).RootElement.GetProperty("count").GetInt32();

    // A hosted world: the ambient tenant context, two minted tenants, and the boundary the seam fans out over.
    private (AsyncLocalTenantContext ambient, HostedTenantBoundary boundary, TenantRegistry tenants,
             PushSubscriptionStore store, TenantId a, TenantId b) NewHostedWorld()
    {
        var ambient = new AsyncLocalTenantContext();
        var db = _h.Open(ambient);
        var tenants = new TenantRegistry(db);
        var boundary = new HostedTenantBoundary(ambient, _h.OpenDevices());
        Assert.True(boundary.IsHosted); // the whole point: this is the mode push used to be switched off in

        var a = tenants.MintOrLookupBySubject("sub-a", "a@example.com");
        var b = tenants.MintOrLookupBySubject("sub-b", "b@example.com");

        var store = new PushSubscriptionStore(db, _h.LegacyPath($"push-{Guid.NewGuid():N}.json"));
        return (ambient, boundary, tenants, store, a, b);
    }

    // Register one phone for a tenant, exactly as POST /push/subscribe does inside that tenant's scope.
    private static void Subscribe(HostedTenantBoundary boundary, PushSubscriptionStore store, TenantId tenant, string endpoint)
    {
        using (boundary.EnterScope(tenant))
            store.Add(endpoint, "p256dh-" + endpoint, "auth-" + endpoint);
    }

    [Fact]
    public async Task Sweep_Hosted_PushesEachTenantsCountToOnlyThatTenantsDevices()
    {
        var (ambient, boundary, tenants, store, a, b) = NewHostedWorld();
        Subscribe(boundary, store, a, "https://push.example/phone-a");
        Subscribe(boundary, store, b, "https://push.example/phone-b");

        var sender = new FakeSender();
        // The snapshot reads the AMBIENT tenant, exactly as the production read does (the folded fleet of the
        // pass now running): tenant A has three sessions needing the user, tenant B has one.
        var notifier = new WebPushNeedsYouNotifier(
            store,
            _ => Task.FromResult(new WebPushNeedsYouNotifier.NeedsYouSnapshot(
                CurrentIs(ambient, a) ? 3 : 1, Array.Empty<string>())),
            sender,
            () => ambient.CurrentOrNull ?? TenantId.Local);
        var sweep = new PushNeedsYouTenantSweep(boundary, tenants, notifier);

        await sweep.SweepAsync();

        // Two pushes, one per tenant - and each phone got ITS OWN tenant's count, never the other's.
        Assert.Equal(2, sender.Sent.Count);
        var toA = Assert.Single(sender.Sent, s => s.endpoint.EndsWith("phone-a", StringComparison.Ordinal));
        var toB = Assert.Single(sender.Sent, s => s.endpoint.EndsWith("phone-b", StringComparison.Ordinal));
        Assert.Equal(3, CountIn(toA.payload));
        Assert.Equal(1, CountIn(toB.payload));
    }

    [Fact]
    public async Task Sweep_Hosted_OneTenantsQuietFleetDoesNotClearAnothersDot()
    {
        // The interleaving that a single flat decision state gets wrong. Tenant A always has work waiting;
        // tenant B never does. With one shared DotState, B's zero-count pass drives the falling-edge counter
        // that belongs to A, so A's dot is cleared and then re-raised on every sweep - a phone that pings
        // itself for ever. Per-tenant state means A rises ONCE and then goes quiet, and B never pushes at all.
        var (ambient, boundary, tenants, store, a, b) = NewHostedWorld();
        Subscribe(boundary, store, a, "https://push.example/phone-a");
        Subscribe(boundary, store, b, "https://push.example/phone-b");

        var sender = new FakeSender();
        var notifier = new WebPushNeedsYouNotifier(
            store,
            _ => Task.FromResult(new WebPushNeedsYouNotifier.NeedsYouSnapshot(
                CurrentIs(ambient, a) ? 2 : 0, Array.Empty<string>())),
            sender,
            () => ambient.CurrentOrNull ?? TenantId.Local);
        var sweep = new PushNeedsYouTenantSweep(boundary, tenants, notifier);

        // Several sweeps, fewer than the heartbeat window, so the only pushes a correct implementation makes
        // are tenant A's single rising edge.
        for (var i = 0; i < 4; i++)
            await sweep.SweepAsync();

        var sent = Assert.Single(sender.Sent);
        Assert.EndsWith("phone-a", sent.endpoint, StringComparison.Ordinal);
        Assert.Equal(2, CountIn(sent.payload));
    }

    [Fact]
    public async Task Sweep_Hosted_ATenantWithNoSubscribedDeviceCostsNoPush()
    {
        // Self-gating survives the fan-out: the notifier skips a tenant whose subscriptions store is empty, so
        // a hosted Gateway full of tenants pays nothing until a phone opts in.
        var (ambient, boundary, tenants, store, a, _) = NewHostedWorld();
        Subscribe(boundary, store, a, "https://push.example/phone-a");

        var sender = new FakeSender();
        var notifier = new WebPushNeedsYouNotifier(
            store,
            _ => Task.FromResult(new WebPushNeedsYouNotifier.NeedsYouSnapshot(5, Array.Empty<string>())),
            sender,
            () => ambient.CurrentOrNull ?? TenantId.Local);
        var sweep = new PushNeedsYouTenantSweep(boundary, tenants, notifier);

        await sweep.SweepAsync();

        var sent = Assert.Single(sender.Sent);
        Assert.EndsWith("phone-a", sent.endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sweep_Hosted_ClearsTheDotForTheTenantThatWentQuiet_AndOnlyThatOne()
    {
        // The half that is easy to miss: the FALLING edge. A dot is only removed by a pushed zero (the phone
        // cannot be asked), so a hosted Gateway that never sweeps can never clear one - which is exactly the
        // "the notifications did not clear" symptom. Tenant A goes quiet and must receive its single zero;
        // tenant B still has work and must keep its dot (no zero).
        var (ambient, boundary, tenants, store, a, b) = NewHostedWorld();
        Subscribe(boundary, store, a, "https://push.example/phone-a");
        Subscribe(boundary, store, b, "https://push.example/phone-b");

        var aCount = 2;
        var sender = new FakeSender();
        var notifier = new WebPushNeedsYouNotifier(
            store,
            _ => Task.FromResult(new WebPushNeedsYouNotifier.NeedsYouSnapshot(
                CurrentIs(ambient, a) ? aCount : 1, Array.Empty<string>())),
            sender,
            () => ambient.CurrentOrNull ?? TenantId.Local);
        var sweep = new PushNeedsYouTenantSweep(boundary, tenants, notifier);

        await sweep.SweepAsync();           // both rise
        Assert.Equal(2, sender.Sent.Count);

        aCount = 0;                          // A's fleet goes quiet
        sender.Sent.Clear();
        for (var i = 0; i < WebPushNeedsYouNotifier.ClearConfirmations; i++)
            await sweep.SweepAsync();        // A settles at zero over the confirmation window

        var cleared = Assert.Single(sender.Sent);
        Assert.EndsWith("phone-a", cleared.endpoint, StringComparison.Ordinal);
        Assert.Equal(0, CountIn(cleared.payload));
    }

    // Which tenant's pass is running right now. The production snapshot answers this by reading the ambient
    // tenant's folded fleet; the test answers it by naming the tenant, which is the same question.
    private static bool CurrentIs(AsyncLocalTenantContext ambient, TenantId tenant)
        => ambient.CurrentOrNull is { } current && string.Equals(current.Value, tenant.Value, StringComparison.Ordinal);
}
