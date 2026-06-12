namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Describes a single agent session (Claude/Pi/Codex/Gemini) running inside a Director.
/// Returned by /sessions on both the Director Control API and the Gateway.
/// </summary>
public sealed class SessionDto
{
    /// <summary>CC Director's internal session GUID. Stable for the life of the session.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Which Director owns this session. Empty in Director-local responses.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Agent CLI kind: ClaudeCode, Pi, Codex, Gemini, OpenCode, RawCli.</summary>
    public string Agent { get; set; } = "";

    /// <summary>The session's declared purpose (issue #211): Implement / Discuss /
    /// BugReport / IssueSubmitter / QA. Identity, not status - set at creation, immutable.
    /// Same axis as <see cref="Agent"/> (orthogonal: which agent vs why the session exists).
    /// Empty/missing means Implement (pre-#211 Directors).</summary>
    public string Type { get; set; } = "";

    /// <summary>Group identity (issue #225) when this session belongs to a group; null for
    /// solo sessions. Lets a by-repo / fleet view keep group members adjacent.</summary>
    public string? GroupId { get; set; }

    /// <summary>The session's role within its group (issue #225); null for solo sessions.</summary>
    public string? GroupRole { get; set; }

    /// <summary>Repository / working directory.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>Process lifecycle status: Starting / Running / Exiting / Exited / Failed.</summary>
    public string Status { get; set; } = "";

    /// <summary>Cognitive activity state: Starting / Idle / Working / WaitingForInput / WaitingForPerm / Exited.
    /// This is the RAW state (issue #186 two-owner model): owned by the Director's dumb
    /// quiet-timer detector, purely mechanical, written by nothing else.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>
    /// The ASSESSED state (issue #186 two-owner model): owned by the GATEWAY, whose brain
    /// reads the transcript after a turn end and can refute the mechanical quiet signal
    /// (e.g. quiet but nothing needs you -> "Idle"). Null when no assessment stands - new
    /// PTY bytes auto-invalidate it. UIs display AssessedState ?? ActivityState. On a
    /// Director's own API this carries the display annotation the Gateway pushed down;
    /// it is NEVER fed into the detector and never re-pushed.
    /// </summary>
    public string? AssessedState { get; set; }

    /// <summary>UTC timestamp the session was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Total bytes the terminal buffer has accumulated since session start. Use as a cursor in /buffer?since=. </summary>
    public long TotalBufferBytes { get; set; }

    /// <summary>Optional friendly name for the session.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The session's position in its owning Director's list, mirroring
    /// <c>Session.SortOrder</c>. This is the user-controlled desktop order
    /// (set by drag-drop on the desktop and persisted), so a client that sorts
    /// by it keeps each session in a stable slot instead of reshuffling. 0 when
    /// the owning Director predates this field. Populated by the Director.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Aggregate at-a-glance status color, written by the Director's
    /// SessionStatusWingman. The UI renders this verbatim and never derives it
    /// from other fields.
    /// Values: "blue" (agent is working), "red" (needs the user - input/permission/idle),
    /// "yellow" (the Wingman is reading the screen and narrating), "purple" (parked on its
    /// own background task, will resume itself), "unknown" (process exited, or source
    /// unreachable/unparseable - rendered gray). On-hold is separate: see <see cref="OnHold"/>.
    /// ("green" is legacy and no longer emitted.)
    /// </summary>
    public string StatusColor { get; set; } = "unknown";

    /// <summary>
    /// Short human-readable reason for the current <see cref="StatusColor"/>
    /// (e.g. "session created", "working", "waiting for input"). Surfaced as a
    /// tooltip on the dot so the user can see WHY the color is what it is.
    /// </summary>
    public string LastStatusReason { get; set; } = "";

    /// <summary>Wingman briefing-pipeline state: "None" | "Briefing" | "Briefed" | "Failed".
    /// Orthogonal to ActivityState (a session can ask AND keep working). "Briefing" renders
    /// the rail's yellow "wingman reading..." chip. Defaults to None on old Directors.</summary>
    public string BriefingState { get; set; } = "None";

    /// <summary>The latest turn brief's needs-you one-liner (&lt;=8 words) for the rail /
    /// FIFO / voice. Null when nothing is needed or no brief exists.</summary>
    public string? RailLine { get; set; }

    /// <summary>Backend type: ConPty / UnixPty / Pipe / Studio / Embedded.</summary>
    public string BackendType { get; set; } = "";

    /// <summary>
    /// The session's agent-driver capability names (e.g. "Cancel", "Interrupt",
    /// "ClearContext", "History", "TranscriptRead"). UIs render action buttons from
    /// this list verbatim - a verb a tool lacks is simply absent, never guessed.
    /// Empty on Directors that predate the driver layer.
    /// </summary>
    public List<string> DriverCapabilities { get; set; } = new();

    /// <summary>
    /// UTC timestamp of the most recent terminal-buffer write. Falls back to
    /// <see cref="CreatedAt"/> if the session has produced no output yet.
    /// Drives the "Idle Xm" freshness column in the Gateway directory view.
    /// </summary>
    public DateTime? LastActivityAt { get; set; }

    /// <summary>
    /// Seconds since the most recent terminal-buffer write -- the raw "time since the last
    /// ConPTY character" idle clock, computed server-side so external callers (e.g. the test
    /// harness) need no clock math. 0 when the session has produced no output yet.
    /// </summary>
    public double IdleSeconds { get; set; }

    /// <summary>
    /// The terminal-state detector's quiet threshold in seconds: how long the terminal must be
    /// COMPLETELY byte-silent before the turn is even a candidate for being over. Paired with
    /// <see cref="IdleSeconds"/> so a test can assert against the live threshold directly.
    /// </summary>
    public double QuietThresholdSeconds { get; set; }

    /// <summary>
    /// Machine name of the Director that owns this session. Populated by the
    /// Gateway aggregator. Empty in Director-local responses.
    /// </summary>
    public string MachineName { get; set; } = "";

    /// <summary>
    /// User the owning Director is running as. Populated by the Gateway aggregator.
    /// Empty in Director-local responses.
    /// </summary>
    public string User { get; set; } = "";

    /// <summary>
    /// Tailnet-reachable base URL of the owning Director (no trailing slash).
    /// Clients use this to talk directly to the session (prompts, renames, etc.)
    /// without going through the Gateway. Populated by the Gateway aggregator.
    /// Empty in Director-local responses.
    /// </summary>
    public string TailnetEndpoint { get; set; } = "";

    /// <summary>
    /// Full URL of this session's view page on its owning Director.
    /// Format: <c>{TailnetEndpoint}/sessions/{SessionId}/view</c>.
    /// Populated by the Gateway aggregator. Empty in Director-local responses.
    /// </summary>
    public string ViewUrl { get; set; } = "";

    /// <summary>
    /// True when this session is currently in walkie-talkie voice mode (a client is
    /// driving it by voice). The authoritative flag every client reads so the desktop
    /// tile, the HTML view, and the Android roster all agree the session is spoken-to
    /// rather than typed-at. Mirrors <c>Session.VoiceMode</c> on the owning Director.
    /// </summary>
    public bool VoiceMode { get; set; }

    /// <summary>
    /// True when the user has parked this session in the FIFO voice queue ("deal with
    /// this later"). A user override orthogonal to <see cref="ActivityState"/> and
    /// <see cref="StatusColor"/>: the underlying state is still reported truthfully, but
    /// the FIFO conductor skips held sessions. Mirrors <c>Session.OnHold</c> on the
    /// owning Director. The UI renders this verbatim and never derives it.
    /// </summary>
    public bool OnHold { get; set; }

    /// <summary>
    /// Whether the Wingman experience is enabled for this session: auto-explain briefing on
    /// turn-end, Voice + Wingman tabs visible, Yellow "Wingman is reading" state available.
    /// Default OFF. When false the session behaves as a plain terminal -- clients hide the
    /// Voice + Wingman tabs and the dot goes straight Blue->Red on turn-end (no Yellow).
    /// Mirrors <c>Session.WingmanEnabled</c> on the owning Director.
    /// </summary>
    public bool WingmanEnabled { get; set; } = false;

    /// <summary>
    /// For GitHub Actions remote sessions: the "owner/repo" the session runs against.
    /// Empty for local sessions. Mirrors the backend's repo slug.
    /// </summary>
    public string RemoteRepo { get; set; } = "";

    /// <summary>
    /// For GitHub Actions remote sessions: web URL of the issue/PR thread driving
    /// the session, or empty until the thread is established / for local sessions.
    /// </summary>
    public string RemoteThreadUrl { get; set; } = "";

    /// <summary>
    /// For GitHub Actions remote sessions: web URL of the most recent workflow run,
    /// or empty when none yet / for local sessions.
    /// </summary>
    public string RemoteRunUrl { get; set; } = "";

    /// <summary>
    /// For GitHub Actions remote sessions: last observed run status
    /// (queued / in_progress / completed / none). Empty for local sessions.
    /// </summary>
    public string RemoteRunStatus { get; set; } = "";
}
