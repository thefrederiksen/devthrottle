using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Snooze;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Epic #1159 step A: the roster serves what the Gateway last knew, whatever its age.
///
/// THE DEFECT THESE HOLD DOWN. A Director re-pushes its sessions every ten seconds; the roster refused to
/// serve any push older than twenty. Two missed ticks therefore blanked a machine, and a grace-window cache
/// sitting behind that then declared it offline and DROPPED its sessions for good. The owner's roster emptied
/// - sessions, colours and all - several times an hour, while the Gateway held the very data it had just
/// refused to show. Every test here drives the REAL <c>GET /sessions</c> route through the production
/// <see cref="GatewayEndpoints.Map"/>, because the defect lived in the endpoint's own decision-making and a
/// test of the store alone would have passed throughout.
///
/// WHY THE DESTRUCTIBILITY CONTROL IS NOT OPTIONAL. "Serve everything, always" passes every serve-it
/// assertion in this file, and it is a bug: sessions would accumulate forever and a machine retired months
/// ago would still be on the roster. <see cref="PastTheEvictionHorizon_TheMachineAndItsSessionsAreRemoved"/>
/// is the control that separates the fix from that bug - it proves something STILL removes sessions, just on
/// an honest event rather than a display timeout. Without it this whole file is satisfied by code that can
/// never forget anything.
///
/// AND WHY THE PRUNE TESTS COME IN PAIRS. This roster is not only a display read - it is the authority two
/// destructive consumers act on, and it now carries sessions from machines that are not answering. A
/// last-known set cannot say a session ENDED, only that nobody has heard otherwise. So each prune is tested
/// twice: it must NOT run on a stale serve, and it MUST still run on a fresh one. Asserting only the first
/// half would be satisfied by deleting the prune entirely.
/// </summary>
public sealed class RosterServesLastKnownTests
{
    private const string DirectorId = "dir-north";
    private const string Machine = "SOREN_NORTH";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(20);

    private static SessionDto Session(string id, string name, string color) => new()
    {
        SessionId = id,
        Name = name,
        ActivityState = "Waiting",
        StatusColor = color,
        LastActivityAt = DateTime.UtcNow,
    };

    /// <summary>
    /// A Director that pushed <paramref name="ageOfLastPush"/> ago and whose tunnel is up or down. The store's
    /// clock is wound back so the push genuinely LANDED in the past - the age is a real elapsed interval the
    /// endpoint measures for itself, not a number handed to it.
    /// </summary>
    private static PushedSessionStore StoreWithPush(TimeSpan ageOfLastPush, bool tunnelUp, params SessionDto[] sessions)
    {
        var pushedAt = DateTime.UtcNow - ageOfLastPush;
        var store = new PushedSessionStore(() => pushedAt);
        store.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(store.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, sessions));
        if (!tunnelUp)
            Assert.True(store.UnregisterConnection(TenantId.Local, DirectorId, "conn-1"));
        return store;
    }

    // ---------------------------------------------------------------------------------------------------
    // The headline: a machine that has been gone for five minutes is still entirely on the roster.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TunnelClosedForFiveMinutes_StillServesEverySession_DimmedAndDated()
    {
        var store = StoreWithPush(TimeSpan.FromMinutes(5), tunnelUp: false,
            Session("s-1", "Architect", "red"),
            Session("s-2", "Manager", "blue"),
            Session("s-3", "Worker", "green"));

        await WithGateway(store, async http =>
        {
            using var body = await GetEnvelopeAsync(http);

            // EVERY session, not a subset. Before this change the count here was zero.
            var sessions = body.RootElement.GetProperty("sessions").EnumerateArray().ToList();
            Assert.Equal(3, sessions.Count);
            Assert.Equal(new[] { "s-1", "s-2", "s-3" },
                sessions.Select(s => s.GetProperty("sessionId").GetString()).OrderBy(x => x).ToArray());

            // Their colours came back too. The blanking took the colours with the rows, which is what made
            // it read as "everything stopped" rather than "the roster is a moment behind".
            Assert.Equal("red", sessions.Single(s => s.GetProperty("sessionId").GetString() == "s-1")
                .GetProperty("statusColor").GetString());

            // DATED: the machine says how long ago the Gateway last heard it, and it is a real interval.
            var director = Assert.Single(body.RootElement.GetProperty("directors").EnumerateArray());
            Assert.Equal(DirectorReachabilityDto.StateOffline, director.GetProperty("state").GetString());
            var age = director.GetProperty("lastSeenAgeSeconds").GetDouble();
            Assert.InRange(age, 290, 360); // five minutes, allowing for test execution time
            Assert.NotEqual(JsonValueKind.Null, director.GetProperty("lastSeenUtc").ValueKind);

            // DIMMED: every session carries the Gateway's own ruling that its machine cannot be acted on, so
            // no client has to join the session list to the reachability list to work it out for itself.
            Assert.All(sessions, s => Assert.False(s.GetProperty("machineReachable").GetBoolean()));
        });
    }

    [Fact]
    public async Task ConnectedButQuietPastTheStalenessWindow_IsWobblyAndStillServes()
    {
        // The exact case that blanked the roster several times an hour: the tunnel is FINE, the Director has
        // simply not pushed for a moment. Twenty-second window, ten-second push interval - two missed ticks.
        var store = StoreWithPush(TimeSpan.FromSeconds(45), tunnelUp: true, Session("s-1", "Architect", "red"));

        await WithGateway(store, async http =>
        {
            using var body = await GetEnvelopeAsync(http);

            Assert.Single(body.RootElement.GetProperty("sessions").EnumerateArray());
            var director = Assert.Single(body.RootElement.GetProperty("directors").EnumerateArray());
            // Wobbly, not offline: the tunnel is the authority on whether the machine is there, and it is up.
            Assert.Equal(DirectorReachabilityDto.StateWobbly, director.GetProperty("state").GetString());
            Assert.InRange(director.GetProperty("lastSeenAgeSeconds").GetDouble(), 40, 90);

            // A wobbly machine is STILL REACHABLE and may still nag. Its tunnel is up, so a command sent to
            // it lands, and suppressing its badge would make the count blink off every time a push ran a few
            // seconds late - the same defect this file exists to end, one disguise further on.
            var session = Assert.Single(body.RootElement.GetProperty("sessions").EnumerateArray());
            Assert.True(session.GetProperty("machineReachable").GetBoolean());
        });
    }

    [Fact]
    public async Task FreshPushOverALiveTunnel_IsOnlineAndReachable()
    {
        // The control for the two above: the ordinary case must still read online, or "everything is wobbly"
        // would satisfy this file just as well as a correct fold.
        var store = StoreWithPush(TimeSpan.FromSeconds(2), tunnelUp: true, Session("s-1", "Architect", "red"));

        await WithGateway(store, async http =>
        {
            using var body = await GetEnvelopeAsync(http);

            var director = Assert.Single(body.RootElement.GetProperty("directors").EnumerateArray());
            Assert.Equal(DirectorReachabilityDto.StateOnline, director.GetProperty("state").GetString());
            // The age is REAL, not the zero the old online branch wrote from the serve-time clock.
            Assert.InRange(director.GetProperty("lastSeenAgeSeconds").GetDouble(), 0, 20);
            Assert.Empty(body.RootElement.GetProperty("machineErrors").EnumerateArray());

            var session = Assert.Single(body.RootElement.GetProperty("sessions").EnumerateArray());
            Assert.True(session.GetProperty("machineReachable").GetBoolean());
        });
    }

    // ---------------------------------------------------------------------------------------------------
    // The destructibility control: something still removes sessions - the eviction horizon, and only it.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PastTheEvictionHorizon_TheMachineAndItsSessionsAreRemoved()
    {
        // Without this test, "serve everything unconditionally and never forget" passes every other
        // assertion in this file. It proves the roster still has an exit, and that the exit is the horizon.
        var store = StoreWithPush(TimeSpan.FromMinutes(5), tunnelUp: false, Session("s-1", "Architect", "red"));

        await WithGateway(store, async (http, registry) =>
        {
            // Still there before the horizon passes.
            using (var before = await GetEnvelopeAsync(http))
                Assert.Single(before.RootElement.GetProperty("sessions").EnumerateArray());

            // Wound past the horizon, then the real sweep runs - the same path a departed machine travels.
            var entry = registry.Get(TenantId.Local, DirectorId);
            Assert.NotNull(entry);
            entry!.LastSeen = DateTime.UtcNow - registry.EvictionHorizon - TimeSpan.FromMinutes(1);
            registry.SweepStale();

            using var after = await GetEnvelopeAsync(http);
            Assert.Empty(after.RootElement.GetProperty("sessions").EnumerateArray());
            Assert.Empty(after.RootElement.GetProperty("directors").EnumerateArray());
            // And the store itself let go, so a machine that never comes back is not a permanent leak.
            Assert.Empty(store.GetLastKnown(TenantId.Local, DirectorId).Sessions);
        },
        // A horizon a test can actually cross. The DEFAULT is a day, and that default is asserted separately
        // below - driving the sweep is what proves the mechanism, not the size of the number.
        evictionHorizon: TimeSpan.FromMinutes(30));
    }

    // ---------------------------------------------------------------------------------------------------
    // Inspection 1, finding 5: the statistics fold takes the CONFIRMED-LIVE subset, and nothing pinned it.
    // The endpoint tests injected neither input statistics nor concurrency, so swapping the confirmed-live
    // subset for the whole served roster passed the entire suite - while the Gateway reported work happening
    // on a sleeping laptop, and went on reporting it on every poll for as long as the machine stayed away.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStaleServe_IsNotCountedAsWorkHappening()
    {
        var store = StoreWithPush(TimeSpan.FromMinutes(5), tunnelUp: false,
            Session("s-1", "Architect", "red"),
            Session("s-2", "Manager", "blue"));

        var statsPath = Path.Combine(Path.GetTempPath(), "cc-conc-" + Guid.NewGuid().ToString("N") + ".json");
        var concurrency = new CcDirector.Gateway.Stats.GatewaySessionConcurrencyStats(statsPath);
        try
        {
            await WithGateway(store, async http =>
            {
                // The rows ARE served - that is the whole point of the branch - so this is not a test about
                // what the roster shows.
                using var body = await GetEnvelopeAsync(http);
                Assert.Equal(2, body.RootElement.GetProperty("sessions").EnumerateArray().Count());

                // ...and not one of them was recorded as activity, because the machine never confirmed them.
                var snapshot = concurrency.Snapshot(DateTime.UtcNow, TenantId.Local);
                Assert.Equal(0, snapshot.Live.Current);
            }, concurrency: concurrency);
        }
        finally
        {
            try { if (File.Exists(statsPath)) File.Delete(statsPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The other half, and without it the test above is satisfied by a statistics fold that counts NOTHING
    /// ever. A confirmed-live serve must still be counted, or the branch has quietly switched the Gateway's
    /// activity record off.
    /// </summary>
    [Fact]
    public async Task AConfirmedLiveServe_IsStillCountedAsWorkHappening()
    {
        var store = StoreWithPush(TimeSpan.FromSeconds(2), tunnelUp: true,
            Session("s-1", "Architect", "red"),
            Session("s-2", "Manager", "blue"));

        var statsPath = Path.Combine(Path.GetTempPath(), "cc-conc-" + Guid.NewGuid().ToString("N") + ".json");
        var concurrency = new CcDirector.Gateway.Stats.GatewaySessionConcurrencyStats(statsPath);
        try
        {
            await WithGateway(store, async http =>
            {
                using var body = await GetEnvelopeAsync(http);
                Assert.Equal(2, body.RootElement.GetProperty("sessions").EnumerateArray().Count());

                var snapshot = concurrency.Snapshot(DateTime.UtcNow, TenantId.Local);
                Assert.Equal(2, snapshot.Live.Current);
            }, concurrency: concurrency);
        }
        finally
        {
            try { if (File.Exists(statsPath)) File.Delete(statsPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TheDefaultEvictionHorizonIsADay_NotAMinute()
    {
        // The value itself matters and is easy to lose in a refactor: it used to be sixty seconds, and a
        // sixty-second horizon reinstates the whole defect no matter how correct the serving path is.
        Assert.Equal(TimeSpan.FromHours(24), DirectorRegistry.DefaultEvictionHorizon);
    }

    // ---------------------------------------------------------------------------------------------------
    // The sharpest risk: destructive consumers must not act on a serve the machine did not confirm.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StaleServe_DoesNotEvictOwnershipRecords()
    {
        // The machine is gone and its last-known set no longer mentions s-gone. That is NOT evidence s-gone
        // ended - only that nobody has heard about it - so its ownership record must survive, which is what
        // keeps the session proxy answering "owner offline" instead of "no such session".
        var store = StoreWithPush(TimeSpan.FromMinutes(5), tunnelUp: false, Session("s-here", "Architect", "red"));
        var owners = new SessionOwnerCache();
        owners.Remember(TenantId.Local, "s-here", DirectorId);
        owners.Remember(TenantId.Local, "s-gone", DirectorId);

        await WithGateway(store, async http =>
        {
            using var _ = await GetEnvelopeAsync(http);
            Assert.Equal(DirectorId, owners.OwnerOf(TenantId.Local, "s-gone"));
        }, owners: owners);
    }

    [Fact]
    public async Task FreshServe_DoesEvictOwnershipRecords()
    {
        // The control. A machine that ANSWERED is authoritative, so a session it no longer lists really has
        // ended and its record really must go. Without this, deleting the prune outright would pass the test
        // above and this defect class would simply move.
        var store = StoreWithPush(TimeSpan.FromSeconds(2), tunnelUp: true, Session("s-here", "Architect", "red"));
        var owners = new SessionOwnerCache();
        owners.Remember(TenantId.Local, "s-here", DirectorId);
        owners.Remember(TenantId.Local, "s-gone", DirectorId);

        await WithGateway(store, async http =>
        {
            using var _ = await GetEnvelopeAsync(http);
            Assert.Null(owners.OwnerOf(TenantId.Local, "s-gone"));
            Assert.Equal(DirectorId, owners.OwnerOf(TenantId.Local, "s-here"));
        }, owners: owners);
    }

    [Fact]
    public async Task StaleServe_DoesNotDeleteSnoozes()
    {
        // The costliest one, because it writes: PruneNotLive deletes rows from the database. A snooze lost
        // while a laptop was shut would never come back, and the loss would surface hours later as a session
        // that quietly stopped being held.
        using var harness = new GatewayDbTestHarness();
        var snoozes = new SnoozeRegistry(harness.Open(), harness.LegacyPath("snooze-stale.json"));
        snoozes.Snooze("s-gone", DateTime.UtcNow.AddHours(2), DirectorId);

        var store = StoreWithPush(TimeSpan.FromMinutes(5), tunnelUp: false, Session("s-here", "Architect", "red"));

        await WithGateway(store, async http =>
        {
            using var _ = await GetEnvelopeAsync(http);
            Assert.NotNull(snoozes.SnoozeUntilFor("s-gone"));
        }, snoozeRegistry: snoozes);
    }

    [Fact]
    public async Task FreshServe_DoesDeleteSnoozesForSessionsThatEnded()
    {
        // The control for the snooze prune - same reasoning as the ownership pair.
        using var harness = new GatewayDbTestHarness();
        var snoozes = new SnoozeRegistry(harness.Open(), harness.LegacyPath("snooze-fresh.json"));
        snoozes.Snooze("s-gone", DateTime.UtcNow.AddHours(2), DirectorId);

        var store = StoreWithPush(TimeSpan.FromSeconds(2), tunnelUp: true, Session("s-here", "Architect", "red"));

        await WithGateway(store, async http =>
        {
            using var _ = await GetEnvelopeAsync(http);
            Assert.Null(snoozes.SnoozeUntilFor("s-gone"));
        }, snoozeRegistry: snoozes);
    }

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private static async Task<JsonDocument> GetEnvelopeAsync(HttpClient http)
    {
        using var response = await http.GetAsync("sessions?envelope=true");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Task WithGateway(
        PushedSessionStore store,
        Func<HttpClient, Task> assertion,
        SessionOwnerCache? owners = null,
        SnoozeRegistry? snoozeRegistry = null,
        TimeSpan? evictionHorizon = null,
        CcDirector.Gateway.Stats.GatewaySessionConcurrencyStats? concurrency = null)
        => WithGateway(store, (http, _) => assertion(http), owners, snoozeRegistry, evictionHorizon, concurrency);

    /// <summary>
    /// Hosts the real Gateway routes over HTTP with the given push store - the same production
    /// <see cref="GatewayEndpoints.Map"/> the shipped Gateway runs - and wires removal exactly as
    /// <c>GatewayHost</c> does, so an eviction really does travel from the registry sweep into the store.
    ///
    /// "Exactly as GatewayHost does" is now ONE subscriber, <c>ForgetIfDisconnected</c>, not a cascade; this
    /// summary called it a cascade after the other two were deleted (inspection 2, finding 1). If a second
    /// subscriber is ever added to <c>GatewayHost</c>, it must be added here too or this harness silently
    /// stops matching the shipped composition - which is the whole reason it wires the real thing.
    /// </summary>
    private static async Task WithGateway(
        PushedSessionStore store,
        Func<HttpClient, DirectorRegistry, Task> assertion,
        SessionOwnerCache? owners = null,
        SnoozeRegistry? snoozeRegistry = null,
        TimeSpan? evictionHorizon = null,
        CcDirector.Gateway.Stats.GatewaySessionConcurrencyStats? concurrency = null)
    {
        var instancesDirectory = Path.Combine(Path.GetTempPath(), "cc-roster-lastknown-" + Guid.NewGuid().ToString("N"));
        WebApplication? app = null;
        DirectorRegistry? registry = null;
        var started = false;
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}");
            app = builder.Build();
            registry = evictionHorizon is TimeSpan horizon
                ? new DirectorRegistry(instancesDirectory) { EvictionHorizon = horizon }
                : new DirectorRegistry(instancesDirectory);
            registry.OnDirectorRemoved += removal => store.ForgetIfDisconnected(removal.Tenant, removal.DirectorId);
            registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", 4242, DateTime.UtcNow, TenantId.Local);

            GatewayEndpoints.Map(
                app,
                registry,
                version: "test",
                token: "test-token",
                // Self-host-only harness. The boundary is required and non-nullable now (finding I1-01), so
                // it gets the REAL self-host boundary: built over the SingleTenantContext, it always
                // resolves Local - behaviour identical to the null it used to state.
                tenantBoundary: new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                    new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry()),
                owners: owners,
                pushedSessions: store,
                streamStaleAfter: StaleAfter,
                snoozeRegistry: snoozeRegistry,
                concurrency: () => concurrency, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown);

            await app.StartAsync();
            var port = BoundPort.Of(app);
            started = true;
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            await assertion(http, registry);
        }
        finally
        {
            if (app is not null)
            {
                if (started)
                    await app.StopAsync();
                await app.DisposeAsync();
            }
            registry?.Dispose();
            try { if (Directory.Exists(instancesDirectory)) Directory.Delete(instancesDirectory, true); }
            catch { /* best effort */ }
        }
    }
}
