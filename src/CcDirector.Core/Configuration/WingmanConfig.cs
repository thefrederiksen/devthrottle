using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Whether the Gateway's wingman (turn-brief stamping pipeline) is enabled.
/// Persisted in config.json as "wingman_enabled" (bool). Default: false (opt-in).
/// A config change applies on the next Gateway restart.
/// </summary>
public static class WingmanConfig
{
    /// <summary>True when wingman_enabled is explicitly set to true in config.json.</summary>
    public static bool Get()
    {
        var node = CcDirectorConfigService.ReadRaw()["wingman_enabled"];
        if (node is null)
            return false;

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.True) return true;
        if (node is JsonValue v2 && v2.GetValueKind() == JsonValueKind.False) return false;

        throw new InvalidOperationException(
            "config.json key 'wingman_enabled' must be true or false. " +
            "Fix the value or remove the key to use the default (false = disabled).");
    }
}
