using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The machine's transcription connection mode (issue #497), persisted in config.json as the
/// top-level string "transcription_mode" ("byo" or "devthrottle"), mirroring the existing
/// "addressing_mode" top-level setting.
///
/// Read locally by each process from its own config.json. A change is honored on the next
/// transcription resolve (the resolver re-reads it), so it does not require a restart.
///
/// No-fallback rule: a key that is present but not one of the allowed strings THROWS with the
/// fix named, rather than silently picking a mode (see <see cref="TranscriptionModeExtensions.Parse"/>).
/// </summary>
public static class TranscriptionModeConfig
{
    /// <summary>The config.json key this setting lives under.</summary>
    public const string ConfigKey = "transcription_mode";

    /// <summary>The default mode when nothing is configured.</summary>
    public const TranscriptionMode Default = TranscriptionMode.Byo;

    /// <summary>Resolve the mode: config.json "transcription_mode" when set, else <see cref="Default"/>.</summary>
    public static TranscriptionMode Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[ConfigKey];
        if (node is null)
            return Default;

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            return TranscriptionModeExtensions.Parse(v.GetValue<string>());

        throw new InvalidOperationException(
            "config.json key 'transcription_mode' must be a string (\"byo\" or \"devthrottle\"). " +
            "Fix the value or remove the key to use the default (byo).");
    }

    /// <summary>Persist the mode to config.json (merge-patch, leaving other keys untouched).</summary>
    public static void Set(TranscriptionMode mode)
    {
        CcDirectorConfigService.MergePatch(
            new JsonObject { [ConfigKey] = mode.ToConfigString() });
    }
}
