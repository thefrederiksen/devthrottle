using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Global alpha-features toggle. When enabled, alpha/experimental features (handover,
/// FIFO, GitHub remote sessions, Assistant/Coach cards, wake-word test) are visible in
/// the desktop UI. Default is OFF: alpha features are hidden until they have been
/// verified well enough to graduate out from behind the flag.
/// GRADUATED out of alpha (2026-06-05): the agent picker, with Claude Code and Pi -
/// the agents whose drivers are live-verified (docs/plans/agent-driver.md). The rule
/// is "verified driver = shipped": agents still on GenericDriver (Codex, Gemini,
/// opencode) remain alpha-only until their drivers are written and verified.
/// Persisted in config.json as "alpha_mode": true/false via the single
/// round-trip-preserving writer <see cref="CcDirectorConfigService"/>.
/// </summary>
public static class AlphaMode
{
    private static bool _isEnabled;
    private static bool _loaded;

    /// <summary>Raised when alpha mode is toggled, so long-lived windows can re-gate live.</summary>
    public static event Action? Changed;

    /// <summary>Whether alpha features are currently enabled. Default false.</summary>
    public static bool IsEnabled
    {
        get
        {
            if (!_loaded) Load();
            return _isEnabled;
        }
    }

    /// <summary>Toggle alpha features on or off and persist to config.json.</summary>
    public static void SetEnabled(bool enabled)
    {
        FileLog.Write($"[AlphaMode] SetEnabled: {enabled}");
        _isEnabled = enabled;
        _loaded = true;
        CcDirectorConfigService.MergePatch(new JsonObject { ["alpha_mode"] = enabled });
        Changed?.Invoke();
    }

    /// <summary>Re-read the flag from config.json (used after an external write, and by tests).</summary>
    public static void Reload()
    {
        _loaded = false;
        Load();
    }

    private static void Load()
    {
        _loaded = true;
        _isEnabled = CcDirectorConfigService.ReadRaw()["alpha_mode"] is JsonValue v
            && v.GetValueKind() == JsonValueKind.True;
        FileLog.Write($"[AlphaMode] Load: alpha_mode={_isEnabled}");
    }
}
