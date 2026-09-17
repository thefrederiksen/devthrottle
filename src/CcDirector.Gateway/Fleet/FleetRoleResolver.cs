using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// THE ONE role resolver. "What is this session's role?" is answered here and nowhere else.
///
/// WHY THIS IS ITS OWN CLASS (defect 5). The resolution used to live inline inside
/// <c>GatewayEndpoints.StampFleetRolesAndFold</c>, which was fine while the roster read was the only thing
/// that needed an answer. Defect 5 adds a SECOND caller - <see cref="FleetRoleObserver"/>, which must know
/// when a role CHANGED so it can push the new one down to the owning Director's desktop. Copying the
/// resolution into that observer would have created a second computer of the role, which is the exact
/// defect class defect 5 exists to close: two authorities, agreeing today, drifting tomorrow, and the drift
/// surfaces as the desktop and the phone disagreeing about the same session - defect 5 reborn wearing a new
/// hat. So the resolution moved here, verbatim, and BOTH callers call it. There is one implementation
/// because there must only ever be one answer.
///
/// WHY ONLY THE GATEWAY MAY DO THIS. "Is this session's controller still alive?" is unanswerable from one
/// Director - the controller may be a session on another machine entirely. That is the whole reason the
/// role is a Gateway-owned fact, and the reason the Director must be TOLD its answer rather than compute
/// one. Note <c>SessionManager.ResolveLocalRole</c> on the Director, which mirrors this logic against the
/// LOCAL roster only: it is a best-effort rail glyph, it is wrong for exactly the cross-machine case, and
/// it must never be wired into the colour fold. (docs/new_architecture/sessions.html, defect 5.)
/// </summary>
internal static class FleetRoleResolver
{
    /// <summary>
    /// Stamp <see cref="SessionDto.SessionRole"/> on every session in the assembled fleet.
    ///
    /// Resolution precedence (automatic session roles, chunk 2.5): an EXPLICIT role wins (sticky -
    /// auto-derivation never overwrites it), and is the only way to be an Architect. Else Worker (controlled
    /// AND the controller is still alive - this wins even if it also controls sub-workers, because nesting
    /// keeps the Worker label). Else Manager (controls at least one live session - and it is a non-worker,
    /// non-architect here because both of those resolved above). Else Standalone.
    ///
    /// Every branch ASSIGNS, so a stale inbound role can never survive this pass - see the ingest discard in
    /// <c>PushedSessionStore</c>, which is what makes that guarantee structural rather than incidental.
    /// </summary>
    /// <param name="all">The assembled fleet. Mutated in place.</param>
    /// <param name="fleetManagerSessionId">The session this account has marked as its Fleet Manager, or null -
    /// then no session is <see cref="SessionDto.OwnedByFleetManager"/>.</param>
    internal static void Stamp(List<SessionDto> all, string? fleetManagerSessionId = null)
        => StampUniverse(all, fleetManagerSessionId);

    /// <summary>
    /// Resolve roles across <paramref name="roleUniverse"/> (the UNFILTERED fleet) and stamp the answer onto
    /// <paramref name="toStamp"/> (the set a caller is about to fold or return), matching BY SESSION ID.
    ///
    /// WHY THIS OVERLOAD EXISTS. Resolving from the universe rather than the filtered set is defect 13: "is
    /// my controller alive?" is a question about sessions the caller may have filtered out, so
    /// <c>?state=Working</c> could drop a waiting controller out of the liveness set and un-suppress its
    /// worker's red. Stamping the universe and then folding the filtered set works ONLY while the filtered
    /// entries are the SAME OBJECTS as the universe entries - an equal-but-copied DTO comes back with a null
    /// SessionRole and a fold computed from it, SILENTLY. That is this mission's own defect shape (a
    /// consumer reading a value production never put there) waiting to be written by the next caller.
    ///
    /// Matching by id removes the trap instead of documenting it: it does not matter whether a caller passes
    /// references or copies, and there is no rule left to forget. A caller passing a session that is not in
    /// the universe at all is a real bug (it is asking for a role resolved from a fleet that does not contain
    /// it), so it fails LOUD rather than quietly folding a null.
    /// </summary>
    /// <param name="roleUniverse">The UNFILTERED fleet. Every role is resolved from this. Mutated in place.</param>
    /// <param name="toStamp">The subset to stamp - references or copies, both work. Mutated in place.</param>
    internal static void Stamp(List<SessionDto> roleUniverse, IReadOnlyList<SessionDto> toStamp,
        string? fleetManagerSessionId = null)
    {
        if (roleUniverse is null) throw new ArgumentNullException(nameof(roleUniverse));
        if (toStamp is null) throw new ArgumentNullException(nameof(toStamp));

        StampUniverse(roleUniverse, fleetManagerSessionId);

        // BOTH RESOLVED FACTS TRAVEL TOGETHER. The supervisor-liveness answer is carried across with the role
        // for the same reason the role is carried at all (defect 13): it is a question about sessions the
        // caller may have filtered out. Copying the role alone would hand the fold a filtered session whose
        // HasLiveSupervisor is still its default false - which surfaces a held worker to the owner. That is
        // the safe direction of this field, but it is not the right ANSWER, and a fold that is merely
        // safely wrong is still wrong.
        var byId = new Dictionary<string, (string? Role, bool HasLiveSupervisor, bool OwnedByFleetManager)>(StringComparer.Ordinal);
        foreach (var s in roleUniverse)
            if (!string.IsNullOrEmpty(s.SessionId))
                byId[s.SessionId] = (s.SessionRole, s.HasLiveSupervisor, s.OwnedByFleetManager);

        foreach (var s in toStamp)
        {
            if (string.IsNullOrEmpty(s.SessionId)) continue;
            if (!byId.TryGetValue(s.SessionId, out var resolved))
                throw new InvalidOperationException(
                    $"Session '{s.SessionId}' was passed to be stamped but is not in the role universe. The role " +
                    "MUST be resolved from the unfiltered fleet (defect 13); stamping a session the universe " +
                    "does not contain would silently fold it with a null role.");
            s.SessionRole = resolved.Role;
            s.HasLiveSupervisor = resolved.HasLiveSupervisor;
            s.OwnedByFleetManager = resolved.OwnedByFleetManager;
        }
    }

    private static void StampUniverse(List<SessionDto> all, string? fleetManagerSessionId)
    {
        if (all is null) throw new ArgumentNullException(nameof(all));

        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        // The live Fleet Manager sessions, for OwnedByFleetManager below. The mark is read in its one place.
        var liveFleetManagers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var controllersWithLiveChild = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in all)
        {
            var alive = !string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);
            if (alive && !string.IsNullOrEmpty(s.SessionId))
                liveIds.Add(s.SessionId);
            if (alive && !string.IsNullOrEmpty(s.SessionId) && FleetManagerSessions.IsFleetManager(s, fleetManagerSessionId))
                liveFleetManagers.Add(s.SessionId);
            if (alive && s.IsControlled && !string.IsNullOrEmpty(s.ControllerSessionId))
                controllersWithLiveChild.Add(s.ControllerSessionId);
        }

        foreach (var s in all)
        {
            // IS SOMEONE ALIVE HOLDING THIS SESSION? The one input to the attention rule, and it is answered
            // HERE because this is the only place that sees the whole fleet.
            //
            // IT IS DELIBERATELY OUTSIDE THE ROLE LADDER BELOW, not merely above it. The seat is a label a
            // spawn can stamp by hand; whether a supervisor is still breathing is not, and the two must never
            // be answered by one branch again. That conflation is the defect: an ExplicitRole short-circuits
            // every derivation below, so a worker stamped "Worker" at spawn kept that seat - and therefore
            // kept being quietened - long after its supervisor had exited the fleet. Reading the liveness set
            // directly cannot be short-circuited by a stamp.
            s.HasLiveSupervisor =
                s.IsControlled
                && !string.IsNullOrEmpty(s.ControllerSessionId)
                && liveIds.Contains(s.ControllerSessionId);

            // IS THAT LIVE OWNER THE ACCOUNT'S MARKED FLEET MANAGER? Only the DIRECT owner counts: a Worker under an
            // Architect the Fleet Manager started is the Architect's.
            s.OwnedByFleetManager = s.HasLiveSupervisor && liveFleetManagers.Contains(s.ControllerSessionId!);

            var explicitRole = SessionRoles.Normalize(s.ExplicitRole);
            if (explicitRole is not null)
                s.SessionRole = explicitRole;
            else if (s.IsControlled && !string.IsNullOrEmpty(s.ControllerSessionId) && liveIds.Contains(s.ControllerSessionId))
                s.SessionRole = SessionRoles.Worker;
            else if (!string.IsNullOrEmpty(s.SessionId) && controllersWithLiveChild.Contains(s.SessionId))
                s.SessionRole = SessionRoles.Manager;
            else
                s.SessionRole = SessionRoles.Standalone;
        }
    }
}
