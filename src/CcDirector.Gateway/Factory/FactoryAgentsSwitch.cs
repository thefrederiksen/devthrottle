using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// Whether the Factory Agents area is on FOR ONE ACCOUNT. The one answer every factory route, the Cockpit's switch
/// question, the roster's factory chip and the scheduler read, so they can never disagree.
///
/// THREE GRANTS, ONE RULE (Factories need Pro mission, 21 Oct 2026: "You need pro to set up factories"). The area is
/// on for an account when ANY of these is true, asked in this order:
///   1. The MACHINE switch (<c>factoryAgents.enabled</c> in this Gateway's config.json) is the operator's decision for
///      the whole Gateway. A self-hosted Gateway serves one owner, so "on for this machine" is "on for that owner".
///      When it is on, every account on this Gateway is on.
///   2. The OWNER switch is a per-account grant recorded in the account's settings under
///      <see cref="TenantSettingKeys.FactoryAgentsSwitch"/>, set only through the administrator route
///      (<c>POST /gateway/admin/factory-agents</c>) with who switched it and why. It is how the owner gives factories to
///      an account that is not paying. It is a local settings read, asked BEFORE the plan, so an account switched on
///      never depends on the entitlement read.
///   3. The PLAN grants <see cref="EntitlementScopes.Factories"/> - Pro, a team seat, the retired hosted tier and the
///      pre-column legacy row. Asked through <see cref="TenantAiGate"/>, the same entitlement read every model call
///      uses (the tenant mapping, <see cref="EntitlementRegistry.Evaluate"/>, the team bill for a team tenant), so there
///      is no second copy of plan policy here. ONLY on a hosted Gateway: a self-hosted Gateway's gate
///      (<see cref="TenantAiGate.SelfHost"/>) answers Allowed for everything, and taking that as a plan grant would
///      switch factories on for every self-hosted Gateway whose machine switch is off.
///
/// An account switched OFF does not override a grant: an off decision is the absence of the owner's grant, not a
/// refusal of the other two. Default: off for everyone - no config block, no recorded decision and no plan is off.
///
/// THREE ANSWERS, NOT TWO. A plan read that FAILS is <see cref="FactoryAgentsState.Unknown"/> - never off (it would tell
/// a paying account to subscribe) and never on (it would hand the area away). A route answers it with a temporary 503;
/// a timer skips this pass and asks again on the next.
///
/// A stored decision that cannot be read is an error, never a quiet "off" - a corrupt row is a defect to find, and
/// answering "off" would hide the area from an account that was switched on with no word of why.
/// </summary>
public sealed class FactoryAgentsSwitch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TenantSettingsStore _settings;
    private readonly TenantAiGate? _plans;
    private readonly Action<string> _log;

    /// <param name="machineWide">The machine switch, read once from config.json at Gateway construction.</param>
    /// <param name="settings">The per-account settings store the owner switch is recorded in.</param>
    /// <param name="plans">The plan read: the hosted Gateway's <see cref="TenantAiGate"/>. Null on a self-hosted Gateway,
    /// and a gate that does not gate (<see cref="TenantAiGate.IsGating"/> false) is treated exactly as null - the plan
    /// grant exists only where plans are checked.</param>
    /// <param name="log">Where the decision lines go; the Gateway log when null. Injected so a test can read them.</param>
    public FactoryAgentsSwitch(bool machineWide, TenantSettingsStore settings, TenantAiGate? plans = null,
        Action<string>? log = null)
    {
        MachineWide = machineWide;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _plans = plans is { IsGating: true } ? plans : null;
        _log = log ?? FileLog.Write;
    }

    /// <summary>The machine switch: on for every account on this Gateway.</summary>
    public bool MachineWide { get; }

    /// <summary>Whether this Gateway grants factories by plan: true only on a hosted Gateway that checks plans.</summary>
    public bool ChecksPlans => _plans is not null;

    /// <summary>Whether the Factory Agents area is on for <paramref name="tenant"/>: <see cref="Answer"/> as a yes or no,
    /// for a caller that has nothing to say about why. An Unknown answer is not on.</summary>
    public bool IsOn(TenantId tenant) => Answer(tenant).IsOn;

    /// <summary>THE DECISION for one account, written to the log with the grant that produced it.</summary>
    /// <param name="tenant">The calling account, or null when no account is bound to the request.</param>
    public FactoryAgentsAnswer Answer(TenantId? tenant) => Decide(tenant, logTheAnswer: true);

    /// <summary>
    /// <see cref="Answer"/> without its log line, for a caller on a hot path that asks the same question over and over
    /// (the roster's factory chip, on every read of the session list). The answer is identical. A plan read that FAILS
    /// is still logged by <see cref="TenantAiGate"/>: that is a fault, not a repeated answer.
    /// </summary>
    public FactoryAgentsAnswer AnswerQuietly(TenantId? tenant) => Decide(tenant, logTheAnswer: false);

    private FactoryAgentsAnswer Decide(TenantId? tenant, bool logTheAnswer)
    {
        var answer = DecideUnlogged(tenant);
        if (logTheAnswer)
            _log($"[FactoryAgentsSwitch] account={tenant?.ToLogString() ?? "none"} state={answer.State} grant={answer.GrantName} "
                 + $"reason={answer.OffReason ?? "none"} tier={answer.Tier ?? "none"}: {answer.Why}");
        return answer;
    }

    private FactoryAgentsAnswer DecideUnlogged(TenantId? tenant)
    {
        if (MachineWide)
            return FactoryAgentsAnswer.On(FactoryAgentsGrant.MachineSwitch, tier: null, "on for this machine");

        if (tenant is { } t && Decision(t) is { Enabled: true } decision)
            return FactoryAgentsAnswer.On(FactoryAgentsGrant.OwnerSwitch, tier: null,
                $"switched on for this account by {decision.Actor} at {decision.RecordedAtUtc:o}");

        if (_plans is null)
            return FactoryAgentsAnswer.Off(FactoryAgentsOffReasons.NotSwitchedOn, tier: null,
                tenant is null ? "no account is bound to this request" : "not switched on on this Gateway or for this account");

        // The gate's timer variant, because THIS class writes the one line for the decision: the answer is identical to
        // ForTenant's, and a read that fails is still logged by the gate.
        var plan = _plans.ForTenantOnATimer(tenant, EntitlementScopes.Factories);
        return plan.Verdict switch
        {
            TenantAiVerdict.Allowed => FactoryAgentsAnswer.On(FactoryAgentsGrant.Plan, plan.Tier, plan.Reason),
            TenantAiVerdict.Refused => FactoryAgentsAnswer.Off(FactoryAgentsOffReasons.PlanRequired, plan.Tier, plan.Reason),
            _ => FactoryAgentsAnswer.Unknown(plan.Reason),
        };
    }

    /// <summary>The account's recorded decision, or null when nobody has ever switched this account.</summary>
    public FactoryAgentsSwitchDecision? Decision(TenantId tenant)
    {
        var raw = _settings.Get(tenant, TenantSettingKeys.FactoryAgentsSwitch);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return JsonSerializer.Deserialize<FactoryAgentsSwitchDecision>(raw, Json)
               ?? throw new InvalidOperationException(
                   $"The factory agents switch recorded for account {tenant.ToLogString()} holds no decision.");
    }

    /// <summary>
    /// Record a decision for one account: on or off, who made it, and why. Replaces the previous decision; the
    /// previous one is written to the log so the history of who switched an account is never lost silently.
    /// </summary>
    public FactoryAgentsSwitchDecision Set(TenantId tenant, bool enabled, string actor, string reason, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An actor is required.", nameof(actor));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.", nameof(reason));

        var previous = Decision(tenant);
        var decision = new FactoryAgentsSwitchDecision(enabled, actor.Trim(), reason.Trim(), nowUtc.ToUniversalTime());
        _settings.Set(tenant, TenantSettingKeys.FactoryAgentsSwitch, JsonSerializer.Serialize(decision, Json), nowUtc);
        FileLog.Write($"[FactoryAgentsSwitch] Set: account={tenant.ToLogString()} enabled={enabled} actor={decision.Actor} "
                      + $"reason=\"{decision.Reason}\" previous={(previous is null ? "none" : $"{previous.Enabled} by {previous.Actor} at {previous.RecordedAtUtc:o}")}");
        return decision;
    }
}

/// <summary>One account's factory agents decision, as recorded in its settings.</summary>
public sealed record FactoryAgentsSwitchDecision(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("recorded_at_utc")] DateTime RecordedAtUtc);

/// <summary>What <see cref="FactoryAgentsSwitch"/> concluded for one account.</summary>
public enum FactoryAgentsState
{
    /// <summary>A grant holds: the routes answer and the schedules run.</summary>
    On,

    /// <summary>Every read succeeded and no grant holds. The routes answer 404 as if unmapped.</summary>
    Off,

    /// <summary>The plan could not be read. Not on and not off: a route answers a temporary 503, a timer skips this pass.</summary>
    Unknown,
}

/// <summary>Which of the three grants put the area on, or <see cref="None"/> when none did.</summary>
public enum FactoryAgentsGrant
{
    /// <summary>No grant: the answer is off or unknown.</summary>
    None,

    /// <summary>The machine switch in this Gateway's config.json.</summary>
    MachineSwitch,

    /// <summary>The owner switched this account on by hand.</summary>
    OwnerSwitch,

    /// <summary>The account's plan grants <see cref="EntitlementScopes.Factories"/>.</summary>
    Plan,
}

/// <summary>Why the area is not on, as the switch question carries it in <c>reason</c>.</summary>
public static class FactoryAgentsOffReasons
{
    /// <summary>Hosted: the plan was read and does not include factories, and the owner switch is not on.</summary>
    public const string PlanRequired = "plan_required";

    /// <summary>Self-hosted: the machine switch is off and the account is not switched on. Never on hosted.</summary>
    public const string NotSwitchedOn = "not_switched_on";

    /// <summary>Hosted: the owner switch is not on and the plan read FAILED.</summary>
    public const string PlanUnknown = "plan_unknown";
}

/// <summary>
/// One answer from <see cref="FactoryAgentsSwitch"/>: on, off or unknown; the grant that put it on; the reason it is not
/// on (one of <see cref="FactoryAgentsOffReasons"/>, null exactly when it is on); the plan tier the plan read named (null
/// when the plan was not read or named none); and plain words for the log.
/// </summary>
public sealed record FactoryAgentsAnswer(
    FactoryAgentsState State, FactoryAgentsGrant Grant, string? OffReason, string? Tier, string Why)
{
    /// <summary>Whether the area is on. False for an Unknown answer.</summary>
    public bool IsOn => State == FactoryAgentsState.On;

    /// <summary>The grant in the log's and the administrator route's words: <c>machine</c>, <c>owner switch</c>,
    /// <c>plan tier pro</c>, or <c>refused</c> / <c>unknown</c> when no grant holds.</summary>
    public string GrantName => Grant switch
    {
        FactoryAgentsGrant.MachineSwitch => "machine",
        FactoryAgentsGrant.OwnerSwitch => "owner switch",
        FactoryAgentsGrant.Plan => $"plan tier {Tier ?? "legacy"}",
        _ => State == FactoryAgentsState.Unknown ? "unknown" : "refused",
    };

    /// <summary>An answer that is on, by <paramref name="grant"/>.</summary>
    public static FactoryAgentsAnswer On(FactoryAgentsGrant grant, string? tier, string why)
        => new(FactoryAgentsState.On, grant, null, tier, why);

    /// <summary>An answer that is off, for <paramref name="reason"/>.</summary>
    public static FactoryAgentsAnswer Off(string reason, string? tier, string why)
        => new(FactoryAgentsState.Off, FactoryAgentsGrant.None, reason, tier, why);

    /// <summary>An answer that could not be decided: the plan read failed.</summary>
    public static FactoryAgentsAnswer Unknown(string why)
        => new(FactoryAgentsState.Unknown, FactoryAgentsGrant.None, FactoryAgentsOffReasons.PlanUnknown, null, why);
}
