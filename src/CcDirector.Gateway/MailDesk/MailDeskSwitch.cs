using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// Whether Mail Desk is on FOR ONE ACCOUNT (the Mail Desk mission, issue #4213, section 5.10). The one answer every
/// Mail Desk route (through <see cref="Api.MailDeskGate"/>) and the check report's refusal read, so they can never
/// disagree.
///
/// A copy of the factory agents switch (<see cref="Factory.FactoryAgentsSwitch"/>) with ONE difference: there is no
/// machine switch. Mail Desk reads a mailbox, and the owner's grant holds only while no other account can have it on,
/// so no <c>config.json</c> edit can ever turn it on for every account of the hosted Gateway. The only way on is a
/// per-account decision recorded in the account's settings under <see cref="TenantSettingKeys.MailDeskSwitch"/>, set
/// only through the administrator route (<c>POST /gateway/admin/mail-desk</c>) with who switched it and why.
///
/// Default: off for every account - no recorded decision is off.
///
/// A stored decision that cannot be read is an error, never a quiet "off": a corrupt row is a defect to find, and
/// answering "off" would hide Mail Desk from an account that was switched on with no word of why.
/// </summary>
public sealed class MailDeskSwitch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TenantSettingsStore _settings;

    /// <param name="settings">The per-account settings store the decision is recorded in.</param>
    public MailDeskSwitch(TenantSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Whether Mail Desk is on for <paramref name="tenant"/>.</summary>
    public bool IsOn(TenantId tenant) => Decision(tenant)?.Enabled == true;

    /// <summary>The account's recorded decision, or null when nobody has ever switched this account.</summary>
    public MailDeskSwitchDecision? Decision(TenantId tenant)
    {
        var raw = _settings.Get(tenant, TenantSettingKeys.MailDeskSwitch);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return JsonSerializer.Deserialize<MailDeskSwitchDecision>(raw, Json)
               ?? throw new InvalidOperationException(
                   $"The Mail Desk switch recorded for account {tenant.ToLogString()} holds no decision.");
    }

    /// <summary>
    /// Record a decision for one account: on or off, who made it, and why. Replaces the previous decision; the
    /// previous one is written to the log so the history of who switched an account is never lost silently.
    /// </summary>
    public MailDeskSwitchDecision Set(TenantId tenant, bool enabled, string actor, string reason, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An actor is required.", nameof(actor));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.", nameof(reason));

        var previous = Decision(tenant);
        var decision = new MailDeskSwitchDecision(enabled, actor.Trim(), reason.Trim(), nowUtc.ToUniversalTime());
        _settings.Set(tenant, TenantSettingKeys.MailDeskSwitch, JsonSerializer.Serialize(decision, Json), nowUtc);
        FileLog.Write($"[MailDeskSwitch] Set: account={tenant.ToLogString()} enabled={enabled} actor={decision.Actor} "
                      + $"reason=\"{decision.Reason}\" previous={(previous is null ? "none" : $"{previous.Enabled} by {previous.Actor} at {previous.RecordedAtUtc:o}")}");
        return decision;
    }
}

/// <summary>One account's Mail Desk decision, as recorded in its settings.</summary>
public sealed record MailDeskSwitchDecision(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("recorded_at_utc")] DateTime RecordedAtUtc);
