using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>Everything a hand over needs from the Gateway around it, as one seam.</summary>
public interface IFleetManagerHandOverEnvironment
{
    /// <summary>The session this account has marked as its Fleet Manager, or null.</summary>
    string? MarkedFleetManager(TenantId tenant);

    /// <summary>The account's fresh pushed roster, with every role and owner answer resolved across the whole of it.</summary>
    IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant);

    /// <summary>The new Fleet Manager waiting to take over from the marked one (a restart or a move under way), or null.</summary>
    string? WaitingFleetManager(TenantId tenant) => null;

    /// <summary>Whether this account's Director said it makes a <c>set-controller</c> change only while the owner is still
    /// the expected one (<see cref="DirectorStreamHello.ChangesOwnerIfExpected"/>). The older verb flag alone is not enough.</summary>
    bool ChangesOwnerIfExpected(TenantId tenant, string directorId);

    /// <summary>Send <c>set-controller</c> to the Director: <paramref name="controllerSessionId"/> owns the session from
    /// now on, provided its owner is still <paramref name="expectedControllerSessionId"/> (null or empty for none). The
    /// session as it reported it after, or the reason it did not - with <c>OwnerMoved</c> true when the Director refused
    /// because the owner was no longer the expected one.</summary>
    Task<(SessionDto? Session, string? Error, bool OwnerMoved)> SetControllerAsync(TenantId tenant, string directorId, string sessionId,
        string? expectedControllerSessionId, string? controllerSessionId, CancellationToken ct);

    /// <summary>Record the change in the account's audit trail. Throws when it cannot.</summary>
    void Audit(TenantId tenant, string sessionId, string actor, string detail);

    /// <summary>Tell the Fleet Manager's events that this session's owner changed.</summary>
    void OwnerChanged(TenantId tenant, string directorId, SessionDto row);
}

/// <summary>How a hand over ended: an HTTP status, and either the answer or the refusal sentence.</summary>
public sealed record FleetHandOverResult(int Status, FleetHandOverResultDto? Answer, string? Error, string? Code = null)
{
    /// <summary>The code on a refusal of a session key that is not the account's live Fleet Manager.</summary>
    public const string NotFleetManager = "not_fleet_manager";

    public static FleetHandOverResult Refused(int status, string sentence, string? code = null) => new(status, null, sentence, code);
}

/// <summary>
/// HAND OVER (the Fleet Manager mission, step 8; design sections 3.1 and 3.4). The owner changes who owns a session
/// that is already running: to the account's Fleet Manager, or back to the owner (no owning session).
///
/// WHERE THE OWNER LIVES. On the Director's session (<c>Session.ControllerSessionId</c>), reported on every pushed row
/// and read by the Gateway's roster fold, the Wingman, the Fleet Manager's events and its digest. So the change is
/// made there, through the <c>set-controller</c> verb, and a Director that has not said it has that verb is refused
/// here with a sentence - an older one would answer "unknown verb", which says nothing about the fix.
///
/// THE CALLER. The route lets through the owner's own device and session keys. A session key is checked HERE, and two
/// callers get through, in ONE direction each:
///  - the account's marked Fleet Manager, running as the Fleet Manager (<see cref="FleetManagerSessions.LiveFleetManager"/>),
///    which meets exactly the rules the owner meets: take a session that answers to the owner (to itself, the only Fleet
///    Manager there is), and hand back a session it owns - never one another running session owns;
///  - ANY session RELEASING a session it owns to the owner (issue #3086, the owner's ruling of 18 September 2026).
///    Giving work away is always safe: it lands where everything lands by default, in front of the person, so no
///    permission is needed for that direction;
///  - ANY session TAKING a session that answers to the owner, TO ITSELF (issue #3096, the owner's ruling of the same
///    day). This one moves work AWAY from the person, so it is the owner who directs it - the session carries out his
///    word. The Gateway cannot see that word and does not pretend to: what makes it safe is that a session may only
///    ever name ITSELF, it can never reach a session another LIVE session holds, the change is audited and shown on
///    the owner's own row, and the owner takes any session back from his own screens.
///
/// THE ONE SENTENCE UNDER ALL THREE: the only owner a session may ever name is ITSELF - the rule <c>spawn_session</c>
/// already enforces for a session it starts, applied to sessions already running. So there is no direction that puts a
/// session under a THIRD session, and none that puts a session under another session on its own initiative. That is not
/// a check which could be relaxed later: <see cref="SessionOwnerChangeDto.Directions"/> has no way to name a session,
/// so it is unsayable.
///
/// Every other session key is refused with <see cref="FleetHandOverResult.NotFleetManager"/> and the reason, and the
/// reason says which direction IS allowed, so an agent that hits it learns the rule from the error. The roster is the
/// caller's own account's, so a session of another account is not found.
///
/// Everything about the session is checked here, and each refusal is one sentence:
///  - a session this account does not run now (another account's session answers exactly the same);
///  - the Fleet Manager itself;
///  - to the Fleet Manager when the account has none marked, or its marked one is not running as the Fleet Manager;
///  - to the Fleet Manager when it already owns the session, or another RUNNING session owns it - that session's work
///    is not taken from it. A session whose owner has ended asks the owner directly, and may be handed over;
///  - back to the owner when the owner already has it, or when the session asking is neither the owner's own device
///    (which takes back a session from ANY session, issue #3096) nor the session that owns it;
///  - TO THE SESSION ASKING when it is that session itself, when it already owns it, or when another RUNNING session
///    owns it - a take never takes work from another session;
///  - a session that has ended.
///
/// The change itself is compare-and-set: the Director is told the owner checked here and refuses, as a 409, when the
/// session's owner is no longer that - so of two hand overs sent at once exactly one is made, and a session that
/// another session acquired between this check and the change is left with that session.
///
/// Every change is recorded in the governance audit trail. A change the Director made but the trail could not record
/// is still a change, so it is answered as made, and the answer says the record is missing.
/// </summary>
public sealed class FleetManagerHandOverService
{
    private readonly IFleetManagerHandOverEnvironment _env;

    public FleetManagerHandOverService(IFleetManagerHandOverEnvironment environment)
    {
        _env = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <param name="callingSessionId">The session whose key made the request, or null when the owner made it.</param>
    public async Task<FleetHandOverResult> HandOverAsync(TenantId tenant, FleetHandOverRequest? request, string actor,
        CancellationToken ct, string? callingSessionId = null)
    {
        FileLog.Write($"[FleetManagerHandOverService] HandOverAsync: tenant={tenant.ToLogString()}, " +
                      $"session={request?.Session}, to={request?.To}, actor={actor}, callingSession={callingSessionId ?? "none"}");
        try
        {
            var result = await HandOverCoreAsync(tenant, request, actor, callingSessionId, ct).ConfigureAwait(false);
            FileLog.Write($"[FleetManagerHandOverService] HandOverAsync: status={result.Status}, " +
                          $"{(result.Error is null ? "changed" : "refused: " + result.Error)}");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[FleetManagerHandOverService] HandOverAsync FAILED: {ex.Message}");
            throw;
        }
    }

    private async Task<FleetHandOverResult> HandOverCoreAsync(TenantId tenant, FleetHandOverRequest? request, string actor,
        string? callingSessionId, CancellationToken ct)
    {
        if (request is null)
            return FleetHandOverResult.Refused(400,
                "A body is required: { \"session\": \"<full session id>\", \"to\": \"fleet-manager\", \"owner\" or \"me\" }.");
        var to = (request.To ?? "").Trim().ToLowerInvariant();
        // A SESSION ID AS THE DESTINATION IS NOT A DIRECTION, and falls in here with every other unknown word. That is
        // the whole of the "never under a third session" rule: it is not refused by a check that could be relaxed, it
        // is not expressible (issue #3096).
        if (!SessionOwnerChangeDto.Directions.Contains(to))
            return FleetHandOverResult.Refused(400,
                $"\"to\" must be \"{SessionOwnerChangeDto.ToFleetManager}\", \"{SessionOwnerChangeDto.ToOwner}\" or " +
                $"\"{SessionOwnerChangeDto.ToMe}\", not \"{request.To}\". A session is never handed to a session named by id: " +
                "a session may take a session TO ITSELF, and may never put one under a third session.");
        var raw = (request.Session ?? "").Trim();
        if (raw.Length == 0)
            return FleetHandOverResult.Refused(400, "\"session\" is required: the full id of the session to hand over.");
        if (!Guid.TryParse(raw, out var parsed))
            return FleetHandOverResult.Refused(400, $"\"{raw}\" is not a session id. Give the full id.");
        var sid = parsed.ToString();

        var roster = _env.Roster(tenant);
        var marked = _env.MarkedFleetManager(tenant);
        // RELEASING IS ALWAYS ALLOWED, ACQUIRING NEVER IS (issue #3086). A session may let go of a session it owns, and
        // only to the owner - the work then lands where everything lands by default, in front of the person. Read here,
        // before the caller is judged, because whether the caller owns the session is what decides whether it may ask.
        var callerOwnsIt = callingSessionId is not null
                           && roster.Any(r => FleetManagerSessions.SameId(r.Session.SessionId, sid)
                                              && FleetManagerSessions.IsOwnedBy(r.Session, callingSessionId));
        var releasing = callerOwnsIt && to == SessionOwnerChangeDto.ToOwner;
        // TAKING IS ALLOWED ON THE OWNER'S DIRECTION (issue #3096), and the guards are what make that safe rather than
        // hopeful: a session may only ever name ITSELF as the new owner, it can only reach a session that already
        // answers to the owner (never one another live session holds), and the owner takes any session back from his
        // own screens. The Gateway cannot see the owner's word, so it is not pretended: the change is audited, it is
        // on the row he reads, and it is one action to undo.
        var taking = to == SessionOwnerChangeDto.ToMe;
        if (taking && callingSessionId is null)
            return FleetHandOverResult.Refused(400,
                $"\"{SessionOwnerChangeDto.ToMe}\" is the SESSION making the request, so it needs a session's own key. " +
                $"From the Cockpit or the phone you are the owner: use \"{SessionOwnerChangeDto.ToOwner}\".");
        if (callingSessionId is not null && !releasing && !taking
            && RefuseUnlessFleetManager(roster, marked, callingSessionId, sid, callerOwnsIt) is { } notAllowed)
            return notAllowed;

        var found = roster.FirstOrDefault(r => FleetManagerSessions.SameId(r.Session.SessionId, sid));
        if (found.Session is null)
            return FleetHandOverResult.Refused(404,
                $"No session {sid} is running in this account on a computer that can be reached now, so it cannot be handed over.");
        var (directorId, session) = found;
        var name = NameOf(session);

        if (FleetManagerSessions.SameId(sid, marked))
            return FleetHandOverResult.Refused(409,
                $"{name} is the Fleet Manager itself. It answers to you only, so it cannot be handed over.");
        if (FleetManagerSessions.SameId(sid, _env.WaitingFleetManager(tenant)))
            return FleetHandOverResult.Refused(409,
                $"{name} is the new Fleet Manager, waiting to take over. It answers to you only, so it cannot be handed over.");
        if (FleetManagerSessions.IsGone(session))
            return FleetHandOverResult.Refused(409, $"{name} has ended, so it cannot be handed over.");

        var owner = session.ControllerSessionId;
        string? newOwner;
        if (to == SessionOwnerChangeDto.ToFleetManager)
        {
            if (string.IsNullOrEmpty(marked))
                return FleetHandOverResult.Refused(409,
                    "This account has no Fleet Manager, so there is nothing to hand the session to. Start the Fleet Manager from Settings first.");
            var fleetManager = FleetManagerSessions.LiveFleetManager(roster.Select(r => r.Session), marked);
            if (fleetManager is null)
                return FleetHandOverResult.Refused(409,
                    $"The Fleet Manager (session {marked}) is not running as the Fleet Manager, so it cannot take {name}. Start it from Settings first.");
            if (FleetManagerSessions.IsOwnedBy(session, fleetManager.SessionId))
                return FleetHandOverResult.Refused(409, $"{name} is already the Fleet Manager's.");
            if (session.HasLiveSupervisor)
                return FleetHandOverResult.Refused(409,
                    $"{name} is owned by {OwnerName(roster, owner)}, which is still running, so it was not handed over. " +
                    "It reports to that session, not to you; taking it would take it from the session that started it.");
            newOwner = fleetManager.SessionId;
        }
        else if (taking)
        {
            if (FleetManagerSessions.SameId(sid, callingSessionId))
                return FleetHandOverResult.Refused(409,
                    $"{name} is the session asking. A session cannot take itself: it already answers to whoever owns it.");
            if (FleetManagerSessions.IsOwnedBy(session, callingSessionId!))
                return FleetHandOverResult.Refused(409, $"{name} is already yours.");
            if (session.HasLiveSupervisor)
                return FleetHandOverResult.Refused(409,
                    $"{name} is owned by {OwnerName(roster, owner)}, which is still running, so it was not taken. " +
                    "It reports to that session, not to the owner; taking it would take it from the session that started it.");
            newOwner = callingSessionId;
        }
        else
        {
            if (string.IsNullOrEmpty(owner))
                return FleetHandOverResult.Refused(409, $"{name} is already yours: no session owns it.");
            // THE OWNER TAKES ANY SESSION BACK, from any session that holds it (issue #3096). It was the Fleet
            // Manager's sessions only, which was the whole undo for a take - missing.
            //
            // A SESSION, INCLUDING THE FLEET MANAGER, STILL HANDS BACK ONLY WHAT IT OWNS ITSELF. Releasing is safe
            // because it is YOUR work you are giving away; releasing somebody else's worker is not a gift, it is
            // taking it from the session that started it - the same reason a take never reaches a session another
            // live session holds. A release by the session that owns it is the one the caller check let through.
            if (callingSessionId is not null && !releasing)
                return FleetHandOverResult.Refused(409,
                    $"{name} is owned by {OwnerName(roster, owner)}, not by the session asking. A session hands back " +
                    "only a session it owns itself; the owner hands any session back from the Cockpit or the phone.");
            newOwner = null;
        }

        // NO RING. Putting a session under one it already owns - directly, or anywhere up that session's chain -
        // leaves the members answering to each other, and each of them then has a live owner, so NOT ONE of them ever
        // goes red again: the owner stops hearing from the whole ring until he notices and breaks it himself. Two
        // sessions are enough for that. Checked ONCE here, for every direction that puts a session under a session,
        // rather than in each branch, so a direction added later cannot miss it.
        if (newOwner is not null
            && FleetManagerSessions.WouldCloseALoop(roster.Select(r => r.Session), sid, newOwner))
            return FleetHandOverResult.Refused(409,
                $"{name} already owns the session it would be handed to, directly or further up that session's " +
                "chain, so it was not handed over: the two would answer to each other and neither would reach the " +
                "owner again. Hand the session it would go to back to the owner first, from the Cockpit or the " +
                "phone, and then this one can be taken.");

        if (!_env.ChangesOwnerIfExpected(tenant, directorId))
            return FleetHandOverResult.Refused(409,
                $"The Director running {name}{OnMachine(session)} is too old to hand a session over safely: it cannot check that the session's owner is still the one checked here, " +
                "so it could overwrite an owner another session set meanwhile. " +
                "Update DevThrottle on that computer, then hand the session over again.");

        // Compare and set: the Director makes the change only if the owner is still the one checked above. A session
        // another session acquired meanwhile, or a second hand over that got there first, is a conflict - never overwritten.
        var (after, error, ownerMoved) = await _env.SetControllerAsync(tenant, directorId, sid, owner, newOwner, ct).ConfigureAwait(false);
        if (ownerMoved)
            return FleetHandOverResult.Refused(409,
                $"{name} was not handed over: its owner changed while the hand over was on its way ({error ?? "no reason given"}). " +
                "Look at who owns it now, then hand it over again if that is still right.");
        if (after is null)
            return FleetHandOverResult.Refused(502,
                $"{name} was not handed over: its Director did not make the change ({error ?? "no answer"}).");
        if (!FleetManagerSessions.SameId(after.ControllerSessionId, newOwner) && !(newOwner is null && string.IsNullOrEmpty(after.ControllerSessionId)))
            return FleetHandOverResult.Refused(502,
                $"{name} was not handed over: its Director answered, but the session's owner is " +
                $"{after.ControllerSessionId ?? "nobody"}, not {newOwner ?? "nobody"}.");

        _env.OwnerChanged(tenant, directorId, after);

        var detail = taking
            ? $"taken by session {newOwner} on the owner's direction; owned before by {owner ?? "the owner"}"
            : newOwner is not null
                ? $"handed to the Fleet Manager {newOwner}; owned before by {owner ?? "the owner"}"
                : releasing
                    ? $"released to the owner by session {owner}, which owned it"
                    : $"handed back to the owner; owned before by {(FleetManagerSessions.SameId(owner, marked) ? "the Fleet Manager " : "session ")}{owner}";
        string? auditNote = null;
        try
        {
            _env.Audit(tenant, sid, actor, detail);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerHandOverService] the owner of {sid} changed, but the audit trail did NOT record it: {ex.Message}");
            auditNote = " The change was made, but the audit trail could not record it.";
        }

        var wasOrphaned = string.IsNullOrEmpty(owner) ? "" : $" It was owned by session {owner}, which is no longer running.";
        var sentence = taking
            ? $"{name} is yours now. When it stops, you are told instead of the owner, and you answer for it." + wasOrphaned
            : newOwner is not null
                ? $"{name} is now the Fleet Manager's. When it stops, the Fleet Manager is told instead of you." + wasOrphaned
                : $"{name} is yours again. It asks you directly from now on.";
        return new FleetHandOverResult(200, new FleetHandOverResultDto
        {
            SessionId = sid,
            To = to,
            OwnerSessionId = newOwner,
            PreviousOwnerSessionId = string.IsNullOrEmpty(owner) ? null : owner,
            Sentence = sentence + (auditNote ?? ""),
            Session = after,
        }, null);
    }

    /// <summary>Null when <paramref name="callingSessionId"/> is the account's live Fleet Manager; otherwise the 403.
    /// Asked only after a release has been ruled out, so every sentence here also says which direction IS allowed -
    /// an agent that hits this learns the rule from the error rather than from a document (issue #3086).
    ///
    /// <paramref name="callerOwnsIt"/> IS THE DIFFERENCE BETWEEN TWO REFUSALS, and saying the wrong one sends the
    /// reader after the wrong fix: a session that OWNS the session and asked for the wrong direction is not a session
    /// with no business here, and must never be told it does not own what it owns.</summary>
    private static FleetHandOverResult? RefuseUnlessFleetManager(
        IReadOnlyList<(string DirectorId, SessionDto Session)> roster, string? marked, string callingSessionId, string sid,
        bool callerOwnsIt)
    {
        const string Rule = " A session may release a session it OWNS to the owner (--to owner), and may take a session " +
                            "that answers to the owner TO ITSELF (--to me) when the owner has directed it. Handing a session " +
                            "to the Fleet Manager is the owner's to direct, from the Cockpit or the phone. No session is ever " +
                            "put under a third session.";
        // THE LIVE FLEET MANAGER PASSES FIRST, exactly as before: it may take a session that answers to the owner, and
        // a session it owns is answered by the ordinary rules below this method (already the Fleet Manager's, and so on).
        var opening = $"Session {callingSessionId} may not hand session {sid} over: it does not own that session, and ";
        string? why;
        if (string.IsNullOrEmpty(marked))
            why = opening + "this account has no Fleet Manager marked.";
        else if (!FleetManagerSessions.SameId(callingSessionId, marked))
            why = opening + $"it is not this account's Fleet Manager session ({marked}).";
        else if (FleetManagerSessions.LiveFleetManager(roster.Select(r => r.Session), marked) is null)
            why = opening + "it is marked as the Fleet Manager but is not running as the Fleet Manager " +
                  "(it has ended, is owned by another session, or no computer of this account reports it).";
        else
            return null;
        // A session that DOES own it asked for the wrong direction, so it is told that and not that it owns nothing.
        if (callerOwnsIt)
            why = $"Session {callingSessionId} already owns session {sid}. It may release it: " +
                  $"cc-devthrottle session hand-over {sid} --to owner.";
        FileLog.Write($"[FleetManagerHandOverService] REFUSED session key {callingSessionId}: {why}");
        return FleetHandOverResult.Refused(403, why + Rule, FleetHandOverResult.NotFleetManager);
    }

    private static string NameOf(SessionDto s)
        => string.IsNullOrWhiteSpace(s.Name) ? $"Session {s.SessionId}" : $"Session \"{s.Name}\"";

    private static string OwnerName(IReadOnlyList<(string DirectorId, SessionDto Session)> roster, string? owner)
    {
        var row = roster.FirstOrDefault(r => FleetManagerSessions.SameId(r.Session.SessionId, owner)).Session;
        return row is null || string.IsNullOrWhiteSpace(row.Name) ? $"session {owner}" : $"session \"{row.Name}\" ({owner})";
    }

    private static string OnMachine(SessionDto s)
        => string.IsNullOrWhiteSpace(s.MachineName) ? "" : $" on {s.MachineName}";
}
