using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// Whether the Factory Agents area is on FOR ONE ACCOUNT. The one answer every factory route, the Cockpit's switch
/// question, and the roster's factory chip read, so the three can never disagree.
///
/// TWO SWITCHES, AND HOW THEY COMBINE:
///   - The MACHINE switch (<c>factoryAgents.enabled</c> in this Gateway's config.json) is the operator's decision
///     for the whole Gateway. A self-hosted Gateway serves one owner, so "on for this machine" is "on for that
///     owner", exactly as before this class existed. When it is on, every account on this Gateway is on.
///   - The ACCOUNT switch is a per-account decision recorded in the account's settings under
///     <see cref="TenantSettingKeys.FactoryAgentsSwitch"/>, set only through the administrator route
///     (<c>POST /gateway/admin/factory-agents</c>) with who switched it and why. The hosted Gateway serves every
///     customer, so its machine switch stays off and an account is switched on one at a time.
///
/// On = machine switch on, OR this account switched on. An account switched OFF does not override a machine switch
/// that is ON: the machine switch is the operator's own "everyone on my Gateway", and a self-hosted operator who
/// turned it on means it. Default: off for everyone - no config block and no recorded decision is off.
///
/// A stored decision that cannot be read is an error, never a quiet "off" - a corrupt row is a defect to find, and
/// answering "off" would hide the area from an account that was switched on with no word of why.
/// </summary>
public sealed class FactoryAgentsSwitch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TenantSettingsStore _settings;

    /// <param name="machineWide">The machine switch, read once from config.json at Gateway construction.</param>
    /// <param name="settings">The per-account settings store the account switch is recorded in.</param>
    public FactoryAgentsSwitch(bool machineWide, TenantSettingsStore settings)
    {
        MachineWide = machineWide;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>The machine switch: on for every account on this Gateway.</summary>
    public bool MachineWide { get; }

    /// <summary>Whether the Factory Agents area is on for <paramref name="tenant"/>.</summary>
    public bool IsOn(TenantId tenant)
    {
        if (MachineWide) return true;
        return Decision(tenant)?.Enabled == true;
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
