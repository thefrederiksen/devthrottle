namespace CcDirector.Gateway.Teams;

/// <summary>What the Owner or a Manager does to a request (devthrottle_internal#2308).</summary>
public enum TeamRequestDecision
{
    /// <summary>Accept it: it will be done.</summary>
    Accept,

    /// <summary>Mark it Not doing this. A reason is required.</summary>
    Decline,

    /// <summary>Mark it Done.</summary>
    MarkDone,

    /// <summary>Forward it to a factory of the team, quoted as the sender's words, in the Owner's name (Team
    /// implementation piece 4.3). The Owner alone, and only through <see cref="TeamRequestStore.RecordForward"/> - never
    /// through <see cref="TeamRequestStore.Decide"/>, because a forward also leaves a message in a factory inbox.</summary>
    Forward,
}

/// <summary>
/// THE STATES OF A REQUEST AND WHICH CHANGE IS ALLOWED FROM EACH, ONCE (devthrottle_internal#2308). A request is
/// <c>sent</c>; the Owner or a Manager accepts it, marks it Not doing this (<c>declined</c>, with a reason), or marks
/// it Done. Accepted may still become Not doing this or Done. Not doing this and Done are final: a request that needs
/// doing again is a new request, so the sender's trail never changes under them.
///
/// FORWARDED (Team implementation piece 4.3): the team's Owner read it and forwarded it to one of the team's factories.
/// A sent or accepted request may be forwarded, once; a forwarded one may still be marked Not doing this or Done, and
/// is never accepted afterwards (forwarding is further along than accepting) or forwarded a second time.
///
/// Who may make a change is not here - that is the role table (<see cref="TeamAction.ReadAndDecideTeamRequests"/>),
/// asked through <see cref="TeamAccess"/>.
/// </summary>
public static class TeamRequestStates
{
    /// <summary>Stored state: sent, waiting for the Owner or a Manager.</summary>
    public const string Sent = "sent";

    /// <summary>Stored state: accepted.</summary>
    public const string Accepted = "accepted";

    /// <summary>Stored state: Not doing this.</summary>
    public const string Declined = "declined";

    /// <summary>Stored state: done.</summary>
    public const string Done = "done";

    /// <summary>Stored state: forwarded to a factory of the team by its Owner.</summary>
    public const string Forwarded = "forwarded";

    /// <summary>The longest note the Owner may add above a forwarded message, in characters.</summary>
    public const int MaxNoteLength = 2000;

    /// <summary>The longest request accepted, in characters.</summary>
    public const int MaxTextLength = 4000;

    /// <summary>The longest reason for "Not doing this" accepted, in characters.</summary>
    public const int MaxReasonLength = 1000;

    /// <summary>The state's name as every screen shows it.</summary>
    public static string Label(string state) => state switch
    {
        Sent => "Sent",
        Accepted => "Accepted",
        Declined => "Not doing this",
        Done => "Done",
        Forwarded => "Forwarded to the factory",
        _ => throw new InvalidOperationException($"team_requests.state holds '{state}', which is not one of sent, accepted, forwarded, declined or done."),
    };

    /// <summary>The state a decision moves a request to.</summary>
    public static string StateAfter(TeamRequestDecision decision) => decision switch
    {
        TeamRequestDecision.Accept => Accepted,
        TeamRequestDecision.Decline => Declined,
        TeamRequestDecision.MarkDone => Done,
        TeamRequestDecision.Forward => Forwarded,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Not a decision about a request."),
    };

    /// <summary>Whether a request in <paramref name="state"/> may be moved by <paramref name="decision"/>.</summary>
    public static bool MayMove(string state, TeamRequestDecision decision)
    {
        var after = StateAfter(decision);
        return state switch
        {
            Sent => true,
            Accepted => after != Accepted,
            Forwarded => after is Declined or Done,
            Declined or Done => false,
            _ => throw new InvalidOperationException($"team_requests.state holds '{state}', which is not one of sent, accepted, forwarded, declined or done."),
        };
    }

    /// <summary>What a person is told when the request's state does not allow the change, in plain words.</summary>
    public static string MoveRefusal(string state, TeamRequestDecision decision) => state switch
    {
        Accepted when decision == TeamRequestDecision.Accept => "This request has already been accepted.",
        Forwarded when decision == TeamRequestDecision.Forward => "This message has already been forwarded to the factory.",
        Forwarded => "This message was forwarded to the factory, which is further along than accepting it.",
        Declined => "This request is marked Not doing this, which is final. If it needs doing after all, ask for it as a new request.",
        Done => "This request is already Done, which is final. If more is needed, ask for it as a new request.",
        _ => throw new InvalidOperationException($"A request in state '{state}' may be moved by {decision}; there is no refusal to give."),
    };

    /// <summary>Why the request's text cannot be sent, or null when it can. Trimmed before it is measured.</summary>
    public static string? TextRefusal(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
            return "Write your request before sending it.";
        if (trimmed.Length > MaxTextLength)
            return $"A request can be at most {MaxTextLength} characters. This one is {trimmed.Length}.";
        return null;
    }

    /// <summary>Why the Owner's note above a forwarded message cannot be used, or null when it can. The note is optional;
    /// it is trimmed before it is measured.</summary>
    public static string? NoteRefusal(string? note)
    {
        var trimmed = note?.Trim() ?? "";
        if (trimmed.Length > MaxNoteLength)
            return $"A note can be at most {MaxNoteLength} characters. This one is {trimmed.Length}.";
        return null;
    }

    /// <summary>Why the reason given cannot be used, or null when it can. A reason is required for "Not doing this"
    /// and is not taken for any other decision.</summary>
    public static string? ReasonRefusal(TeamRequestDecision decision, string? reason)
    {
        var trimmed = reason?.Trim() ?? "";
        if (decision != TeamRequestDecision.Decline)
            return null;
        if (trimmed.Length == 0)
            return "Say why this request is not being done. The person who sent it reads the reason.";
        if (trimmed.Length > MaxReasonLength)
            return $"A reason can be at most {MaxReasonLength} characters. This one is {trimmed.Length}.";
        return null;
    }
}
