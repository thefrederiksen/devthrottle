using CcDirector.Gateway.Rules;

namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One standing instruction the account gave, as stored (<c>session_rules</c>). A rule is an INSTRUCTION,
/// not a form (owner ruling 14): <see cref="Instruction"/> is the sentence the account actually said and
/// it is the authority. Everything else on this row was DERIVED from that sentence by a model and is
/// there to make the rule cheap and reviewable - never to replace it.
///
/// There is no code column here and there cannot be one (owner ruling 15). <see cref="Calls"/> holds
/// typed calls to the verified checks the Gateway ships - a name plus argument values - and every one of
/// them is validated against the real signature by <see cref="RuleCallValidator"/> before this row can be
/// written. A migration able to store a code string would be a mistake even if nothing wrote one.
///
/// <c>tenant_id</c>, the global query filter and the Gateway-minted primary key are inherited from
/// <see cref="GatewayMintedKeyEntity"/>, so one account never reads another's rules and no caller can
/// present a rule id - there is nothing to squat and nothing to disclose.
/// </summary>
public sealed class SessionRuleEntity : GatewayMintedKeyEntity
{
    /// <summary>THE AUTHORITY: the sentence the account said, in their own words, unaltered.</summary>
    public string Instruction { get; set; } = "";

    /// <summary>What the rule watches for, in plain English, as the model understood the instruction.
    /// A description the account reads - never a matching expression, and never editable as one.</summary>
    public string ScreenDescription { get; set; } = "";

    /// <summary>The cheap words that keep the rule from costing anything: unless one of these is on the
    /// screen, nothing further happens. Derived by the model, never chosen by the account.</summary>
    public List<string> TriggerWords { get; set; } = new();

    /// <summary>The verified checks this rule runs, as validated calls. Never code.</summary>
    public List<RulePrimitiveCall> Calls { get; set; } = new();

    /// <summary>Scope: only sessions running this agent, when set. Null means any agent.</summary>
    public string? ScopeAgent { get; set; }

    /// <summary>Scope: only sessions in this repository, when set. Null means any repository.</summary>
    public string? ScopeRepository { get; set; }

    /// <summary>Scope: only sessions on this machine, when set. Null means any machine.</summary>
    public string? ScopeMachine { get; set; }

    /// <summary>Scope: only sessions on this mission, when set. Null means any mission.</summary>
    public string? ScopeMission { get; set; }

    /// <summary>The ceiling, part one: how long this rule must wait before acting on the same session
    /// again. Required and positive - an agent in a loop is the failure mode with the worst tail.</summary>
    public int CooldownSeconds { get; set; }

    /// <summary>The ceiling, part two: how many times a day this rule may act on one session. Required
    /// and positive, for the same reason.</summary>
    public int DailyCap { get; set; }

    /// <summary>"dry_run" or "live". A plain value, not an enum column, so a third state later costs one
    /// migration and no branching (Architect ruling A1). A rule is ALWAYS created in dry run.</summary>
    public string State { get; set; } = "";

    /// <summary>Who moved this rule out of dry run, as the request pipeline named them. Empty while the
    /// rule is in dry run. A live rule that could not say who made it live would be a rule nobody is
    /// accountable for.</summary>
    public string PromotedBy { get; set; } = "";

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    // ---- Session Rules part 2, phase 1: kinds, where a rule applies, and hook triggers ---------------

    /// <summary>What starts this rule: "screen", "hook" or "session_start" (mission decision D6). Rows
    /// written before kinds existed are "screen".</summary>
    public string Kind { get; set; } = RuleKinds.Screen;

    /// <summary>Where this rule applies: "everywhere", "machine", "repo" or "repo_on_machine" (ruling P4).
    /// Rows written before this existed apply everywhere.</summary>
    public string AppliesToKind { get; set; } = RuleAppliesToKinds.Everywhere;

    /// <summary>The machines this rule applies on, as the Directors report their names.</summary>
    public List<string> AppliesToMachines { get; set; } = new();

    /// <summary>The machines this rule does NOT apply on; never set together with <see cref="AppliesToMachines"/>.</summary>
    public List<string> AppliesToExceptMachines { get; set; } = new();

    /// <summary>The repository this rule applies in, in the one spelling of its origin remote (ruling P6).
    /// Empty when the rule is not tied to a repository.</summary>
    public string AppliesToRepo { get; set; } = "";

    /// <summary>A hook rule's event, e.g. "before_command". Empty for every other kind.</summary>
    public string HookEvent { get; set; } = "";

    /// <summary>A hook rule's tool, e.g. "shell". Empty for every other kind.</summary>
    public string HookTool { get; set; } = "";

    /// <summary>A hook rule's handler, e.g. "match". Empty for every other kind.</summary>
    public string HookHandler { get; set; } = "";

    /// <summary>A Match handler's literal text: the rule matches a command that contains any of these
    /// (ruling P1). Literal text, never an expression and never code.</summary>
    public List<string> HookMatch { get; set; } = new();

    /// <summary>A hook rule's result: "deny" or "allow". Empty for every other kind.</summary>
    public string HookResult { get; set; } = "";
}
