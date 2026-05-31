namespace CcDirectorClient.Voice;

/// <summary>
/// Client-side view of one agent session in the roster, projected from the
/// Gateway's /sessions response (a subset of the server's SessionDto). This is a
/// plain data holder with no MAUI or Android dependency so it can be unit tested
/// off-device and reused by the conductor.
/// </summary>
public sealed class SessionInfo
{
    /// <summary>CC Director's internal session GUID.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Friendly name supplied by the server, or null.</summary>
    public string? Name { get; set; }

    /// <summary>Repository / working directory of the session.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>Cognitive activity state: Idle / Working / WaitingForInput / WaitingForPerm / Exited.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>
    /// Authoritative at-a-glance status color written by the owning Director's
    /// SessionStatusWingman: green / blue / yellow / red / unknown. "red" means
    /// the session needs the user (pending question, error, or permission). The
    /// client renders and filters on this verbatim and never derives it.
    /// </summary>
    public string StatusColor { get; set; } = "unknown";

    /// <summary>Short human reason for the current <see cref="StatusColor"/>.</summary>
    public string LastStatusReason { get; set; } = "";

    /// <summary>
    /// Tailnet-reachable base URL of the owning Director (no trailing slash). The
    /// client talks directly to this for the per-session voice round-trip.
    /// </summary>
    public string TailnetEndpoint { get; set; } = "";

    /// <summary>Machine name of the owning Director (for display).</summary>
    public string MachineName { get; set; } = "";

    /// <summary>
    /// True when this session is currently in walkie-talkie voice mode. Mirrors the
    /// server's authoritative <c>Session.VoiceMode</c> so the roster can show that a
    /// session is being talked to (from this phone or any other client) rather than
    /// typed at. The client renders this verbatim and never derives it.
    /// </summary>
    public bool VoiceMode { get; set; }

    /// <summary>
    /// True when the user has parked this session in the FIFO voice queue ("deal with
    /// this later"). Mirrors the server's authoritative <c>Session.OnHold</c>. A held
    /// session is dropped from the FIFO rotation until it is taken off hold; its
    /// underlying <see cref="StatusColor"/> and <see cref="ActivityState"/> are still
    /// reported truthfully. The client renders and filters on this verbatim.
    /// </summary>
    public bool OnHold { get; set; }

    /// <summary>
    /// Whether the Wingman experience is enabled for this session: auto-explain briefing on
    /// turn-end, Voice + Wingman tabs visible, Yellow "Wingman is reading" state available.
    /// Default OFF. When false the session behaves as a plain terminal -- the phone hides
    /// the Voice + Wingman tabs and the FIFO conveyor skips it (no briefing to deliver).
    /// Mirrors the server's <c>Session.WingmanEnabled</c>.
    /// </summary>
    public bool WingmanEnabled { get; set; } = false;

    /// <summary>
    /// Human-friendly label for the session: the server-supplied name when set,
    /// otherwise the repository folder name, otherwise a short id. Never empty.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name!.Trim();
            var folder = Path.GetFileName(RepoPath.TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(folder)) return folder;
            return string.IsNullOrWhiteSpace(SessionId) ? "session" : SessionId;
        }
    }

    /// <summary>
    /// The repository folder name (the last path segment of <see cref="RepoPath"/>),
    /// e.g. "cc-director" for "D:\ReposFred\cc-director". Empty when no repo path is
    /// known. Used to spell out the repo aloud alongside the session name.
    /// </summary>
    public string RepoName => Path.GetFileName(RepoPath.TrimEnd('\\', '/'));
}
