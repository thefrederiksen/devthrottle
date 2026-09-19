using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Whether this Director writes a per-session log to disk under <c>session-logs/</c>.
///
/// The log is FOUR append-only streams per session, and the expensive one is <c>raw.jsonl</c>: every
/// chunk of bytes the terminal emitted, base64-encoded. That is not a record of what the agent said -
/// it is a record of what the screen PAINTED, so every spinner frame, progress bar and repaint of a
/// status line is captured, then inflated by a third by base64 and wrapped in JSON with a timestamp
/// per chunk. One Codex session measured 1.1 GB; one machine's collection measured 35 GB across 3,422
/// sessions, 99.6% of them belonging to sessions that no longer exist.
///
/// It was ON for every session on every install, with no setting, no cap and no age limit, and
/// nothing in the product ever read a byte of it: the only readers are the unit tests and the
/// <c>raw-view-harness</c> developer tool. The streams were built as raw material for work that was
/// never finished - <c>agent-view.jsonl</c> is described in its own writer as a "slot reserved for a
/// follow-up slice", and the bytes were captured, in ProcessHost's words, "so we could" use them.
/// The terminal evidence the product ACTUALLY reads is the wingman's bounded per-turn
/// <c>ScreenTail</c>, which the Gateway holds.
///
/// So the default is now OFF, and turning it on is a visible decision in config.json:
///
///   "session_logs": { "enabled": true }
///
/// Turn it on when chasing a terminal or escape-sequence defect - <c>raw.jsonl</c> is what
/// <c>cc-raw-view-harness</c> replays through the ANSI parser to reproduce a screen without
/// rebuilding the Director - then turn it off again. <c>CC_DIRECTOR_SESSION_LOGS</c> overrides in
/// BOTH directions, so that can be done for one run without editing a file.
///
/// This is the same defect, and the same remedy, as <see cref="SessionRecordingConfig"/>: an internal
/// engineering capture, collected from every install, invisible in the product, with no age limit.
/// That one was fixed and this one was missed. What we collect for our own benefit has to be
/// something the user can see they turned on.
///
/// No-fallback rule: a present-but-wrong-typed key THROWS with the fix named, rather than silently
/// picking a default (matching <see cref="SessionRecordingConfig"/> and <see cref="AutoResumeConfig"/>).
/// </summary>
public sealed record SessionLogConfig(bool Enabled)
{
    /// <summary>The default posture: not writing session logs.</summary>
    public static readonly SessionLogConfig Default = new(Enabled: false);

    /// <summary>The environment override, honoured in both directions.</summary>
    public const string EnvironmentVariable = "CC_DIRECTOR_SESSION_LOGS";

    /// <summary>The config.json section this reads.</summary>
    public const string SectionName = "session_logs";

    /// <summary>
    /// The effective answer for this machine: the environment override if it says anything, then
    /// config.json, then off.
    /// </summary>
    public static bool IsEnabled()
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            var value = env.Trim();
            if (value is "0" or "false" or "False" or "FALSE" or "no" or "off") return false;
            if (value is "1" or "true" or "True" or "TRUE" or "yes" or "on") return true;

            throw new InvalidOperationException(
                $"The environment variable {EnvironmentVariable} must be 1 or 0 (or true/false), not '{value}'. "
                + $"Fix the value or unset it to use config.json's {SectionName}.enabled.");
        }

        return Get().Enabled;
    }

    /// <summary>Read the effective config from config.json's section; a missing key means off.</summary>
    public static SessionLogConfig Get()
    {
        var node = CcDirectorConfigService.ReadRaw()[SectionName];
        if (node is null)
            return Default;

        if (node is not JsonObject obj)
            throw new InvalidOperationException(
                $"config.json key '{SectionName}' must be an object. "
                + "Fix the value or remove the key to use the default (not writing session logs).");

        return new SessionLogConfig(Enabled: ReadBool(obj, "enabled", Default.Enabled));
    }

    private static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        var node = obj[key];
        if (node is null)
            return fallback;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.True) return true;
        if (node is JsonValue v2 && v2.GetValueKind() == JsonValueKind.False) return false;

        throw new InvalidOperationException(
            $"config.json key '{SectionName}.{key}' must be true or false. "
            + "Fix the value or remove the key to use the default.");
    }
}
