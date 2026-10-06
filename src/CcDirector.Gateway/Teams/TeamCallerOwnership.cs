using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// WHOSE IS WHAT A TEAM REQUEST TOUCHES (devthrottle_internal#2311). Inside a team's tenant a person's sessions and
/// computers are private to them, so <see cref="TeamEndpointGate"/> asks, for a rule whose target is
/// <see cref="TeamTarget.CallersOwn"/>, whether the request touches only the caller's own. HOW IT IS REALLY
/// ANSWERED, including the two things in it a member's own Director writes (review F2):
///
/// <list type="bullet">
/// <item>A Director's tunnel (<c>/director-stream</c>) is the calling key's own connection: Hello binds it to that
/// key's tenant, a Director id stays with the key that first registered it, and a TEAM key may say Hello only under the
/// Director id its own device row was enrolled with (<c>DirectorHub.Hello</c>).</item>
/// <item>A Director (<c>/directors/{id}/...</c>) is its owner's: the person the key it said Hello on was issued to, read
/// from the device registry. The Director id in Hello is client-written, but inside a team it cannot be another
/// member's: their id is their key's enrolled id, which a different person's key is refused under.</item>
/// <item>A session - any route naming one by <c>{sid}</c>: <c>/sessions/{sid}/...</c>, its transcript, its prompts - is
/// the caller's own when its KEY ROW names a Director of the caller's (the durable record, written by the session's own
/// Director before the session is listed, and never taken over), or, for a session with no key row, only when EXACTLY
/// ONE Director in the tenant holds that id in its roster and that Director is the caller's. The session ids in a roster
/// are client-written: the roster accepts any id from any Director, so two
/// Directors holding one id is not a session anyone can be said to own, and it is answered Unknown and refused - never
/// the first one found. The roster itself does not refuse the duplicate: a roster that kept the first writer would let a
/// Director that pushed a colleague's id first hide the colleague's own session from them.
/// AND the session's STORED conversation, when the Gateway holds one, must have been written only by Directors the
/// caller owns - its head's Director and the Director of every turn row of its current generation. A roster lists live
/// sessions only, so once a colleague's session has ended a member's Director can be the only holder of the colleague's
/// old id; the stored conversation still names the colleague's Director, and the route that would serve it is refused.
/// AND, when nothing is stored yet, the session's KEY ROW must not name another person's Director (#3552 review, S2-F6):
/// that row is written by the session's own Director before the session can be listed or pushed, and is never taken over,
/// so a colleague who lists the id until the session ends cannot become its owner by keeping its first rows from being
/// stored. Personal tenants never reach this class.</item>
/// <item>Pushing prompts (<c>POST /prompts</c>, exactly) is the caller's own: the Gateway stamps every record it writes
/// with the caller, from the calling key, so what the request writes can only ever be the caller's
/// (devthrottle_internal#2305). Every other method on <c>/prompts</c> - reading, exporting, deleting - reaches the whole
/// team's log and is Unknown, so it stays refused inside a team.</item>
/// <item>A dev report the person published in the team (<c>/teams/{teamId}/reports/mine/{reportId}/...</c>,
/// devthrottle_internal#2309) is its AUTHOR's: the person recorded on the report when it was published, who was read
/// then through <see cref="OwnerOf"/>. A report this tenant does not hold, or one with no author recorded, is Unknown.
/// The route is called from the person's own account, so the gate asks this in the team the route names.</item>
/// </list>
///
/// Anything else - a list across the whole team, a session or Director this Gateway does not know, one registered by
/// something other than a device key, or a request not made with a device key - is
/// <see cref="TeamOwnership.Unknown"/>, which the gate refuses. Refused, never guessed.
/// </summary>
public sealed class TeamCallerOwnership
{
    private const string DeviceCredentialPrefix = "device:";

    private readonly DirectorRegistry _directors;
    private readonly PushedSessionStore _sessions;
    private readonly DeviceRegistry _devices;
    private readonly SessionTurnStore _turns;
    private readonly HostedTenantBoundary _boundary;
    private readonly SessionKeyRegistry _sessionKeys;
    private readonly Func<TenantId, Guid, string?>? _reportAuthor;

    /// <param name="turns">The stored conversations - the one store the Gateway serves them from.</param>
    /// <param name="boundary">Enters the team's tenant scope for the stored-conversation read, which is partitioned by
    /// it.</param>
    /// <param name="sessionKeys">The session key rows - the record of whose a session id is (<see cref="SessionKeyDirectorOf"/>).</param>
    /// <param name="reportAuthor">The author recorded on a dev report in a tenant, or null when the tenant holds no such
    /// report or none was recorded (devthrottle_internal#2309). Null answers Unknown for every report route, which the
    /// gate refuses.</param>
    public TeamCallerOwnership(DirectorRegistry directors, PushedSessionStore sessions, DeviceRegistry devices,
        SessionTurnStore turns, HostedTenantBoundary boundary, SessionKeyRegistry sessionKeys,
        Func<TenantId, Guid, string?>? reportAuthor = null)
    {
        _directors = directors ?? throw new ArgumentNullException(nameof(directors));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _turns = turns ?? throw new ArgumentNullException(nameof(turns));
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        _sessionKeys = sessionKeys ?? throw new ArgumentNullException(nameof(sessionKeys));
        _reportAuthor = reportAuthor;
    }

    /// <summary>
    /// Whether the request to <paramref name="routePattern"/> in <paramref name="tenant"/>, made by
    /// <paramref name="callerSubject"/> with a device key, touches only the caller's own.
    /// </summary>
    /// <param name="method">The request's HTTP method. Only <c>POST /prompts</c> depends on it; null answers as any other
    /// method would.</param>
    public TeamOwnership Whose(TenantId tenant, string callerSubject, string? routePattern, Func<string, string?> routeValue,
        string? method = null)
    {
        ArgumentNullException.ThrowIfNull(routeValue);
        if (string.IsNullOrWhiteSpace(callerSubject) || routePattern is null)
            return TeamOwnership.Unknown;

        var pattern = TeamEndpointRules.Normalize(routePattern);
        if (string.Equals(pattern, "/prompts", StringComparison.Ordinal) && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            return TeamOwnership.Callers;
        if (IsUnder(pattern, "/director-stream"))
            return TeamOwnership.Callers;

        if (IsUnder(pattern, "/directors"))
        {
            var directorId = routeValue("directorId") ?? routeValue("id");
            return string.IsNullOrWhiteSpace(directorId)
                ? TeamOwnership.Unknown
                : OwnerOfDirector(tenant, directorId, callerSubject, "director");
        }

        if (IsUnder(pattern, Api.TeamReportEndpoints.MineReportPattern))
            return OwnerOfReport(tenant, routeValue("reportId"), callerSubject);

        // Any route that names a session by {sid} - the session family, its transcript, its prompts - touches that one
        // session. Whose Director it is comes from the one rule ClaimOf uses: the session's KEY ROW when there is one -
        // the durable record, written by its own Director before the session is listed (#3558), and never taken over -
        // and only without one, the one Director in the tenant that holds it. A keyed session is therefore its owner's
        // even while no Director lists it (between its key and its listing, or after a Gateway restart until its
        // Director reconnects) and while a colleague lists its id too (#3552 review, the second #2309 reports test).
        // The stored conversation is then checked as before.
        var sessionId = routeValue("sid");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var director = SessionKeyDirectorOf(tenant, sessionId);
            if (director is null)
            {
                var holders = _sessions.DirectorsHoldingSession(tenant, sessionId);
                if (holders.Count == 0)
                {
                    FileLog.Write($"[TeamCallerOwnership] Whose: session {sessionId} has no key row and is not known in tenant {tenant.ToLogString()} - unknown");
                    return TeamOwnership.Unknown;
                }
                if (holders.Count > 1)
                {
                    FileLog.Write($"[TeamCallerOwnership] Whose: session {sessionId} has no key row and is held by {holders.Count} Directors in tenant {tenant.ToLogString()} - unknown");
                    return TeamOwnership.Unknown;
                }
                director = holders[0];
            }
            var live = OwnerOfDirector(tenant, director, callerSubject, "session");
            if (live != TeamOwnership.Callers)
                return live;
            return OwnerOfStoredConversation(tenant, sessionId, director, callerSubject);
        }

        return TeamOwnership.Unknown;
    }

    /// <summary>The caller's own only when every Director that wrote the session's stored conversation is the caller's.
    /// Otherwise the first writer that is not the caller's decides the answer - someone else's, or unknown - and either is
    /// refused. With nothing stored, the session's key row decides instead (#3552 review, S2-F6): a row naming a Director
    /// other than the live holder is answered by that Director's owner, so another person's is someone else's; no row is
    /// nothing to refuse.</summary>
    private TeamOwnership OwnerOfStoredConversation(TenantId tenant, string sessionId, string liveHolder, string callerSubject)
    {
        IReadOnlyList<string> writers;
        using (_boundary.EnterScope(tenant))
            writers = _turns.DirectorsOfCurrentConversation(sessionId);

        if (writers.Count == 0)
        {
            var keyed = SessionKeyDirectorOf(tenant, sessionId);
            if (keyed is null || DeviceCredentialIdentity.SameDirectorId(keyed, liveHolder))
                return TeamOwnership.Callers;
            var owner = OwnerOfDirector(tenant, keyed, callerSubject, "session key");
            if (owner != TeamOwnership.Callers)
                FileLog.Write($"[TeamCallerOwnership] Whose: session {sessionId} - nothing stored, and its key was registered by director={keyed}, not the caller's - refused");
            return owner;
        }

        foreach (var writer in writers)
        {
            if (string.Equals(writer, liveHolder, StringComparison.OrdinalIgnoreCase))
                continue;
            var owner = OwnerOfDirector(tenant, writer, callerSubject, "stored conversation");
            if (owner != TeamOwnership.Callers)
            {
                FileLog.Write($"[TeamCallerOwnership] Whose: session {sessionId} - its stored conversation was written by director={writer}, not the caller's - refused");
                return owner;
            }
        }
        return TeamOwnership.Callers;
    }

    /// <summary>
    /// THE ONE ANSWER TO "WHOSE DIRECTOR IS THIS" in a tenant (devthrottle_internal#2311, seam-director-key.md): the
    /// account subject on the ACTIVE device credential the Director said Hello on, bound to THIS tenant. Null - nobody's,
    /// so refused wherever it is asked - when the Director was registered with no device key, or its credential is
    /// revoked, bound to another tenant, or names nobody. The team gate asks it here; the team Fleet Map (#2312) is to ask
    /// it too, so the question has one copy. Personally identifying: the answer is never logged.
    /// </summary>
    public string? OwnerOf(TenantId tenant, string directorId)
    {
        if (string.IsNullOrWhiteSpace(directorId))
            return null;
        var credential = _directors.RegisteringCredentialOf(tenant, directorId);
        if (credential is null || !credential.StartsWith(DeviceCredentialPrefix, StringComparison.Ordinal))
        {
            FileLog.Write($"[TeamCallerOwnership] OwnerOf: director={directorId} was not registered by a device key in tenant {tenant.ToLogString()} - nobody's");
            return null;
        }

        var owner = _devices.AccountSubjectOfActiveDevice(credential[DeviceCredentialPrefix.Length..], tenant);
        if (owner is null)
            FileLog.Write($"[TeamCallerOwnership] OwnerOf: director={directorId} - its credential is revoked, bound to another tenant, or names nobody - nobody's");
        return owner;
    }

    /// <summary>The caller's own when the report's recorded author is the caller; someone else's when it is another
    /// person; Unknown when there is no such report here, no author on it, or no way to read one.</summary>
    private TeamOwnership OwnerOfReport(TenantId tenant, string? reportId, string callerSubject)
    {
        if (_reportAuthor is null || !Guid.TryParse(reportId, out var id))
        {
            FileLog.Write($"[TeamCallerOwnership] Whose: report {reportId} in tenant {tenant.ToLogString()} - not a report id, or no author reader - unknown");
            return TeamOwnership.Unknown;
        }

        var author = _reportAuthor(tenant, id);
        if (string.IsNullOrWhiteSpace(author))
        {
            FileLog.Write($"[TeamCallerOwnership] Whose: report {id} in tenant {tenant.ToLogString()} - no such report, or no author recorded - unknown");
            return TeamOwnership.Unknown;
        }

        var mine = string.Equals(author, callerSubject, StringComparison.Ordinal);
        FileLog.Write($"[TeamCallerOwnership] Whose: report {id} in tenant {tenant.ToLogString()} - {(mine ? "the caller's own" : "someone else's")}");
        return mine ? TeamOwnership.Callers : TeamOwnership.SomeoneElses;
    }

    /// <summary>
    /// THE ONE ANSWER TO "IS THIS SESSION THIS DIRECTOR'S OWN" inside a team (#3552 review, round 4) - asked by the hub
    /// before it accepts a turn push, by the turn-end watcher before a Director's report of a session's state may move
    /// that session's turn, and by the display push before a session's folded state is sent to a Director. One rule, so
    /// a Director that lists a colleague's session id cannot write into it, cannot end its turns, and is not sent what
    /// the Gateway knows about it:
    /// <list type="bullet">
    /// <item>Anything stored for the id, in any generation: its own only when every Director that wrote it is this one or
    /// another Director of <paramref name="person"/> (<see cref="WrittenByAnotherPerson"/>).</item>
    /// <item>Nothing stored, and a session key row: its own only when the row names this Director (S2-F6).</item>
    /// <item>Neither: its own only when this Director is the ONE Director in the tenant whose roster lists the id; with
    /// two, nobody's for now (S2-F2).</item>
    /// </list>
    /// <paramref name="person"/> is the person whose key the Director said Hello on.
    /// </summary>
    public TeamSessionClaim ClaimOf(TenantId tenant, string directorId, string person, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(person))
            return TeamSessionClaim.NoPerson;

        IReadOnlyList<string> writers;
        using (_boundary.EnterScope(tenant))
            writers = _turns.DirectorsOfAnyGeneration(sessionId);
        if (writers.Count > 0)
            return WrittenByAnotherPerson(tenant, writers, directorId, person)
                ? TeamSessionClaim.AnotherPersonWroteIt
                : TeamSessionClaim.Its;

        var keyed = SessionKeyDirectorOf(tenant, sessionId);
        if (keyed is not null)
            return DeviceCredentialIdentity.SameDirectorId(keyed, directorId)
                ? TeamSessionClaim.Its
                : TeamSessionClaim.AnotherDirectorsKey;

        var holders = _sessions.DirectorsHoldingSession(tenant, sessionId);
        return holders.Count == 1 && DeviceCredentialIdentity.SameDirectorId(holders[0], directorId)
            ? TeamSessionClaim.Its
            : TeamSessionClaim.NotNow;
    }

    /// <summary><see cref="ClaimOf(TenantId, string, string, string)"/> for a Director named by its id alone: its person
    /// is <see cref="OwnerOf"/>, and a Director that is nobody's is <see cref="TeamSessionClaim.NoPerson"/>.</summary>
    public TeamSessionClaim ClaimOf(TenantId tenant, string directorId, string sessionId)
    {
        var person = OwnerOf(tenant, directorId);
        return person is null ? TeamSessionClaim.NoPerson : ClaimOf(tenant, directorId, person, sessionId);
    }

    /// <summary>
    /// Whether, in a team, a Director's report about a session - a state, or a removal - may move that session's turn,
    /// and whether that Director may be sent the session's folded state (#3552 review, round 4): only when the session is
    /// its own by <see cref="ClaimOf(TenantId, string, string)"/>. A REMOVAL is refused only when the session is someone
    /// else's for good: one that is nobody's for now (two holders, no record) may still be forgotten, or its own
    /// Director's removal would never be heard. The caller asks this only for a team's tenant.
    /// </summary>
    public bool AcceptsReport(TenantId tenant, string directorId, string sessionId, bool isRemoval)
    {
        if (string.IsNullOrWhiteSpace(directorId) || string.IsNullOrWhiteSpace(sessionId))
            return false;
        var claim = ClaimOf(tenant, directorId, sessionId);
        var accepted = claim == TeamSessionClaim.Its || (isRemoval && claim == TeamSessionClaim.NotNow);
        if (!accepted)
            FileLog.Write($"[TeamCallerOwnership] AcceptsReport: director={directorId} session={sessionId} claim={claim} removal={isRemoval} - not accepted");
        return accepted;
    }

    /// <summary>Whether any of <paramref name="writers"/> - the Directors that wrote a session's stored rows - is neither
    /// <paramref name="directorId"/> nor another Director of <paramref name="person"/> (<see cref="OwnerOf"/>; a writer it
    /// cannot name is not the person's). THE ONE stored-writer question: <see cref="ClaimOf(TenantId, string, string, string)"/>
    /// and the hub's session key check both ask it (#3552 review, S2-F8).</summary>
    public bool WrittenByAnotherPerson(TenantId tenant, IReadOnlyList<string> writers, string directorId, string person)
    {
        ArgumentNullException.ThrowIfNull(writers);
        foreach (var writer in writers)
        {
            if (DeviceCredentialIdentity.SameDirectorId(writer, directorId))
                continue;
            if (!string.Equals(OwnerOf(tenant, writer), person, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The Director named on a session's key row in <paramref name="tenant"/>, or null when it never registered one
    /// (<see cref="SessionKeyRegistry.DirectorOfSession"/>). THE ONE READER of that record inside a team: the ownership
    /// answer above and the hub's first-rows rule both ask it here (#3552 review, S2-F6).
    /// </summary>
    public string? SessionKeyDirectorOf(TenantId tenant, string sessionId) => _sessionKeys.DirectorOfSession(tenant, sessionId);

    /// <summary>
    /// Whether, in <paramref name="tenant"/>, a person other than <paramref name="person"/> holds an ACTIVE key set up
    /// for Director <paramref name="directorId"/> (<see cref="DeviceRegistry.AnotherPersonHoldsActiveKeyForDirectorInTenant"/>).
    /// A team key's Hello is refused when it does (#3552 review, S2-F5).
    /// </summary>
    public bool IsAnotherPersonsDirector(TenantId tenant, string person, string directorId) =>
        _devices.AnotherPersonHoldsActiveKeyForDirectorInTenant(tenant, person, directorId);

    /// <summary>
    /// THE ONE ANSWER TO "WHO IS ASKING" inside a team's tenant (devthrottle_internal#2311, seam-director-key.md, seams 1
    /// and 2), for a device key and a session key alike. A DEVICE key bound to this tenant: the person it was issued to.
    /// A SESSION key of this tenant: its Director's owner, <see cref="OwnerOf"/> - read live, so a session whose
    /// Director's key is revoked has no person. Anything else - no key, a key of another tenant, the machine token -
    /// is null, and every caller refuses it. The team gate and the access lease both ask this; nothing else may
    /// resolve a team caller. Personally identifying: the answer is never logged.
    /// </summary>
    public string? PersonOf(TenantId tenant, DeviceCredentialIdentity? device, SessionCredentialIdentity? session)
    {
        if (device is not null)
            return string.Equals(device.TenantId, tenant.Value, StringComparison.Ordinal) ? device.AccountSubject : null;
        if (session is not null)
        {
            if (session.Tenant != tenant)
                return null;
            var person = OwnerOf(tenant, session.DirectorId);
            if (person is null)
                FileLog.Write($"[TeamCallerOwnership] PersonOf: a session key of director={session.DirectorId} in tenant {tenant.ToLogString()} - its Director is nobody's, so the request names no person");
            return person;
        }
        return null;
    }

    /// <summary>
    /// Whose a session is inside a team's tenant: the person who owns the ONE Director that holds it, when every
    /// Director that wrote its stored conversation is theirs too (the same answer <see cref="Whose"/> gives). Null when
    /// no Director or several hold it, or a writer is not the holder's owner's. Personally identifying: never logged.
    /// </summary>
    public string? PersonOfSession(TenantId tenant, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;
        var holders = _sessions.DirectorsHoldingSession(tenant, sessionId);
        if (holders.Count != 1)
        {
            FileLog.Write($"[TeamCallerOwnership] PersonOfSession: session {sessionId} is held by {holders.Count} Directors in tenant {tenant.ToLogString()} - nobody's");
            return null;
        }
        var owner = OwnerOf(tenant, holders[0]);
        if (owner is null)
            return null;
        return OwnerOfStoredConversation(tenant, sessionId, holders[0], owner) == TeamOwnership.Callers ? owner : null;
    }

    private TeamOwnership OwnerOfDirector(TenantId tenant, string directorId, string callerSubject, string what)
    {
        var owner = OwnerOf(tenant, directorId);
        if (owner is null)
        {
            FileLog.Write($"[TeamCallerOwnership] Whose: {what} of director={directorId} in tenant {tenant.ToLogString()} - nobody's, unknown");
            return TeamOwnership.Unknown;
        }

        var mine = string.Equals(owner, callerSubject, StringComparison.Ordinal);
        FileLog.Write($"[TeamCallerOwnership] Whose: {what} of director={directorId} in tenant {tenant.ToLogString()} - {(mine ? "the caller's own" : "someone else's")}");
        return mine ? TeamOwnership.Callers : TeamOwnership.SomeoneElses;
    }

    private static bool IsUnder(string pattern, string prefix) =>
        string.Equals(pattern, prefix, StringComparison.Ordinal)
        || pattern.StartsWith(prefix + "/", StringComparison.Ordinal);
}

/// <summary>The answer of <see cref="TeamCallerOwnership.ClaimOf(TenantId, string, string, string)"/>.</summary>
public enum TeamSessionClaim
{
    /// <summary>The session is this Director's own.</summary>
    Its,

    /// <summary>Nothing records whose it is and two Directors list it: nobody's for now; it ends when the other stops.</summary>
    NotNow,

    /// <summary>Its stored rows were written by another person's Director. For good: rows are never taken back.</summary>
    AnotherPersonWroteIt,

    /// <summary>Nothing is stored and its session key row names another Director. For good: the row is never taken over.</summary>
    AnotherDirectorsKey,

    /// <summary>The Director is nobody's, or the key it said Hello on names no person.</summary>
    NoPerson,
}
