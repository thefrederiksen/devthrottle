using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// Records whether the person has seen and acknowledged the first-run consent step - the short,
/// honest explanation of what DevThrottle does and does not collect, shown once after the account
/// sign-in. The flag is persisted in <c>config.json</c> under <c>consent.acknowledged</c> and
/// defaults to <c>false</c>: a fresh install (or an existing install upgrading into this version)
/// has never acknowledged it, so the consent step is shown until the person confirms it, at which
/// point this records <c>true</c> and the step is not shown again.
///
/// This is deliberately separate from the usage-telemetry opt-out (<see cref="TelemetrySettings"/>):
/// the consent step lets the person set that opt-out, but whether they have BEEN SHOWN the
/// explanation is its own fact, so re-showing the explanation and changing the telemetry choice are
/// never confused for one another.
///
/// Reads and writes go through <see cref="CcDirectorConfigService"/> so the non-lossy deep-merge
/// preserves every other section of <c>config.json</c>.
/// </summary>
public static class FirstRunConsent
{
    /// <summary>The config.json section holding the first-run consent flag.</summary>
    public const string Section = "consent";

    /// <summary>The config.json key under <see cref="Section"/> holding the acknowledged flag.</summary>
    public const string AcknowledgedKey = "acknowledged";

    /// <summary>
    /// Returns whether the first-run consent step has been acknowledged on this install. Defaults to
    /// <c>false</c> when no value has ever been persisted (the step has not been shown yet), and reads
    /// the persisted boolean otherwise.
    /// </summary>
    public static bool HasAcknowledged()
    {
        var root = CcDirectorConfigService.ReadRaw();
        if (root[Section] is JsonObject section
            && section[AcknowledgedKey] is JsonValue value
            && value.TryGetValue<bool>(out var acknowledged))
        {
            FileLog.Write($"[FirstRunConsent] HasAcknowledged: persisted value acknowledged={acknowledged}");
            return acknowledged;
        }

        FileLog.Write("[FirstRunConsent] HasAcknowledged: no persisted value -> default false (consent step not yet shown)");
        return false;
    }

    /// <summary>
    /// Persists that the first-run consent step has been acknowledged, writing <c>true</c> to
    /// <c>config.json</c> under <c>consent.acknowledged</c> and merging into the existing file so no
    /// other section is dropped.
    /// </summary>
    public static void MarkAcknowledged()
    {
        FileLog.Write("[FirstRunConsent] MarkAcknowledged: persisting acknowledged=true");
        var patch = new JsonObject
        {
            [Section] = new JsonObject { [AcknowledgedKey] = true },
        };
        CcDirectorConfigService.MergePatch(patch);
        FileLog.Write("[FirstRunConsent] MarkAcknowledged: persisted");
    }
}
