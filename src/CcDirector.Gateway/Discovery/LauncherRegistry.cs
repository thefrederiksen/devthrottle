using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// In-memory registry of live cc-launcher processes.
///
/// A launcher POSTs <see cref="LauncherRegistrationRequest"/> to /launchers/register on
/// startup and heartbeats every 30 s, so the Gateway can LIST which machines have a live launcher
/// and how stale each one is.
///
/// Remove-the-network-port mission, phase 6: this registry used to also hold each launcher's port,
/// network address and bearer token, because the relay dialed the launcher's REST interface back over
/// them. That interface is gone - commands ride DOWN the persistent stream connection
/// (<see cref="Streaming.LauncherConnectionRegistry"/>) - so this is presence-and-identity metadata
/// only. Holding a dial-back address or credential for a surface that no longer exists would be an
/// invitation to build a second door.
///
/// Keys are the composite <see cref="LauncherKey"/> - the OWNING TENANT plus the machine name
/// (case-insensitive). A machine name is unique only within a tenant, so the tenant is half the key:
/// one tenant naming a machine can only ever reach its OWN launcher entry, never another tenant's launcher
/// registered under the same bare machine name. This mirrors the composite (tenant, id) keying of
/// <see cref="DirectorRegistry"/>.
///
/// Issue #331.
/// </summary>
public sealed class LauncherRegistry
{
    /// <summary>How long without a heartbeat before a launcher is considered stale and swept.</summary>
    public static TimeSpan HeartbeatTimeout { get; } = TimeSpan.FromSeconds(90);

    /// <summary>The composite key: the owning tenant plus the machine name (canonicalized, case-insensitive).</summary>
    private readonly record struct LauncherKey(TenantId Tenant, string Machine);

    private static LauncherKey Key(TenantId tenant, string machineName) =>
        new(tenant, machineName.Trim().ToLowerInvariant());

    private sealed class Entry
    {
        public required LauncherDto Dto;
        public DateTime LastSeenAt;
    }

    private readonly ConcurrentDictionary<LauncherKey, Entry> _launchers = new();

    // THE LAST STREAM CLOSE PER MACHINE, KEPT APART FROM THE ROWS (issue #4423, review finding 1). A launcher that quits
    // cleanly - which is how both update paths restart it - closes its stream and then DELETEs its registration, and
    // the new process registers before it opens its own stream. Held on the row, the close went with the DELETE and the
    // new launcher read as too old. Held here, Remove does not touch it and Upsert hands it to the new row. One entry
    // per machine an account has ever streamed from, so it is bounded by the machines, not by time.
    private readonly ConcurrentDictionary<LauncherKey, DateTime> _streamClosedAt = new();

    private Timer? _sweepTimer;

    private readonly TimeProvider _clock;

    /// <summary>A registry on the system clock - what the Gateway runs.</summary>
    public LauncherRegistry() : this(TimeProvider.System) { }

    /// <summary>
    /// A registry whose idea of "now" - the registration and heartbeat stamps, and the sweep's cutoff - comes
    /// from <paramref name="clock"/>, so a test can fix it instead of reading the machine's clock.
    /// </summary>
    public LauncherRegistry(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// "Now" on this registry's clock: the moment its registration, heartbeat and stream-close stamps are
    /// judged against. Read from here rather than from the machine, so the judge and the stamps share a clock.
    /// </summary>
    public DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Register or refresh the calling tenant's launcher. Returns the stored <see cref="LauncherDto"/> for
    /// the 201/200 response body.
    /// </summary>
    public LauncherDto Upsert(TenantId tenant, LauncherRegistrationRequest req)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        // A RE-REGISTRATION CARRIES THE STREAM'S LAST CLOSE (issue #4423). A launcher restarting into a new version
        // registers BEFORE it opens its stream, and that window is exactly when "heartbeating, no stream" must
        // read as restarting rather than too old - so the evidence must survive the new process's registration,
        // and the old one's DELETE before it (see _streamClosedAt).
        var dto = new LauncherDto
        {
            MachineName = req.MachineName,
            Pid = req.Pid,
            Version = req.Version,
            StartedAt = req.StartedAt,
            LastSeenAt = now,
            RegisteredAt = now,
            StreamClosedAt = _streamClosedAt.TryGetValue(Key(tenant, req.MachineName), out var closedAt) ? closedAt : null,
        };

        _launchers[Key(tenant, req.MachineName)] = new Entry
        {
            Dto = dto,
            LastSeenAt = now,
        };

        FileLog.Write($"[LauncherRegistry] Upsert: tenant={tenant.Value}, machine={req.MachineName}, pid={req.Pid}, version={req.Version}");
        return dto;
    }

    /// <summary>
    /// Record a heartbeat from a known launcher of the calling tenant. Returns true if found;
    /// false (-> 410) if unknown (it should re-register).
    /// </summary>
    public bool Heartbeat(TenantId tenant, string machineName)
    {
        if (!_launchers.TryGetValue(Key(tenant, machineName), out var entry))
        {
            FileLog.Write($"[LauncherRegistry] Heartbeat: unknown tenant={tenant.Value}, machine={machineName} -> 410");
            return false;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        entry.LastSeenAt = now;
        entry.Dto.LastSeenAt = now;
        FileLog.Write($"[LauncherRegistry] Heartbeat: tenant={tenant.Value}, machine={machineName}");
        return true;
    }

    /// <summary>
    /// Record that the calling tenant's launcher for that machine closed its command stream at
    /// <paramref name="closedAtUtc"/>. Called when the stream connection registry clears that machine's live
    /// stream. Kept whether or not the machine has a registration row right now, because a launcher that quits
    /// cleanly may delete its row before or after its stream is seen to close, and the next registration reads it.
    /// </summary>
    public void NoteStreamClosed(TenantId tenant, string machineName, DateTime closedAtUtc)
    {
        _streamClosedAt[Key(tenant, machineName)] = closedAtUtc;
        if (_launchers.TryGetValue(Key(tenant, machineName), out var entry))
            entry.Dto.StreamClosedAt = closedAtUtc;
        FileLog.Write($"[LauncherRegistry] NoteStreamClosed: tenant={tenant.Value}, machine={machineName}, at={closedAtUtc:o}, registered={entry is not null}");
    }

    /// <summary>
    /// Remove the calling tenant's launcher entry (called on graceful launcher shutdown).
    /// </summary>
    public void Remove(TenantId tenant, string machineName)
    {
        if (_launchers.TryRemove(Key(tenant, machineName), out _))
            FileLog.Write($"[LauncherRegistry] Remove: tenant={tenant.Value}, machine={machineName}");
    }

    /// <summary>
    /// Look up the calling tenant's launcher by machine name. Returns null if not registered.
    /// </summary>
    public LauncherDto? Get(TenantId tenant, string machineName) =>
        _launchers.TryGetValue(Key(tenant, machineName), out var e) ? e.Dto : null;

    /// <summary>
    /// The calling tenant's registered launchers, as public DTOs. Never another tenant's.
    /// </summary>
    public IReadOnlyList<LauncherDto> ListLaunchers(TenantId tenant) =>
        _launchers
            .Where(kv => kv.Key.Tenant.Equals(tenant))
            .Select(kv => kv.Value.Dto)
            .ToList();

    /// <summary>
    /// Start the periodic stale-entry sweep (removes launchers that have not heartbeat
    /// within <see cref="HeartbeatTimeout"/>). Tenant-agnostic: it removes stale entries by full key.
    /// </summary>
    public void StartSweep()
    {
        _sweepTimer = new Timer(Sweep, null,
            dueTime: TimeSpan.FromSeconds(30),
            period: TimeSpan.FromSeconds(30));
        FileLog.Write("[LauncherRegistry] Sweep timer started (30s interval)");
    }

    private void Sweep(object? _)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - HeartbeatTimeout;
        foreach (var kv in _launchers)
        {
            if (kv.Value.LastSeenAt < cutoff)
            {
                if (_launchers.TryRemove(kv.Key, out var removed))
                    FileLog.Write($"[LauncherRegistry] Sweep: removed stale launcher tenant={kv.Key.Tenant.Value}, machine={kv.Key.Machine} (last-seen={removed.LastSeenAt:o})");
            }
        }
    }

    public void Dispose()
    {
        _sweepTimer?.Dispose();
    }
}
