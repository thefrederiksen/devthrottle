using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="RegistryDirectorTargetResolver"/> (epic #479, #503): resolving a cron
/// job's target MACHINE to a runnable Director. Covers the three paths - a Director is already
/// running (no launch), none is running so the launcher starts one and it registers (launch + poll),
/// and the launcher cannot start one (clean error). A fake launcher and a mutable Director list stand
/// in for the live registry, with tiny wall-clock waits so the poll terminates instantly.
///
/// The resolver's result is a DirectorId (or an error) - not an endpoint. In tunnel-only mode a
/// registered Director is reached over its tunnel by id, so resolving must succeed even when the
/// Director's control endpoint is blank, which every tunnel-only Director's is (issue #1727).
/// </summary>
public sealed class RegistryDirectorTargetResolverTests
{
    private static readonly TimeSpan FastTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    private static DirectorDto Director(string id, string machine, string endpoint) => new()
    {
        DirectorId = id, MachineName = machine, ControlEndpoint = endpoint, Version = "0.9.10",
    };

    private sealed class FakeLauncher : IDirectorLauncher
    {
        private readonly Func<string, bool> _onStart;
        public int StartCount { get; private set; }
        public string? LastMachine { get; private set; }

        /// <summary>The tenant the resolver handed to the launch, so a test can assert that the resolved
        /// tenant reaches the launcher rather than being dropped on the way.</summary>
        public TenantId? LastTenant { get; private set; }

        public FakeLauncher(Func<string, bool> onStart) => _onStart = onStart;

        public Task<bool> StartAsync(TenantId tenant, string machine, CancellationToken ct)
        {
            StartCount++;
            LastMachine = machine;
            LastTenant = tenant;
            return Task.FromResult(_onStart(machine));
        }
    }

    [Fact]
    public async Task Resolve_DirectorAlreadyRunning_WithBlankControlEndpoint_ReturnsItsId_DoesNotLaunch()
    {
        // Tunnel-only shape: the registered Director's control endpoint is blank. Resolving must still
        // succeed and return its id - the id is the whole result (issue #1727).
        var directors = new List<DirectorDto>
        {
            Director("d-7882", "MACHINE_A", ""),
            Director("d-7879", "MACHINE_B", ""),
        };
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch when one is running"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => directors, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("MACHINE_A", director: null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("d-7882", result.DirectorId);
        Assert.Equal(0, launcher.StartCount);                      // a running Director means no launch
    }

    [Fact]
    public async Task Resolve_NoDirector_LauncherStartsOne_RegistersAfterLaunch_Resolves()
    {
        var directors = new List<DirectorDto> { Director("d-7879", "MACHINE_B", "") };
        // The launcher "starts" a Director on MACHINE_A: it registers in the list and the next poll finds it.
        var launcher = new FakeLauncher(machine =>
        {
            directors.Add(Director("d-new", machine, ""));
            return true;
        });
        var resolver = new RegistryDirectorTargetResolver(
            _ => directors, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("MACHINE_A", director: null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("d-new", result.DirectorId);
        Assert.Equal(1, launcher.StartCount);
        Assert.Equal("MACHINE_A", launcher.LastMachine);
        // The resolved tenant reaches the launcher. It used to be dropped here: the launch went out as a fresh
        // loopback request to the Gateway's own relay, which carries no device key and so arrived with no
        // tenant at all.
        Assert.Equal(TenantId.Local, launcher.LastTenant);
    }

    [Fact]
    public async Task Resolve_NoDirector_LauncherCannotStart_ReturnsError()
    {
        var directors = new List<DirectorDto>();
        var launcher = new FakeLauncher(_ => false);              // launcher fails to start one
        var resolver = new RegistryDirectorTargetResolver(
            _ => directors, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("MACHINE_A", director: null, CancellationToken.None);

        Assert.Null(result.DirectorId);
        Assert.NotNull(result.Error);
        Assert.Equal(1, launcher.StartCount);
    }

    [Fact]
    public async Task Resolve_LaunchedButNeverRegisters_ReturnsTimeoutError()
    {
        var directors = new List<DirectorDto>();
        var launcher = new FakeLauncher(_ => true);               // claims success but nothing registers
        var resolver = new RegistryDirectorTargetResolver(
            _ => directors, () => TenantId.Local, launcher, TimeSpan.FromMilliseconds(120), FastPoll);

        var result = await resolver.ResolveAsync("MACHINE_A", director: null, CancellationToken.None);

        Assert.Null(result.DirectorId);
        Assert.NotNull(result.Error);
        Assert.Contains("registered", result.Error!);
    }

    [Fact]
    public async Task Resolve_EmptyMachine_ReturnsError_DoesNotLaunch()
    {
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch for empty machine"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => new List<DirectorDto>(), () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("   ", director: null, CancellationToken.None);

        Assert.Null(result.DirectorId);
        Assert.NotNull(result.Error);
        Assert.Equal(0, launcher.StartCount);
    }

    // ---- Hosted Multi-Tenancy: cross-tenant machine collision (audit H1, gap audit-e) -------------------
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Resolve_scopes_to_the_callers_tenant_never_another_tenants_same_machine_director()
    {
        // Two tenants each own a Director on the SAME machine name (the registry key is (tenant, id), so this
        // is a legitimate hosted state - two accounts, two Directors, one shared machine name). Tenant A's cron
        // fire resolves that machine. It must resolve A's OWN Director; B's Director must never be picked, or
        // A's CronRunRecord would persist a cross-tenant DirectorId that A cannot even address.
        var dirA = Director("d-alice", "SHARED_MACHINE", "");
        var dirB = Director("d-bob", "SHARED_MACHINE", "");
        // The tenant-scoped reader the production registry provides: one tenant's partition per call. B is
        // listed FIRST in the fleet so a tenant-blind (pre-fix, fleet-global) resolve would pick B, making the
        // isolation assertion meaningful rather than order-lucky.
        var byTenant = new Dictionary<TenantId, DirectorDto[]>
        {
            [TenantA] = new[] { dirA },
            [TenantB] = new[] { dirB },
        };
        IEnumerable<DirectorDto> ForTenant(TenantId t) =>
            byTenant.TryGetValue(t, out var list) ? list : Array.Empty<DirectorDto>();
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch when one is running"));

        var asAlice = new RegistryDirectorTargetResolver(ForTenant, () => TenantA, launcher, FastTimeout, FastPoll);
        var resolvedForAlice = await asAlice.ResolveAsync("SHARED_MACHINE", director: null, CancellationToken.None);
        Assert.Null(resolvedForAlice.Error);
        Assert.Equal("d-alice", resolvedForAlice.DirectorId);   // A's own Director, never B's

        // Symmetric: B resolving the same machine gets B's Director, never A's.
        var asBob = new RegistryDirectorTargetResolver(ForTenant, () => TenantB, launcher, FastTimeout, FastPoll);
        var resolvedForBob = await asBob.ResolveAsync("SHARED_MACHINE", director: null, CancellationToken.None);
        Assert.Equal("d-bob", resolvedForBob.DirectorId);

        // Revert-proof: a tenant-BLIND reader (the pre-fix fleet-global list, B first) resolves B's Director for
        // A's fire - the exact cross-tenant pick the fix removes. This pins that the tenant scoping, not list
        // order, is what protects A: reverting the resolver to ignore the tenant reddens the assertion above.
        var fleetGlobal = new[] { dirB, dirA };
        var blind = new RegistryDirectorTargetResolver(_ => fleetGlobal, () => TenantA, launcher, FastTimeout, FastPoll);
        var blindPick = await blind.ResolveAsync("SHARED_MACHINE", director: null, CancellationToken.None);
        Assert.Equal("d-bob", blindPick.DirectorId);            // the leak, demonstrated
    }

    // ---- Naming ONE Director: "start it on THAT Director", not "on that computer" --------------------
    //
    // A machine that runs several named instances is the case the machine name cannot answer. The
    // fixture below is deliberately built so a machine-only resolve gets the WRONG one: the named
    // Director is second in the list, so a resolve that ignored the name would return the first and
    // still report success. That is the failure these tests exist to catch - it is silent.

    private static DirectorDto Named(string id, string machine, string displayName) => new()
    {
        DirectorId = id, MachineName = machine, ControlEndpoint = "", DisplayName = displayName, Version = "0.9.10",
    };

    private static readonly DirectorDto[] TwoOnOneMachine =
    {
        Named("d-first", "SOREN_NORTH", "North daily"),
        Named("d-slot-5", "SOREN_NORTH", "North build"),
    };

    [Fact]
    public async Task Resolve_byDisplayName_picksThatDirector_notTheMachinesFirst()
    {
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch: it is running"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => TwoOnOneMachine, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("SOREN_NORTH", "North build", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("d-slot-5", result.DirectorId);   // the SECOND row: the name decided, not the order
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task Resolve_byDirectorId_isCaseInsensitive_andNeedsNoMachine()
    {
        // The id identifies the machine by itself, so a caller holding only an id can spawn without
        // knowing where it runs - which is the whole point of handing an id to another agent.
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => TwoOnOneMachine, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync(machine: "", "D-SLOT-5", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("d-slot-5", result.DirectorId);
    }

    [Fact]
    public async Task Resolve_namedDirectorNotRegistered_failsLoud_neverSubstitutesOrLaunches()
    {
        // The substitution this forbids is the dangerous one: another Director IS running on that
        // machine, so a resolver that fell back would succeed, and the session would open on the wrong
        // Director with nothing in the reply to say so. The launcher cannot start a PARTICULAR named
        // instance either, so launching here would start the wrong one and then spawn into it.
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch a named target"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => TwoOnOneMachine, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("SOREN_NORTH", "North experiments", CancellationToken.None);

        Assert.Null(result.DirectorId);
        Assert.NotNull(result.Error);
        Assert.Contains("North experiments", result.Error!);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task Resolve_nameMatchesTwoDirectors_failsLoud_listingBoth()
    {
        // Display names are editable free text, so two machines can carry the same one. Picking either
        // would be a guess the caller cannot see; the error names both ids so they can pick.
        var twins = new[]
        {
            Named("d-a", "MACHINE_A", "Build box"),
            Named("d-b", "MACHINE_B", "Build box"),
        };
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => twins, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync(machine: "", "Build box", CancellationToken.None);

        Assert.Null(result.DirectorId);
        Assert.NotNull(result.Error);
        Assert.Contains("d-a", result.Error!);
        Assert.Contains("d-b", result.Error!);
    }

    // ID PRECEDENCE in the GATEWAY resolver, which is where the create is finally pinned. A Director's
    // display name is free text, so it can be set to another Director's id - and with ids and names
    // matched at equal rank, asking for that id finds TWO candidates and the resolve refuses a request
    // that is perfectly unambiguous. Without this case the resolver's tests pass just as well against
    // the old equal-precedence matching, which is to say they were not testing the rule at all.
    [Fact]
    public async Task Resolve_byId_isNotMadeAmbiguousByAnotherDirectorsDisplayName()
    {
        var fleet = new[]
        {
            Named("d-impostor", "SOREN_NORTH", "d-slot-5"),   // display name IS the id being asked for
            Named("d-slot-5", "SOREN_NORTH", "North build"),
        };
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => fleet, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("SOREN_NORTH", "d-slot-5", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("d-slot-5", result.DirectorId);
    }

    [Fact]
    public async Task Resolve_namedDirector_staysInsideTheCallersTenant()
    {
        // The same partition rule the machine match follows. Display names are free text another
        // account may have chosen too, so a fleet-global name match would resolve across the boundary
        // on the caller's behalf - and an ID match would do it with no collision needed at all.
        var mine = new[] { Named("d-mine", "SHARED_MACHINE", "Build box") };
        var theirs = new[] { Named("d-theirs", "SHARED_MACHINE", "Build box") };
        IEnumerable<DirectorDto> ForTenant(TenantId t) => t == TenantA ? mine : theirs;
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch"));

        var asAlice = new RegistryDirectorTargetResolver(ForTenant, () => TenantA, launcher, FastTimeout, FastPoll);

        Assert.Equal("d-mine", (await asAlice.ResolveAsync("", "Build box", CancellationToken.None)).DirectorId);
        // Bob's Director id, asked for by Alice, is not found - not resolved and quietly used.
        var reachingAcross = await asAlice.ResolveAsync("", "d-theirs", CancellationToken.None);
        Assert.Null(reachingAcross.DirectorId);
        Assert.NotNull(reachingAcross.Error);
    }

    [Fact]
    public async Task Resolve_denies_when_no_tenant_scope_is_in_effect()
    {
        // Deny-by-default: on hosted a resolve reached with no tenant scope (a boundary bug) must fail loud,
        // never fall back to a partition it would then scan cross-tenant.
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => new[] { Director("d-x", "M", "") }, () => (TenantId?)null, launcher, FastTimeout, FastPoll);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("M", director: null, CancellationToken.None));
    }

    /// <summary>A Director that said goodbye: MarkStopped stamps StoppedAtUtc and keeps the entry for the
    /// 24h eviction horizon, so a stopped registration is a normal thing to find in the list.</summary>
    private static DirectorDto Stopped(string id, string machine, DateTime lastSeen) => new()
    {
        DirectorId = id, MachineName = machine, ControlEndpoint = "", Version = "0.9.10",
        LastSeen = lastSeen, StoppedAtUtc = lastSeen,
    };

    private static DirectorDto Running(string id, string machine, DateTime lastSeen) => new()
    {
        DirectorId = id, MachineName = machine, ControlEndpoint = "", Version = "0.9.10",
        LastSeen = lastSeen, StoppedAtUtc = null,
    };

    [Fact]
    public async Task Resolve_SkipsTheStoppedRegistration_AndPicksTheLiveDirectorOnTheSameMachine()
    {
        // Issue #2785. A restart leaves the previous registration in the list, stopped, for up to 24h.
        // The stopped one is deliberately placed FIRST: the old rule was an unordered FirstOrDefault, so
        // this ordering is what made it dispatch into a closed tunnel and record infraStatus=not-started.
        var directors = new List<DirectorDto>
        {
            Stopped("d-corpse", "SOREN_NORTH", new DateTime(2026, 9, 8, 19, 41, 35, DateTimeKind.Utc)),
            Running("d-live", "SOREN_NORTH", new DateTime(2026, 9, 9, 15, 4, 18, DateTimeKind.Utc)),
        };
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch when one is running"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => directors, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("SOREN_NORTH", director: null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("d-live", result.DirectorId);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task Resolve_PrefersTheFreshestHeartbeat_WhenSeveralAreRunning()
    {
        // Two live Directors on one machine is ambiguous by construction (issue #2786); the freshest
        // heartbeat is at least a stated rule rather than dictionary order.
        var directors = new List<DirectorDto>
        {
            Running("d-stale", "SOREN_NORTH", new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc)),
            Running("d-fresh", "SOREN_NORTH", new DateTime(2026, 9, 9, 15, 0, 0, DateTimeKind.Utc)),
        };
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => directors, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("SOREN_NORTH", director: null, CancellationToken.None);

        Assert.Equal("d-fresh", result.DirectorId);
    }

    [Fact]
    public async Task Resolve_EveryRegistrationStopped_AsksTheLauncher_RatherThanResolvingToACorpse()
    {
        // The whole point of excluding the stopped ones: "none running" must reach the launcher branch.
        // Resolving to a corpse instead is what made a missed schedule look like a started one.
        var directors = new List<DirectorDto>
        {
            Stopped("d-corpse-1", "SOREN_NORTH", new DateTime(2026, 9, 8, 19, 41, 35, DateTimeKind.Utc)),
            Stopped("d-corpse-2", "SOREN_NORTH", new DateTime(2026, 9, 8, 20, 0, 0, DateTimeKind.Utc)),
        };
        var launcher = new FakeLauncher(machine =>
        {
            directors.Add(Running("d-launched", machine, new DateTime(2026, 9, 9, 16, 0, 0, DateTimeKind.Utc)));
            return true;
        });
        var resolver = new RegistryDirectorTargetResolver(
            _ => directors, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("SOREN_NORTH", director: null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("d-launched", result.DirectorId);
        Assert.Equal(1, launcher.StartCount);
        Assert.Equal("SOREN_NORTH", launcher.LastMachine);
    }
}
