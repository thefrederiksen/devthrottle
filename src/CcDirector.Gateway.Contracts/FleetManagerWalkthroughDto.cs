namespace CcDirector.Gateway.Contracts;

// "Take me through them" - the Fleet Manager's walkthrough (the Fleet Manager mission, step 7). Served by
// GET /gateway/fleet-manager/walkthrough and folded once on the Gateway (FleetManagerWalkthroughFold): every
// heading, sentence, order, count, mark and permission is decided there, and the Cockpit renders it as sent
// (CLAUDE.md rule 7).

/// <summary>The whole walkthrough: this round's items in order, what is not in it, and how the round ends.</summary>
public sealed class FleetManagerWalkthroughDto
{
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>The account's Fleet Manager, when one is running as the Fleet Manager - the session a card's words are
    /// sent to after its record is answered. Null otherwise.</summary>
    public string? FleetManagerSessionId { get; set; }

    public string Title { get; set; } = "";
    public string Intro { get; set; } = "";

    /// <summary>The way out, back to the Fleet Manager page.</summary>
    public string BackLabel { get; set; } = "";

    /// <summary>The left column's heading, with the count: "This round - 3".</summary>
    public string RoundTitle { get; set; } = "";

    /// <summary>The records this round is made of, in order. The client sends them back on every read
    /// (<c>?round=</c>), so the round stays the one the owner started even as records are answered and new ones
    /// arrive.</summary>
    public List<string> RoundIds { get; set; } = new();

    public List<FleetWalkthroughItemDto> Items { get; set; } = new();

    /// <summary>"Not in this round: ..." - the Gateway's counts of what waits outside this round, or null when
    /// nothing does.</summary>
    public string? NotInRound { get; set; }

    /// <summary>Items still waiting in this round.</summary>
    public int OpenCount { get; set; }

    /// <summary>What the end of the round says.</summary>
    public string EndTitle { get; set; } = "";
    public string EndText { get; set; } = "";

    /// <summary>The button that goes through the items still open again (the ones skipped), or null when none are.</summary>
    public string? AgainLabel { get; set; }

    /// <summary>The button that starts a new round, when records are waiting outside this one; null otherwise.</summary>
    public string? NewRoundLabel { get; set; }

    /// <summary>Shown when the round has no items at all.</summary>
    public string? EmptyText { get; set; }
}

/// <summary>One item of the round: one open (or, earlier in this round, settled) outcome record.</summary>
public sealed class FleetWalkthroughItemDto
{
    /// <summary>The outcome record's id.</summary>
    public string Id { get; set; } = "";

    /// <summary>1-based place in the round, and its words: "2 of 3".</summary>
    public int Position { get; set; }
    public string PositionLabel { get; set; } = "";

    /// <summary><c>ready</c>, <c>finding</c> or <c>decision</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The record's title, verbatim.</summary>
    public string Title { get; set; } = "";

    /// <summary>The session's full name, when the record is about one.</summary>
    public string? SessionName { get; set; }

    /// <summary>The line under the heading: the session's repository, computer and agent, or the record's kind.</summary>
    public string Meta { get; set; } = "";

    /// <summary>"waiting 42m".</summary>
    public string? WaitLabel { get; set; }

    /// <summary>The left column's second line for this item: its session and wait, or, once settled, what was done.</summary>
    public string StepLine { get; set; } = "";

    /// <summary>Settled earlier in this round (answered, closed or snoozed). The left column shows it done.</summary>
    public bool Done { get; set; }

    /// <summary>The session the record is about, or null.</summary>
    public string? SessionId { get; set; }

    public FleetWalkthroughReadingDto Reading { get; set; } = new();
    public FleetWalkthroughAdviceDto Advice { get; set; } = new();
    public FleetWalkthroughScreenDto Screen { get; set; } = new();

    /// <summary>How this item is answered: <c>session</c> (the Wingman's options, sent to the session),
    /// <c>fleet-manager</c> (the record's own card buttons, answered and told to the Fleet Manager) or <c>none</c>
    /// (settled).</summary>
    public string AnswerMode { get; set; } = "";

    /// <summary>Set when <see cref="AnswerMode"/> is <c>session</c>.</summary>
    public FleetWalkthroughAnswerDto? Answer { get; set; }

    /// <summary>The record's card, drawn exactly as the Fleet Manager page draws it, when <see cref="AnswerMode"/> is
    /// <c>fleet-manager</c>.</summary>
    public FleetOutcomeCardDto? Card { get; set; }

    public FleetWalkthroughSnoozeDto Snooze { get; set; } = new();
    public string SkipLabel { get; set; } = "";
    public FleetWalkthroughOpenDto Open { get; set; } = new();
    public FleetWalkthroughCloseDto Close { get; set; } = new();
}

/// <summary>What the Wingman read at this item's session's current stop, verbatim, or the sentence saying why
/// there is nothing to show.</summary>
public sealed class FleetWalkthroughReadingDto
{
    public string Heading { get; set; } = "";

    /// <summary>True when there is a current reading to show.</summary>
    public bool Available { get; set; }

    /// <summary>The Gateway's sentence when there is no current reading, or when the one it has may be out of date.</summary>
    public string? Note { get; set; }

    /// <summary>The verdict's label, summary and evidence, copied exactly.</summary>
    public string? Label { get; set; }
    public string? Summary { get; set; }
    public string? EvidenceLead { get; set; }
    public string? Evidence { get; set; }

    /// <summary>"Risk: irreversible", or null when there is none.</summary>
    public string? RiskLine { get; set; }
}

/// <summary>The Fleet Manager's one line of advice.</summary>
public sealed class FleetWalkthroughAdviceDto
{
    public string Heading { get; set; } = "";

    /// <summary>The advice, verbatim, or null.</summary>
    public string? Text { get; set; }

    /// <summary>Shown when there is no advice.</summary>
    public string? EmptyText { get; set; }
}

/// <summary>The session's last lines, read by the client from the existing terminal buffer route.</summary>
public sealed class FleetWalkthroughScreenDto
{
    public string Heading { get; set; } = "";

    /// <summary>True when the client should read <c>GET /sessions/{sid}/buffer?lines=</c>.</summary>
    public bool Offered { get; set; }

    /// <summary>How many lines to ask for.</summary>
    public int Lines { get; set; }

    /// <summary>Shown instead of the screen when it is not offered.</summary>
    public string? Note { get; set; }

    public string LoadingText { get; set; } = "";
}

/// <summary>The Wingman's answer buttons for the current verdict, sent through
/// <c>POST /sessions/{sid}/turn-verdict/answer</c>.</summary>
public sealed class FleetWalkthroughAnswerDto
{
    public string VerdictId { get; set; } = "";

    /// <summary>The picker's question, when the answer is a menu selection.</summary>
    public string? Question { get; set; }

    /// <summary>True when several options are picked and sent together.</summary>
    public bool Multiple { get; set; }

    /// <summary>True when the stop is a typed reply waiting for its confirm (no options; one button sends an empty
    /// selection).</summary>
    public bool ParkedReply { get; set; }

    public List<FleetWalkthroughOptionDto> Options { get; set; } = new();

    /// <summary>Said when the Fleet Manager's pick names no option on the screen now.</summary>
    public string? PickNote { get; set; }

    public string SendChosenLabel { get; set; } = "";
    public string ParkedReplyLabel { get; set; } = "";
    public string SendingText { get; set; } = "";

    /// <summary>Shown after the session took the answer but the record could not be updated; the Gateway's own
    /// reason follows it.</summary>
    public string RecordFailedLead { get; set; } = "";
}

/// <summary>One of the Wingman's options, with both picks marked by the Gateway.</summary>
public sealed class FleetWalkthroughOptionDto
{
    /// <summary>The option's place in the verdict's options - what the answer route takes.</summary>
    public int Index { get; set; }

    /// <summary>The option's key, verbatim.</summary>
    public string Label { get; set; } = "";

    public string? Note { get; set; }

    /// <summary>The session's own recommended option.</summary>
    public bool SessionPick { get; set; }

    /// <summary>The option the Fleet Manager picked.</summary>
    public bool FleetManagerPick { get; set; }

    /// <summary>The words beside the button: "its pick", "Fleet Manager's pick", or both. Null when neither.</summary>
    public string? MarkText { get; set; }
}

public sealed class FleetWalkthroughSnoozeDto
{
    public bool Offered { get; set; }
    public string Label { get; set; } = "";

    /// <summary>The snooze length sent to <c>POST /sessions/{sid}/hold</c>.</summary>
    public int Minutes { get; set; }

    /// <summary>Why snooze is not offered, when it is not.</summary>
    public string? Note { get; set; }
}

public sealed class FleetWalkthroughOpenDto
{
    public bool Offered { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>Whether the session may be closed from here, and the words of the confirmation. Decided by
/// <c>FleetManagerCloseRule</c>; the close route decides again before it stops anything.</summary>
public sealed class FleetWalkthroughCloseDto
{
    public bool Offered { get; set; }
    public string Label { get; set; } = "";

    /// <summary>Why close is not offered, when the item is about a session and it is not.</summary>
    public string? RefusedText { get; set; }

    public string ConfirmTitle { get; set; } = "";
    public string ConfirmMessage { get; set; } = "";
    public string ConfirmLabel { get; set; } = "";
    public string BusyLabel { get; set; } = "";
}

/// <summary>The body of <c>POST /gateway/fleet-manager/walkthrough/{id}/answered</c>: the verdict the session was
/// just answered on. The Gateway records the options its answer route stored for that verdict.</summary>
public sealed class FleetWalkthroughAnsweredRequest
{
    /// <summary>The verdict the answer route was asked to answer. Required.</summary>
    public string? VerdictId { get; set; }

    /// <summary>Optional: the positions the client sent. Never recorded - only compared with what the answer route
    /// stored, and a difference refuses the request.</summary>
    public List<int>? OptionIndexes { get; set; }
}

/// <summary>What <c>POST /gateway/fleet-manager/walkthrough/{id}/close</c> did: the record as it now is, and the
/// stop's own answer.</summary>
public sealed class FleetWalkthroughCloseResponse
{
    public FleetOutcomeDto? Outcome { get; set; }
    public SessionStopResponse Stop { get; set; } = new();

    /// <summary>Set when the session was stopped but the record could not be answered: the Gateway's reason.</summary>
    public string? RecordError { get; set; }
}
