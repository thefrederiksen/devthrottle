namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A single action the Wingman wants to take on a session's terminal, decided by a
/// strong-model side-call (<c>POST /sessions/{sid}/wingman/act</c>). This is the
/// "structured-intent" actuation path: the Wingman subprocess stays TOOL-LESS and only
/// emits this JSON shape; trusted Director code (<c>WingmanActionExecutor</c>) is what
/// actually writes to the PTY. The model never gets a write tool.
///
/// Fail-closed: the default and the safe fallback for any ambiguous or unparseable
/// decision is <see cref="ActNone"/> (do nothing).
/// </summary>
public sealed class WingmanAction
{
    /// <summary>One of: "none" | "type" | "send_keys" | "submit".</summary>
    public string Action { get; set; } = ActNone;

    /// <summary>Text to type (for <see cref="ActType"/>) or type-then-Enter (for <see cref="ActSubmit"/>).</summary>
    public string? Text { get; set; }

    /// <summary>Named keys for <see cref="ActSendKeys"/>, e.g. ["Down","Enter"]. See WingmanActionExecutor.KeyChords.</summary>
    public List<string> Keys { get; init; } = new();

    /// <summary>One short sentence explaining the choice (for the audit log / UI).</summary>
    public string Reason { get; set; } = "";

    /// <summary>"low" | "medium" | "high". Telemetry only in this slice; not a gate.</summary>
    public string Confidence { get; set; } = "low";

    public const string ActNone = "none";
    public const string ActType = "type";
    public const string ActSendKeys = "send_keys";
    public const string ActSubmit = "submit";
}

/// <summary>
/// Outcome of a <c>POST /sessions/{sid}/wingman/act</c> call: what the Wingman decided
/// and whether the Director actually performed it.
/// </summary>
public sealed class WingmanActResult
{
    /// <summary>"ok" | "no_claude" | "session_gone" | "suppressed" | "wingman_failed" | "bad_request".</summary>
    public string Status { get; set; } = StatusOk;

    /// <summary>True only when bytes were actually written to the session.</summary>
    public bool Performed { get; set; }

    /// <summary>The chosen action ("none" when nothing was done).</summary>
    public string Action { get; set; } = WingmanAction.ActNone;

    /// <summary>Echo of the typed/submitted text, if any.</summary>
    public string? Text { get; set; }

    /// <summary>Echo of the keys sent, if any.</summary>
    public List<string> Keys { get; init; } = new();

    /// <summary>The Wingman's stated reason for the decision.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The strong model the decision ran on.</summary>
    public string Model { get; set; } = "";

    /// <summary>Round-trip latency for the decision call.</summary>
    public long LatencyMs { get; set; }

    /// <summary>Free-text error detail when <see cref="Status"/> != "ok".</summary>
    public string? Error { get; set; }

    public const string StatusOk = "ok";
    public const string StatusNoClaude = "no_claude";
    public const string StatusSessionGone = "session_gone";
    public const string StatusSuppressed = "suppressed";
    public const string StatusWingmanFailed = "wingman_failed";
    public const string StatusBadRequest = "bad_request";
}

/// <summary>
/// One row in the per-session Wingman actuation audit trail, surfaced via
/// <c>GET /sessions/{sid}/wingman</c> alongside the colour-change events.
/// </summary>
public sealed class WingmanActionDto
{
    public DateTime At { get; set; }
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Reason { get; set; } = "";
}
