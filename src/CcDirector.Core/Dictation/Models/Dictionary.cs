namespace CcDirector.Core.Dictation.Models;

/// <summary>
/// User-editable dictation dictionary loaded from a YAML file.
///
/// Two layers of knowledge:
/// 1. <see cref="Vocabulary"/> - canonical terms the speaker uses. Packed into
///    the OpenAI prompt parameter as a soft decode-time bias.
/// 2. <see cref="CommonMistranscriptions"/> - known mistranscription patterns
///    the user has observed in practice. Passed to the cleanup LLM so it can
///    repair known cases and generalize to similar near-misses.
///
/// Profiles let the same dictionary serve multiple contexts (code mode,
/// email mode, verbatim mode) with different cleanup behavior.
/// </summary>
public sealed record DictationDictionary(
    IReadOnlyList<string> Vocabulary,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CommonMistranscriptions,
    IReadOnlyDictionary<string, DictationProfile> Profiles)
{
    public static DictationDictionary Empty { get; } = new(
        Array.Empty<string>(),
        new Dictionary<string, IReadOnlyList<string>>(),
        new Dictionary<string, DictationProfile>());
}

/// <summary>
/// A named cleanup profile. Controls whether and how the post-transcription
/// LLM pass runs for a given dictation context.
/// </summary>
public sealed record DictationProfile(
    string Name,
    bool CleanupEnabled,
    string? StylePrompt);
