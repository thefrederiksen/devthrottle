using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Where one request to restart a Director stands. Issue #2725 (restart epic, Phase 6).
///
/// SERIALISED AS ITS NAME, NEVER ITS NUMBER. This is a record a person reads on a phone and decides on;
/// a request that renders <c>state: 3</c> is a request nobody can act on. The Gateway API has no global
/// string-enum converter, so every enum on this contract carries the converter itself.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DirectorRestartRequestState
{
    /// <summary>A session asked, the machine scrutinised and said the restart can work, and the owner has
    /// not yet answered. The only state an accept is valid from.</summary>
    Pending,

    /// <summary>The owner accepted, and the Director on that machine took the cycle. From here nothing is
    /// asked of anybody: the Director drains, re-checks, asks its launcher and reports.</summary>
    Accepted,

    /// <summary>The owner said no. Nothing was done.</summary>
    Declined,

    /// <summary>Nobody accepted within the expiry window, so the approval that was being waited for can
    /// never arrive. A restart nobody currently wants must be asked for again.</summary>
    Expired,

    /// <summary>The Director could not proceed and stopped, never forcing. <see cref="DirectorRestartRequestDto.StateReason"/>
    /// says why, in the Director's own words, and the sessions it had already closed are in the workspace
    /// record it names.</summary>
    Abandoned,

    /// <summary>The Director ran the whole cycle: drained, wrote the workspace, re-checked the machine and
    /// asked its launcher, and the launcher accepted the guarded restart.</summary>
    Completed,
}

/// <summary>
/// The body a session sends to ask for a restart. Everything else on the record - who asked, which tenant,
/// what the machine said - is established by the Gateway from the authenticated credential and its own
/// registries, never taken from the body.
/// </summary>
public sealed class CreateDirectorRestartRequest
{
    /// <summary>Why, in the requester's own words. Required: a request with no reason gives the owner
    /// nothing to decide on.</summary>
    public string Reason { get; set; } = "";

    /// <summary>
    /// Which Director on that machine, when the machine runs several. Optional: when omitted, the Gateway
    /// uses the requesting session's OWN Director if it is on that machine, and otherwise requires the
    /// machine to have exactly one registered Director. It never guesses between two.
    /// </summary>
    public string? DirectorId { get; set; }
}

/// <summary>
/// What the Director reports back as it runs the cycle it was handed. Sent on the Director's own
/// credential, never a session key.
/// </summary>
public sealed class DirectorRestartProgressReport
{
    /// <summary>Which state the cycle has reached. Only <see cref="DirectorRestartRequestState.Accepted"/>
    /// (still running, with a new <see cref="Progress"/> line), <see cref="DirectorRestartRequestState.Abandoned"/>
    /// and <see cref="DirectorRestartRequestState.Completed"/> are accepted from a Director; the other
    /// states are the owner's and the Gateway's to set.</summary>
    public DirectorRestartRequestState State { get; set; }

    /// <summary>One plain sentence saying what the Director is doing, or why it stopped. Rendered verbatim.</summary>
    public string Progress { get; set; } = "";

    /// <summary>The workspace slug the drain wrote its record under, once it has one, so a reader of an
    /// abandoned request can find the sessions that were already closed.</summary>
    public string? WorkspaceId { get; set; }
}

/// <summary>
/// One request to restart a Director, as the owner reads it and as a session reads its own request back.
///
/// EVERY SENTENCE ON IT IS FINISHED ON THE GATEWAY. The Cockpit and the phone render these verbatim and
/// decide nothing: which button to offer is <see cref="CanAccept"/>, what the machine said is
/// <see cref="Capability"/>'s own reason sentences, how many sessions are live is
/// <see cref="LiveSessionsSentence"/>. A client that branched on <see cref="State"/> to write its own
/// sentence would, the first time it met a state it did not expect, render something plausible instead
/// of something true.
/// </summary>
public sealed class DirectorRestartRequestDto
{
    /// <summary>The request's identifier. Minted by the Gateway.</summary>
    public string Id { get; set; } = "";

    /// <summary>The machine whose Director is to be restarted, as the requester named it.</summary>
    public string Machine { get; set; } = "";

    /// <summary>The Director on that machine that will run the cycle.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>That Director's display name, or its machine name when it has none.</summary>
    public string DirectorName { get; set; } = "";

    /// <summary>The session that asked.</summary>
    public string RequestedBySessionId { get; set; } = "";

    /// <summary>That session's name at the moment it asked, so the owner sees who rather than a number.</summary>
    public string RequestedBySessionName { get; set; } = "";

    /// <summary>Why, in the requester's own words.</summary>
    public string Reason { get; set; } = "";

    /// <summary>When the request was made.</summary>
    public DateTime RequestedAtUtc { get; set; }

    /// <summary>When a pending request stops being acceptable. Thirty minutes after it was made.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>When the owner accepted, or null.</summary>
    public DateTime? AcceptedAtUtc { get; set; }

    /// <summary>When the request left the pending state for good - declined, expired, abandoned or
    /// completed - or null while it is pending or the cycle is still running.</summary>
    public DateTime? ClosedAtUtc { get; set; }

    /// <summary>Where the request stands.</summary>
    public DirectorRestartRequestState State { get; set; }

    /// <summary>Why it is in that state, when the state needs a reason: the owner's decline, the expiry,
    /// the Director's own words when it abandoned the cycle. Empty while pending.</summary>
    public string StateReason { get; set; } = "";

    /// <summary>The last progress line the Director reported, verbatim, and when.</summary>
    public string Progress { get; set; } = "";

    /// <summary>When <see cref="Progress"/> was last written, or null when the Director has not reported.</summary>
    public DateTime? ProgressAtUtc { get; set; }

    /// <summary>The workspace slug the drain wrote its record under, once the Director has reported one.</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>How many sessions the Gateway held for that Director at the moment of the request.</summary>
    public int LiveSessionCount { get; set; }

    /// <summary>The live count as one plain sentence.</summary>
    public string LiveSessionsSentence { get; set; } = "";

    /// <summary>What the machine said when it was scrutinised, in full: the verdict, the reason in Phase 1's
    /// own words, and whether the launcher would refuse a restart while sessions are live.</summary>
    public MachineRestartCapabilityDto? Capability { get; set; }

    /// <summary>The one line a screen puts at the top.</summary>
    public string Title { get; set; } = "";

    /// <summary>Who asked and why, as one sentence.</summary>
    public string AskedBySentence { get; set; } = "";

    /// <summary>What accepting does, as one sentence the card shows beside its confirmation. Written here so
    /// the promise the owner reads is the Gateway's and cannot drift from what the Director will do.</summary>
    public string AcceptSentence { get; set; } = "";

    /// <summary>Whether an accept would be honoured RIGHT NOW: pending and not yet expired. A client shows
    /// the accept and decline actions when this is true and never works it out from the timestamps itself.</summary>
    public bool CanAccept { get; set; }
}

/// <summary>The list envelope.</summary>
public sealed class DirectorRestartRequestListDto
{
    public List<DirectorRestartRequestDto> Requests { get; set; } = new();
}
