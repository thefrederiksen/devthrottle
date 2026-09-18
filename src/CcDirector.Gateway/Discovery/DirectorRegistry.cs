using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// In-memory registry of live Directors. Two ingress paths feed it, both kept
/// permanently:
///
///   1. Filesystem watch on %LOCALAPPDATA%\cc-director\config\director\instances\.
///      Used by same-machine Directors. Same-machine Directors do not need any
///      Gateway URL configured to be discovered. See <see cref="InstancesDirectory"/>.
///
///   2. HTTP register / heartbeat / unregister. Used by Directors that have
///      <c>gateway.url</c> configured (typically cross-machine). The Director POSTs
///      <see cref="Upsert"/> on startup, calls <see cref="Heartbeat"/> every 15 s, and
///      DELETEs via <see cref="Remove"/> on graceful shutdown.
///
/// De-duplication: keys are <c>(tenantId, directorId)</c> - see <see cref="DirectorKey"/> for why the
/// tenant is part of the key and not merely recorded beside the entry. If both paths report the same id
/// within one tenant, the HTTP entry wins because it carries <see cref="DirectorDto.TailnetEndpoint"/>
/// which the FSW path cannot provide.
///
/// A client request is served <see cref="ListDirectors(TenantId)"/> or <see cref="Get(TenantId, string)"/>,
/// which answer from one tenant's partition only. The no-tenant <see cref="ListDirectors()"/> overload is a
/// fleet-global internal view (the roster fan-out / reconcile poll) and is NOT an answer to a client. There
/// is deliberately NO bare-id <c>Get</c> overload (MTR-01): a Director is only ever resolved WITH the tenant
/// that owns it, so a client-supplied id can never resolve, read, or reach another tenant's Director.
/// </summary>
public sealed class DirectorRegistry : IDisposable
{
    public static string InstancesDirectory { get; } = CcStorage.DirectorInstances();

    /// <summary>The directory this registry actually watches (the shared default in production).</summary>
    public string WatchDirectory { get; }

    /// <param name="instancesDirectory">
    /// Override the watched instances directory. Tests pass an isolated temp directory so a
    /// real Director running on the dev machine is never discovered by (or polluted with)
    /// test hosts. Production omits it and uses the shared <see cref="InstancesDirectory"/>.
    /// </param>
    public DirectorRegistry(string? instancesDirectory = null)
    {
        WatchDirectory = instancesDirectory ?? InstancesDirectory;
    }

    /// <summary>
    /// Epic #1159 step A: the eviction horizon - how long a machine may be gone before its registry entry
    /// is swept and its sessions leave the roster. This is THE ONLY elapsed-time rule in the Gateway that
    /// removes a session, and it is deliberately measured in hours.
    ///
    /// IT USED TO BE SIXTY SECONDS, and that was the second half of this step's defect. The roster refused
    /// to SERVE a machine after twenty seconds of silence, and sixty seconds later the registry DELETED it
    /// outright - taking its cached sessions, its snoozes (self-host <c>ClearForDirector</c>) and its
    /// session numbers with them. Restoring the roster read alone would therefore have bought about a
    /// minute; the acceptance for this step is five. A short timeout is the right instrument for "may I
    /// route a command at this machine" - which is the tunnel connection, and is answered elsewhere - and
    /// the wrong one for "does this machine exist".
    ///
    /// The snooze and session-number destruction described above is HISTORY, not current behaviour: both
    /// were deleted (inspection 2, finding 1) because their liveness guard could not be atomic with the
    /// destruction. Passing this horizon now drops a machine from the READ MODEL and does nothing else.
    /// </summary>
    public static TimeSpan DefaultEvictionHorizon { get; } =
        TimeSpan.FromHours(Core.Configuration.GatewayConfig.DefaultDirectorEvictionHorizonHours);

    /// <summary>
    /// This registry's eviction horizon. Settable so a test can drive a REAL eviction in milliseconds
    /// rather than asserting a constant - the destructibility control the roster tests need, since a
    /// serve-everything bug and a correct fix are indistinguishable without proving something still
    /// removes sessions eventually.
    /// </summary>
    public TimeSpan EvictionHorizon { get; init; } = DefaultEvictionHorizon;

    // Gateway Cleanup mission (post-cut): the reachability circuit-breaker (consecutive-failure counting,
    // cooldown, unreachable-evict) and the advertised-endpoint re-verification state machine are DELETED.
    // Liveness is the tunnel connection itself now, not an HTTP probe, so there is nothing to circuit-break.

    /// <summary>
    /// Issue #1847: the registry key. It is COMPOSITE - the owning tenant AND the director id - so a write
    /// naming a director id can only ever reach the entry belonging to the tenant doing the writing.
    ///
    /// This is the whole fix for the cross-tenant WRITE. The tunnel Hello's director id is chosen by the
    /// client, and the entry used to be keyed by that id alone, so tenant A saying Hello with tenant B's
    /// director id (enumerated from the then-fleet-global <c>GET /directors</c>) overwrote B's machine name,
    /// operating system user, process id and client version. Keying by (tenant, id) makes naming another
    /// tenant's entry STRUCTURALLY IMPOSSIBLE rather than merely refused, so there is no id-ownership
    /// judgement to make and - the reason this shape was chosen over rejecting the Hello - no lockout: a
    /// machine re-enrolled from one account to another keeps its local director id and simply registers a
    /// fresh entry under its new tenant, instead of having every Hello aborted forever.
    ///
    /// It matches the composite tenant primary keys this codebase already adopted for workflows, workflow
    /// versions and mission notes.
    /// </summary>
    private readonly record struct DirectorKey(TenantId Tenant, string DirectorId);

    private readonly ConcurrentDictionary<DirectorKey, DirectorDto> _directors = new();

    /// <summary>
    /// Look up THE Local-tenant entry for a bare director id. The HTTP discovery plane (register / heartbeat /
    /// unregister) is the same-machine path and always keys its entries to <see cref="TenantId.Local"/> (see
    /// <see cref="Upsert"/>), so a bare id from that plane resolves to exactly one partition - Local - and
    /// never scans across tenants. On the hosted Gateway a Local entry is served to no account, so this stays
    /// deny-by-default there. Returns false when no Local entry exists for the id.
    /// </summary>
    private bool TryResolveLocalKey(string directorId, out DirectorKey key)
    {
        key = new DirectorKey(TenantId.Local, directorId);
        return !string.IsNullOrEmpty(directorId) && _directors.ContainsKey(key);
    }

    /// <summary>
    /// Directors that have answered at least one fleet probe (issue #197). Deliberately
    /// NOT cleared by <see cref="Upsert"/>: the unreachable-evict / 410 / re-register
    /// cycle re-registers the same live process every few minutes, and whether it EVER
    /// answered is exactly the signal that distinguishes "endpoint was never provisioned
    /// (check Tailscale Serve on that machine)" from "was fine, went dark". Cleared on
    /// graceful unregister and when the heartbeat-stale sweep removes a dead process -
    /// a future process under the same id starts with a truthful blank slate.
    /// </summary>
    /// Keyed by BARE director id, not by the composite key: it records what a Director PROCESS has ever
    /// done, it is never served to a client, and keeping it bare leaves its lifecycle exactly as it was.
    private readonly ConcurrentDictionary<string, bool> _everReachable = new();

    /// <summary>
    /// Serialises the stale sweep's REMOVAL DECISION against the two writes that make a Director live again.
    ///
    /// Inspection 1, finding 1: the sweep used to judge a snapshot taken by <c>ToArray()</c> and then remove
    /// BY KEY, so a Director that reconnected between the judgement and the removal was destroyed anyway -
    /// its session numbers released, its pushed entry forgotten, its snoozes deleted on self-host.
    ///
    /// An atomic compare-and-remove on the VALUE does not fix this and is the trap worth naming:
    /// <see cref="Heartbeat"/> mutates <c>LastSeen</c> IN PLACE on the live object, so the same instance
    /// simply carries a newer timestamp, a reference comparison still matches, and the removal still happens.
    /// The decision has to re-READ and re-JUDGE the current entry, which is what this gate makes possible.
    ///
    /// The removal EVENT is raised outside the gate on purpose - a subscriber that called back into the
    /// registry while it was held would deadlock.
    /// </summary>
    private readonly object _livenessGate = new();

    /// <summary>
    /// Test-only seam, fired by <see cref="SweepStale"/> after it has judged an entry stale and BEFORE it
    /// takes <see cref="_livenessGate"/> to re-judge and remove - that is, inside the exact window finding 1
    /// describes. A test reconnects the Director from inside this callback and asserts it survives.
    ///
    /// It proves the ORDERING window exists and is closed. It proves NOTHING about the concurrent race under
    /// real thread scheduling, and it must not be described as if it did. Null and inert in production.
    /// </summary>
    /// <remarks>
    /// Deliberately parameterless. It carried the <c>DirectorKey</c> being judged until the compiler
    /// pointed out that the key type is PRIVATE, so an internal field of that type is more accessible
    /// than its own type. Rather than widen a private type to suit a test seam - which would let test
    /// scaffolding dictate production visibility - the seam drops the argument it never needed: a test
    /// already knows which Director it registered.
    /// </remarks>
    internal Action? OnSweepJudgedForTest;

    private FileSystemWatcher? _watcher;
    private Timer? _sweeper;
    private bool _disposed;

    /// <summary>
    /// Raised when a Director appears (file created, local registration, or tunnel registration). The payload
    /// carries the owning tenant because a Director identifier is unique only within a tenant.
    /// </summary>
    public event Action<DirectorArrival>? OnDirectorAdded;

    /// <summary>
    /// Raised when a Director disappears (file removed, HTTP unregister, or stale).
    ///
    /// It carries a <see cref="DirectorRemoval"/> - the TENANT as well as the id - and not a bare id, and
    /// that is the whole point of the type. Issue #1847 made the registry's own key composite, so the
    /// removal itself is already tenant-correct; but the event used to be typed <c>Action&lt;string&gt;</c>,
    /// so it DROPPED the tenant the removal path had in its hand and every subscriber inherited the loss -
    /// notably the roster cache's forget, which then had nothing to scope by.
    ///
    /// A signature that cannot represent the owner is not a smaller problem than a missing check; it is the
    /// same problem with no place to put the check. Keep the tenant on this event.
    /// </summary>
    public event Action<DirectorRemoval>? OnDirectorRemoved;

    /// <summary>
    /// Raise <see cref="OnDirectorRemoved"/> without letting a subscriber kill the process.
    ///
    /// This is an ENTRY-POINT boundary in the CodingStyle sense (an external event subscription): every
    /// caller is either a FileSystemWatcher callback or the stale-sweep timer, both of which run on
    /// thread-pool threads with NO enclosing try/catch. An exception thrown by a subscriber there is
    /// UNHANDLED, so it does not merely fail the removal - it terminates the whole Gateway process.
    ///
    /// Not hypothetical, though it is now HISTORY: a subscriber used to write the tenant-scoped snooze
    /// store, and when that store's database was unavailable the throw came straight up this path and took
    /// the process down - observed as a test run that ABORTED partway through while still reporting exit
    /// code 0. That subscriber is deleted (see below), so this particular fault cannot recur; the guard
    /// stays because the hazard is structural and belongs to the event, not to any one subscriber. The
    /// failure is logged LOUD - this catches to keep the process alive, not to hide the fault.
    ///
    /// Each subscriber is invoked INDEPENDENTLY. A plain Invoke on a multicast delegate stops at the first
    /// handler that throws, so one faulting subscriber would silently deprive every later one of the event
    /// - trading a process crash for a quiet partial removal, which is harder to notice and just as wrong.
    /// That matters even though there is currently only ONE permanent subscriber, because the next one
    /// added must not be able to suppress it.
    ///
    /// WHAT SUBSCRIBES TODAY, so this document cannot be read as a list of cleanups to maintain: exactly
    /// one permanent subscriber, <c>PushedSessionStore.ForgetIfDisconnected</c>, plus a transient listener
    /// on the fleet event stream that only enqueues a notification and destroys nothing. The session-number
    /// release and the snooze clear that used to be here are DELETED (epic #1159 step A, inspection 2
    /// finding 1) - their liveness check could not be made atomic with their destruction, so a Director
    /// reconnecting in between was destroyed while live. Do not add them back.
    /// </summary>
    /// <remarks>
    /// Takes the <see cref="DirectorKey"/> the removal already resolved, so the tenant reaches the
    /// subscribers rather than being dropped at this signature. Every raise site below holds a key.
    /// </remarks>
    private void RaiseDirectorRemoved(DirectorKey key)
    {
        var removal = new DirectorRemoval(key.Tenant, key.DirectorId);
        foreach (var handler in OnDirectorRemoved?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try
            {
                ((Action<DirectorRemoval>)handler)(removal);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorRegistry] OnDirectorRemoved subscriber FAILED for tenant={key.Tenant.Value}, director={key.DirectorId}: {ex}");
            }
        }
    }

    /// <summary>
    /// THE single removal seam. Every path that drops a Director - graceful unregister, the stale sweep, the
    /// instance-file delete/rename - goes through here, so there is ONE place that knows how an entry leaves
    /// the registry. Returns true when an entry was actually present. It deliberately does NOT touch
    /// <c>_everReachable</c>: that flag's clear-on-graceful-goodbye lifecycle is the callers' (see the note on
    /// the field) and centralising it here would silently change it.
    /// </summary>
    private bool TryRemoveEntry(DirectorKey key, out DirectorDto? removed)
    {
        var present = _directors.TryRemove(key, out removed);
        if (present) _registeredBy.TryRemove(key, out _);
        return present;
    }

    /// <summary>
    /// The credential each stream-registered Director said Hello on (<see cref="Util.AuthMiddleware.RegisteringCredential"/>),
    /// by (tenant, id). Not a second identity: it is the device key the hub already authenticated and bound this
    /// Director id to, kept so an HTTP route can ask whether its caller is that Director (the Message Load
    /// mission, inspection 11). Several Directors on one machine share one device key, so one credential may
    /// register several ids. Cleared with the entry.
    /// </summary>
    private readonly ConcurrentDictionary<DirectorKey, string> _registeredBy = new();

    /// <summary>
    /// True when <paramref name="directorId"/> is registered in <paramref name="tenant"/> AND its stream Hello was
    /// authenticated by <paramref name="credential"/>. False for a null credential, an unknown Director, or a
    /// Director registered some other way (a file or an HTTP registration carries no credential).
    /// </summary>
    public bool IsRegisteredByCredential(TenantId tenant, string directorId, string? credential)
    {
        if (string.IsNullOrEmpty(directorId) || string.IsNullOrEmpty(credential)) return false;
        var key = new DirectorKey(tenant, directorId);
        return _directors.ContainsKey(key)
               && _registeredBy.TryGetValue(key, out var registered)
               && string.Equals(registered, credential, StringComparison.Ordinal);
    }

    // ===== HTTP path =====

    /// <summary>
    /// Add or refresh an HTTP-registered Director. Idempotent. The dto is stamped
    /// with <c>Source="http"</c> and <c>LastSeen=UtcNow</c>. If an FSW entry already
    /// exists for the same id, the HTTP entry replaces it (it has the tailnet endpoint).
    /// </summary>
    public DirectorDto Upsert(DirectorRegistrationRequest req)
    {
        if (string.IsNullOrEmpty(req.DirectorId))
            throw new ArgumentException("directorId is required", nameof(req));

        var now = DateTime.UtcNow;
        var dto = new DirectorDto
        {
            DirectorId = req.DirectorId,
            Pid = req.Pid,
            StartedAt = req.StartedAt == default ? now : req.StartedAt,
            ControlEndpoint = req.TailnetEndpoint, // HTTP path: the tailnet endpoint IS the control endpoint
            TailnetEndpoint = req.TailnetEndpoint,
            // Issue #324: a flagged no-endpoint registration carries the Director's own
            // reason; readers (fan-outs) must not probe an endpoint the Director declared dead.
            EndpointUnreachableReason = string.IsNullOrWhiteSpace(req.EndpointUnreachableReason) ? null : req.EndpointUnreachableReason,
            MachineName = req.MachineName,
            User = req.User,
            Version = req.Version,
            SchemaVersion = 1,
            LastSeen = now,
            Source = "http",
        };

        // The HTTP register path is the same-machine / self-host path; it carries no account credential, so
        // its entries belong to the single Local tenant. On hosted, where a request's tenant is a real
        // account, a Local-keyed entry is therefore served to no account - which is the correct answer.
        var key = new DirectorKey(TenantId.Local, req.DirectorId);
        // Under the liveness gate for the same reason as RegisterFromStream: the sweep evicts "http" entries on
        // exactly the same staleness rule, so this refresh has to be visible to the sweep's re-judge or the
        // HTTP path keeps the hole the stream path just closed.
        bool existed;
        lock (_livenessGate)
        {
            existed = _directors.TryGetValue(key, out _);
            _directors[key] = dto;
        }
        FileLog.Write(dto.EndpointUnreachableReason is null
            ? $"[DirectorRegistry] Upsert (http): id={dto.DirectorId}, endpoint={dto.TailnetEndpoint}, existed={existed}"
            : $"[DirectorRegistry] Upsert (http, FLAGGED no reachable endpoint): id={dto.DirectorId}, existed={existed}, reason={dto.EndpointUnreachableReason}");
        if (!existed)
            OnDirectorAdded?.Invoke(new DirectorArrival(key.Tenant, dto));
        return dto;
    }

    /// <summary>
    /// Gateway Cleanup mission (tunnel-only): register (or refresh) a Director from its DirectorHub stream
    /// Hello. The tunnel is the ONLY registration path now (HTTP register is gone), so a live stream IS the
    /// Director's presence. Stamped <c>Source="stream"</c> with an EMPTY control/tailnet endpoint - the Gateway
    /// never dials a Director, it only reaches it down this stream. Marks it state-reporting so the reconcile
    /// poll skips it. Idempotent; refreshes LastSeen on every Hello (connect + reconnect + periodic re-push).
    ///
    /// Issue #1847: <paramref name="tenant"/> is the tenant the Hello's AUTHENTICATED device key resolved to,
    /// and it is REQUIRED - it is half of the registry key, so this call can only ever create or refresh THIS
    /// tenant's own entry and is structurally incapable of naming another tenant's, however the client chose
    /// its director id. It is never read from the Hello payload, and there is no default: an unresolved tenant
    /// is a rejected Hello at the hub, not a registration under a guessed owner.
    /// </summary>
    public DirectorDto RegisterFromStream(string directorId, string machineName, string user, string version, int pid, DateTime startedAt, TenantId tenant,
        string displayName = "", string? registeredByCredential = null)
    {
        if (string.IsNullOrEmpty(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));
        if (!tenant.IsValid)
            throw new ArgumentException("a valid tenant is required to register a Director", nameof(tenant));

        // devthrottle_internal#1176: the display name is CLIENT-WRITTEN and cosmetic, so it is sanitized
        // here - the single place every stream registration passes through - before it can reach the
        // cockpit: control characters stripped, length clamped. Never keyed on, never trusted for tenancy.
        var cleanDisplayName = SanitizeDisplayName(displayName);

        var now = DateTime.UtcNow;
        var key = new DirectorKey(tenant, directorId);
        // Merge with any existing entry - THIS TENANT'S entry for this id, never another's. A Hello field that
        // arrives empty must not wipe a value the entry already carries (e.g. a file-discovered entry's machine
        // name). Production Hellos always carry the full identity; this just makes re-registration and
        // mixed-source ordering safe.
        //
        // The merge-and-replace runs under the liveness gate (see _livenessGate): this is the write that makes
        // a reconnecting Director live again, and the stale sweep must either see it or not have decided yet.
        // Without the gate the sweep can judge the OLD entry stale and then remove the new one by key.
        DirectorDto dto;
        bool existed;
        lock (_livenessGate)
        {
            _directors.TryGetValue(key, out var existing);
            dto = new DirectorDto
            {
                DirectorId = directorId,
                Pid = pid > 0 ? pid : (existing?.Pid ?? 0),
                StartedAt = startedAt != default ? startedAt : (existing?.StartedAt ?? now),
                ControlEndpoint = "",     // tunnel-only: the Gateway never dials this Director
                TailnetEndpoint = null,
                MachineName = !string.IsNullOrEmpty(machineName) ? machineName : (existing?.MachineName ?? ""),
                User = !string.IsNullOrEmpty(user) ? user : (existing?.User ?? ""),
                // devthrottle_internal#1176: same merge guard as the fields above - an empty name from an
                // older build's Hello is "no statement", not an instruction to erase the stored name.
                DisplayName = !string.IsNullOrEmpty(cleanDisplayName) ? cleanDisplayName : (existing?.DisplayName ?? ""),
                Version = !string.IsNullOrEmpty(version) ? version : (existing?.Version ?? ""),
                SchemaVersion = 1,
                LastSeen = now,
                Source = "stream",
                // StoppedAtUtc is deliberately NOT merged from the existing entry - a Hello IS the statement
                // that this Director is running, so the previous process's farewell must not survive into it.
                // Leaving it to the merge above would mean a restarted Director stayed "not running" forever.
            };
            existed = existing is not null;
            _directors[key] = dto;
            // The credential this Hello authenticated with. A reconnect on another key re-binds the id to it; a
            // registration that names none leaves no binding, so nothing can prove itself to be this Director.
            if (registeredByCredential is not null) _registeredBy[key] = registeredByCredential;
            else _registeredBy.TryRemove(key, out _);
        }
        _stateReporting.TryAdd(directorId, true);
        if (!existed)
        {
            FileLog.Write($"[DirectorRegistry] RegisterFromStream: id={directorId}, machine={machineName}, version={version}");
            OnDirectorAdded?.Invoke(new DirectorArrival(key.Tenant, dto));
        }
        return dto;
    }

    /// <summary>Longest display name the registry will store; anything longer is truncated, not rejected.</summary>
    public const int MaxDisplayNameLength = 80;

    /// <summary>
    /// devthrottle_internal#1176: normalize a client-written display name before it is stored and served
    /// to the cockpit. Trims, strips control characters (a name must never carry escape sequences into a
    /// terminal or log line), and clamps to <see cref="MaxDisplayNameLength"/>. Empty in, empty out.
    /// </summary>
    internal static string SanitizeDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "";
        var chars = displayName.Trim().Where(c => !char.IsControl(c)).ToArray();
        var clean = new string(chars);
        return clean.Length <= MaxDisplayNameLength ? clean : clean[..MaxDisplayNameLength];
    }

    /// <summary>
    /// Refresh the heartbeat timestamp on an existing HTTP-registered Director.
    /// Returns false if the id is unknown (caller can choose to ask the Director to re-register).
    /// </summary>
    public bool Heartbeat(string directorId)
    {
        if (!TryResolveLocalKey(directorId, out var key)) return false;
        // Under the liveness gate: this is an IN-PLACE write to the live object, which is precisely why the
        // sweep cannot judge a captured reference (see _livenessGate). Taking the same gate is what makes the
        // sweep's re-read see this heartbeat rather than race it.
        lock (_livenessGate)
        {
            if (!_directors.TryGetValue(key, out var existing)) return false;
            existing.LastSeen = DateTime.UtcNow;
        }
        return true;
    }

    /// <summary>
    /// Record that this tenant's Director said goodbye - the tunnel farewell sent at the start of an orderly
    /// shutdown (<c>DirectorHub.DirectorStopping</c>). Stamps <see cref="DirectorDto.StoppedAtUtc"/> so the
    /// roster read can tell a Director that STOPPED from one it cannot REACH. Returns false when this tenant
    /// has no entry for the id.
    ///
    /// It does NOT remove the entry, and that is the point. Dropping a Director the moment its tunnel closes
    /// is the behaviour <c>OnDisconnectedAsync</c> deliberately does not have (a brief reconnect must not flap
    /// the roster, and the machine's cached sessions have to outlive the gap); this marks the entry instead, so
    /// everything that depends on it surviving is untouched and only the VERDICT about it changes.
    ///
    /// Keyed by (tenant, id) like every other write here, so a farewell can only ever reach the entry belonging
    /// to the tenant whose authenticated connection sent it. Written in place under the liveness gate, exactly
    /// as <see cref="Heartbeat"/> is, so the stale sweep's re-read sees this stamp rather than racing it.
    /// </summary>
    public bool MarkStopped(TenantId tenant, string directorId)
    {
        if (string.IsNullOrEmpty(directorId) || !tenant.IsValid) return false;
        var key = new DirectorKey(tenant, directorId);
        lock (_livenessGate)
        {
            if (!_directors.TryGetValue(key, out var existing)) return false;
            existing.StoppedAtUtc = DateTime.UtcNow;
        }
        FileLog.Write($"[DirectorRegistry] MarkStopped: tenant={tenant.Value}, id={directorId} (said goodbye; not running, not unreachable)");
        return true;
    }

    /// <summary>
    /// Mark a Director as PUSH-CAPABLE (issue #186): it rang the doorbell or sent a
    /// heartbeat carrying a session-state snapshot, so the Gateway's turn tracker does
    /// not need to poll it. File-discovered and old-build Directors never get marked
    /// and stay on the 15s reconcile poll. In-memory: resets on Gateway restart and
    /// re-establishes on the Director's first ping.
    /// </summary>
    public void MarkStateReporting(string directorId)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        if (_stateReporting.TryAdd(directorId, true))
            FileLog.Write($"[DirectorRegistry] {directorId} is state-reporting (doorbell/heartbeat push); reconcile poll skips it");
    }

    /// <summary>True when the Director pushes its own session-state signals.</summary>
    public bool IsStateReporting(string directorId)
        => !string.IsNullOrEmpty(directorId) && _stateReporting.ContainsKey(directorId);

    /// Keyed by BARE director id, like _everReachable: it is a capability flag about a Director PROCESS (does
    /// it push its own session state, so the reconcile poll can skip it), it is never served to a client, and
    /// no session content or identity rides on it. Keeping it bare leaves its behaviour untouched.
    private readonly ConcurrentDictionary<string, bool> _stateReporting = new();

    /// <summary>
    /// Remove a Director from the registry (HTTP graceful shutdown). Returns true if
    /// it was present.
    /// </summary>
    public bool Remove(string directorId)
    {
        if (!TryResolveLocalKey(directorId, out var key)) return false;
        if (TryRemoveEntry(key, out _))
        {
            _everReachable.TryRemove(directorId, out _); // graceful goodbye: next process starts blank
            FileLog.Write($"[DirectorRegistry] Remove (http): id={directorId}");
            RaiseDirectorRemoved(key);
            return true;
        }
        return false;
    }

    // ===== FSW path =====

    /// <summary>Begin watching the instances directory and start the stale sweeper.</summary>
    public void Start()
    {
        FileLog.Write($"[DirectorRegistry] Start: watching {WatchDirectory}");
        Directory.CreateDirectory(WatchDirectory);

        LoadExisting();

        _watcher = new FileSystemWatcher(WatchDirectory, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Created += OnFileCreatedOrChanged;
        _watcher.Changed += OnFileCreatedOrChanged;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;

        // Stale sweeper - every 30s.
        // FSW entries: drop if file gone or PID dead.
        // HTTP entries: drop if LastSeen older than HttpHeartbeatTimeout.
        _sweeper = new Timer(_ => SweepStale(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Snapshot of all currently-known Directors, FLEET-GLOBAL - every tenant's. This is the system
    /// aggregation view (the "is there any fleet at all" guard, reconcile, a future operator surface); it
    /// is NEVER the answer to a client request. Serving a client is <see cref="ListDirectors(TenantId)"/>.
    ///
    /// It takes a <see cref="Tenancy.SystemScope"/> so that a request handler - which never holds one -
    /// physically cannot call it. The fleet-global reach is a capability, not a convention: the old
    /// no-argument overload was reachable by any code and was where cross-tenant leaks kept appearing.
    /// </summary>
    public IReadOnlyCollection<DirectorDto> ListDirectors(Tenancy.SystemScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return _directors.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Issue #1847: the Directors ONE tenant owns - what a client request is served. Before this, the
    /// <c>GET /directors</c> list was fleet-global while the by-id legs were gated, so any authenticated
    /// account could enumerate every other account's Directors and read back their machine name, operating
    /// system user, process id, client version and liveness - and, having the id, address them.
    ///
    /// Deny by default: an entry whose owning tenant was never recorded matches nobody. There is no
    /// fall-back to the fleet-global list for any caller, tenant, or deployment.
    /// </summary>
    public IReadOnlyCollection<DirectorDto> ListDirectors(TenantId tenant)
        => _directors
            .Where(kv => kv.Key.Tenant.Equals(tenant))
            .Select(kv => kv.Value)
            .ToList()
            .AsReadOnly();

    /// <summary>
    /// Look up ONE tenant's Director by id. Null if that tenant has no such Director. This is the honest
    /// lookup: the caller says whose Director it means, so no other tenant's entry can answer.
    /// </summary>
    public DirectorDto? Get(TenantId tenant, string directorId)
        => !string.IsNullOrEmpty(directorId) && _directors.TryGetValue(new DirectorKey(tenant, directorId), out var d)
            ? d
            : null;

    /// <summary>
    /// True when <paramref name="directorId"/> is registered to <paramref name="caller"/> - i.e. the caller
    /// PROVABLY owns it (the Director ownership guard). It answers ONLY the yes/no
    /// ownership question a caller-side gate needs; it never returns another tenant's identity or its Director,
    /// so it is NOT the bare-id serving lookup MTR-01 removed - a caller learns nothing but "that id is (not)
    /// yours".
    ///
    /// This is a POSITIVE-ownership check: it returns true ONLY for an id keyed to the caller's own tenant.
    /// A null/blank/malformed id, an id owned by a DIFFERENT tenant, and an as-yet-UNKNOWN id (registered to
    /// nobody) all return false. The caller (the startup endpoint) uses that to REJECT any startup
    /// observation the caller cannot prove it owns, rather than accepting a forgeable one: the audit's threat
    /// is a caller creating a startup observation for a director_id it does not own, and an unknown id is not
    /// provably the caller's, so it is refused. The cost is that a startup report that races the tunnel Hello
    /// (arriving before the caller's own id is registered) is dropped - acceptable for a best-effort,
    /// swallowed startup ping, where a forgeable cross-tenant observation is the worse failure.
    ///
    /// <paramref name="directorId"/> is NULLABLE on purpose: the caller resolves a missing/blank/wrong-typed
    /// director_id to null (an ownership-INCAPABLE sentinel, never a real-looking placeholder string), and a
    /// null id can never be provably owned no matter what any tenant registers, so it returns false here.
    /// </summary>
    public bool IsDirectorOwnedByTenant(TenantId caller, string? directorId)
    {
        if (string.IsNullOrEmpty(directorId) || !caller.IsValid)
            return false;
        return _directors.ContainsKey(new DirectorKey(caller, directorId));
    }

    private void LoadExisting()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(WatchDirectory, "*.json"))
                TryParseAndAdd(f);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRegistry] LoadExisting FAILED: {ex.Message}");
        }
    }

    private void OnFileCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        FileLog.Write($"[DirectorRegistry] File created/changed: {e.Name}");
        // FileSystemWatcher fires before write is complete on some systems - retry briefly.
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (TryParseAndAdd(e.FullPath)) return;
                await Task.Delay(100);
            }
            FileLog.Write($"[DirectorRegistry] Could not parse {e.Name} after retries");
        });
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        FileLog.Write($"[DirectorRegistry] File deleted: {e.Name}");
        var id = Path.GetFileNameWithoutExtension(e.Name ?? "");
        if (string.IsNullOrEmpty(id)) return;
        // Only remove if the entry came from the FSW path. An HTTP entry must not be
        // wiped by a stray file delete - it lives by its own heartbeat lifecycle.
        // A file-discovered entry is always keyed to the Local tenant (see TryParseAndAdd), so a file event
        // can only ever reach the Local partition - it can never disturb a hosted account's Director.
        var key = new DirectorKey(TenantId.Local, id);
        if (_directors.TryGetValue(key, out var existing) && existing.Source == "file")
        {
            if (TryRemoveEntry(key, out _))
                RaiseDirectorRemoved(key);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        var oldId = Path.GetFileNameWithoutExtension(e.OldName ?? "");
        var oldKey = new DirectorKey(TenantId.Local, oldId);
        if (!string.IsNullOrEmpty(oldId)
            && _directors.TryGetValue(oldKey, out var existing)
            && existing.Source == "file"
            && TryRemoveEntry(oldKey, out _))
        {
            RaiseDirectorRemoved(oldKey);
        }
        TryParseAndAdd(e.FullPath);
    }

    private bool TryParseAndAdd(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return false;

            var dto = JsonSerializer.Deserialize<DirectorDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (dto is null || string.IsNullOrEmpty(dto.DirectorId)) return false;

            // A file-discovered Director is by definition on THIS machine and carries no account credential,
            // so it is keyed to the Local tenant (see the note on Upsert) - a file appearing on the Gateway's
            // disk can therefore never write into a hosted account's partition.
            var key = new DirectorKey(TenantId.Local, dto.DirectorId);

            // If an HTTP-registered entry exists for the same id, leave it alone.
            // HTTP carries the tailnet endpoint which FSW cannot supply.
            if (_directors.TryGetValue(key, out var existing) && existing.Source == "http")
            {
                FileLog.Write($"[DirectorRegistry] Skipping FSW upsert for id={dto.DirectorId}: HTTP entry already present");
                return true;
            }

            dto.LastSeen = DateTime.UtcNow;
            dto.Source = "file";
            var wasNew = !_directors.ContainsKey(key);
            _directors[key] = dto;
            if (wasNew) OnDirectorAdded?.Invoke(new DirectorArrival(key.Tenant, dto));
            FileLog.Write($"[DirectorRegistry] Added (file): id={dto.DirectorId}, endpoint={dto.ControlEndpoint}");
            return true;
        }
        catch (IOException) { return false; /* file still being written */ }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRegistry] TryParseAndAdd FAILED for {path}: {ex.Message}");
            return false;
        }
    }

    /// <remarks>
    /// Internal rather than private so a test can drive the STALE SWEEP itself - the path that fires
    /// <see cref="OnDirectorRemoved"/> for a Director whose tunnel died. That is the path a cross-tenant
    /// removal actually travels, and a test that instead calls the graceful <see cref="Remove"/> would be
    /// exercising a different, tenant-ambiguous seam. Production still reaches it only via the 30s timer.
    /// </remarks>
    internal void SweepStale()
    {
        if (_disposed) return;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _directors.ToArray())
            {
                // Gateway Cleanup mission (tunnel-only): "stream" entries are aged out exactly like "http" -
                // by staleness. A connected Director refreshes LastSeen on every Hello + the ~10s periodic
                // re-push, so it never ages; a Director whose tunnel closed stops refreshing and is swept once
                // it passes the eviction horizon. This is why the DirectorHub does NOT drop the entry the
                // instant the stream closes: a dead Director's cached roster must survive so a Gateway-owned
                // snooze can still fire it back to "needs you" from the cache, and a brief reconnect blip never
                // flaps the roster.
                //
                // Epic #1159 step A: the horizon is a DAY, not a minute. Passing it is the one elapsed-time
                // event allowed to remove a session - and it removes the machine from the READ MODEL and
                // nothing else. There is no removal cascade: the session-number release and the snooze clear
                // that once hung here are DELETED (inspection 2, finding 1), because a liveness check followed
                // by a destructive action is two operations and a Director reconnecting between them was
                // destroyed anyway. The single subscriber is PushedSessionStore.ForgetIfDisconnected, which is
                // one atomic operation under the store's membership gate - whose atomicity is REASONED from
                // the source and NOT proven by any test. Do not restore the others here.
                if (kv.Value.Source == "http" || kv.Value.Source == "stream")
                {
                    var lastSeen = kv.Value.LastSeen ?? DateTime.MinValue;
                    if (now - lastSeen > EvictionHorizon)
                    {
                        // The snapshot says stale. That is a CANDIDATE, not a verdict: the entry may have been
                        // refreshed since ToArray() captured it. Inspection 1 finding 1 - re-read and re-judge
                        // the CURRENT entry under the gate the refresh paths take, so a Director that came back
                        // in this window is not destroyed on the strength of a timestamp it has already beaten.
                        OnSweepJudgedForTest?.Invoke();

                        DirectorDto? removed = null;
                        TimeSpan age = default;
                        var spared = false;
                        lock (_livenessGate)
                        {
                            if (_directors.TryGetValue(kv.Key, out var current))
                            {
                                var currentLastSeen = current.LastSeen ?? DateTime.MinValue;
                                age = DateTime.UtcNow - currentLastSeen;
                                if (age > EvictionHorizon)
                                {
                                    if (TryRemoveEntry(kv.Key, out removed))
                                        _everReachable.TryRemove(kv.Key.DirectorId, out _);
                                }
                                else
                                {
                                    spared = true;
                                }
                            }
                        }

                        // Raised OUTSIDE the gate: subscribers run arbitrary code and one of them calling back
                        // into the registry while the gate was held would deadlock.
                        if (removed is not null)
                        {
                            FileLog.Write($"[DirectorRegistry] Sweeper removed stale {removed.Source} entry: {kv.Key.DirectorId} (last seen {age.TotalSeconds:F0}s ago)");
                            RaiseDirectorRemoved(kv.Key);
                        }
                        else if (spared)
                        {
                            FileLog.Write($"[DirectorRegistry] Sweeper SPARED {kv.Key.DirectorId}: judged stale on the swept snapshot, but the live entry is current again");
                        }
                    }
                    continue;
                }

                // FSW path: file gone or PID dead.
                var f = Path.Combine(WatchDirectory, $"{kv.Key.DirectorId}.json");
                if (!File.Exists(f))
                {
                    if (TryRemoveEntry(kv.Key, out _))
                    {
                        FileLog.Write($"[DirectorRegistry] Sweeper removed orphan (file gone): {kv.Key.DirectorId}");
                        RaiseDirectorRemoved(kv.Key);
                    }
                    continue;
                }

                var pid = kv.Value.Pid;
                if (pid > 0)
                {
                    try { System.Diagnostics.Process.GetProcessById(pid); }
                    catch (ArgumentException) // process not running
                    {
                        // Issue #891: do NOT swallow a failed delete. If the file is locked or we lack
                        // permission it stays on disk; the orphan-file sweep below completes the delete
                        // on a later pass (this loop iterates the in-memory roster, from which the entry
                        // is about to be removed, so it would otherwise never be revisited).
                        try { File.Delete(f); }
                        catch (Exception ex)
                        {
                            FileLog.Write($"[DirectorRegistry] Sweeper could not delete instance file for {kv.Key.DirectorId} (pid {pid} dead); orphan sweep will retry: {ex.Message}");
                        }
                        if (TryRemoveEntry(kv.Key, out _))
                        {
                            FileLog.Write($"[DirectorRegistry] Sweeper removed orphan (pid {pid} dead): {kv.Key.DirectorId}");
                            RaiseDirectorRemoved(kv.Key);
                        }
                    }
                    catch { /* permission errors etc - leave it for next pass */ }
                }
            }

            SweepOrphanInstanceFiles();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRegistry] SweepStale error: {ex.Message}");
        }
    }

    /// <summary>
    /// Directory-level safety net (issue #891). The in-memory sweep above removes a dead Director from
    /// the roster even if deleting its backing instance file failed (locked, permission). Because that
    /// loop iterates the in-memory dictionary, such a file is never revisited and, on the next Gateway
    /// restart, <see cref="LoadExisting"/> re-imports it - which can resurrect a phantom Director if the
    /// recorded process id has since been recycled to an unrelated live process. This scan completes the
    /// delete: it enumerates the watch directory for files that have NO live in-memory entry and whose
    /// recorded process id is dead, and deletes them. Files with a live entry are handled by the sweep
    /// above; files whose process is still alive, or that predate pid stamping (pid &lt;= 0), are left
    /// untouched so a healthy Director's file is never removed.
    /// </summary>
    internal void SweepOrphanInstanceFiles()
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(WatchDirectory, "*.json");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRegistry] Orphan-file sweep could not enumerate {WatchDirectory}: {ex.Message}");
            return;
        }

        foreach (var f in files)
        {
            var id = Path.GetFileNameWithoutExtension(f);
            // A file backing a still-known Director is the in-memory sweep's responsibility; leave it.
            // Local-keyed: an instance file on this disk only ever backs a Local-tenant entry.
            if (!string.IsNullOrEmpty(id) && _directors.ContainsKey(new DirectorKey(TenantId.Local, id)))
                continue;

            int pid;
            try
            {
                var json = File.ReadAllText(f);
                if (string.IsNullOrWhiteSpace(json)) continue; // being written; try next pass
                var dto = JsonSerializer.Deserialize<DirectorDto>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
                if (dto is null) continue;
                pid = dto.Pid;
            }
            catch (IOException) { continue; /* still being written */ }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorRegistry] Orphan-file sweep could not read {Path.GetFileName(f)}: {ex.Message}");
                continue;
            }

            // Only delete when we can PROVE the process is gone. pid <= 0 predates pid stamping, so we
            // cannot prove death and must leave the file rather than risk deleting a live Director's.
            if (pid <= 0 || !IsProcessDead(pid))
                continue;

            try
            {
                File.Delete(f);
                FileLog.Write($"[DirectorRegistry] Orphan-file sweep deleted stale instance file {Path.GetFileName(f)} (pid {pid} dead)");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorRegistry] Orphan-file sweep could not delete {Path.GetFileName(f)} (pid {pid} dead); will retry next sweep: {ex.Message}");
            }
        }
    }

    /// <summary>True when no process with <paramref name="pid"/> is currently running. A permission or
    /// other unexpected error returns false (do not assume dead) so we never delete on uncertainty.</summary>
    private static bool IsProcessDead(int pid)
    {
        try
        {
            using var _ = System.Diagnostics.Process.GetProcessById(pid);
            return false;
        }
        catch (ArgumentException) { return true; }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sweeper?.Dispose();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
