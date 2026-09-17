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

    /// <summary>Whether this account's Director said it carries out the <c>set-controller</c> verb.</summary>
    bool ChangesOwner(TenantId tenant, string directorId);

    /// <summary>Send <c>set-controller</c> to the Director. The session as it reported it after, or the reason it did
    /// not.</summary>
    Task<(SessionDto? Session, string? Error)> SetControllerAsync(TenantId tenant, string directorId, string sessionId,
        string? controllerSessionId, CancellationToken ct);

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
/// THE CALLER. The route lets through the owner's own device and session keys. A session key is checked HERE, before
/// the session asked about is even looked up: only the account's marked Fleet Manager, running as the Fleet Manager
/// (<see cref="FleetManagerSessions.LiveFleetManager"/>), may hand over, and every other session is refused with
/// <see cref="FleetHandOverResult.NotFleetManager"/> and the reason. The Fleet Manager then meets exactly the rules the
/// owner meets, which already say what it may do: take a session that answers to the owner (to itself, the only Fleet
/// Manager there is), and hand back a session it owns - never one another running session owns. The roster is the
/// caller's own account's, so a session of another account is not found.
///
/// Everything about the session is checked here, and each refusal is one sentence:
///  - a session this account does not run now (another account's session answers exactly the same);
///  - the Fleet Manager itself;
///  - to the Fleet Manager when the account has none marked, or its marked one is not running as the Fleet Manager;
///  - to the Fleet Manager when it already owns the session, or another RUNNING session owns it - that session's work
///    is not taken from it. A session whose owner has ended asks the owner directly, and may be handed over;
///  - back to the owner when the owner already has it, or when another session than the Fleet Manager owns it;
///  - a session that has ended.
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
        if (callingSessionId is not null && RefuseUnlessFleetManager(roster, marked, callingSessionId) is { } notFleetManager)
            return notFleetManager;

        var found = roster.FirstOrDefault(r => FleetManagerSessions.SameId(r.Session.SessionId, sid));
        if (found.Session is null)
            return FleetHandOverResult.Refused(404,
                $"No session {sid} is running in this account on a computer that can be reached now, so it cannot be handed over.");
        var (directorId, session) = found;
        var name = NameOf(session);

        if (FleetManagerSessions.SameId(sid, marked))
            return FleetHandOverResult.Refused(409,
                $"{name} is the Fleet Manager itself. It answers to you only, so it cannot be handed over.");
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
            if (!FleetManagerSessions.IsOwnedBy(session, marked ?? ""))
                return FleetHandOverResult.Refused(409,
                    $"{name} is owned by {OwnerName(roster, owner)}, not by the Fleet Manager. Only the Fleet Manager's sessions are handed back here.");
            newOwner = null;
        }

        if (!_env.ChangesOwner(tenant, directorId))
            return FleetHandOverResult.Refused(409,
                $"The Director running {name}{OnMachine(session)} is older than hand over and cannot change a session's owner. " +
                "Update DevThrottle on that computer, then hand the session over again.");

        var (after, error) = await _env.SetControllerAsync(tenant, directorId, sid, newOwner, ct).ConfigureAwait(false);
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

    /// <summary>Null when <paramref name="callingSessionId"/> is the account's live Fleet Manager; otherwise the 403.</summary>
    private static FleetHandOverResult? RefuseUnlessFleetManager(
        IReadOnlyList<(string DirectorId, SessionDto Session)> roster, string? marked, string callingSessionId)
    {
        const string Owner = " The owner hands sessions over from the Cockpit or the phone.";
        string? why = null;
        if (string.IsNullOrEmpty(marked))
            why = $"Only this account's Fleet Manager session may hand a session over, and this account has no Fleet Manager marked, so session {callingSessionId} may not.";
        else if (!FleetManagerSessions.SameId(callingSessionId, marked))
            why = $"Only this account's Fleet Manager session ({marked}) may hand a session over; session {callingSessionId} is not it.";
        else if (FleetManagerSessions.LiveFleetManager(roster.Select(r => r.Session), marked) is null)
            why = $"Session {callingSessionId} is marked as the Fleet Manager but is not running as the Fleet Manager " +
                  "(it has ended, is owned by another session, or no computer of this account reports it), so it may not hand a session over.";
        if (why is null) return null;
        FileLog.Write($"[FleetManagerHandOverService] REFUSED session key {callingSessionId}: {why}");
        return FleetHandOverResult.Refused(403, why + Owner, FleetHandOverResult.NotFleetManager);
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
