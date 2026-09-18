using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// PINNING AND THE OFFERED CHANGE OF OWNER, folded once for every row of the roster (the Fleet Manager mission,
/// step 8; design section 3.4). CLAUDE.md rule 7: the clients render these answers and decide nothing.
///
///  - THE PIN. The account's live Fleet Manager (<see cref="FleetManagerSessions.LiveFleetManager"/>) is pinned first
///    and wears the "Fleet Manager" mark. Its team - the sessions it owns, and theirs - is the ownership tree the list
///    already draws, collapsed under it. Every other row carries no pin and keeps its place.
///  - THE CHANGE OF OWNER. While the account has a live Fleet Manager, a session that asks the owner directly
///    (<see cref="FleetManagerSessions.AsksOwnerDirectly"/>) offers "Hand to the Fleet Manager", and a session the
///    Fleet Manager owns directly offers "Hand back to me". Nothing else offers either: the Fleet Manager itself, a
///    session another live session owns, a session that has ended. The route checks the same rules again, because a
///    row can be a moment old.
/// </summary>
internal static class FleetManagerRosterFold
{
    /// <summary>The visible mark on the pinned row.</summary>
    public const string Mark = "Fleet Manager";

    public const string PinTitle =
        "Your Fleet Manager. The sessions it owns are under it and report to it, not to you.";

    public const string OthersHeading = "Not the Fleet Manager's - they ask you directly";

    public const string HandOverLinkLabel = "Hand sessions to the Fleet Manager...";

    /// <summary>The offer on a session that asks the owner directly.</summary>
    public static SessionOwnerChangeDto HandToFleetManager() => new()
    {
        To = SessionOwnerChangeDto.ToFleetManager,
        Label = "Hand to the Fleet Manager",
        Title = "The Fleet Manager owns this session from now on: when it stops, the Fleet Manager is told instead of you.",
        BusyLabel = "Handing it to the Fleet Manager...",
    };

    /// <summary>The offer on a session the Fleet Manager owns.</summary>
    public static SessionOwnerChangeDto HandBack() => new()
    {
        To = SessionOwnerChangeDto.ToOwner,
        Label = "Hand back to me",
        Title = "This session stops being the Fleet Manager's and asks you directly again.",
        BusyLabel = "Handing it back to you...",
    };

    /// <summary>The pin the live Fleet Manager wears.</summary>
    public static SessionPinDto Pin() => new()
    {
        Rank = 0,
        Mark = Mark,
        Title = PinTitle,
        OthersHeading = OthersHeading,
        HandOverLinkLabel = HandOverLinkLabel,
    };

    /// <summary>The change of owner offered on <paramref name="session"/>, or null. <paramref name="fleetManagerId"/> is
    /// the account's LIVE Fleet Manager, or null when it has none; the row's
    /// <see cref="SessionDto.HasLiveSupervisor"/> must already be resolved across the account.</summary>
    public static SessionOwnerChangeDto? OwnerChangeFor(SessionDto session, string? fleetManagerId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrEmpty(fleetManagerId) || FleetManagerSessions.IsGone(session)) return null;
        if (FleetManagerSessions.SameId(session.SessionId, fleetManagerId)) return null;
        if (FleetManagerSessions.IsOwnedBy(session, fleetManagerId)) return HandBack();
        return FleetManagerSessions.AsksOwnerDirectly(session, fleetManagerId) ? HandToFleetManager() : null;
    }

    /// <summary>
    /// Stamp the pin and the offered change of owner on every row of <paramref name="toStamp"/>, from the live Fleet
    /// Manager found in <paramref name="roleUniverse"/> (the unfiltered account, already resolved by
    /// <see cref="FleetRoleResolver"/>). Every row is ASSIGNED, so nothing a Director sent survives.
    /// </summary>
    public static void Stamp(IReadOnlyList<SessionDto> roleUniverse, IReadOnlyList<SessionDto> toStamp, string? marked)
    {
        ArgumentNullException.ThrowIfNull(roleUniverse);
        ArgumentNullException.ThrowIfNull(toStamp);
        var fleetManager = FleetManagerSessions.LiveFleetManager(roleUniverse, marked)?.SessionId;
        foreach (var s in toStamp)
        {
            s.Pin = fleetManager is not null && FleetManagerSessions.SameId(s.SessionId, fleetManager) ? Pin() : null;
            s.OwnerChange = OwnerChangeFor(s, fleetManager);
        }
    }
}
