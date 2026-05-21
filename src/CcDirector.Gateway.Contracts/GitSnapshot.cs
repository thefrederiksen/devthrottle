namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Phase 6: a quick snapshot of the git state of a session's repo.
/// Produced by the Supervisor (or a direct `git` invocation) after each turn.
/// Surfaced in the Agent View and feeds the status colour when "idle + dirty".
/// </summary>
public sealed class GitSnapshot
{
    /// <summary>Current branch.  Empty if not a git repo or git unavailable.</summary>
    public string Branch { get; set; } = "";

    /// <summary>True when there are uncommitted changes (working tree or index).</summary>
    public bool Dirty { get; set; }

    /// <summary>Number of commits ahead of upstream.  0 when unknown.</summary>
    public int Ahead { get; set; }

    /// <summary>Number of commits behind upstream.  0 when unknown.</summary>
    public int Behind { get; set; }

    /// <summary>The last commit's short SHA + subject  (e.g. "a1b2c3d feat: thing").  Empty if unknown.</summary>
    public string LastCommit { get; set; } = "";

    /// <summary>"ok" | "not_a_repo" | "git_failed".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Free-text error detail when Status != "ok".</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Phase 7: a markdown-ish blob the user can paste into a new session after
/// the previous one crashed, capturing what was happening and where to pick up.
/// </summary>
public sealed class RecoveryPrompt
{
    public string SessionId { get; set; } = "";
    public string MarkdownBlob { get; set; } = "";

    /// <summary>"ok" | "no_data" | "generated_with_warnings".</summary>
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }
}
