using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// WHETHER A ROW IS RAISED, AND THE CHANGE OFFERED ON IT, folded once for every row of the roster (the Fleet Manager
/// Improvement mission, phase 1). Critical rule 7 of the project instructions: the clients render these answers and decide nothing - so the
/// control that raises and lowers a session adds no conditional that decides what a state means.
///
///  - THE FLEET MANAGER's row wears the "Raised" mark and offers nothing: the mark raises it, so lowering it means
///    moving or clearing the mark, not pressing a button that would change nothing (10 October 2026).
///  - A RAISED row wears the "Raised" mark and offers "Lower".
///  - Every other running row offers "Raise", with the question to confirm first: raising hands a session the owner's
///    own permissions, which is not something to do with one stray press.
///  - A session that has ended offers nothing. A raised entry ends with its session.
///
/// The offer is the owner's. The routes serve the owner's own signed-in device only, and check again.
/// </summary>
internal static class RaisedSessionRosterFold
{
    public const string Mark = "Raised";

    public const string MarkTitle =
        "You raised this session: it acts with your permissions inside your account. It can type into your other "
        + "sessions and message any of them without limit. Everything it does that way is recorded.";

    public const string FleetManagerMarkTitle =
        "This is your Fleet Manager, and the Fleet Manager is always raised: it acts with your permissions inside your "
        + "account. It can type into your other sessions, message any of them without limit, and hand any of them over. "
        + "Everything it does that way is recorded. To lower it, move or clear the Fleet Manager in Settings.";

    /// <summary>The state of the account's Fleet Manager: raised by the mark, and nothing offered.</summary>
    public static SessionRaiseDto FleetManagerRow() => new()
    {
        Raised = true,
        Mark = Mark,
        MarkTitle = FleetManagerMarkTitle,
    };

    /// <summary>The state of a raised session: marked, and offering to be lowered.</summary>
    public static SessionRaiseDto RaisedRow() => new()
    {
        Raised = true,
        Mark = Mark,
        MarkTitle = MarkTitle,
        Offer = SessionRaiseDto.OfferLower,
        Label = "Lower",
        Title = "This session goes back to what every session may do: it can no longer type into your other sessions, "
                + "and its messages are limited again.",
        BusyLabel = "Lowering it...",
    };

    /// <summary>The state of a running session that is not raised: offering to be raised, after a question.</summary>
    public static SessionRaiseDto NotRaisedRow() => new()
    {
        Raised = false,
        Offer = SessionRaiseDto.OfferRaise,
        Label = "Raise",
        Title = "Let this session act with your permissions inside your account.",
        BusyLabel = "Raising it...",
        Confirm = "Raise this session? It will be able to type into your other sessions and message any of them "
                  + "without limit, as you can. It cannot add devices, sign in or out, change billing, or raise "
                  + "another session. Everything it does that way is recorded, and you can lower it at any time.",
    };

    /// <summary>The state of a session that has ended: not raised, and nothing offered.</summary>
    public static SessionRaiseDto EndedRow() => new() { Raised = false };

    /// <summary>The raise state of one row. <paramref name="raised"/> is
    /// <see cref="RaisedSessions.IsRaised"/>'s answer for it.</summary>
    /// <param name="markedSessionId">The account's Fleet Manager mark, or null.</param>
    public static SessionRaiseDto For(SessionDto session, bool raised, string? markedSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (FleetManagerSessions.IsGone(session)) return EndedRow();
        if (FleetManagerSessions.SameId(session.SessionId, markedSessionId)) return FleetManagerRow();
        return raised ? RaisedRow() : NotRaisedRow();
    }

    /// <summary>Stamp every row of <paramref name="toStamp"/> from the account's raised session ids. Every row is
    /// ASSIGNED, so nothing a Director sent survives.</summary>
    public static void Stamp(IReadOnlyList<SessionDto> toStamp, IReadOnlySet<string> raisedSessionIds, string? markedSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(toStamp);
        ArgumentNullException.ThrowIfNull(raisedSessionIds);
        foreach (var s in toStamp)
            s.Raise = For(s, !string.IsNullOrEmpty(s.SessionId) && raisedSessionIds.Contains(s.SessionId), markedSessionId);
    }
}
