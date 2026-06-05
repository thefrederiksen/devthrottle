namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One wingman turn brief - the strong model's interpretation of a completed turn
/// (docs/architecture/wingman/TURN_BRIEFING.md, contract v2.2). Generated eagerly at turn
/// end by the Director, stored durably, and rendered verbatim by every consumer (Cockpit
/// Brief page, rail, phone FIFO, voice). Consumers NEVER parse or post-process this -
/// interpretation happened once, on the Director, with the best model available (D6).
/// </summary>
public sealed class TurnBriefDto
{
    public string SessionId { get; set; } = "";

    /// <summary>Transcript widget count when this brief was generated - the staleness key.</summary>
    public int TurnNumber { get; set; }

    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>Generator identity ("wingman:opus", "stub", "condenser:gpt-4.1-mini").</summary>
    public string Model { get; set; } = "stub";

    /// <summary>True when this brief came from a degrade tier, not the wingman. The UI labels it.</summary>
    public bool Degraded { get; set; }

    /// <summary>"stub" | "condenser" | null - which degrade tier produced it (when Degraded).</summary>
    public string? DegradeTier { get; set; }

    /// <summary>The current CHAPTER's title (v2.3; introduced as the session headline in
    /// v2.2): &lt;= 6 words naming WHAT the session is working on - never how. Several turns
    /// share one chapter; the wingman may refine the title's wording as the work drifts
    /// WITHOUT starting a new chapter. Empty on pre-v2.2 briefs and stub briefs with no
    /// prior title to carry; consumers fall back to Intent.</summary>
    public string Headline { get; set; } = "";

    /// <summary>True when THIS turn started a new chapter (v2.3): the session moved to a
    /// genuinely different piece of work. The wingman signals this explicitly - consumers
    /// group briefs into chapter cards on this flag, never by comparing headline strings
    /// (a refined title is the same chapter). Always false on degrade-tier briefs.</summary>
    public bool NewChapter { get; set; }

    /// <summary>One-line title of THIS turn (v2.2): &lt;= 8 words, past tense, the turn-card
    /// header. Empty on pre-v2.2 briefs; consumers fall back to the first Did bullet.</summary>
    public string TurnTitle { get; set; } = "";

    /// <summary>Rolling intent: what the user is trying to get done, carried and updated
    /// across turns - never the literal last message.</summary>
    public string Intent { get; set; } = "";

    /// <summary>What the agent concretely did/decided this turn. Proportional: small turn, few bullets.</summary>
    public List<string> Did { get; set; } = new();

    /// <summary>Null when the turn needs nothing from the user.</summary>
    public TurnBriefNeedsYou? NeedsYou { get; set; }
}

/// <summary>The needs-you block of a turn brief. See TURN_BRIEFING.md section 4.</summary>
public sealed class TurnBriefNeedsYou
{
    /// <summary>Synthesized, crisp: leads with whether anything is broken/blocking, then the action(s).</summary>
    public string Statement { get; set; } = "";

    /// <summary>"reply" (typed message) | "keys" (on-screen menu answered via raw key sends).</summary>
    public string AnswerVia { get; set; } = "reply";

    /// <summary>"single" | "multiple". Multiple = pick-any-that-apply checklist: option sends
    /// TOGGLE, and <see cref="Submit"/> completes the answer.</summary>
    public string SelectionMode { get; set; } = "single";

    /// <summary>The completing send for SelectionMode "multiple" (e.g. "\r"); null for single.</summary>
    public string? Submit { get; set; }

    /// <summary>Real choices the wingman decided exist. May be empty (composer always works).</summary>
    public List<TurnBriefOption> Options { get; set; } = new();

    /// <summary>VERBATIM quote(s) from the reply or the screen - validated server-side.
    /// Never a paraphrase; empty string when validation failed (UI hides the receipts).</summary>
    public string Evidence { get; set; } = "";

    /// <summary>"blocking" | "review" | "fyi". fyi does NOT turn the rail red.</summary>
    public string Urgency { get; set; } = "review";

    /// <summary>"high" | "ambiguous". Ambiguous statements say so honestly.</summary>
    public string Confidence { get; set; } = "high";

    /// <summary>&lt;= 8 words for the rail / FIFO card / voice.</summary>
    public string RailLine { get; set; } = "";
}

/// <summary>One answer option: the visible key/label and the exact send that answers it.</summary>
public sealed class TurnBriefOption
{
    /// <summary>Short visible label ("1 Terse", "Looks good - commit it").</summary>
    public string Key { get; set; } = "";

    /// <summary>What one tap transmits: reply text, or a raw key sequence for answerVia "keys".</summary>
    public string Send { get; set; } = "";

    /// <summary>Scope/risk flag (e.g. "standing grant"). Shown next to the button.</summary>
    public string? Note { get; set; }
}

/// <summary>GET /sessions/{sid}/turnbriefs response.</summary>
public sealed class TurnBriefsResponse
{
    public string SessionId { get; set; } = "";

    /// <summary>"None" | "Briefing" | "Briefed" | "Failed" - the session's briefing-pipeline state.</summary>
    public string BriefingState { get; set; } = "None";

    /// <summary>Stored briefs, newest first.</summary>
    public List<TurnBriefDto> Items { get; set; } = new();
}

/// <summary>POST /sessions/{sid}/turnbriefs/feedback - "this brief is wrong" (D7). The report
/// is stored as a labeled example for prompt iteration.</summary>
public sealed class TurnBriefFeedbackRequest
{
    /// <summary>Which brief (its TurnNumber); 0 = latest.</summary>
    public int TurnNumber { get; set; }

    /// <summary>The user's note on what was wrong.</summary>
    public string Note { get; set; } = "";
}
