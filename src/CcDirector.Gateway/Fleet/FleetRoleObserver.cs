using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// Pushes each session's resolved role DOWN to the Director that owns it, so the desktop rail folds the
/// SAME role the phone and the Cockpit fold (defect 5). This is the PUSH seam, modelled exactly on
/// <c>SnoozeLandingObserver</c>: every session a Director pushes up its tunnel passes through here.
///
/// THE DEFECT. <c>ControlEndpoints.Map</c> never set <see cref="SessionDto.SessionRole"/> - the role is
/// computed at the Gateway from the WHOLE fleet, because "is this session's controller still alive?" cannot
/// be answered from one Director. The desktop's fold is fed by its own Director through that same mapper,
/// so on the desktop the field was always null, so the fold's red-suppression
/// (<c>SessionOrdering.BaseColor</c>) could never fire. A live Worker read slate "Sub-agent" on the phone
/// and red "Needs you" on the desktop AT THE SAME INSTANT - a disagreement by construction, for every red
/// Worker with a live controller.
///
/// THE SHAPE OF THE FIX, and the line it must not cross. The Gateway STAMPS; the Director CARRIES. The
/// Director performs no resolution - it caches a value it was told and reads it back out through its
/// mapper. That is the same distinction Phase 1 drew for the snooze clock: the Director reports the
/// landing, the Gateway owns the clock. The Director computing its own role is what law 3 forbids and is
/// exactly the trap sitting one call away in <c>SessionManager.ResolveLocalRole</c> (which is wrong
/// cross-machine, and must not be wired into the fold).
///
/// WHY THE FLEET, NOT THE PUSHED SESSION. A role change for session X is very often caused by a change to
/// session Y - Y is X's controller and Y just exited, so X stops being a Worker and its red must surface.
/// Y may live on a DIFFERENT Director than X. So a push from ANY Director re-resolves the WHOLE fleet and
/// fans out to every Director whose sessions changed, not just the one that pushed.
///
/// WHY THE CHANGE GATE IS LOAD-BEARING. Sending a role down makes the Director report it back up on its
/// next delta, which lands here again. Ungated, that is an infinite echo: role -> delta -> observe -> role.
/// The send is gated on the role having actually CHANGED from what we last sent, so the echo resolves to
/// the same value, sends nothing, and the loop terminates on its first turn. <see cref="_lastSent"/> is
/// that gate and it is the only thing standing between this class and a spin.
///
/// NO GATEWAY, NO ROLE. A Director with no tunnel never receives a stamp, so its desktop leaves
/// SessionRole null and a Worker's red surfaces. That is the honest answer and the status quo: the Gateway
/// owns the fact, and a client inventing a local one IS the defect.
/// </summary>
public sealed class FleetRoleObserver
{
    private readonly Func<IReadOnlyList<(string DirectorId, SessionDto Session)>> _snapshot;
    // Spelled out rather than using DirectorCommandRouter.SendDirectorCommandAsync (which the auto-dismiss
    // sweeper takes): that delegate is internal, and this observer is public because the public DirectorHub
    // takes one. Structurally identical - GatewayHost.SendCommandAsync satisfies both.
    private readonly Func<string, DirectorCommand, CancellationToken, Task<DirectorCommandResult?>> _sendCommand;

    /// <summary>Resolves the current tenant scope's key (TenantId.Value) so the change gate can be partitioned
    /// per tenant; null on self-host or when no seam is supplied (the unit tests), which resolves to the one
    /// <see cref="DefaultScope"/> partition - byte-identical to the flat single-tenant gate before this.</summary>
    private readonly Func<string?>? _currentScopeKey;

    /// <summary>
    /// The change gate, PARTITIONED BY TENANT SCOPE: scope key -> (session id -> the role we last successfully
    /// sent down). An entry is only written AFTER the send is accepted, so a dropped send is retried on the
    /// next push rather than being recorded as delivered. Bounded by pruning sessions that have left the fleet.
    ///
    /// PARTITIONED because on the hosted Gateway the hub push path folds one tenant's fleet at a time (the
    /// snapshot reads the AMBIENT tenant). A single flat gate pruned against one tenant's live set would delete
    /// every OTHER tenant's entries, so the next push would re-send them all - a role-stamp storm. Each tenant's
    /// pass reads and prunes ONLY its own partition. Self-host has one partition (<see cref="DefaultScope"/>),
    /// unchanged. This is the partitioning the FleetRoles-on-Local comment in GatewayHost was blocked on.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _lastSentByScope
        = new(StringComparer.Ordinal);

    /// <summary>The one gate partition used when no tenant scope is in effect (self-host and the unit tests).</summary>
    private const string DefaultScope = " single";

    /// <summary>This pass's gate partition, resolved from the ambient tenant scope. MUST be captured once per
    /// <see cref="Sweep"/> synchronously BEFORE any await, because the fire-and-forget sends can outlive the
    /// scope that a hub push ran under.</summary>
    private ConcurrentDictionary<string, string> GateForCurrentScope()
        => _lastSentByScope.GetOrAdd(
            _currentScopeKey?.Invoke() is { Length: > 0 } key ? key : DefaultScope,
            _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));

    /// <param name="snapshot">The fresh pushed sessions across every stream-connected Director, each paired
    /// with its owning directorId (PushedSessionStore.SnapshotFresh) - the same fleet read the auto-dismiss
    /// sweeper uses. Roles need the WHOLE fleet, not one Director's slice.</param>
    /// <param name="sendCommand">The down-channel command sender (GatewayHost.SendCommandAsync). A null
    /// RESULT means that Director has no stream, which is the documented "no Gateway, no role" floor.</param>
    /// <param name="currentScopeKey">Resolves the ambient tenant scope's key so the change gate is partitioned
    /// per tenant (GatewayHost passes <c>() =&gt; _tenantPass.Current?.Value</c>). Omit on self-host and in unit
    /// tests: the gate then uses the single <see cref="DefaultScope"/> partition, unchanged.</param>
    public FleetRoleObserver(
        Func<IReadOnlyList<(string DirectorId, SessionDto Session)>> snapshot,
        Func<string, DirectorCommand, CancellationToken, Task<DirectorCommandResult?>> sendCommand,
        Func<string?>? currentScopeKey = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
        _currentScopeKey = currentScopeKey;
    }

    /// <summary>Observe one pushed session. Any push can change any session's role, so this re-resolves the
    /// whole fleet - see the class remarks.</summary>
    public void Observe(SessionDto? session)
    {
        if (session is null || string.IsNullOrEmpty(session.SessionId)) return;
        Sweep();
    }

    /// <summary>Observe a whole pushed snapshot - the reconnect path, where a role change can hide.</summary>
    public void ObserveSnapshot(IReadOnlyList<SessionDto>? sessions)
    {
        if (sessions is null || sessions.Count == 0) return;
        Sweep();
    }

    /// <summary>
    /// Observe a session LEAVING the fleet. A departure changes other sessions' roles exactly as an arrival
    /// does, and it was the case this observer missed: a controller's tombstone should stop its workers
    /// being Workers, and the last worker's tombstone should stop its controller being a Manager.
    ///
    /// Nothing re-stamped on a remove, so the Gateway's own roster recomputed from the store on the next
    /// read while the DIRECTOR kept the role it was last told - until some unrelated push happened to
    /// trigger a sweep. That is a desktop-versus-phone disagreement window with no bound on it, which is
    /// the thing this mission exists to close.
    ///
    /// Call AFTER the store has applied the removal: Sweep resolves from the snapshot, so the departing
    /// session must already be gone from it or it resolves the fleet that no longer exists.
    /// </summary>
    public void ObserveRemoval(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        Sweep();
    }

    /// <summary>
    /// Re-resolve every session's role from the whole fleet and push down the ones that CHANGED.
    ///
    /// Fire-and-forget by design: this runs on the hub's push path, and a Director that is slow to answer a
    /// role stamp must not stall the session delta that triggered it. A dropped send costs one stale role
    /// until the next push, which is the same bound every other pushed fact carries.
    /// </summary>
    internal void Sweep()
    {
        // Capture THIS pass's per-tenant gate up front, synchronously: the hub push path runs inside one
        // tenant's scope and the async sends below can outlive it, so the gate reference must be resolved here.
        var gate = GateForCurrentScope();
        var fleet = _snapshot();
        if (fleet is null || fleet.Count == 0) return;

        // Resolve over the WHOLE fleet. SnapshotFresh already hands back deep copies, so stamping these
        // cannot touch the cache - the roster read does its own stamping pass on its own copies.
        var resolved = fleet.Select(f => f.Session).ToList();
        FleetRoleResolver.Stamp(resolved);

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (directorId, s) in fleet)
        {
            if (string.IsNullOrEmpty(s.SessionId) || string.IsNullOrEmpty(directorId)) continue;
            live.Add(s.SessionId);

            var role = s.SessionRole ?? SessionRoles.Standalone;
            // THE GATE. Unchanged answer -> no send -> the echo of our own stamp dies here rather than
            // becoming the next push. Removing this makes the observer spin.
            //
            // THE KEY CARRIES BOTH FACTS, and that is not tidiness. A supervisor dying does not change the
            // seat, so a role-only key would gate away the one delta that must always get through: the
            // session whose supervisor has just gone. It would stay quiet on the desktop with nobody holding
            // it - the exact failure this rule exists to close - and it would look like the observer working.
            var answer = $"{role}/{(s.HasLiveSupervisor ? "held" : "alone")}";
            if (gate.TryGetValue(s.SessionId, out var sent) && string.Equals(sent, answer, StringComparison.Ordinal))
                continue;

            _ = SendRoleAsync(directorId, s.SessionId, role, s.HasLiveSupervisor, answer, gate);
        }

        // Keep the gate bounded: a session that has left THIS TENANT'S fleet keeps no entry. Prune only this
        // tenant's partition against this tenant's live set - never another tenant's, which would re-send it.
        foreach (var key in gate.Keys)
            if (!live.Contains(key))
                gate.TryRemove(key, out _);
    }

    private async Task SendRoleAsync(string directorId, string sessionId, string role, bool hasLiveSupervisor,
        string answer, ConcurrentDictionary<string, string> gate)
    {
        try
        {
            var command = new DirectorCommand
            {
                CommandId = Guid.NewGuid().ToString("N"),
                Verb = "set-resolved-role",
                SessionId = sessionId,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                    new SetResolvedRoleRequest { Role = role, HasLiveSupervisor = hasLiveSupervisor },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            };

            var result = await _sendCommand(directorId, command, CancellationToken.None);
            if (result is null)
            {
                // No tunnel. Not an error - that Director's desktop simply has no stamped role, which is
                // the documented "no Gateway, no role" floor. Record NOTHING, so the stamp is retried the
                // moment the Director reconnects and pushes.
                FileLog.Write($"[FleetRoleObserver] sid={sessionId}: no stream for director={directorId}; role '{role}' not delivered");
                return;
            }
            if (result.Status != DirectorCommandStatus.Ok)
            {
                FileLog.Write($"[FleetRoleObserver] sid={sessionId}: director={directorId} rejected role '{role}': {result.Status} {result.Error}");
                return;
            }

            // Recorded ONLY on a confirmed delivery, so a dropped stamp is re-sent on the next push.
            gate[sessionId] = answer;
            FileLog.Write($"[FleetRoleObserver] sid={sessionId}: role '{role}' stamped down to director={directorId}");
        }
        catch (Exception ex)
        {
            // A boundary: this is fire-and-forget off the hub's push path, so a faulting send must not
            // take down the push that triggered it, and must not be recorded as delivered.
            FileLog.Write($"[FleetRoleObserver] sid={sessionId}: role stamp FAILED for director={directorId}: {ex.Message}");
        }
    }
}
