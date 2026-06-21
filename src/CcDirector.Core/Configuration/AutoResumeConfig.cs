using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Settings for the transient-error auto-resume feature (issue #476): the Wingman watches a
/// Claude Code session's terminal for a TRANSIENT Anthropic API server error (an HTTP 500
/// "Internal server error ... usually temporary", or a 529 "Overloaded" / "try again"), and,
/// when enabled, automatically nudges the stalled session to continue on a cadence until it
/// recovers or a give-up bound is reached.
///
/// Persisted in config.json under the top-level object "auto_resume", in the style of the
/// "autoUpdate" section, with these keys:
///   - "enabled"             (bool)  - master switch. DEFAULT FALSE (opt-in). The Director only
///                                     auto-continues a stalled session when the user has
///                                     explicitly turned this on (human decision on assumption
///                                     A-3: the write action is approved, default OFF).
///   - "first_retry_seconds" (int)   - delay from detection to the FIRST auto-continue. Sooner
///                                     than the steady interval because these errors are usually
///                                     temporary. DEFAULT 60.
///   - "interval_seconds"    (int)   - delay between subsequent auto-continue attempts while the
///                                     error persists. DEFAULT 300 (5 minutes).
///   - "max_attempts"        (int)   - give-up bound: stop after this many attempts. DEFAULT 12.
///   - "max_elapsed_minutes" (int)   - give-up bound: stop after this much wall-clock time since
///                                     the first detection. DEFAULT 120 (2 hours). Whichever
///                                     bound is hit first wins.
///
/// No-fallback rule: a present-but-wrong-typed key THROWS with the fix named, rather than
/// silently picking a default (matching <see cref="AddressingModeConfig"/> /
/// <see cref="WingmanConfig"/>). A read applies on the next time the value is consulted; the
/// scheduler re-reads the live config each cycle, so changes take effect without a restart.
/// </summary>
public sealed record AutoResumeConfig(
    bool Enabled,
    int FirstRetrySeconds,
    int IntervalSeconds,
    int MaxAttempts,
    int MaxElapsedMinutes)
{
    /// <summary>The default posture: disabled (opt-in), first retry after 1 minute, then every
    /// 5 minutes, give up after 12 attempts or 2 hours.</summary>
    public static readonly AutoResumeConfig Default = new(
        Enabled: false,
        FirstRetrySeconds: 60,
        IntervalSeconds: 300,
        MaxAttempts: 12,
        MaxElapsedMinutes: 120);

    /// <summary>Convenience: the delay before the first auto-continue.</summary>
    public TimeSpan FirstRetryDelay => TimeSpan.FromSeconds(FirstRetrySeconds);

    /// <summary>Convenience: the delay between subsequent auto-continue attempts.</summary>
    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);

    /// <summary>Convenience: the max wall-clock time bound.</summary>
    public TimeSpan MaxElapsed => TimeSpan.FromMinutes(MaxElapsedMinutes);

    /// <summary>Read the effective config from config.json's "auto_resume" object; missing keys
    /// fall back to <see cref="Default"/> per key.</summary>
    public static AutoResumeConfig Get()
    {
        var node = CcDirectorConfigService.ReadRaw()["auto_resume"];
        if (node is null)
            return Default;

        if (node is not JsonObject obj)
            throw new InvalidOperationException(
                "config.json key 'auto_resume' must be an object. " +
                "Fix the value or remove the key to use the defaults (disabled).");

        return new AutoResumeConfig(
            Enabled: ReadBool(obj, "enabled", Default.Enabled),
            FirstRetrySeconds: ReadPositiveInt(obj, "first_retry_seconds", Default.FirstRetrySeconds),
            IntervalSeconds: ReadPositiveInt(obj, "interval_seconds", Default.IntervalSeconds),
            MaxAttempts: ReadPositiveInt(obj, "max_attempts", Default.MaxAttempts),
            MaxElapsedMinutes: ReadPositiveInt(obj, "max_elapsed_minutes", Default.MaxElapsedMinutes));
    }

    private static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        var node = obj[key];
        if (node is null)
            return fallback;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.True) return true;
        if (node is JsonValue v2 && v2.GetValueKind() == JsonValueKind.False) return false;

        throw new InvalidOperationException(
            $"config.json key 'auto_resume.{key}' must be true or false. " +
            "Fix the value or remove the key to use the default.");
    }

    private static int ReadPositiveInt(JsonObject obj, string key, int fallback)
    {
        var node = obj[key];
        if (node is null)
            return fallback;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
        {
            var n = v.GetValue<int>();
            if (n <= 0)
                throw new InvalidOperationException(
                    $"config.json key 'auto_resume.{key}' must be a positive whole number of seconds/minutes/attempts. " +
                    "Fix the value or remove the key to use the default.");
            return n;
        }

        throw new InvalidOperationException(
            $"config.json key 'auto_resume.{key}' must be a positive whole number. " +
            "Fix the value or remove the key to use the default.");
    }
}
