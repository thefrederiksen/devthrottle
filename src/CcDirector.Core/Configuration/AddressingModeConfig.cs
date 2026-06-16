using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The fleet's network addressing mode (issue #457), persisted in config.json as the
/// top-level string "addressing_mode" ("tailscale" or "lan"), mirroring the existing
/// "brain_model" / "wingman_enabled" top-level settings.
///
/// Read locally by each process (Gateway and every Director) from its own config.json.
/// A change applies on the next process start - the bind interface and advertised endpoint
/// are decided at Director startup, so the mode cannot hot-swap a running Director.
///
/// No-fallback rule: a key that is present but not one of the allowed strings THROWS with the
/// fix named, rather than silently picking a mode (see <see cref="AddressingModeExtensions.Parse"/>).
/// </summary>
public static class AddressingModeConfig
{
    /// <summary>The fleet default.</summary>
    public const AddressingMode Default = AddressingMode.Tailscale;

    /// <summary>Resolve the mode: config.json "addressing_mode" when set, else <see cref="Default"/>.</summary>
    public static AddressingMode Get()
    {
        var node = CcDirectorConfigService.ReadRaw()["addressing_mode"];
        if (node is null)
            return Default;

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            return AddressingModeExtensions.Parse(v.GetValue<string>());

        throw new InvalidOperationException(
            "config.json key 'addressing_mode' must be a string (\"tailscale\" or \"lan\"). " +
            "Fix the value or remove the key to use the default (tailscale).");
    }
}
