namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One judged stop: what the Wingman says this turn end MEANS, with the agent's own words as the
/// receipt. Stored on the Gateway per tenant and per session, and served to the clients, which render
/// it and never re-derive it.
///
/// A record is written for EVERY judged stop, including one whose answer was refused. A refused answer
/// sets <see cref="Failed"/> with a <see cref="FailureReason"/> and carries no verdict word, and the
/// session row stays exactly as the detector left it. That is the whole shape of the safety property:
/// silence is never a decision, and a broken answer never moves a row toward a calm colour.
/// </summary>
public sealed class TurnVerdictDto
{
    /// <summary>This verdict's own identity, so a client can say which one it is showing and an
    /// option activation can be bound to the answer it came from.</summary>
    public string VerdictId { get; set; } = "";

    /// <summary>When the judge answered (UTC).</summary>
    public DateTime JudgedAtUtc { get; set; }

    /// <summary>When the detector OBSERVED the turn end (UTC). This is the join key between a verdict
    /// row and a turn-log record: (tenant, session id, this time). It is the moment being judged, not
    /// the moment of judging, so it stays the same however long the judge took.</summary>
    public DateTime TurnEndObservedAtUtc { get; set; }

    /// <summary>The full-grid fingerprint of the screen this verdict was formed on. A repaint changes
    /// it, which is what makes a stale option tap refusable.</summary>
    public string ScreenHash { get; set; } = "";

    /// <summary>Which judge answered.</summary>
    public string Model { get; set; } = "";

    /// <summary>The contract version that produced and validated this answer.</summary>
    public string ContractVersion { get; set; } = "";

    /// <summary>"agent-reply" or "terminal-failure".</summary>
    public string PackageKind { get; set; } = "";

    /// <summary>True when the answer was refused. The reason is in <see cref="FailureReason"/> and
    /// every field below is at its default.</summary>
    public bool Failed { get; set; }

    /// <summary>Why the answer was refused, in plain words, when <see cref="Failed"/> is true.</summary>
    public string? FailureReason { get; set; }

    /// <summary>One of the six verdict words. Empty when <see cref="Failed"/>.</summary>
    public string Verdict { get; set; } = "";

    /// <summary>"high" or "ambiguous". Ambiguous never demotes a red stop to a calm one.</summary>
    public string Confidence { get; set; } = "";

    /// <summary>The agent's decisive sentence, copied character for character from the reply or the
    /// screen. Checked against the source before this record is accepted, so a reader can trust it as
    /// the agent's own words rather than the judge's. Empty only on a "cannot-tell" verdict.</summary>
    public string Evidence { get; set; } = "";

    /// <summary>The one line a row shows: the ask, or the report.</summary>
    public string Label { get; set; } = "";

    /// <summary>One or two sentences for a reader who has not looked at this session for hours.</summary>
    public string Summary { get; set; } = "";

    /// <summary>The agent's own recommendation, quoted or closely paraphrased, when it made one.</summary>
    public string? AgentRecommends { get; set; }

    /// <summary>"reply" (typed words) or "keys" (a selection in a picker).</summary>
    public string AnswerVia { get; set; } = "";

    /// <summary>The picker on the screen, when the answer is a selection. Null otherwise.</summary>
    public TurnVerdictMenuDto? Menu { get; set; }

    /// <summary>The ways of answering. Empty, or two or more - never exactly one, because one option
    /// is not a choice.</summary>
    public List<TurnVerdictOptionDto> Options { get; set; } = new();

    /// <summary>"none", "irreversible", "standing-grant" or "spends-money".</summary>
    public string Risk { get; set; } = "";

    /// <summary>The same content for the ear, about thirty seconds, opening with the session title.
    /// Produced for every owned stop; audio is synthesised only when somebody is listening.</summary>
    public string Spoken { get; set; } = "";
}

/// <summary>The picker on the screen that a "keys" answer selects from.</summary>
public sealed class TurnVerdictMenuDto
{
    /// <summary>The choice being asked, in plain words.</summary>
    public string Question { get; set; } = "";

    /// <summary>"single" or "multiple".</summary>
    public string SelectionMode { get; set; } = "single";

    /// <summary>What completes the selection: empty when selecting is enough, or a carriage return
    /// when the picker needs one. A "multiple" menu always needs one - a checklist with no way to
    /// submit it is unanswerable.</summary>
    public string Submit { get; set; } = "";
}

/// <summary>One way of answering the stop.</summary>
public sealed class TurnVerdictOptionDto
{
    /// <summary>The short label, naming the ACTION being decided rather than the mechanism.</summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// ONLY the bytes that CHOOSE this option, and never a carriage return or a line feed.
    ///
    /// For a reply option the activation route sends these bytes and appends exactly one Enter. For a
    /// keys option it sends the selected options' bytes in the order given and THEN the menu's submit,
    /// under one screen lock - so the confirm lives in <see cref="TurnVerdictMenuDto.Submit"/> and never
    /// inside a send.
    ///
    /// This record said the opposite until the contract was amended: that a keys option carried its own
    /// carriage return and the route appended none. Three parts of the product each described a
    /// different rule, and the result was a multiple-select nobody could answer - the route took one
    /// option, re-checked the screen, and refused the second toggle by its own lock. A reader building
    /// against the old sentence would build exactly the action the validator now rejects.
    /// </summary>
    public string Send { get; set; } = "";

    /// <summary>True on at most one option.</summary>
    public bool Recommended { get; set; }

    /// <summary>The consequence and the risk of choosing this one.</summary>
    public string Note { get; set; } = "";
}
