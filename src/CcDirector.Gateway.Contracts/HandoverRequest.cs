namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /handover (on both Director and Gateway).
///
/// Atomic move: read the source session's context, deliver it to a target
/// (either a brand-new session or an existing one), and optionally archive
/// a markdown copy to the vault. Returns the target session DTO.
/// </summary>
public sealed class HandoverRequest
{
    /// <summary>Source session id (required).</summary>
    public string FromSessionId { get; set; } = "";

    /// <summary>
    /// Existing target session id. Mutually exclusive with ToRepoPath.
    /// When set, the context is pushed into this session as a new prompt.
    /// </summary>
    public string? ToSessionId { get; set; }

    /// <summary>
    /// Repository path for a brand-new target session. Mutually exclusive with
    /// ToSessionId. When set, the agent at ToAgent is launched in this repo with
    /// the handover context as its first prompt.
    /// </summary>
    public string? ToRepoPath { get; set; }

    /// <summary>
    /// Which Director to spawn the new session on (Gateway-side only).
    /// If null, the Gateway picks the Director with the fewest active sessions.
    /// Ignored on the Director-side endpoint.
    /// </summary>
    public string? ToDirectorId { get; set; }

    /// <summary>Agent kind for the new session if ToRepoPath is used. Default: ClaudeCode.</summary>
    public string ToAgent { get; set; } = "ClaudeCode";

    /// <summary>
    /// Free-text the caller wants appended to the auto-generated context. Use for
    /// "and please continue by doing X" instructions that the source session
    /// didn't articulate.
    /// </summary>
    public string? ExtraContext { get; set; }

    /// <summary>
    /// When true, also write a markdown copy of the handover to the vault
    /// (%LOCALAPPDATA%\cc-director\vault\handovers\YYYYMMDD_HHMM_*.md), matching
    /// the format produced by the /handover skill. Default: true.
    /// </summary>
    public bool ArchiveToVault { get; set; } = true;
}

/// <summary>
/// POST /handover response.
/// </summary>
public sealed class HandoverResponse
{
    /// <summary>True when the handover prompt was delivered.</summary>
    public bool Accepted { get; set; }

    /// <summary>The target session (newly created or pre-existing) that received the prompt.</summary>
    public SessionDto? TargetSession { get; set; }

    /// <summary>The prose context that was sent as the prompt.</summary>
    public string ContextSent { get; set; } = "";

    /// <summary>Path of the vault-archived markdown if ArchiveToVault was true.</summary>
    public string? ArchivedAt { get; set; }

    /// <summary>Error message if Accepted == false.</summary>
    public string? Error { get; set; }
}
