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

    /// <summary>Agent CLI kind: ClaudeCode, Pi, Codex, Gemini.</summary>
    public string Agent { get; set; } = "";

    /// <summary>Repository / working directory.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>Process lifecycle status: Starting / Running / Exiting / Exited / Failed.</summary>
    public string Status { get; set; } = "";

    /// <summary>Cognitive activity state: Starting / Idle / Working / WaitingForInput / WaitingForPerm / Exited.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>UTC timestamp the session was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Total bytes the terminal buffer has accumulated since session start. Use as a cursor in /buffer?since=. </summary>
    public long TotalBufferBytes { get; set; }

    /// <summary>Optional friendly name for the session.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Aggregate at-a-glance status color, written by the Director's
    /// SessionStatusWingman. The UI renders this verbatim and never derives it
    /// from other fields.
    /// Values: "green" (greenfield - new or just finished a task cleanly),
    /// "blue" (agent is working), "yellow" (soft warning, idle with uncommitted
    /// work, soft rule violation), "red" (needs the user - input/permission/error),
    /// "unknown" (data-quality state - source unreachable or unparseable).
    /// </summary>
    public string StatusColor { get; set; } = "unknown";

    /// <summary>
    /// Short human-readable reason for the current <see cref="StatusColor"/>
    /// (e.g. "session created", "working", "waiting for input"). Surfaced as a
    /// tooltip on the dot so the user can see WHY the color is what it is.
    /// </summary>
    public string LastStatusReason { get; set; } = "";

    /// <summary>Backend type: ConPty / UnixPty / Pipe / Studio / Embedded.</summary>
    public string BackendType { get; set; } = "";

    /// <summary>
    /// UTC timestamp of the most recent terminal-buffer write. Falls back to
    /// <see cref="CreatedAt"/> if the session has produced no output yet.
    /// Drives the "Idle Xm" freshness column in the Gateway directory view.
    /// </summary>
    public DateTime? LastActivityAt { get; set; }

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
}
