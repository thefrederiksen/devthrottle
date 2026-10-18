namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One firing of one rule, as stored (<c>session_rule_firings</c>). THE RECORD IS THE PRODUCT (owner
/// ruling 14): rules that act while the account is asleep are only worth having if the morning says
/// exactly what happened. So a firing keeps the whole chain - what was on the screen, what the agent
/// understood, what it decided and why, which verified checks ran with what arguments and what they
/// answered, what was typed, and what happened next.
///
/// A dry-run firing is a real firing: it records what WOULD have been done and types nothing, which is
/// what makes dry run reviewable rather than invisible.
/// </summary>
public sealed class SessionRuleFiringEntity : GatewayMintedKeyEntity
{
    /// <summary>The rule that fired.</summary>
    public Guid RuleId { get; set; }

    /// <summary>The session it fired on.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>When it fired (UTC).</summary>
    public DateTime OccurredUtc { get; set; }

    /// <summary>What was on the terminal screen - the only input a rule reads (owner ruling 11).</summary>
    public string ScreenText { get; set; } = "";

    /// <summary>What the agent understood the screen to be, in its own words.</summary>
    public string Understanding { get; set; } = "";

    /// <summary>What it decided - including a decision to DECLINE, which is a first-class outcome.</summary>
    public string Decision { get; set; } = "";

    /// <summary>Why it decided that. A decline with no reason is not a record of anything.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Which verified checks ran, with what arguments, and what each answered.</summary>
    public List<RulePrimitiveRunEntity> PrimitiveRuns { get; set; } = new();

    /// <summary>What was typed into the session. EMPTY for a dry-run firing, always: a rule in dry run
    /// types nothing, and the store refuses to write a firing that claims otherwise.</summary>
    public string TypedText { get; set; } = "";

    /// <summary>What happened next, once the rule had acted or declined.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>What checking the stated reason against this screen found (Architect ruling A12). NEVER
    /// blank: a firing that says "nothing was quoted, so there was nothing to check" is a different fact
    /// from a firing where the check never ran, and a record that cannot tell those apart is not evidence
    /// of either.</summary>
    public string Grounding { get; set; } = "";

    // ---- Session Rules part 2, phase 1: hook firings (wire contract W5) ------------------------------

    /// <summary>"screen" or "hook". Rows written before hook firings existed are "screen".</summary>
    public string Kind { get; set; } = Rules.RuleKinds.Screen;

    /// <summary>The hook's id for the decision, unique per account, so a retried post is recognised as the
    /// same decision rather than written twice. Null for a screen firing.</summary>
    public string? DecisionId { get; set; }

    /// <summary>The hook event, e.g. "before_command". Empty for a screen firing.</summary>
    public string HookEvent { get; set; } = "";

    /// <summary>The tool, e.g. "shell". Empty for a screen firing.</summary>
    public string HookTool { get; set; } = "";

    /// <summary>The command text the rule matched, at most 4000 characters. Empty for a screen firing.</summary>
    public string Subject { get; set; } = "";

    /// <summary>The machine the hook ran on. Empty for a screen firing.</summary>
    public string Machine { get; set; } = "";

    /// <summary>The session's repository identity when the hook ran, possibly empty.</summary>
    public string Repo { get; set; } = "";

    /// <summary>The agent that ran the hook, e.g. "claude_code".</summary>
    public string Agent { get; set; } = "";

    /// <summary>The agent's own name for the event, e.g. "PreToolUse".</summary>
    public string AgentEvent { get; set; } = "";

    /// <summary>The agent's own name for the tool, e.g. "Bash".</summary>
    public string AgentTool { get; set; } = "";

    /// <summary>The less specific matching rules the deciding rule overrode (ruling P3).</summary>
    public List<Guid> OverriddenRuleIds { get; set; } = new();

    /// <summary>Every rule that matched the command, the deciding one included.</summary>
    public List<Guid> MatchedRuleIds { get; set; } = new();

    /// <summary>How long the hook took to decide, in microseconds.</summary>
    public long EvaluationMicroseconds { get; set; }
}

/// <summary>One verified check that ran during a firing: which one, with what arguments, and what it
/// answered. Stored as part of the firing so an action nobody can reconstruct never exists.</summary>
public sealed class RulePrimitiveRunEntity
{
    /// <summary>The check's wire name, e.g. "is_path_inside".</summary>
    public string Name { get; set; } = "";

    /// <summary>The arguments as they were supplied, rendered for reading.</summary>
    public string Arguments { get; set; } = "";

    /// <summary>What the check answered, rendered for reading.</summary>
    public string Answer { get; set; } = "";
}
