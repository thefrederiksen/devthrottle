using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Records whether the GATEWAY has shown and the user has acknowledged the first-run consent screen -
/// the short, honest explanation of what DevThrottle does and does not collect, shown once at the
/// Gateway's first launch alongside the sign-in (Gateway Centralization Phase 3, issue #650). The
/// per-Director first-run consent screen has been removed (issue #651): with the account centralized on
/// the Gateway, the consent screen is shown once on the Gateway rather than on every Director, so the
/// acknowledgement is recorded here, on the Gateway, not per-Director.
///
/// Persisted in the Gateway's <c>config.json</c> under <c>gateway_consent.acknowledged</c> - the same
/// store the other Gateway settings use (<see cref="TelemetryConsentConfig"/>, the centralized consent
/// the screen's usage-sharing choice writes). It defaults to <c>false</c>: a Gateway that has never
/// shown the screen has not acknowledged it, so the screen is shown until the user confirms it, at
/// which point this records <c>true</c> and the screen is not shown again on a subsequent launch.
///
/// The key (<c>gateway_consent</c>) is deliberately distinct from the legacy per-Director <c>consent</c>
/// section an older build may have left in <c>config.json</c>: on a co-located dev machine where a
/// Director and the Gateway share one <c>config.json</c>, the gateway acknowledgement and any leftover
/// per-Director one are separate facts and must never be confused. (The per-Director consent surface
/// itself was removed in the Director cleanup, issue #651.)
///
/// Reads and writes go through <see cref="CcDirectorConfigService"/> so the non-lossy deep-merge
/// preserves every other section of <c>config.json</c>.
/// </summary>
public static class GatewayConsentAck
{
    /// <summary>The config.json section holding the Gateway's first-run consent acknowledgement.</summary>
    public const string Section = "gateway_consent";

    /// <summary>The config.json key under <see cref="Section"/> holding the acknowledged flag.</summary>
    public const string AcknowledgedKey = "acknowledged";

    /// <summary>
    /// Returns whether the Gateway's first-run consent screen has been acknowledged. Defaults to
    /// <c>false</c> when no value has ever been persisted (the screen has not been shown yet), and
    /// reads the persisted boolean otherwise.
    /// </summary>
    public static bool HasAcknowledged()
    {
        var root = CcDirectorConfigService.ReadRaw();
        if (root[Section] is JsonObject section
            && section[AcknowledgedKey] is JsonValue value
            && value.TryGetValue<bool>(out var acknowledged))
        {
            FileLog.Write($"[GatewayConsentAck] HasAcknowledged: persisted value acknowledged={acknowledged}");
            return acknowledged;
        }

        FileLog.Write("[GatewayConsentAck] HasAcknowledged: no persisted value -> default false (gateway consent screen not yet shown)");
        return false;
    }

    /// <summary>
    /// Persists that the Gateway's first-run consent screen has been acknowledged, writing <c>true</c>
    /// to <c>config.json</c> under <c>gateway_consent.acknowledged</c> and merging into the existing
    /// file so no other section is dropped.
    /// </summary>
    public static void MarkAcknowledged()
    {
        FileLog.Write("[GatewayConsentAck] MarkAcknowledged: persisting acknowledged=true");
        var patch = new JsonObject
        {
            [Section] = new JsonObject { [AcknowledgedKey] = true },
        };
        CcDirectorConfigService.MergePatch(patch);
        FileLog.Write("[GatewayConsentAck] MarkAcknowledged: persisted");
    }
}
