using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// WHAT A MEMBER'S OWN DIRECTOR MAY WRITE ABOUT ITSELF INSIDE A TEAM (devthrottle_internal#2311, live proof F2), for the
/// three endpoints whose body names a Director or a session, which the team gate cannot read: a session's number
/// (<c>POST /session-numbers/allocate</c>), a skill placement report (<c>POST /gateway/skills/placement</c>) and the
/// activity ledger (<c>POST /activity-events/batch</c>). The gate lets them through as the caller's own
/// (<see cref="TeamCallerOwnership.AboutTheCallingKey"/>); each endpoint then asks here, and every answer comes from the
/// one ownership rule through <see cref="TeamCaller"/>, never a second copy.
///
/// The rule is the role table's principle - a person's own Director and sessions, yes; anyone else's, no:
/// <list type="bullet">
/// <item>A request whose caller names no person, or whose key names no Director, is refused: it cannot be shown to be
/// anyone's.</item>
/// <item>An event must name the calling key's own Director. A number and a placement report are filed under that Director
/// whatever the body says, so they need no such check.</item>
/// <item>A session another person's Director owns - its stored rows or its key row are another person's
/// (<see cref="TeamSessionClaim.AnotherPersonWroteIt"/>, <see cref="TeamSessionClaim.AnotherDirectorsKey"/>), or its number
/// was handed to another person's Director - is refused. A session nothing records yet (a new one, before its key row)
/// is not another person's, so it is not refused.</item>
/// </list>
/// </summary>
public static class TeamCallerChecks
{
    /// <summary>What a request is told when the calling key cannot be tied to one of the caller's Directors.</summary>
    public const string NoKeyDirectorRefusal =
        "DevThrottle cannot tell which of your Directors is making this request, so inside a team it refuses it. Nothing was done.";

    /// <summary>What a Director is told when it writes in another Director's name.</summary>
    public const string NotThisDirectorRefusal =
        "Inside a team a Director may write only about itself, and this names another Director. Nothing was done.";

    /// <summary>What a Director is told when it writes about another person's session.</summary>
    public const string AnotherPersonsSessionRefusal =
        "Inside a team a Director may act only for its own sessions, and this names a session that is another person's. Nothing was done.";

    /// <summary>Why a number may not be handed out for <paramref name="sessionId"/>, or null when it may.
    /// <paramref name="numberedFor"/> is the Director the session's number was already handed to, or null.</summary>
    public static string? RefuseNumber(TeamCaller caller, string sessionId, string? numberedFor)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.Person is null || caller.KeyDirector is null)
            return Log("number", NoKeyDirectorRefusal);
        if (numberedFor is not null && !caller.OwnsDirector(numberedFor))
            return Log("number", AnotherPersonsSessionRefusal);
        return IsAnotherPersons(caller.ClaimOfSession(sessionId)) ? Log("number", AnotherPersonsSessionRefusal) : null;
    }

    /// <summary>Why a skill placement report may not be filed, or null when it may (under the calling key's own
    /// Director).</summary>
    public static string? RefusePlacement(TeamCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.Person is null || caller.KeyDirector is null ? Log("placement", NoKeyDirectorRefusal) : null;
    }

    /// <summary>Why an activity batch may not be written, or null when every event is the caller's own: each names the
    /// calling key's own Director, and no event's session is another person's. One event that is not refuses the whole
    /// batch, so nothing of it is written.</summary>
    public static string? RefuseActivityBatch(TeamCaller caller, IEnumerable<(string DirectorId, string SessionId)> events)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(events);
        if (caller.Person is null || caller.KeyDirector is null)
            return Log("activity", NoKeyDirectorRefusal);
        foreach (var (directorId, sessionId) in events)
        {
            if (!caller.IsKeyDirector(directorId))
                return Log("activity", NotThisDirectorRefusal);
            if (!string.IsNullOrWhiteSpace(sessionId) && IsAnotherPersons(caller.ClaimOfSession(sessionId)))
                return Log("activity", AnotherPersonsSessionRefusal);
        }
        return null;
    }

    /// <summary>The 403 a refused request is answered with - the team gate's own refusal code, so a client tells it
    /// apart from any other 403.</summary>
    public static IResult Refused(string message) =>
        Results.Json(new { error = message, code = TeamEndpointGate.RefusalCode }, statusCode: StatusCodes.Status403Forbidden);

    private static bool IsAnotherPersons(TeamSessionClaim claim) =>
        claim is TeamSessionClaim.AnotherPersonWroteIt or TeamSessionClaim.AnotherDirectorsKey or TeamSessionClaim.NoPerson;

    private static string Log(string what, string refusal)
    {
        FileLog.Write($"[TeamCallerChecks] {what}: REFUSED - {refusal}");
        return refusal;
    }
}
