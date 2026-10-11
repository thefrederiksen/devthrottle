using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Inspection 1, findings 1 and 5: the eviction cascade must not destroy a machine that is already back,
/// and the horizon the owner configures must be the one the SHIPPED Gateway uses.
///
/// WHY THESE ARE ONE FILE. Both are about the same gap between a decision and its consequences. The sweep
/// decides a machine is gone, and something then acts on that decision. Everything here is about that
/// interval, and about whether the thing being decided is even the thing the owner configured.
///
/// THAT GAP IS NOW CLOSED BY REMOVING THE WORK, NOT BY GUARDING IT. When this file was written the sweep
/// fed THREE destructive subscribers - releasing session numbers, forgetting the pushed cache, deleting
/// snoozes - and this summary described them in the present tense long after two were deleted. Today
/// exactly one thing acts on the decision, <c>PushedSessionStore.ForgetIfDisconnected</c>, and it is a
/// single atomic operation under the store's membership gate rather than a check followed by an act. The
/// other two are gone for good (inspection 2, finding 1), and
/// <see cref="EvictionLeavesSnoozesAndNumbersAlone_OnTheRealHost"/> below exists to redden if anyone
/// restores either one.
///
/// WHAT THE SEAM TEST DOES AND DOES NOT PROVE. <see cref="DirectorRegistry.OnSweepJudgedForTest"/> fires
/// inside the exact window finding 1 describes, and a reconnect driven from it proves the ORDERING window
/// exists and is closed. It proves NOTHING about the concurrent race under real thread scheduling - no test
/// here starts a second thread or attempts to interleave one. Said plainly because the difference is easy to
/// lose: a passing seam test is evidence about sequence, not about concurrency.
/// </summary>
[Collection("DirectorRoot")]
public sealed class EvictionRaceAndCompositionTests : IDisposable
{
    private const string DirectorId = "dir-north";
    private const string Machine = "SOREN_NORTH";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-evictrace-" + Guid.NewGuid().ToString("N"));

    public EvictionRaceAndCompositionTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-evictrace-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static SessionDto Session(string id) => new()
    {
        SessionId = id,
        Name = id,
        ActivityState = "Waiting",
        StatusColor = "red",
        LastActivityAt = DateTime.UtcNow,
    };

    private static DirectorRegistry RegistryWith(TimeSpan horizon, string instancesDir)
    {
        var registry = new DirectorRegistry(instancesDir) { EvictionHorizon = horizon };
        registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        return registry;
    }

    /// <summary>Wind the entry's last-seen back so the NEXT sweep judges it stale on its snapshot.</summary>
    private static void AgePastHorizon(DirectorRegistry registry)
    {
        var entry = registry.Get(TenantId.Local, DirectorId);
        Assert.NotNull(entry);
        entry!.LastSeen = DateTime.UtcNow - registry.EvictionHorizon - TimeSpan.FromMinutes(1);
    }

    // ---------------------------------------------------------------------------------------------------
    // Finding 1a: the sweep judges a SNAPSHOT and used to remove by key.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SweepJudgedItStale_ThenItReconnects_TheDirectorSurvivesAndNoRemovalFires()
    {
        using var registry = RegistryWith(TimeSpan.FromMinutes(30), _instancesDir);
        var removals = new List<string>();
        registry.OnDirectorRemoved += r => removals.Add(r.DirectorId);

        AgePastHorizon(registry);

        // The reconnect lands in the window: after the sweep has judged the captured entry stale, before it
        // removes. This is the machine coming back at the worst possible moment.
        var reconnected = 0;
        registry.OnSweepJudgedForTest = () =>
        {
            if (reconnected++ > 0) return;
            registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234,
                startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        };

        registry.SweepStale();

        Assert.Equal(1, reconnected);                                    // the window really was entered
        Assert.NotNull(registry.Get(TenantId.Local, DirectorId));        // the machine is still here
        Assert.Empty(removals);                                          // and nothing downstream was told to destroy it
    }

    /// <summary>
    /// The control, and it is not optional. Without it, code that simply never removes anything passes the
    /// test above - and "never forget a machine" is the opposite bug, the one the eviction horizon exists to
    /// prevent. This proves removal is still reachable on the same path with the same setup.
    /// </summary>
    [Fact]
    public void SweepJudgedItStale_AndNothingReconnects_TheDirectorIsRemovedAndTheRemovalFires()
    {
        using var registry = RegistryWith(TimeSpan.FromMinutes(30), _instancesDir);
        var removals = new List<string>();
        registry.OnDirectorRemoved += r => removals.Add(r.DirectorId);

        AgePastHorizon(registry);
        registry.SweepStale();

        Assert.Null(registry.Get(TenantId.Local, DirectorId));
        Assert.Equal(new[] { DirectorId }, removals.ToArray());
    }

    /// <summary>
    /// The heartbeat is the trap finding 1 names: it mutates LastSeen IN PLACE on the live object, so a
    /// compare-and-remove against the captured VALUE still matches the same instance and still removes. Only
    /// re-reading and re-judging survives this.
    /// </summary>
    [Fact]
    public void SweepJudgedItStale_ThenAHeartbeatRefreshesTheSameInstance_TheDirectorSurvives()
    {
        using var registry = RegistryWith(TimeSpan.FromMinutes(30), _instancesDir);
        var removals = new List<string>();
        registry.OnDirectorRemoved += r => removals.Add(r.DirectorId);

        AgePastHorizon(registry);

        var beat = 0;
        registry.OnSweepJudgedForTest = () =>
        {
            if (beat++ > 0) return;
            Assert.True(registry.Heartbeat(DirectorId));
        };

        registry.SweepStale();

        Assert.Equal(1, beat);
        Assert.NotNull(registry.Get(TenantId.Local, DirectorId));
        Assert.Empty(removals);
    }

    // ---------------------------------------------------------------------------------------------------
    // Finding 1b: the window between the removal DECISION and the destruction acting on it. That window is
    // now closed by there BEING no separate act - eviction runs one atomic operation rather than the three
    // destructive subscribers this comment used to name. These drive the REAL GatewayHost, so what is under
    // test is the shipped composition. A hand-wired harness would have proved only that a guard works when
    // a test remembers to wire it.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EvictionOfALiveMachine_DoesNothingAtAll()
    {
        await using var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345",
            authEnabled: true, instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));

        gateway.Registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        gateway.PushedSessions.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(gateway.PushedSessions.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, new[] { Session("s-1") }));

        var removalFired = false;
        gateway.Registry.OnDirectorRemoved += _ => removalFired = true;

        AgePastHorizon(gateway.Registry);
        gateway.Registry.SweepStale();

        Assert.True(removalFired);   // the registry entry did go - eviction is not silently disabled
        // ...and the read model kept the machine, because it holds a live stream. ONE operation decided
        // that, inside the store's membership gate, so there is no window between a check and an act.
        Assert.NotEmpty(gateway.PushedSessions.GetLastKnown(TenantId.Local, DirectorId).Sessions);
    }

    /// <summary>
    /// The control. Without it a ForgetIfDisconnected that always declined would pass the test above, and
    /// "never forget a machine" is the unbounded leak the horizon exists to prevent.
    /// </summary>
    [Fact]
    public async Task EvictionOfAGenuinelyGoneMachine_DropsItFromTheReadModel()
    {
        await using var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345",
            authEnabled: true, instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));

        gateway.Registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        gateway.PushedSessions.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(gateway.PushedSessions.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, new[] { Session("s-1") }));
        Assert.True(gateway.PushedSessions.UnregisterConnection(TenantId.Local, DirectorId, "conn-1"));

        AgePastHorizon(gateway.Registry);
        gateway.Registry.SweepStale();

        Assert.Empty(gateway.PushedSessions.GetLastKnown(TenantId.Local, DirectorId).Sessions);
    }

    // ---------------------------------------------------------------------------------------------------
    // Inspection 2, finding 1: the two destructive steps that were DELETED from eviction.
    //
    // These pin an ABSENCE, which is the kind of thing a later tidy-up silently restores - someone notices
    // a retired machine's numbers are still held, "fixes" it here, and reinstates a race that can free a
    // live session's number or delete an owner's snoozes. The deletion is the fix; these say so.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EvictionLeavesSnoozesAndNumbersAlone_OnTheRealHost()
    {
        await using var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345",
            authEnabled: true, instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));

        // The host is used without being started, and the database is no longer opened by the constructor:
        // StartAsync opens it right after the listener binds so a slow database cannot delay the bind
        // (#2383, #2585). This test wants the stores, not a bound port, so it runs that step directly.
        gateway.EnsureStoresReady();

        gateway.Registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        gateway.PushedSessions.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(gateway.PushedSessions.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, new[] { Session("s-1") }));
        Assert.True(gateway.PushedSessions.UnregisterConnection(TenantId.Local, DirectorId, "conn-1"));

        // A number the Director owns, adopted the way the roster read adopts one.
        gateway.SessionNumbers.Adopt(TenantId.Local, "s-1", DirectorId, 742);
        Assert.Equal(742, gateway.SessionNumbers.NumberFor(TenantId.Local, "s-1"));

        // And a snooze the OWNER set - the thing nothing can reconstruct once it is gone. This assertion
        // was missing when the test was first written: its NAME said snoozes and its body only checked
        // numbers, which is an overclaim of exactly the kind this mission keeps finding in other people's
        // work. Without it, re-adding the snooze clearing would have left the test green.
        gateway.SnoozeRegistry.Snooze("s-1", DateTime.UtcNow.AddHours(2), DirectorId);
        Assert.NotNull(gateway.SnoozeRegistry.SnoozeUntilFor("s-1"));

        AgePastHorizon(gateway.Registry);
        gateway.Registry.SweepStale();

        // The machine left the read model...
        Assert.Empty(gateway.PushedSessions.GetLastKnown(TenantId.Local, DirectorId).Sessions);
        // ...and the owner's snooze survived it. This is the irrecoverable one: a released number can be
        // re-adopted and a forgotten entry is repopulated by the next Hello, but nothing anywhere can
        // reconstruct an owner's intention to set a machine aside until a particular time.
        Assert.NotNull(gateway.SnoozeRegistry.SnoozeUntilFor("s-1"));
        // ...and its number was NOT freed. Freeing it is what could hand a live session's number to a new
        // one, and no cleanup is worth that. If anyone re-wires ReleaseForDirector onto OnDirectorRemoved,
        // this returns null and the test fails - which is the whole point of pinning a deletion.
        Assert.Equal(742, gateway.SessionNumbers.NumberFor(TenantId.Local, "s-1"));
    }

    // ---------------------------------------------------------------------------------------------------
    // Finding 5: the SHIPPED composition. The horizon was advertised as configurable and no test built a
    // real GatewayHost from a configured value - so replacing the one line that reads configuration with
    // the default constant left every existing test green while the setting did nothing at all.
    // ---------------------------------------------------------------------------------------------------

    private static void SeedGatewayConfig(string json)
    {
        var path = CcStorage.ConfigJson();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public async Task AConfiguredEvictionHorizon_ReachesTheProductionRegistry()
    {
        SeedGatewayConfig("""{ "gateway": { "directorEvictionHorizonHours": 6 } }""");

        await using var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345",
            authEnabled: true, instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));

        // Not the loader's answer - the REGISTRY THE GATEWAY ACTUALLY SWEEPS WITH. That is the whole point:
        // the configuration tests already proved GatewayConfig.Load reads the key, and passed happily while
        // nothing carried the value onward.
        Assert.Equal(TimeSpan.FromHours(6), gateway.Registry.EvictionHorizon);
        Assert.NotEqual(DirectorRegistry.DefaultEvictionHorizon, gateway.Registry.EvictionHorizon);
    }

    [Fact]
    public async Task WithNoConfiguredHorizon_TheProductionRegistryUsesTheDayLongDefault()
    {
        SeedGatewayConfig("""{ "gateway": { } }""");

        await using var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345",
            authEnabled: true, instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));

        Assert.Equal(DirectorRegistry.DefaultEvictionHorizon, gateway.Registry.EvictionHorizon);
        Assert.Equal(TimeSpan.FromHours(24), gateway.Registry.EvictionHorizon);
    }
}
