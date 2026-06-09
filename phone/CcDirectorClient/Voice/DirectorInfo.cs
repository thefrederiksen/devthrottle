namespace CcDirectorClient.Voice;

/// <summary>
/// Client-side view of one Director (machine) in the fleet, projected from the
/// Gateway's GET /directors response (a subset of the server's DirectorDto). Used by
/// the "start a new session" flow to pick which machine to start the session on. A
/// plain data holder with no MAUI or Android dependency so it is unit tested
/// off-device.
/// </summary>
public sealed class DirectorInfo
{
    /// <summary>The Director's stable id (used in the /directors/{id}/... routes).</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Machine name for display (e.g. "soren-north").</summary>
    public string MachineName { get; set; } = "";

    /// <summary>
    /// Tailnet-reachable base URL of the Director (no trailing slash). Stamped onto a
    /// session created on this Director so the phone can then open its terminal/voice.
    /// </summary>
    public string TailnetEndpoint { get; set; } = "";

    /// <summary>When the Gateway last heard from this Director (heartbeat), or null.</summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>The Director's build version, for display.</summary>
    public string Version { get; set; } = "";

    /// <summary>Never-empty label for the picker: machine name, else id, else "director".</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(MachineName)) return MachineName.Trim();
            return string.IsNullOrWhiteSpace(DirectorId) ? "director" : DirectorId;
        }
    }
}
