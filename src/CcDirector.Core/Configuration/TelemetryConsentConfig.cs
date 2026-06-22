using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The GATEWAY-OWNED, fleet-wide richer-usage-telemetry consent (opt-out) setting (Gateway
/// Centralization Phase 3, issue #649). This is the authoritative consent toggle: it lives on the
/// Gateway, where the DevThrottle account lives, so one setting governs the whole fleet rather than
/// a per-machine flag on each Director.
///
/// Persisted in <c>config.json</c> as the top-level boolean key <c>telemetry_consent</c>, the same
/// store the other Gateway settings use (<see cref="AddressingModeConfig"/>,
/// <see cref="WingmanTrainingCaptureConfig"/>). Default ON: a Gateway with no persisted value reports
/// the richer usage telemetry as consented, so a fresh install reports usage unless the user opts out.
/// Read at the moment of decision, so toggling it takes effect immediately - no Gateway restart.
///
/// This setting gates ONLY the richer, user-facing usage telemetry (<see cref="Account.UsageTelemetry"/>).
/// The always-on authentication-floor events (the login and director-startup events, issues #628/#631)
/// are inherent to having an account and are NEVER gated by this setting.
///
/// Distinct from the per-Director <see cref="Account.TelemetrySettings"/> (the local
/// <c>telemetry.enabled</c> flag, issue #582): that local toggle remains in place here and is removed
/// in the Director-cleanup issue (#651); this issue only establishes the authoritative Gateway setting.
/// </summary>
public static class TelemetryConsentConfig
{
    /// <summary>The config.json top-level key holding the fleet-wide consent flag.</summary>
    public const string Key = "telemetry_consent";

    /// <summary>The default when no value has ever been persisted: consented (ON).</summary>
    public const bool Default = true;

    /// <summary>
    /// Returns whether the fleet has consented to the richer usage telemetry. Defaults to <c>true</c>
    /// (consented) when no value has ever been persisted, and reads the persisted boolean otherwise.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The <c>telemetry_consent</c> key is present but is not a JSON boolean.
    /// </exception>
    public static bool Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[Key];
        if (node is null)
        {
            FileLog.Write("[TelemetryConsentConfig] Get: no persisted value -> default ON");
            return Default;
        }

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.True)
        {
            FileLog.Write("[TelemetryConsentConfig] Get: persisted value enabled=true");
            return true;
        }

        if (node is JsonValue v2 && v2.GetValueKind() == JsonValueKind.False)
        {
            FileLog.Write("[TelemetryConsentConfig] Get: persisted value enabled=false");
            return false;
        }

        throw new InvalidOperationException(
            "config.json key 'telemetry_consent' must be true or false. " +
            "Fix the value or remove the key to use the default (true = consented).");
    }

    /// <summary>
    /// Persists the fleet-wide consent flag to <c>config.json</c> under <c>telemetry_consent</c>,
    /// merging into the existing file so no other section is dropped.
    /// </summary>
    public static void Set(bool enabled)
    {
        FileLog.Write($"[TelemetryConsentConfig] Set: enabled={enabled}");
        CcDirectorConfigService.MergePatch(new JsonObject { [Key] = enabled });
        FileLog.Write("[TelemetryConsentConfig] Set: persisted");
    }
}
