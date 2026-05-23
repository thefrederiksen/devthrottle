namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Output of the Wingman's per-turn summariser  (Phase 2 of the SessionWingman goal).
///
/// One of these is produced for each completed turn  (Stop hook fires) by a
/// short Haiku side-call.  It feeds two surfaces:
///
/// 1. The Agent View tab in the session-view UI - users read the headline +
///    decisions instead of the verbose raw transcript.
/// 2. The Voice mode TTS pipeline - the dedicated <see cref="SpokenText"/>
///    field is what gets read aloud, not the raw reply.  Designed for the ear.
///
/// All fields default to safe empties so even when the Wingman call fails
/// or returns junk JSON, callers can ToString / serialise without nulls.
/// </summary>
public sealed class TurnSummary
{
    /// <summary>UTC timestamp this summary was produced.</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>UTC timestamp the turn itself started.</summary>
    public DateTime TurnStartedAt { get; set; }

    /// <summary>One short sentence describing what the agent did this turn.</summary>
    public string Headline { get; set; } = "";

    /// <summary>Distinct file paths touched this turn (max 5).</summary>
    public List<string> FilesTouched { get; set; } = new();

    /// <summary>Distinct shell commands run this turn (max 3).</summary>
    public List<string> CommandsRun { get; set; } = new();

    /// <summary>Key decisions / findings the agent surfaced (max 3).</summary>
    public List<string> Decisions { get; set; } = new();

    /// <summary>One of: "no" | "question" | "error" | "permission" | "idle".</summary>
    public string NeedsUser { get; set; } = "no";

    /// <summary>Short sentence elaborating <see cref="NeedsUser"/> when not "no".</summary>
    public string NeedsUserDetail { get; set; } = "";

    /// <summary>
    /// Phase 4e: one CRISP sentence (under 200 chars) the wingman uses as the
    /// Session View's prominent prompt and the Director's <see cref="Sessions.Session.LastStatusReason"/>
    /// when a session turns red. Distinct from <see cref="NeedsUserDetail"/>: the
    /// detail can be a paragraph; this must fit a single visual row. Empty when
    /// <see cref="NeedsUser"/> is "no" or the Wingman failed to produce one.
    /// </summary>
    public string NeedsUserShort { get; set; } = "";

    /// <summary>
    /// The TTS-ready text for hands-free voice playback.  One to three short
    /// sentences, max ~280 chars, no code, no symbols, no file paths.
    /// Reads the FINDING / OUTCOME, not the process.
    /// </summary>
    public string SpokenText { get; set; } = "";

    /// <summary>"ok" | "wingman_failed" | "parse_failed".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Free-text error detail when Status != "ok".</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Response shape of  GET /sessions/{sid}/turn-summaries  -  the cached list of
/// summaries for one session, ordered oldest -> newest.
/// </summary>
public sealed class TurnSummariesResponse
{
    public string SessionId { get; set; } = "";
    public List<TurnSummary> Summaries { get; set; } = new();
}
