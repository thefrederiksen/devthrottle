using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The user-facing read/write path for the tooling auto-update switch (issue #828), stored in
/// config.json under "tools" -&gt; "autoUpdate" -&gt; "enabled". When enabled (the default), the
/// Director automatically reconciles its cc-* tools (install-missing, purge-drift, repair-broken)
/// in the background. The Tools settings page toggle reads and writes this key through here.
///
/// This is the C#-app counterpart of the setup engine's read-only
/// <c>CcDirector.Setup.Engine.ToolAutoUpdateConfig</c>: both honor the SAME key. To keep the two
/// read paths in agreement, the read here is DELIBERATELY lenient and matches the engine - a
/// missing file, a missing "tools"/"autoUpdate" section, a missing key, or a non-boolean value all
/// fall back to enabled. Only an explicit <c>false</c> turns it off, so a fresh install self-heals
/// its tools with no config required (issue #827/#828).
/// </summary>
public static class ToolAutoUpdateSetting
{
    /// <summary>Default posture: enabled. Absent key = on; only an explicit opt-out turns it off.</summary>
    public const bool Default = true;

    /// <summary>
    /// Read the effective value of <c>tools.autoUpdate.enabled</c> from config.json. Any absence or
    /// wrong type falls back to <see cref="Default"/> (enabled), matching the setup engine's read
    /// path so the toggle and the lifecycle never disagree.
    /// </summary>
    public static bool Get() => ReadFrom(CcDirectorConfigService.ReadRaw());

    /// <summary>
    /// Read the value from an already-loaded config root. Exposed for unit tests and callers that
    /// already hold the raw document; the absence/wrong-type rules are identical to <see cref="Get"/>.
    /// </summary>
    public static bool ReadFrom(JsonObject root)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));

        if (root["tools"] is not JsonObject tools)
            return Default;
        if (tools["autoUpdate"] is not JsonObject autoUpdate)
            return Default;
        if (autoUpdate["enabled"] is not JsonValue value)
            return Default;

        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => Default,
        };
    }

    /// <summary>
    /// Persist <paramref name="enabled"/> to <c>tools.autoUpdate.enabled</c>, deep-merging into
    /// config.json so every other section is preserved.
    /// </summary>
    public static void Set(bool enabled) => CcDirectorConfigService.MergePatch(BuildPatch(enabled));

    /// <summary>Build the partial patch that sets <c>tools.autoUpdate.enabled</c> to the given value.</summary>
    public static JsonObject BuildPatch(bool enabled) => new()
    {
        ["tools"] = new JsonObject
        {
            ["autoUpdate"] = new JsonObject
            {
                ["enabled"] = enabled,
            },
        },
    };
}
