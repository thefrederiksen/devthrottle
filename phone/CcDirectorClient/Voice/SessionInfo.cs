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
}
