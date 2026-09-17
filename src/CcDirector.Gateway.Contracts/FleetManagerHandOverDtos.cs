namespace CcDirector.Gateway.Contracts;

// Pinning and hand over (the Fleet Manager mission, step 8). The Gateway decides which row is pinned, which change
// of owner is offered on a row, and every word of both; the clients render them as sent.

/// <summary>A row pinned first in the session list, and the words it wears.</summary>
public sealed class SessionPinDto
{
    /// <summary>Where the row sits among the pinned rows: 0 first.</summary>
    public int Rank { get; set; }

    /// <summary>The visible mark on the row, for example "Fleet Manager".</summary>
    public string Mark { get; set; } = "";

    /// <summary>What the mark says when pointed at.</summary>
    public string Title { get; set; } = "";

    /// <summary>The heading over the rows that are not pinned, for example "Not the Fleet Manager's - they ask you
    /// directly".</summary>
    public string OthersHeading { get; set; } = "";

    /// <summary>The words of the link to the list of sessions the owner can hand over.</summary>
    public string HandOverLinkLabel { get; set; } = "";
}

/// <summary>The one change of owner offered on a session, and its words.</summary>
public sealed class SessionOwnerChangeDto
{
    /// <summary>Hand the session to the account's Fleet Manager.</summary>
    public const string ToFleetManager = "fleet-manager";

    /// <summary>Hand the session back to the owner: no session owns it any more.</summary>
    public const string ToOwner = "owner";

    /// <summary>Both directions, as the route and the command line accept them.</summary>
    public static readonly IReadOnlyList<string> Directions = new[] { ToFleetManager, ToOwner };

    /// <summary><see cref="ToFleetManager"/> or <see cref="ToOwner"/> - what the client sends as <c>to</c>.</summary>
    public string To { get; set; } = "";

    /// <summary>The button's words.</summary>
    public string Label { get; set; } = "";

    /// <summary>What the button says when pointed at: what will change.</summary>
    public string Title { get; set; } = "";

    /// <summary>The words shown while the change is being made.</summary>
    public string BusyLabel { get; set; } = "";
}

/// <summary>The body of <c>POST /gateway/fleet-manager/hand-over</c>.</summary>
public sealed class FleetHandOverRequest
{
    /// <summary>The full id of the session to hand over.</summary>
    public string Session { get; set; } = "";

    /// <summary><see cref="SessionOwnerChangeDto.ToFleetManager"/> or <see cref="SessionOwnerChangeDto.ToOwner"/>.</summary>
    public string To { get; set; } = "";
}

/// <summary>What <c>POST /gateway/fleet-manager/hand-over</c> answers when the owner changed.</summary>
public sealed class FleetHandOverResultDto
{
    /// <summary>The session that changed owner.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The direction it went.</summary>
    public string To { get; set; } = "";

    /// <summary>The session that owns it now, or null when the owner does.</summary>
    public string? OwnerSessionId { get; set; }

    /// <summary>The session that owned it before, or null when the owner did.</summary>
    public string? PreviousOwnerSessionId { get; set; }

    /// <summary>The Gateway's sentence saying what changed, to show as sent.</summary>
    public string Sentence { get; set; } = "";

    /// <summary>The session as its Director reported it after the change.</summary>
    public SessionDto? Session { get; set; }
}

/// <summary>
/// The body of the Director's <c>set-controller</c> verb: the session that owns this one from now on, or null for
/// none. The Gateway decides it; the Director stores it.
/// </summary>
public sealed class SetControllerRequest
{
    /// <summary>The owning session's full id, or null when the owner owns the session.</summary>
    public string? ControllerSessionId { get; set; }
}
