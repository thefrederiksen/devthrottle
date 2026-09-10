using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Settings for the dictation corpus: keeping each dictation clip together with the transcript it
/// produced, so the transcriber can be measured against real speech instead of studio-clean test clips.
///
/// WHY THIS EXISTS. Nothing keeps the pair today. The disk safety net
/// (<c>DictationRecordingStore</c>) deletes the clip the moment the words are delivered, and the hosted
/// Gateway does not archive audio at all, so a transcript that comes back with a word the user never
/// said leaves no evidence behind. On 2026-09-09 roughly one dictation in eight carried an invented
/// filler ("Thank you.", "you", "Bye.") that the speech model wrote over a pause, and not one of those
/// clips still existed. A fix cannot be proven against audio nobody kept.
///
/// Persisted in config.json under the top-level object "dictation_corpus":
///   - "enabled"       (bool) - master switch. DEFAULT FALSE (opt-in). Keeping recorded speech on disk
///                              is the user's decision, never a default. Nothing is written, and no
///                              directory is created, until this is explicitly turned on.
///   - "max_clips"     (int)  - keep at most this many clips, newest first. DEFAULT 2000.
///   - "max_age_days"  (int)  - drop a clip older than this. DEFAULT 90.
/// Both bounds apply, so neither a quiet month nor a busy afternoon can run the disk up.
///
/// LOCAL ONLY. The corpus is written on the machine that recorded it and is never transmitted,
/// exactly like the safety net it sits beside.
///
/// No-fallback rule: a present-but-wrong-typed key THROWS with the fix named, rather than silently
/// picking a default (matching <see cref="AutoResumeConfig"/>).
/// </summary>
public sealed record DictationCorpusConfig(
    bool Enabled,
    int MaxClips,
    int MaxAgeDays)
{
    /// <summary>The default posture: off. A user who has not asked for this keeps nothing.</summary>
    public static readonly DictationCorpusConfig Default = new(
        Enabled: false,
        MaxClips: 2000,
        MaxAgeDays: 90);

    /// <summary>Convenience: the age bound as a span.</summary>
    public TimeSpan MaxAge => TimeSpan.FromDays(MaxAgeDays);

    /// <summary>Read the effective config from config.json's "dictation_corpus" object; missing keys
    /// fall back to <see cref="Default"/> per key.</summary>
    public static DictationCorpusConfig Get()
    {
        var node = CcDirectorConfigService.ReadRaw()["dictation_corpus"];
        if (node is null)
            return Default;

        if (node is not JsonObject obj)
            throw new InvalidOperationException(
                "config.json key 'dictation_corpus' must be an object. " +
                "Fix the value or remove the key to use the defaults (disabled).");

        return new DictationCorpusConfig(
            Enabled: ReadBool(obj, "enabled", Default.Enabled),
            MaxClips: ReadPositiveInt(obj, "max_clips", Default.MaxClips),
            MaxAgeDays: ReadPositiveInt(obj, "max_age_days", Default.MaxAgeDays));
    }

    private static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        var node = obj[key];
        if (node is null)
            return fallback;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.True) return true;
        if (node is JsonValue v2 && v2.GetValueKind() == JsonValueKind.False) return false;

        throw new InvalidOperationException(
            $"config.json key 'dictation_corpus.{key}' must be true or false. " +
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
                    $"config.json key 'dictation_corpus.{key}' must be a positive whole number. " +
                    "Fix the value or remove the key to use the default.");
            return n;
        }

        throw new InvalidOperationException(
            $"config.json key 'dictation_corpus.{key}' must be a positive whole number. " +
            "Fix the value or remove the key to use the default.");
    }
}
