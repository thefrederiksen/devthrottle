using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The tooling auto-update switch, read from the shared config.json "tools" -&gt; "autoUpdate" -&gt;
/// "enabled" key. This governs whether the Director automatically reconciles its cc-* tools
/// (install-missing, purge-drift, repair-broken) in the background via <see cref="ToolReconciler"/>.
///
/// It is DELIBERATELY distinct from the Director self-update switch (<see cref="AutoUpdateConfig"/>'s
/// top-level "autoUpdate" section): tooling auto-update is its own switch (issue #827). Defaults to
/// enabled - a missing file, a missing "tools"/"autoUpdate" section, a missing key, or a parse error
/// all fall back to enabled, so a fresh install self-heals its tools with no config required. The
/// user-facing opt-out toggle on the Tools settings page is a separate follow-up issue (#828); this
/// type is only the read path that the toggle and the lifecycle both honor.
/// </summary>
public sealed record ToolAutoUpdateConfig(bool Enabled)
{
    public static readonly ToolAutoUpdateConfig Default = new(Enabled: true);

    public static ToolAutoUpdateConfig Load(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        try
        {
            if (!File.Exists(layout.ConfigPath)) return Default;
            using var doc = JsonDocument.Parse(File.ReadAllText(layout.ConfigPath));
            if (!doc.RootElement.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Object)
                return Default;
            if (!tools.TryGetProperty("autoUpdate", out var au) || au.ValueKind != JsonValueKind.Object)
                return Default;

            var enabled = au.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? e.GetBoolean()
                : Default.Enabled;
            return new ToolAutoUpdateConfig(enabled);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[ToolAutoUpdateConfig] load failed ({layout.ConfigPath}): {ex.Message}; using defaults");
            return Default;
        }
    }
}
