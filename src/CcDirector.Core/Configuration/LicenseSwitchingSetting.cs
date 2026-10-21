using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The License Switching mission's one Director setting, stored in config.json under "licenseSwitching" -&gt;
/// "enabled". With it on, the Director runs <c>cc-devthrottle license ...</c> against the machine's real default Claude
/// login: it reads usage, reports it, and carries out the switches the Gateway sends.
///
/// WHAT IT IS WHEN NOTHING IS WRITTEN (issue #4327). An explicit <c>true</c> is on and an explicit <c>false</c> is off -
/// the false is how the owner opts a machine out. When config.json says neither (no file, no section, no key, or a
/// value that is not a boolean), the setting is ON when this machine's license store holds at least one spare, and off
/// when it holds none. A fresh Director data folder on a machine that already holds spares therefore comes up taking
/// part in switches, instead of silently dropping out of them the way one machine did on 21 October 2026.
///
/// Off means this Director never runs the license tool, never reads usage, never switches, and answers every
/// <c>license-switch</c> failed with a plain sentence. When it holds spares it still tells the Gateway that switching is
/// off here, so the machine is not missed. It is read when the Director starts, so changing it takes a restart.
/// </summary>
public static class LicenseSwitchingSetting
{
    /// <summary>What config.json says, from an already-loaded config root: true or false when it says so, null when it
    /// says nothing - a missing section or key, or a value that is not a boolean.</summary>
    public static bool? ReadExplicit(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root["licenseSwitching"] is not JsonObject section) return null;
        if (section["enabled"] is not JsonValue value) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>The effective setting: what config.json says, else on exactly when the machine holds a spare.</summary>
    public static bool Resolve(bool? explicitValue, bool holdsSpare) => explicitValue ?? holdsSpare;

    /// <summary>Persists <paramref name="enabled"/>, deep-merging into config.json so every other section is kept.</summary>
    public static void Set(bool enabled) => CcDirectorConfigService.MergePatch(BuildPatch(enabled));

    /// <summary>The partial patch that sets <c>licenseSwitching.enabled</c>.</summary>
    public static JsonObject BuildPatch(bool enabled) => new()
    {
        ["licenseSwitching"] = new JsonObject { ["enabled"] = enabled },
    };
}
