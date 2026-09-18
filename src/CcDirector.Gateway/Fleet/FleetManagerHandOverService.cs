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
///    permission is needed for that direction. It is the mirror of the rule in <c>spawn_session</c>, where a session may
///    only ever name ITSELF as the owner of what it spawns.
///
/// Every other session key is refused with <see cref="FleetHandOverResult.NotFleetManager"/> and the reason, and the
/// reason says which direction IS allowed, so an agent that hits it learns the rule from the error. In particular a
/// session never ACQUIRES: not a session it does not own, not a session it does own (<c>--to fleet-manager</c>), and
/// never for another session. Taking a session, or handing one to the Fleet Manager, is the owner's to direct. The
/// roster is the caller's own account's, so a session of another account is not found.
///
/// Everything about the session is checked here, and each refusal is one sentence:
///  - a session this account does not run now (another account's session answers exactly the same);
///  - the Fleet Manager itself;
///  - to the Fleet Manager when the account has none marked, or its marked one is not running as the Fleet Manager;
///  - to the Fleet Manager when it already owns the session, or another RUNNING session owns it - that session's work
///    is not taken from it. A session whose owner has ended asks the owner directly, and may be handed over;
///  - back to the owner when the owner already has it, or when the session asking is neither the owner's own device
///    (which hands back the Fleet Manager's sessions) nor the session that owns it;
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
            return FleetHandOverResult.Refused(400, "A body is required: { \"session\": \"<full session id>\", \"to\": \"fleet-manager\" or \"owner\" }.");
        var to = (request.To ?? "").Trim().ToLowerInvariant();
        if (!SessionOwnerChangeDto.Directions.Contains(to))
            return FleetHandOverResult.Refused(400,
                $"\"to\" must be \"{SessionOwnerChangeDto.ToFleetManager}\" or \"{SessionOwnerChangeDto.ToOwner}\", not \"{request.To}\".");
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
        if (callingSessionId is not null && !releasing
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
        else
        {
            if (string.IsNullOrEmpty(owner))
                return FleetHandOverResult.Refused(409, $"{name} is already yours: no session owns it.");
            // The owner's own device hands back the Fleet Manager's sessions, as it always has. A session hands back
            // only what it owns itself, and that is the release the caller check above already let through.
            if (!releasing && !FleetManagerSessions.IsOwnedBy(session, marked ?? ""))
                return FleetHandOverResult.Refused(409,
                    $"{name} is owned by {OwnerName(roster, owner)}, not by the Fleet Manager. Only the Fleet Manager's sessions are handed back here.");
            newOwner = null;
        }

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

        var detail = newOwner is not null
            ? $"handed to the Fleet Manager {newOwner}; owned before by {owner ?? "the owner"}"
            : releasing
                ? $"released to the owner by session {owner}, which owned it"
                : $"handed back to the owner; owned before by the Fleet Manager {owner}";
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

        var sentence = newOwner is not null
            ? $"{name} is now the Fleet Manager's. When it stops, the Fleet Manager is told instead of you."
              + (string.IsNullOrEmpty(owner) ? "" : $" It was owned by session {owner}, which is no longer running.")
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
        const string Rule = " A session may hand over a session it OWNS, and only to the owner (--to owner). " +
                            "Taking a session, or handing one to the Fleet Manager, is the owner's to direct: he does it " +
                            "from the Cockpit or the phone, or tells a session to do it on his word.";
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
            why = $"Session {callingSessionId} owns session {sid}, but the only change of owner it may make on its " +
                  $"own is to release it: cc-devthrottle session hand-over {sid} --to owner.";
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
