using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Whether the Gateway captures wingman training data: for every wingman summary, save up to
/// 20,000 characters of the session's terminal alongside the wingman's spoken response, so the
/// pairs can be used to test and improve the wingman. Persisted in config.json as
/// "wingman_training_capture" (bool). Default: false (opt-in). Read at the moment of capture, so
/// toggling it takes effect immediately - no Gateway restart.
/// </summary>
public static class WingmanTrainingCaptureConfig
{
    /// <summary>True when wingman_training_capture is explicitly set to true in config.json.</summary>
    public static bool Get()
    {
        var node = CcDirectorConfigService.ReadRaw()["wingman_training_capture"];
        if (node is null)
            return false;

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.True) return true;
        if (node is JsonValue v2 && v2.GetValueKind() == JsonValueKind.False) return false;

        throw new InvalidOperationException(
            "config.json key 'wingman_training_capture' must be true or false. " +
            "Fix the value or remove the key to use the default (false = disabled).");
    }
}
