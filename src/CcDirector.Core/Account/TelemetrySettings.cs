using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The user-controllable usage-telemetry opt-out flag (issue #582), persisted in <c>config.json</c>
/// under <c>telemetry.enabled</c>. It defaults to ON: a fresh install with no stored value reports
/// the richer usage telemetry as enabled, and turning the toggle off persists <c>false</c> so the
/// richer reporting stops. This flag controls ONLY the richer, user-facing usage telemetry; the
/// always-on authentication-floor events (login and logout, recorded by <see cref="AuthEventLog"/>)
/// are inherent to having an account and are not affected by it.
///
/// Reads and writes go through <see cref="CcDirectorConfigService"/> so the non-lossy deep-merge
/// preserves every other section of <c>config.json</c>.
/// </summary>
public static class TelemetrySettings
{
    /// <summary>The config.json section holding the telemetry flag.</summary>
    public const string Section = "telemetry";

    /// <summary>The config.json key under <see cref="Section"/> holding the on/off flag.</summary>
    public const string EnabledKey = "enabled";

    /// <summary>
    /// Returns whether the richer usage telemetry is enabled. Defaults to <c>true</c> when no value
    /// has ever been persisted (telemetry is on unless the user has explicitly opted out), and reads
    /// the persisted boolean otherwise.
    /// </summary>
    public static bool IsEnabled()
    {
        var root = CcDirectorConfigService.ReadRaw();
        if (root[Section] is JsonObject section
            && section[EnabledKey] is JsonValue value
            && value.TryGetValue<bool>(out var enabled))
        {
            FileLog.Write($"[TelemetrySettings] IsEnabled: persisted value enabled={enabled}");
            return enabled;
        }

        FileLog.Write("[TelemetrySettings] IsEnabled: no persisted value -> default ON");
        return true;
    }

    /// <summary>
    /// Persists the richer-usage telemetry flag to <c>config.json</c> under <c>telemetry.enabled</c>,
    /// merging into the existing file so no other section is dropped.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        FileLog.Write($"[TelemetrySettings] SetEnabled: enabled={enabled}");
        var patch = new JsonObject
        {
            [Section] = new JsonObject { [EnabledKey] = enabled },
        };
        CcDirectorConfigService.MergePatch(patch);
        FileLog.Write("[TelemetrySettings] SetEnabled: persisted");
    }
}
