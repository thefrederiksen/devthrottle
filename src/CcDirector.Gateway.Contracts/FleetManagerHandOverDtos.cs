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

    /// <summary>
    /// TAKE the session: the SESSION MAKING THE REQUEST owns it from now on (issue #3096). It is the only way a
    /// session may become an owner, and it names the caller rather than a session id ON PURPOSE - a session may take
    /// work to itself, on the owner's direction, and may never put a session under a THIRD session, nor put itself
    /// under another session. The destination is therefore not expressible as an id, and a request whose <c>to</c> is
    /// a session id is refused like any other unknown direction.
    /// </summary>
    public const string ToMe = "me";

    /// <summary>Every direction, as the route and the command line accept them.</summary>
    public static readonly IReadOnlyList<string> Directions = new[] { ToFleetManager, ToOwner, ToMe };

    /// <summary><see cref="ToFleetManager"/>, <see cref="ToOwner"/> or <see cref="ToMe"/> - what the client sends as
    /// <c>to</c>. A client of the owner's own (the Cockpit, the phone) never sends <see cref="ToMe"/>: the owner IS
    /// "me" there, which is <see cref="ToOwner"/>.</summary>
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
/// none - applied only if the session's owner is still the one the Gateway checked. The Gateway decides it; the
/// Director compares and stores it.
/// </summary>
public sealed class SetControllerRequest
{
    /// <summary>The value of <see cref="ExpectedControllerSessionId"/> when the Gateway found no owning session.</summary>
    public const string NoOwner = "none";

    /// <summary>The owning session's full id, or null when the owner owns the session.</summary>
    public string? ControllerSessionId { get; set; }

    /// <summary>
    /// The owner the Gateway checked before it decided: an owning session's full id, or <see cref="NoOwner"/>. Required.
    /// The Director changes the owner only if the session's owner is still this, and otherwise refuses with
    /// <see cref="DirectorCommandStatus.Conflict"/> - so a session another session acquired meanwhile is never taken,
    /// and of two hand overs sent at once exactly one is made.
    /// </summary>
    public string? ExpectedControllerSessionId { get; set; }
}
