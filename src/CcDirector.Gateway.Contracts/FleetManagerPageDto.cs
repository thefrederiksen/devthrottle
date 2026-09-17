namespace CcDirector.Gateway.Contracts;

/// <summary>
/// THE FLEET MANAGER PAGE, folded once on the Gateway (the Fleet Manager mission, step 6). Answered by
/// <c>GET /gateway/fleet-manager/page</c>: the cards drawn from outcome records, the live right panel, the rail's
/// badge count and the quick prompts.
///
/// The header (agent, computer, state line) and the not-running state are NOT here: they are the Fleet Manager
/// setting's answer (<see cref="FleetManagerPlacementDto"/>), read from its own route, so the same facts are never
/// folded twice. The conversation itself is the marked session's history, read through the Chat tab's reader.
///
/// THE CLIENT RENDERS THIS AS SENT (CLAUDE.md rule 7). Every heading, sentence, age, tone, label, count, and every
/// button with the exact words it sends is decided here. Times are UTC; every string that shows a time already
/// carries it written in the account's display time zone.
/// </summary>
public sealed class FleetManagerPageDto
{
    /// <summary>When this answer was folded (UTC).</summary>
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>The session the account marked as its Fleet Manager, or null when none is marked.</summary>
    public string? FleetManagerSessionId { get; set; }

    /// <summary>The sentence the conversation shows when no Fleet Manager is marked, so there is no conversation to
    /// read. Null when one is marked (the conversation's own empty sentence then comes with its history).</summary>
    public string? NoConversationText { get; set; }

    /// <summary>How many records are waiting on the owner - the red badge on the rail's Fleet Manager entry.</summary>
    public int WaitingCount { get; set; }

    /// <summary>The way into the walkthrough from "Waiting on you" (step 7): "Take me through them", or null when
    /// nothing is waiting.</summary>
    public string? WalkthroughLabel { get; set; }

    /// <summary>The buttons beside the title that send fixed words to the Fleet Manager ("What did I miss?").</summary>
    public List<FleetManagerQuickPromptDto> QuickPrompts { get; set; } = new();

    /// <summary>One card per outcome record the page shows, oldest first. The page places each card in the
    /// conversation at its <see cref="FleetOutcomeCardDto.FiledAtUtc"/>.</summary>
    public List<FleetOutcomeCardDto> Cards { get; set; } = new();

    /// <summary>The records waiting on the owner, most important first.</summary>
    public FleetPanelSectionDto Waiting { get; set; } = new();

    /// <summary>The live sessions the Fleet Manager owns.</summary>
    public FleetPanelSectionDto UnderWay { get; set; } = new();

    /// <summary>What the Gateway can honestly say finished today.</summary>
    public FleetPanelSectionDto Landed { get; set; } = new();

    /// <summary>The honest count of live sessions that still ask the owner directly.</summary>
    public FleetNotMineDto NotMine { get; set; } = new();
}

/// <summary>A button that sends fixed words to the Fleet Manager, verbatim.</summary>
public sealed class FleetManagerQuickPromptDto
{
    public string Label { get; set; } = "";

    /// <summary>The exact words sent as the prompt.</summary>
    public string Words { get; set; } = "";
}

/// <summary>
/// One outcome record as the page draws it. Exactly one of <see cref="Ready"/>, <see cref="Finding"/> and
/// <see cref="Decision"/> is set, matching <see cref="Kind"/>; every field in them is the record's own, verbatim.
/// </summary>
public sealed class FleetOutcomeCardDto
{
    public const string ToneReady = "ready";
    public const string ToneFinding = "finding";
    public const string ToneDecision = "decision";

    public string Id { get; set; } = "";

    /// <summary><c>ready</c>, <c>finding</c> or <c>decision</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The colour of the card's edge: one of the Tone constants.</summary>
    public string Tone { get; set; } = "";

    /// <summary>The small heading, for example "Ready for you".</summary>
    public string KindLabel { get; set; } = "";

    /// <summary>The record's title, verbatim.</summary>
    public string Title { get; set; } = "";

    /// <summary>When the record was filed (UTC) - where the card sits in the conversation.</summary>
    public DateTime FiledAtUtc { get; set; }

    /// <summary>The line above the card, for example "Fleet Manager - 10:14".</summary>
    public string WhoLine { get; set; } = "";

    public FleetReadyCardDto? Ready { get; set; }
    public FleetFindingCardDto? Finding { get; set; }
    public FleetDecisionCardDto? Decision { get; set; }

    /// <summary>True once the owner has answered. An answered card offers no buttons.</summary>
    public bool Answered { get; set; }

    /// <summary>On an answered card: the heading of the answer, for example "Answered 08:42". Null otherwise.</summary>
    public string? AnswerLabel { get; set; }

    /// <summary>On an answered card: the owner's words, verbatim. Null otherwise.</summary>
    public string? Answer { get; set; }

    /// <summary>The buttons that answer the record. Empty on an answered card.</summary>
    public List<FleetCardActionDto> Actions { get; set; } = new();
}

/// <summary>The Ready fields, verbatim, with the labels the card shows beside them.</summary>
public sealed class FleetReadyCardDto
{
    /// <summary>The risk pill, for example "Risk low".</summary>
    public string RiskLabel { get; set; } = "";

    /// <summary>low | medium | high - the colour of the pill.</summary>
    public string RiskTone { get; set; } = "";

    /// <summary>The facts in the row under the title, in order.</summary>
    public List<FleetCardFactDto> Facts { get; set; } = new();

    /// <summary>The one sentence on what changed, verbatim.</summary>
    public string Change { get; set; } = "";

    /// <summary>The link to the pull request.</summary>
    public FleetCardLinkDto PullRequest { get; set; } = new();
}

/// <summary>One labelled fact on a card, for example "Checks" / "passed".</summary>
public sealed class FleetCardFactDto
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>A link on a card.</summary>
public sealed class FleetCardLinkDto
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>The Finding fields, verbatim: the answer first, then the reason, then the links.</summary>
public sealed class FleetFindingCardDto
{
    public string Answer { get; set; } = "";
    public string? Reason { get; set; }
    public List<FleetCardLinkDto> Links { get; set; } = new();
}

/// <summary>The Decision fields, verbatim.</summary>
public sealed class FleetDecisionCardDto
{
    /// <summary>The question, or null when it is the same as the card's title (it is then not shown twice).</summary>
    public string? Question { get; set; }

    /// <summary>Every option, in the record's order.</summary>
    public List<FleetDecisionOptionDto> Options { get; set; } = new();

    /// <summary>The marker shown on the recommended option.</summary>
    public string RecommendedLabel { get; set; } = "";

    /// <summary>Why it recommends that option, with its lead-in ("Why: ..."), or null when it gave no reason.</summary>
    public string? Why { get; set; }
}

public sealed class FleetDecisionOptionDto
{
    public string Text { get; set; } = "";
    public bool Recommended { get; set; }
}

/// <summary>
/// A button on a card. Pressing it sends the owner's answer as if the owner had said it: the same words answer
/// the record and go to the Fleet Manager as a prompt.
/// </summary>
public sealed class FleetCardActionDto
{
    public string Label { get; set; } = "";

    /// <summary>primary | secondary | ghost.</summary>
    public string Style { get; set; } = "";

    /// <summary>The exact words sent. Null when the button first asks the owner for words.</summary>
    public string? Words { get; set; }

    /// <summary>True when the button asks for the owner's words before sending.</summary>
    public bool AsksForWords { get; set; }

    /// <summary>When it asks for words: what goes in front of them, so the Fleet Manager knows which card they
    /// answer. The words sent are this prefix followed by the owner's words exactly as typed.</summary>
    public string? WordsPrefix { get; set; }

    /// <summary>When it asks for words: the hint in the empty box.</summary>
    public string? Placeholder { get; set; }

    /// <summary>When it asks for words: the label of the button that sends them.</summary>
    public string? SendLabel { get; set; }
}

/// <summary>One section of the right panel.</summary>
public sealed class FleetPanelSectionDto
{
    public string Title { get; set; } = "";

    /// <summary>How many items the section holds.</summary>
    public int Count { get; set; }

    /// <summary>attention | plain - the colour of the heading.</summary>
    public string Tone { get; set; } = "";

    public List<FleetPanelItemDto> Items { get; set; } = new();

    /// <summary>The sentence shown when there are no items, or null when there are.</summary>
    public string? EmptyText { get; set; }

    /// <summary>A sentence under the items that says what the section cannot know, or null.</summary>
    public string? Note { get; set; }
}

/// <summary>One row of the right panel.</summary>
public sealed class FleetPanelItemDto
{
    public const string DotRed = "red";
    public const string DotBlue = "blue";
    public const string DotGreen = "green";
    public const string DotGrey = "grey";

    /// <summary>The record id or session id the row is about.</summary>
    public string Id { get; set; } = "";

    /// <summary>The full name - never shortened.</summary>
    public string Title { get; set; } = "";

    /// <summary>The dim line under the title.</summary>
    public string Meta { get; set; } = "";

    /// <summary>The Wingman's label for the session the row belongs to, verbatim, or null.</summary>
    public string? Label { get; set; }

    /// <summary>How long ago, for example "42m" or "1h 12m", or null.</summary>
    public string? Age { get; set; }

    /// <summary>The colour of the row's dot: one of the Dot constants.</summary>
    public string Dot { get; set; } = "";

    /// <summary>True for a row that is waiting on the owner (drawn tinted).</summary>
    public bool Attention { get; set; }

    /// <summary>The session the row belongs to, when there is one, so the page can open it.</summary>
    public string? SessionId { get; set; }
}

/// <summary>The honest count of live sessions that are neither the Fleet Manager nor owned by it, and that go
/// red for the owner.</summary>
public sealed class FleetNotMineDto
{
    public int Count { get; set; }

    /// <summary>The bold lead, for example "26 sessions are not the Fleet Manager's."</summary>
    public string Lead { get; set; } = "";

    /// <summary>The rest of the sentence, for example "They still ask you directly."</summary>
    public string Rest { get; set; } = "";
}
