using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The model the Gateway's warm brain - the fleet's wingman - runs on (issue #204).
/// The wingman is the single highest-leverage intelligence point in the product: it
/// interprets every turn of every session for the user. So the model is pinned to the
/// SMARTEST tier by default instead of silently inheriting the account default, and the
/// pin is recorded on every brief it writes.
///
/// Persisted in config.json as "brain_model" (a claude CLI model alias or full model
/// id, e.g. "opus"). Read once at Gateway start - the BrainSupervisor's launch options
/// are fixed when the host is constructed, so a change applies on the next Gateway
/// restart.
///
/// No-fallback rule: a key that is present but not a non-empty string THROWS with the
/// fix. The brain must never silently run on a model nobody chose.
/// </summary>
public static class BrainModelConfig
{
    /// <summary>The smartest claude tier, by alias so it tracks the newest opus.</summary>
    public const string Default = "opus";

    /// <summary>Resolve the brain model: config.json "brain_model" when set, else <see cref="Default"/>.</summary>
    public static string Get()
    {
        var node = CcDirectorConfigService.ReadRaw()["brain_model"];
        if (node is null)
            return Default;

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            var model = v.GetValue<string>().Trim();
            if (model.Length > 0)
                return model;
        }

        throw new InvalidOperationException(
            "config.json key 'brain_model' must be a non-empty string (a claude model alias " +
            $"or id, e.g. \"{Default}\"). Fix the value or remove the key to use the default.");
    }
}
