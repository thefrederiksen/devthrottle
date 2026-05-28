namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Response from <c>POST /sessions/{sid}/wingman/ask</c>. One strong-model round-trip;
/// no conversation memory. The <see cref="ContextDigest"/> is a short human-readable
/// string describing what session state was piped into the prompt, so the UI can show
/// the user what the wingman "saw".
/// </summary>
public sealed class WingmanAskResult
{
    /// <summary>Plain-text answer from the wingman's strong model. Trimmed.</summary>
    public string Answer { get; set; } = "";

    /// <summary>Model used for the answer, e.g. "opus". Empty when wingman not configured.</summary>
    public string Model { get; set; } = "";

    /// <summary>Round-trip latency for the wingman call (ms).</summary>
    public long LatencyMs { get; set; }

    /// <summary>
    /// One-line summary of what context was piped to the wingman, e.g.
    /// "events:12, turns:5, buffer:3.8KB, repo:cc-director". Lets the UI explain
    /// to the user WHY the wingman's answer is what it is.
    /// </summary>
    public string ContextDigest { get; set; } = "";

    /// <summary>"ok" | "wingman_failed" | "no_claude" | "bad_request".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Free-text error detail when Status != "ok".</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Tap-to-answer options the wingman suggested for the decision the agent is waiting on,
    /// e.g. ["Yes, go ahead", "No, stop"]. Empty when there is no clear choice to make.
    /// Populated in explain mode; the model chooses these, they are not parsed from prose.
    /// Each entry is the literal text sent back to the session when tapped.
    /// </summary>
    public List<string> QuickReplies { get; set; } = new();

    /// <summary>
    /// One short line summarising the situation at a glance, populated in explain mode.
    /// Drives the per-session headline above the on-screen briefing. Empty for free-text
    /// ask responses.
    /// </summary>
    public string Headline { get; set; } = "";

    /// <summary>
    /// On-screen "what's happened" section, populated in explain mode. The QUICK version -
    /// one short sentence the user can read at a glance when returning to the session.
    /// May contain a single markdown table when the agent presented tabular content the
    /// user must see. Empty for free-text ask responses.
    /// </summary>
    public string WhatHappened { get; set; } = "";

    /// <summary>
    /// On-screen "what's happened" section, LONGER version (1-2 short paragraphs) with the
    /// extra detail the quick line cannot carry: which files were touched, key decisions,
    /// what the agent looked at. Populated in explain mode. Empty for free-text ask
    /// responses. Lets the Wingman tab show the quick line at the top and expand into the
    /// fuller story below for users who want more context.
    /// </summary>
    public string LongDescription { get; set; } = "";

    /// <summary>
    /// On-screen "what Claude wants" section, populated in explain mode. Verbatim agent
    /// phrasing for the pending question when state is red, anchored to the badge color
    /// otherwise. Empty for free-text ask responses.
    /// </summary>
    public string WhatClaudeWants { get; set; } = "";

    /// <summary>
    /// Spoken version of the briefing - smooth prose, no markdown, optimised for TTS.
    /// Populated in explain mode and read on demand by the phone's voice mode when the
    /// user opens a session; we do NOT pre-render TTS audio. Empty for free-text ask
    /// responses.
    /// </summary>
    public string Say { get; set; } = "";
}
