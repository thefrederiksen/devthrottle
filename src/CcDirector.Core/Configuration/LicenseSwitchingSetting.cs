using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The License Switching mission's one Director setting, stored in config.json under "licenseSwitching" -&gt;
/// "enabled". OFF by default, and only an explicit <c>true</c> turns it on: until phase 5 nothing may touch a machine's
/// real default Claude login, and with it on a Director whose Gateway takes usage reports runs
/// <c>cc-devthrottle license ...</c> against that login.
///
/// Off means this Director reads no usage, sends no report, never takes the key holder lock, and answers every
/// <c>license-switch</c> failed with a plain sentence. The live proof and phase 5 turn it on. It is read when the
/// Director starts, so changing it takes a restart.
/// </summary>
public static class LicenseSwitchingSetting
{
    /// <summary>Default posture: off. A missing file, section, key or a value that is not a boolean is off.</summary>
    public const bool Default = false;

    /// <summary>The effective value of <c>licenseSwitching.enabled</c> in config.json.</summary>
    public static bool Get() => ReadFrom(CcDirectorConfigService.ReadRaw());

    /// <summary>The value from an already-loaded config root; the same rules as <see cref="Get"/>.</summary>
    public static bool ReadFrom(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root["licenseSwitching"] is not JsonObject section) return Default;
        return section["enabled"] is JsonValue value && value.GetValueKind() == JsonValueKind.True;
    }

    /// <summary>Persists <paramref name="enabled"/>, deep-merging into config.json so every other section is kept.</summary>
    public static void Set(bool enabled) => CcDirectorConfigService.MergePatch(BuildPatch(enabled));

    /// <summary>The partial patch that sets <c>licenseSwitching.enabled</c>.</summary>
    public static JsonObject BuildPatch(bool enabled) => new()
    {
        ["licenseSwitching"] = new JsonObject { ["enabled"] = enabled },
    };
}
