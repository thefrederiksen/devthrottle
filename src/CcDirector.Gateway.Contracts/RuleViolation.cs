namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One detected violation of a CLAUDE.md rule by the main agent's recent activity.
/// Produced by the Wingman's CheckRulesAsync call (Phase 5).
/// Surfaced in the Agent View as a warning chip and feeds the status colour.
/// </summary>
public sealed class RuleViolation
{
    /// <summary>The rule text the agent violated, verbatim from CLAUDE.md.</summary>
    public string Rule { get; set; } = "";

    /// <summary>One sentence describing what the agent did that violated it.</summary>
    public string What { get; set; } = "";

    /// <summary>Severity hint: "info" | "warn" | "block" (driven by the model).</summary>
    public string Severity { get; set; } = "warn";

    /// <summary>Which CLAUDE.md the rule came from (absolute path).</summary>
    public string? Source { get; set; }
}

/// <summary>GET /sessions/{sid}/rule-violations response.</summary>
public sealed class RuleViolationsResponse
{
    public string SessionId { get; set; } = "";
    public List<RuleViolation> Violations { get; set; } = new();

    /// <summary>"ok" | "no_rules" | "no_summary" | "wingman_failed" | "parse_failed".</summary>
    public string Status { get; set; } = "ok";

    public string? Error { get; set; }
}
