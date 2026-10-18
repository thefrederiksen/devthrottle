namespace CcDirector.Gateway.Rules;

/// <summary>
/// What a rule is allowed to do right now. TWO states ship (Architect ruling A1): a rule is always
/// created in <see cref="DryRun"/>, where it records what it WOULD have done and types nothing, and only
/// a person can move it to <see cref="Live"/>. The "asks me first" middle state is not built - the owner
/// has not decided it, and building an undecided state is guessing.
/// </summary>
public enum RuleState
{
    /// <summary>Reports what it would have done. Types nothing. Every rule starts here.</summary>
    DryRun,

    /// <summary>Carries the instruction out for real.</summary>
    Live,
}

/// <summary>
/// The sessions a rule is allowed to act on - the first real bound (owner ruling 14, bound 1). Each part
/// is a filter: null means "any", a value means "only this one". All four null means every session the
/// account has, which is what the mockups call "All sessions".
/// </summary>
public sealed record RuleScope(string? Agent, string? Repository, string? Machine, string? Mission)
{
    /// <summary>Every session the account has.</summary>
    public static RuleScope AllSessions { get; } = new(null, null, null, null);
}

/// <summary>
/// One standing instruction, as read back out of the store. <see cref="Instruction"/> is the authority -
/// the sentence the account said - and everything else was derived from it. <see cref="PromotedBy"/> is
/// who moved it out of dry run, and it is empty for exactly as long as the rule is in dry run - a live rule
/// can always say which person made it live.
/// </summary>
public sealed record SessionRule(
    Guid Id,
    string Instruction,
    string ScreenDescription,
    IReadOnlyList<string> TriggerWords,
    IReadOnlyList<RulePrimitiveCall> Calls,
    RuleScope Scope,
    int CooldownSeconds,
    int DailyCap,
    RuleState State,
    string PromotedBy,
    DateTime CreatedUtc,
    DateTime UpdatedUtc)
{
    /// <summary>What starts this rule - <see cref="RuleKinds"/>. A rule written before kinds existed is a
    /// screen rule, which is what the default says.</summary>
    public string Kind { get; init; } = RuleKinds.Screen;

    /// <summary>Where this rule applies (ruling P4). Every rule written before phase 1 applies everywhere.</summary>
    public RuleAppliesTo AppliesTo { get; init; } = RuleAppliesTo.Everywhere;

    /// <summary>What a hook rule listens for and says; null for every other kind.</summary>
    public HookRuleTrigger? Hook { get; init; }
}

/// <summary>One verified check that ran during a firing: which one, with what arguments, what it said.</summary>
public sealed record RulePrimitiveRun(string Name, string Arguments, string Answer);

/// <summary>
/// One firing of one rule, as read back out of the store. The record is the product: an action nobody can
/// reconstruct is an action nobody can supervise. <see cref="Grounding"/> is what the check of the stated
/// reason against the screen found (Architect ruling A12) and is never blank - a firing that could not say
/// what that check found would be indistinguishable from one where the check never ran.
/// </summary>
public sealed record SessionRuleFiring(
    Guid Id,
    Guid RuleId,
    string SessionId,
    DateTime OccurredUtc,
    string ScreenText,
    string Understanding,
    string Decision,
    string Reason,
    IReadOnlyList<RulePrimitiveRun> PrimitiveRuns,
    string TypedText,
    string Outcome,
    string Grounding)
{
    /// <summary>The kind of rule that fired - a screen firing or a hook firing.</summary>
    public string Kind { get; init; } = RuleKinds.Screen;

    /// <summary>A hook firing's decision id from the hook, unique per account so a retried post is not a
    /// second firing; null for a screen firing.</summary>
    public string? DecisionId { get; init; }

    /// <summary>A hook firing's event, e.g. before_command. Empty for a screen firing.</summary>
    public string Event { get; init; } = "";

    /// <summary>A hook firing's tool, e.g. shell. Empty for a screen firing.</summary>
    public string Tool { get; init; } = "";

    /// <summary>The command a hook rule matched, cut to 4000 characters. Empty for a screen firing.</summary>
    public string Subject { get; init; } = "";

    /// <summary>The machine the hook ran on. Empty for a screen firing.</summary>
    public string Machine { get; init; } = "";

    /// <summary>The session's repository identity when the hook ran; may be empty. Empty for a screen firing.</summary>
    public string Repo { get; init; } = "";

    /// <summary>The agent that ran the hook, e.g. claude_code.</summary>
    public string Agent { get; init; } = "";

    /// <summary>The agent's own name for the event, e.g. PreToolUse.</summary>
    public string AgentEvent { get; init; } = "";

    /// <summary>The agent's own name for the tool, e.g. Bash.</summary>
    public string AgentTool { get; init; } = "";

    /// <summary>The less specific matching rules this firing's rule overrode (ruling P3).</summary>
    public IReadOnlyList<Guid> OverriddenRuleIds { get; init; } = Array.Empty<Guid>();

    /// <summary>Every rule that matched the command, the deciding one included.</summary>
    public IReadOnlyList<Guid> MatchedRuleIds { get; init; } = Array.Empty<Guid>();

    /// <summary>How long the hook took to decide, in microseconds.</summary>
    public long EvaluationMicroseconds { get; init; }
}

/// <summary>
/// Thrown when the store REFUSES to write something, carrying the reason in plain English. A refusal is
/// always stated: a rule that could not be stored and did not say why is worse than no rule at all.
/// </summary>
public sealed class RuleRejectedException : Exception
{
    public RuleRejectedException(string reason) : base(reason) => Reason = reason;

    /// <summary>Why it was refused, in the words the account reads.</summary>
    public string Reason { get; }
}
