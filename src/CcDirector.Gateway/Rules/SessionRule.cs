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
    DateTime UpdatedUtc);

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
    string Grounding);

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

/// <summary>
/// What may be done to a rule, as recorded in its change history. A closed set, so the history can be read
/// rather than interpreted. Built from these constants and never typed out again.
/// </summary>
public static class RuleChangeActions
{
    /// <summary>A rule was written. Always in dry run.</summary>
    public const string Create = "create";

    /// <summary>A rule's body was changed. This puts the rule back in dry run.</summary>
    public const string Change = "change";

    /// <summary>A rule was moved out of dry run.</summary>
    public const string Promote = "promote";

    /// <summary>A rule was deleted. Its firing record and its history are kept.</summary>
    public const string Delete = "delete";

    /// <summary>Every action, in the order a rule usually meets them.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Create, Change, Promote, Delete };
}

/// <summary>
/// WHO MADE A CHANGE TO A RULE, as the request pipeline named them (owner decision D7: "Every change is recorded
/// with the session and agent that made it"). Obtained only from the inbound request by
/// <c>SessionRuleEndpoints</c>, never from a value the caller wrote in a body.
/// </summary>
/// <param name="ChangedBy">"session:&lt;id&gt;", "device:&lt;id&gt;", or the signed-in person's name.</param>
/// <param name="SessionId">The calling session, or null when the caller was not a session.</param>
/// <param name="Agent">The agent the calling session runs, or null when the caller was not a session.</param>
public sealed record RuleChangeActor(string ChangedBy, string? SessionId, string? Agent);

/// <summary>One recorded change to one rule, as read back out of the store. <see cref="Before"/> and
/// <see cref="After"/> are JSON text: the rule's body and state, empty on a create and on a delete
/// respectively.</summary>
public sealed record SessionRuleChange(
    Guid Id,
    Guid RuleId,
    string Action,
    string ChangedBy,
    string? SessionId,
    string? Agent,
    DateTime OccurredUtc,
    string Before,
    string After);
