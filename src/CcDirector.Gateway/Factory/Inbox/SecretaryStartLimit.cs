using CcDirector.Gateway.History;

namespace CcDirector.Gateway.Factory.Inbox;

/// <summary>The refusal a start door gives a Secretary: the status, a machine code, and the sentence the agent reads.</summary>
public sealed record SecretaryStartRefusal(int Status, string Code, string Sentence);

/// <summary>
/// A SECRETARY STARTS NOTHING (Factory Inbox mission, phase 2, task D). The Secretary files its factory's inbox and hands
/// one bundle to the Boss; the Boss decides who does the work. So a session recorded as a Secretary may not start a session,
/// a seat or a program on a machine - through any door.
///
/// WHY ONE PLACE AND NOT ONE CHECK PER DOOR. The session key guard (<c>Util/SessionKeyGuard</c>) cannot say it: it is a pure
/// function of a method and a path, and the generic spawn is open to every session key. But every session key request
/// passes ONE step - the authentication middleware's session-key accept - and that step knows who the caller is. The
/// refusal is made there, for every path <see cref="IsAStartDoor"/> names, before any route runs. A door that checked for
/// itself could forget; a door listed here cannot be reached by a Secretary at all.
///
/// KEYED ON THE CALLER'S HISTORY ROW, NEVER ON THE REQUEST. The caller is the session its own key proves, and whether it
/// is a Secretary is read from that session's recorded seat (<see cref="FactoryInboxSeats.Secretary"/>, the mark the
/// Gateway's Secretary start writes). Nothing in a body or a header is read. An UNDEFINED factory's Secretary carries no
/// seat - nothing defines one for it - so the second fact is the wake's own record: the session the wake started as that
/// factory's Secretary (<c>factory_inbox_wakes.SecretarySessionId</c>, the record the inbox routes accept it by). Either
/// fact makes the caller a Secretary, and this one refusal is the only place that says so.
///
/// THE START DOORS a session key can reach, each listed because it starts a session or launches a program:
/// <list type="bullet">
/// <item><c>POST /machines/{machine}/sessions</c> - the generic spawn, which is also the owner's Boss start door
///   (<c>Api/SpawnFactory.cs</c> TryStartTheBoss) for a session in no factory.</item>
/// <item><c>POST /directors/{id}/sessions</c> - the same spawn on one named Director.</item>
/// <item><c>POST /machines/{machine}/launch</c> - an application on a machine.</item>
/// <item><c>POST /gateway/factory/seats/{seat}/start</c> - a Boss starting a seat (<c>FactorySeatDelegation</c>).</item>
/// <item><c>POST /directors/{id}/handovers</c> - a handover, which can start a new session for the work.</item>
/// <item><c>POST /gateway/workspaces/{id}/restore</c> - restoring a workspace starts its sessions again.</item>
/// <item><c>POST /cron/jobs</c>, <c>PUT /cron/jobs/{id}</c>, <c>POST /cron/jobs/{id}/run</c> - a schedule starts a session
///   when it fires, and a run now starts one at once.</item>
/// <item><c>POST /triggers</c>, <c>PUT /triggers/{id}</c>, <c>POST /triggers/{id}/resume</c> - a trigger starts a session
///   when its check fires.</item>
/// <item><c>POST /directors/{id}/browsers/{browser}/start</c> and <c>/signin</c> - both launch a browser.</item>
/// <item><c>POST /gateway/fleet-manager/start</c>, <c>/restart</c>, <c>/move</c> - the Fleet Manager placement, reachable
///   only by a raised session key.</item>
/// </list>
/// Not doors, deliberately: a restart REQUEST (it restarts nothing until the owner accepts it), creating a browser profile
/// (it launches nothing), pausing a schedule or a trigger, and every read. Routes only a person's device reaches - the Talk
/// button, the Factory Manager talk - are refused to every session key by the guard already.
/// </summary>
public static class SecretaryStartLimit
{
    /// <summary>The refusal, said to the Secretary that tried.</summary>
    public const string Refusal =
        "a factory's Secretary may not start a session, a seat or a program. It files the factory inbox and hands one " +
        "bundle to the Boss, and the Boss decides who does the work. Hand the bundle with cc-devthrottle factory inbox " +
        "bundle; nothing was started.";

    /// <summary>The refusal while the caller's history row is not written yet: whether it is a Secretary cannot be read,
    /// so nothing is started on a guess. Phrased as a retry because it is one - the row is written from the first push.</summary>
    public const string NotYetKnown =
        "this session is not yet known to the Gateway, so whether it is a factory's Secretary cannot be read and nothing " +
        "is started until it can; try again in a moment";

    /// <summary>True when the caller's recorded seat is the Secretary - started as it, inherited, or moved with a
    /// handover - or when the wake recorded it as its factory's Secretary. A session that is the Secretary in any of
    /// these ways starts nothing.</summary>
    /// <param name="recordedByTheWake">Whether the wake's row for the caller's factory records the caller as its
    /// Secretary; asked only of a session in a factory whose seat does not already say so. Null when this host keeps no
    /// wake record.</param>
    public static bool IsASecretary(SessionFactoryLookup caller, Func<string, bool>? recordedByTheWake = null)
    {
        if (!caller.IsInAFactory) return false;
        if (string.Equals(caller.Seat?.Trim(), FactoryInboxSeats.Secretary, StringComparison.OrdinalIgnoreCase)) return true;
        return recordedByTheWake is not null && recordedByTheWake(caller.Factory!);
    }

    /// <summary>The refusal for this caller at a start door, or null when it may go on to the route.</summary>
    /// <param name="recordedByTheWake">See <see cref="IsASecretary"/>.</param>
    public static SecretaryStartRefusal? Refuse(SessionFactoryLookup caller, Func<string, bool>? recordedByTheWake = null)
    {
        if (!caller.IsKnown) return new SecretaryStartRefusal(409, "session_not_yet_known", NotYetKnown);
        return IsASecretary(caller, recordedByTheWake) ? new SecretaryStartRefusal(403, "secretary_starts_nothing", Refusal) : null;
    }

    /// <summary>
    /// True when the request is one of the start doors listed on this class. Matched on the STRUCTURE of the path,
    /// lower-cased, the way the session key guard matches it: ASP.NET routing ignores case, so a check that did not would
    /// be passed by <c>/Machines</c>. Identifier segments are never read.
    /// </summary>
    public static bool IsAStartDoor(string? method, string? path)
    {
        var verb = (method ?? "").ToUpperInvariant();
        var s = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < s.Length; i++) s[i] = s[i].ToLowerInvariant();

        if (verb == "POST")
        {
            if (s.Length == 3 && s[0] == "machines" && s[2] is "sessions" or "launch") return true;
            if (s.Length == 3 && s[0] == "directors" && s[2] is "sessions" or "handovers") return true;
            if (s.Length == 5 && s[0] == "gateway" && s[1] == "factory" && s[2] == "seats" && s[4] == "start") return true;
            if (s.Length == 4 && s[0] == "gateway" && s[1] == "workspaces" && s[3] == "restore") return true;
            if (s.Length == 2 && s[0] == "cron" && s[1] == "jobs") return true;
            if (s.Length == 4 && s[0] == "cron" && s[1] == "jobs" && s[3] == "run") return true;
            if (s.Length == 1 && s[0] == "triggers") return true;
            if (s.Length == 3 && s[0] == "triggers" && s[2] == "resume") return true;
            if (s.Length == 5 && s[0] == "directors" && s[2] == "browsers" && s[4] is "start" or "signin") return true;
            if (s.Length == 3 && s[0] == "gateway" && s[1] == "fleet-manager" && s[2] is "start" or "restart" or "move") return true;
            return false;
        }

        if (verb == "PUT")
        {
            if (s.Length == 3 && s[0] == "cron" && s[1] == "jobs") return true;
            if (s.Length == 2 && s[0] == "triggers") return true;
        }
        return false;
    }
}
