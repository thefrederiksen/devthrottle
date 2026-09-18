namespace CcDirector.Gateway.Contracts;

// The Fleet Manager's stored news and the digest it reads at the start of every conversation (the Fleet
// Manager mission, step 3). Served by /gateway/fleet-manager/*. The Cockpit draws its cards from these
// shapes, never from the model's prose, so every field a card needs is here, typed.

/// <summary>The fields only a READY record carries: work that is ready for the owner.</summary>
public sealed class FleetReadyDetails
{
    /// <summary>The full link to the pull request.</summary>
    public string PullRequest { get; set; } = "";

    /// <summary><c>low</c>, <c>medium</c> or <c>high</c>.</summary>
    public string Risk { get; set; } = "";

    /// <summary><c>passed</c>, <c>failed</c> or <c>none</c>.</summary>
    public string Checks { get; set; } = "";

    /// <summary>How it was tested.</summary>
    public string Tested { get; set; } = "";

    /// <summary>Who reviewed it.</summary>
    public string ReviewedBy { get; set; } = "";

    /// <summary>One sentence on what changed, for a user.</summary>
    public string Change { get; set; } = "";
}

/// <summary>The fields only a FINDING record carries: a report or investigation that is finished.</summary>
public sealed class FleetFindingDetails
{
    /// <summary>The answer, first.</summary>
    public string Answer { get; set; } = "";

    /// <summary>The reason, when given.</summary>
    public string? Reason { get; set; }

    /// <summary>Links to the reports.</summary>
    public List<string> Links { get; set; } = new();
}

/// <summary>The fields only a DECISION record carries: something only the owner can settle.</summary>
public sealed class FleetDecisionDetails
{
    /// <summary>The question.</summary>
    public string Question { get; set; } = "";

    /// <summary>The options on offer - at least two.</summary>
    public List<string> Options { get; set; } = new();

    /// <summary>The option the Fleet Manager recommends, exactly as it appears in <see cref="Options"/>.</summary>
    public string? Recommended { get; set; }

    /// <summary>Why it recommends that one.</summary>
    public string? Why { get; set; }
}

/// <summary>The body of <c>POST /gateway/fleet-manager/outcomes</c>. Exactly one of the three detail blocks
/// is given, the one that matches <see cref="Kind"/>.</summary>
public sealed class FleetOutcomeFileRequest
{
    /// <summary><c>ready</c>, <c>finding</c> or <c>decision</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>One line naming the news.</summary>
    public string? Title { get; set; }

    /// <summary>The session the news is about, when there is one.</summary>
    public string? SessionId { get; set; }

    public FleetReadyDetails? Ready { get; set; }
    public FleetFindingDetails? Finding { get; set; }
    public FleetDecisionDetails? Decision { get; set; }
}

/// <summary>The body of <c>POST /gateway/fleet-manager/outcomes/{id}/answer</c>.</summary>
public sealed class FleetOutcomeAnswerRequest
{
    /// <summary>The owner's words, exactly as given.</summary>
    public string? Answer { get; set; }
}

/// <summary>One stored record, as every route returns it.</summary>
public sealed class FleetOutcomeDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";

    /// <summary>The session id that filed it, or <c>owner</c>.</summary>
    public string FiledBy { get; set; } = "";

    /// <summary>The session the news is about, or null.</summary>
    public string? SessionId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Set on a <c>ready</c> record only.</summary>
    public FleetReadyDetails? Ready { get; set; }

    /// <summary>Set on a <c>finding</c> record only.</summary>
    public FleetFindingDetails? Finding { get; set; }

    /// <summary>Set on a <c>decision</c> record only.</summary>
    public FleetDecisionDetails? Decision { get; set; }

    public DateTime? AnsweredAtUtc { get; set; }

    /// <summary>The owner's answer, exactly as given.</summary>
    public string? Answer { get; set; }

    /// <summary><c>owner</c>, or the session id of the Fleet Manager that relayed the owner's word.</summary>
    public string? AnsweredBy { get; set; }

    /// <summary>WHO gave the answer: <c>owner</c> (the owner, on their own signed-in device) or
    /// <c>fleet-manager</c> (the account's Fleet Manager session, relaying the owner's word). Null while open.</summary>
    public string? AnsweredByRole { get; set; }

    /// <summary>On an answered decision: whether the answer was one of the options. Null otherwise.</summary>
    public bool? AnswerMatchedOption { get; set; }
}

/// <summary>The body of <c>POST /gateway/fleet-manager/preferences</c>.</summary>
public sealed class FleetPreferenceRequest
{
    /// <summary>The owner's words, stored exactly as given.</summary>
    public string? Text { get; set; }
}

/// <summary>One standing preference.</summary>
public sealed class FleetPreferenceDto
{
    public string Id { get; set; } = "";

    /// <summary>The owner's words, exactly as given.</summary>
    public string Text { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The session id that kept it, or <c>owner</c>.</summary>
    public string CreatedBy { get; set; } = "";
}

/// <summary>One session a Fleet Manager of this account owns - the current one or an earlier one - as the digest
/// carries it.</summary>
public sealed class FleetOwnedSessionDto
{
    public string SessionId { get; set; } = "";

    /// <summary>The session that controls it: the current Fleet Manager, or an earlier Fleet Manager of this
    /// account. A session still owned by an earlier one has NOT been handed over; that is a later step.</summary>
    public string OwnerSessionId { get; set; } = "";

    /// <summary>The full name, never shortened.</summary>
    public string Name { get; set; } = "";

    /// <summary>The session's word in its owner's crew line: <c>needs-you</c>, <c>working</c> or
    /// <c>stopped</c> (<see cref="SessionTree.CrewState"/>).</summary>
    public string State { get; set; } = "";

    /// <summary>The roster's own label for the row, verbatim (<see cref="SessionOrdering.StateLabel"/>).</summary>
    public string StateLabel { get; set; } = "";

    public string? MissionName { get; set; }

    /// <summary>Uncommitted files in its working copy, or null when its Director has not said.</summary>
    public int? UncommittedCount { get; set; }

    /// <summary>The Wingman's latest stored reading of this session - what
    /// <c>GET /sessions/{sid}/turn-verdict</c> returns - or null when it has none.</summary>
    public TurnVerdictDto? TurnVerdict { get; set; }
}

/// <summary>Open records by kind.</summary>
public sealed class FleetOutcomeCounts
{
    public int Ready { get; set; }
    public int Finding { get; set; }
    public int Decision { get; set; }
    public int Total { get; set; }
}

/// <summary>Owned sessions by crew word.</summary>
public sealed class FleetOwnedSessionCounts
{
    public int NeedsYou { get; set; }
    public int Working { get; set; }
    public int Stopped { get; set; }
    public int Total { get; set; }
}

/// <summary>The answer of <c>GET /gateway/fleet-manager/digest</c>: everything a Fleet Manager reads at the
/// start of a conversation, in one read. Only the account's Fleet Manager session, or the owner on their own
/// signed-in device, may read it.</summary>
public sealed class FleetDigestDto
{
    /// <summary>The session the digest was built for.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Whether that session IS the account's Fleet Manager
    /// (<c>FleetManagerSessions.IsFleetManager</c>).</summary>
    public bool IsFleetManager { get; set; }

    /// <summary>The session the account has marked as its Fleet Manager now, or null when it has marked none.</summary>
    public string? FleetManagerSessionId { get; set; }

    /// <summary>The Fleet Managers whose owned sessions are listed, oldest mark first: the current mark, and each
    /// session this account marked before it that still controls at least one live session.</summary>
    public List<string> FleetManagerSessionIds { get; set; } = new();

    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>EVERY open record of the account, newest first - never a capped slice.
    /// <see cref="OutcomeCounts"/> is counted by the database, so the two always agree.</summary>
    public List<FleetOutcomeDto> Outcomes { get; set; } = new();

    /// <summary>The sessions controlled by the current Fleet Manager or by any earlier one of this account,
    /// each with its <see cref="FleetOwnedSessionDto.OwnerSessionId"/>.</summary>
    public List<FleetOwnedSessionDto> OwnedSessions { get; set; } = new();

    /// <summary>The account's standing preferences, oldest first.</summary>
    public List<FleetPreferenceDto> Preferences { get; set; } = new();

    public FleetOutcomeCounts OutcomeCounts { get; set; } = new();
    public FleetOwnedSessionCounts OwnedSessionCounts { get; set; } = new();

    /// <summary>The OLDEST unacknowledged events about sessions a Fleet Manager owns, delivered or not, up to one page
    /// of 200 (step 4). A stop still waiting for its reading is among them and says so. When
    /// <see cref="EventsHasMore"/> is true, more remain: follow <see cref="EventsNextCursor"/> with
    /// <c>GET /gateway/fleet-manager/events?cursor=</c>.</summary>
    public List<FleetManagerEventDto> Events { get; set; } = new();

    /// <summary>Every unacknowledged event of the account, counted by the database.</summary>
    public int EventsTotal { get; set; }

    /// <summary>How many of those are stops still waiting for their reading.</summary>
    public int EventsWaitingForReading { get; set; }

    /// <summary>True when more unacknowledged events follow <see cref="Events"/>.</summary>
    public bool EventsHasMore { get; set; }

    /// <summary>The cursor for the unacknowledged events after <see cref="Events"/>, or null when there are none.</summary>
    public string? EventsNextCursor { get; set; }

    /// <summary>Why the events are not being delivered to the Fleet Manager right now, written by the Gateway for a page
    /// to show as it is - for example, the owner has unsent text in the Fleet Manager. Null when nothing holds them back.</summary>
    public string? EventsDeliveryNote { get; set; }
}
