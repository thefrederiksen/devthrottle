namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /sessions/{sid}/brief response - the data behind the Cockpit's full-page session
/// Brief (docs/plans/cockpit-brief-view.md). Three blocks, sourced from the Claude JSONL
/// transcript (never the terminal screen): what the user asked, what the agent did
/// (condensed), and what the agent needs from the user (verbatim).
/// </summary>
public sealed class BriefResponse
{
    public string SessionId { get; set; } = "";

    /// <summary>"ok" | "no_session_id" | "no_jsonl" | "parse_error".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Free-text error message if Status != "ok".</summary>
    public string? Error { get; set; }

    /// <summary>Session activity state at response time (Working / WaitingForInput / Idle...).</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>Widget count in the transcript; the staleness key for the condensation cache.</summary>
    public int TurnCount { get; set; }

    /// <summary>When the Director session was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The session's goal: the FIRST user prompt in the transcript (truncated for display).
    /// Labeled "earliest available" semantics - a compacted/forked transcript starts mid-way.
    /// </summary>
    public string? Goal { get; set; }

    /// <summary>The most recent user prompt (the "YOU ASKED" block), truncated for display.</summary>
    public string? LastAsk { get; set; }

    /// <summary>
    /// True when the transcript has no assistant reply after the last user prompt: the agent
    /// is still replying, or is blocked in an INTERACTIVE on-screen prompt (option picker /
    /// permission dialog) that the transcript cannot see until it completes. Clients send the
    /// user to the Terminal tab when this is true while the session waits for input.
    /// </summary>
    public bool ReplyPending { get; set; }

    /// <summary>Condensed "CLAUDE DID" bullets for the latest reply. Empty when the condenser
    /// was unavailable - clients then show <see cref="FullReply"/> directly.</summary>
    public List<string> DidBullets { get; set; } = new();

    /// <summary>
    /// The "NEEDS YOU" text - ALWAYS a verbatim substring of <see cref="FullReply"/>
    /// (model-extracted and substring-validated server-side, or the reply's final paragraph
    /// as fallback). Null when the reply asks nothing of the user.
    /// </summary>
    public string? NeedsYou { get; set; }

    /// <summary>"model" (extracted + validated) | "fallback" (final paragraph) | null.</summary>
    public string? NeedsYouSource { get; set; }

    /// <summary>The agent's latest full reply, verbatim markdown (the [full reply] expander).</summary>
    public string? FullReply { get; set; }

    /// <summary>Condenser identity ("openai:gpt-4.1-mini") or "unavailable" (no API key /
    /// call failed) - an explicit degrade signal, never silent.</summary>
    public string Condenser { get; set; } = "unavailable";

    /// <summary>When the condensation was generated (UTC); null when Condenser is unavailable.</summary>
    public DateTime? GeneratedAt { get; set; }
}
