using CcDirector.Core.Instances;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Running;

/// <summary>The outcome of resolving a cron job's target machine to a runnable Director (#503).</summary>
/// <param name="DirectorId">The chosen Director's id, or null when none could be resolved/launched. In
/// tunnel-only mode a resolved DirectorId is the WHOLE result: the Director is reached over its tunnel
/// by id, not by dialing a control endpoint. There used to be an Endpoint field here carrying the
/// Director's ControlEndpoint; it was always blank in tunnel-only mode and a stale guard rejected the
/// spawn on it, so it has been retired (issue #1727).</param>
/// <param name="Error">A human-readable reason when no Director could be resolved/launched, else null.</param>
public sealed record DirectorTargetResult(string? DirectorId, string? Error);

/// <summary>
/// Resolves a spawn's target to a runnable Director (epic #479, #503).
///
/// TWO WAYS TO NAME THE TARGET, and they behave differently on purpose:
///   - BY MACHINE (<paramref name="director"/> blank): picks the first reachable Director on that
///     machine, and if none is running asks the launcher to start one and waits (bounded) for it to
///     register. "Any Director on that computer will do."
///   - BY DIRECTOR (<paramref name="director"/> set): pins the resolve to ONE named Director - by id
///     or display name - and NEVER launches or substitutes. One machine runs several named instances,
///     so the machine name alone cannot say which; a caller that named one meant that one.
///
/// Behind an interface so the firing engine is unit-testable without a live registry/launcher.
/// Production is <see cref="RegistryDirectorTargetResolver"/>.
/// </summary>
public interface IDirectorTargetResolver
{
    /// <param name="machine">The target machine. Blank is an error UNLESS <paramref name="director"/>
    /// names a Director, which identifies its machine by itself.</param>
    /// <param name="director">Optional Director id or display name. When set, the resolve is pinned to
    /// that one Director: unknown or ambiguous is an error naming it, never another Director.</param>
    Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct);
}

/// <summary>
/// Production resolver. Reads the live Director list (the <see cref="DirectorRegistry"/>) and uses an
/// <see cref="IDirectorLauncher"/> to start a Director on demand. Uses wall-clock waits (small in
/// tests) for the launch poll, so it never depends on the engine's injected clock.
///
/// Hosted Multi-Tenancy (audit H1, gap audit-e): the resolve is confined to the CALLER'S tenant. The
/// <paramref name="listDirectors"/> reader takes the tenant to list (production: the registry's
/// tenant-scoped <c>ListDirectors(TenantId)</c> overload), and the tenant is resolved from
/// <paramref name="resolveTenant"/> - the scope of the current unit of work
/// (<c>() =&gt; _tenantPass.Current</c> in production), which flows across the launch poll's awaits with the
/// ambient scope. A bare machine-name match against the FLEET-GLOBAL list could pick another tenant's
/// Director on the same machine and persist that cross-tenant DirectorId in this tenant's CronRunRecord;
/// scoping to the caller's partition makes that structurally impossible. On self-host the tenant is always
/// Local - one partition, unchanged.
///
/// TWO CALLERS, NOT ONE. This began as the cron firing engine's resolver, and the naming still shows it. It
/// also serves the INTERACTIVE spawn - POST /machines/{machine}/sessions, the route "start a session on
/// another computer" uses - which enters the caller's tenant scope before calling in. Both callers therefore
/// arrive already scoped, which is why a null tenant here is a bug in the caller and not a case to handle.
///
/// THE TENANT IS RESOLVED ONCE PER RESOLVE AND PASSED DOWN, including to the LAUNCH. That matters because
/// the launch is the step that reaches another machine: it used to go out as a fresh loopback request to the
/// Gateway's own relay, which carries no device key and so arrived with no tenant at all.
/// </summary>
public sealed class RegistryDirectorTargetResolver : IDirectorTargetResolver
{
    private readonly Func<TenantId, IEnumerable<DirectorDto>> _listDirectors;
    private readonly Func<TenantId?> _resolveTenant;
    private readonly IDirectorLauncher _launcher;
    private readonly TimeSpan _launchTimeout;
    private readonly TimeSpan _pollInterval;

    /// <param name="listDirectors">
    /// The live Director list for ONE tenant (production: the registry's <c>ListDirectors(TenantId)</c>
    /// overload). It is only ever called with the tenant <paramref name="resolveTenant"/> yields.</param>
    /// <param name="resolveTenant">
    /// The tenant of the current unit of work - the cron fire's scope (production: <c>() =&gt; _tenantPass.Current</c>),
    /// always <see cref="TenantId.Local"/> on self-host. A fire is only ever reached inside a resolved scope, so a
    /// null here (hosted, no scope) is a boundary bug and DENIES (throws) rather than defaulting to a partition.</param>
    /// <param name="launcher">Starts a Director on a machine when none is running.</param>
    public RegistryDirectorTargetResolver(
        Func<TenantId, IEnumerable<DirectorDto>> listDirectors,
        Func<TenantId?> resolveTenant,
        IDirectorLauncher launcher,
        TimeSpan? launchTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _listDirectors = listDirectors ?? throw new ArgumentNullException(nameof(listDirectors));
        _resolveTenant = resolveTenant ?? throw new ArgumentNullException(nameof(resolveTenant));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _launchTimeout = launchTimeout ?? TimeSpan.FromSeconds(90);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
    }

    public async Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct)
    {
        // Resolve the tenant ONCE, at the top, and carry it through both the registry reads and the launch.
        // Reading it separately per step would let a resolve that starts in one scope finish in another, and
        // would leave the LAUNCH - the one step that reaches another machine - with no tenant at all.
        var tenant = RequireTenant();

        // A NAMED Director answers the whole question, machine included, so it is resolved first and
        // separately: no launcher, no first-available, no fallback of any kind.
        if (!string.IsNullOrWhiteSpace(director))
            return PickNamed(tenant, machine, director.Trim());

        if (string.IsNullOrWhiteSpace(machine))
            return new DirectorTargetResult(null, "no target machine");

        var d = PickReachable(tenant, machine);
        if (d is not null)
            return new DirectorTargetResult(d.DirectorId, null);

        // No Director running on the machine: ask THIS TENANT'S launcher to start one, then wait for it to
        // register. A launcher another tenant registered under the same bare machine name is not reachable
        // from here and is never asked.
        FileLog.Write($"[RegistryDirectorTargetResolver] no Director on tenant={tenant.Value}, machine={machine}; asking launcher to start one");
        if (!await _launcher.StartAsync(tenant, machine, ct))
            return new DirectorTargetResult(null, $"no Director on '{machine}' and the launcher could not start one");

        var deadline = DateTime.UtcNow + _launchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(_pollInterval, ct);
            d = PickReachable(tenant, machine);
            if (d is not null)
            {
                FileLog.Write($"[RegistryDirectorTargetResolver] launched Director registered on machine={machine}: {d.DirectorId}");
                return new DirectorTargetResult(d.DirectorId, null);
            }
        }
        return new DirectorTargetResult(null, $"launched a Director on '{machine}' but none registered within {_launchTimeout.TotalSeconds:0}s");
    }

    /// <summary>
    /// The tenant of the current unit of work. On hosted a null means the caller path was not bound at its
    /// tenant boundary, which is a bug in that path and DENIES loudly rather than defaulting to a partition.
    /// </summary>
    private TenantId RequireTenant() =>
        _resolveTenant()
            ?? throw new InvalidOperationException(
                "A machine target resolution ran with no tenant scope in effect. The Director list and the " +
                "launcher registry are both partitioned by tenant; the caller path was not bound at its " +
                "tenant boundary.");

    /// <summary>
    /// Resolve ONE named Director - by id or by display name, case-insensitively - within the caller's
    /// tenant, optionally narrowed to a machine. A named Director identifies its own machine, so
    /// <paramref name="machine"/> is only a further filter and may be blank.
    ///
    /// THREE OUTCOMES, ALL LOUD. Exactly one match resolves. No match is an error naming what was asked
    /// for - it is NOT "use another Director on that machine", and it is NOT a launch: the launcher
    /// starts "a Director on a machine" and has no way to start a PARTICULAR named instance, so calling
    /// it here would start the wrong one and then silently spawn the session there. Two matches is an
    /// error listing both, because picking either would be a guess the caller cannot see.
    /// </summary>
    private DirectorTargetResult PickNamed(TenantId tenant, string? machine, string director)
    {
        // Same tenant confinement as the machine match: another tenant's Director may carry the same
        // display name, and resolving to it would cross the partition on the caller's behalf.
        //
        // DirectorHandle.Pick applies ID PRECEDENCE - an exact id match wins outright and display names
        // are not consulted - so a Director renamed to another's id cannot make that id ambiguous. It is
        // the same rule the Director floor resolves with, from one implementation, because a floor and a
        // Gateway that disagreed about what a name means would route a session somewhere the caller was
        // never told about.
        var candidates = DirectorHandle
            .Pick(_listDirectors(tenant), director, x => x.DirectorId, x => x.DisplayName)
            .Where(x => string.IsNullOrWhiteSpace(machine)
                     || string.Equals(x.MachineName, machine, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var on = string.IsNullOrWhiteSpace(machine) ? "" : $" on '{machine}'";

        if (candidates.Count == 0)
        {
            FileLog.Write($"[RegistryDirectorTargetResolver] no Director '{director}'{on} in tenant={tenant.Value}");
            return new DirectorTargetResult(null,
                $"no Director '{director}'{on} is registered. It is either not running or is named " +
                "something else - list what is registered with: cc-devthrottle director list");
        }

        if (candidates.Count > 1)
        {
            var listed = string.Join(", ", candidates.Select(c => $"{c.DirectorId} ({c.MachineName})"));
            FileLog.Write($"[RegistryDirectorTargetResolver] '{director}'{on} is ambiguous: {listed}");
            return new DirectorTargetResult(null,
                $"'{director}' names {candidates.Count} Directors ({listed}). Name one by its Director id.");
        }

        var chosen = candidates[0];
        FileLog.Write($"[RegistryDirectorTargetResolver] '{director}' resolved to {chosen.DirectorId} on {chosen.MachineName}");
        return new DirectorTargetResult(chosen.DirectorId, null);
    }

    /// <summary>
    /// The RUNNING Director on the machine, freshest heartbeat first, or null when none is running.
    /// Tunnel-only: a running Director is reached over its tunnel by id, so this requires neither a
    /// non-empty ControlEndpoint nor an advertised-endpoint reachability state (both artifacts of the
    /// deleted HTTP-dial path). What it does require is that the Director has not said goodbye - see
    /// the note in the body, and issue #2785.
    /// </summary>
    private DirectorDto? PickReachable(TenantId tenant, string machine)
    {
        // Confine the machine-name match to the caller's OWN tenant partition (audit H1, gap audit-e): a
        // fleet-global scan could return another tenant's Director that happens to run on the same machine.
        //
        // REGISTERED IS NOT RUNNING (issue #2785). A Director that said goodbye keeps its registration
        // until the 24h eviction horizon - MarkStopped stamps StoppedAtUtc and deliberately never removes
        // the entry - so for a full day after any restart a machine carries both a corpse and the live
        // Director. An unordered FirstOrDefault over the registry dictionary picked between them by
        // hash-bucket order, which is stable for the life of the Gateway process: lose that draw and EVERY
        // schedule on that machine dispatches into a closed tunnel and records infraStatus=not-started
        // against the dead Director's id, silently, until the corpse is evicted.
        //
        // So skip the stopped ones and prefer the freshest heartbeat. When every registration on the
        // machine is stopped this returns null, which is the right answer and not a failure: the caller
        // then asks the launcher to start one, which is what should have happened all along.
        return _listDirectors(tenant)
            .Where(x => string.Equals(x.MachineName, machine, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.StoppedAtUtc is null)
            .OrderByDescending(x => x.LastSeen ?? DateTime.MinValue)
            .FirstOrDefault();
    }
}
