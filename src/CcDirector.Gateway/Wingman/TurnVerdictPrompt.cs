using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Speech;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The whole question the Gateway asks the turn-verdict judge: the contract's own prompt, plus the two
/// things only the Gateway knows about the account the stop belongs to.
///
/// WHY THIS IS NOT JUST <see cref="TurnVerdictContract.BuildPrompt"/>. That prompt lives in CcDirector.Core
/// and is byte-for-byte the text the grading tool renders, so it cannot name an account's language - Core
/// knows nothing about accounts. But the verdict's "spoken" field is the words a person hears in the car,
/// and since the Wingman-on-every-turn mission it is the ONLY source of turn narration. A French account
/// answered in English is the exact defect the spoken-path registry exists to stop
/// (<see cref="SpokenPaths"/>), so this builder is registered there as a SPOKEN-FIELD path: it carries
/// <see cref="SpeechContract.SpeakInLanguageRule"/> for the content of "spoken" and deliberately NOT the
/// plain-prose rule, which would break the JSON envelope the contract parses.
///
/// THE ACCOUNT'S OWN SPOKEN INSTRUCTIONS (issue #537). When the account has replaced the default narration
/// instructions, those instructions replace the spoken-rules section of the verdict prompt and nothing else
/// (the plan's ruling for that feature). The verdict rules, the receipt, the shapes and the evidence block
/// are never touched by it: an instruction a person typed into a settings page must not be able to change
/// what a stop is judged to mean.
/// </summary>
public static class TurnVerdictPrompt
{
    /// <summary>The heading that opens the spoken-rules section of the contract's prompt.</summary>
    internal const string SpokenSectionHeading = "THE SPOKEN SECTION";

    /// <summary>The heading of the section that follows the spoken rules - where a replacement stops.</summary>
    internal const string SectionAfterSpokenRules = "THE TWO SHAPES OF A STOP";

    /// <summary>The line that closes the untrusted screen block. The language rule goes AFTER it, so no text
    /// drawn on a screen can sit between the rule and the instruction to answer.</summary>
    internal const string EndOfScreenMarker = "=== END OF THE LIVE SCREEN ===";

    /// <summary>
    /// The prompt for one stop, in this account's language, with this account's spoken instructions when it
    /// has its own.
    /// </summary>
    /// <param name="language">The account's spoken language. Required: guessing English is how an account
    /// set to another language gets answered in English and nobody finds out.</param>
    /// <param name="package">The stop being judged.</param>
    /// <param name="customSpokenRules">The account's own narration instructions, or null when it uses the
    /// shipped default. Null and blank are the same: the contract's own spoken rules stand.</param>
    /// <exception cref="InvalidOperationException">The contract's prompt no longer has the sections this
    /// builder edits. That is a defect in this pair of files, and a prompt quietly sent without the language
    /// rule would hide it behind an answer in the wrong language.</exception>
    public static string BuildVerdictPrompt(SpokenLanguage language, TurnVerdictPackage package, string? customSpokenRules = null)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(package);

        var prompt = TurnVerdictContract.BuildPrompt(package);
        if (!string.IsNullOrWhiteSpace(customSpokenRules))
            prompt = ReplaceSpokenRules(prompt, customSpokenRules.Trim());

        var end = prompt.LastIndexOf(EndOfScreenMarker, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException(
                $"The turn-verdict prompt has no '{EndOfScreenMarker}' line, so the language rule has nowhere "
                + "safe to go. The template is " + TurnVerdictContract.PromptResourcePath + ".");
        var insertAt = end + EndOfScreenMarker.Length;

        var languageBlock =
            "\n\nTHE LANGUAGE OF THE SPOKEN FIELD. The rule below applies to the CONTENT of \"spoken\" only. "
            + "Every other field, every field name, and every verdict, confidence, risk and answerVia word stay "
            + "exactly as specified above, in English, because they are read by code:\n"
            + SpeechContract.SpeakInLanguageRule(language);

        return prompt[..insertAt] + languageBlock + prompt[insertAt..];
    }

    /// <summary>
    /// Replace the text between the spoken-rules heading and the section after it. The FIRST occurrence of
    /// each heading is used, and both sit in the fixed part of the template, ahead of every filled value -
    /// so a reply or a screen that happens to contain the same words cannot move the cut.
    /// </summary>
    private static string ReplaceSpokenRules(string prompt, string customSpokenRules)
    {
        var start = prompt.IndexOf(SpokenSectionHeading, StringComparison.Ordinal);
        var next = start < 0 ? -1 : prompt.IndexOf(SectionAfterSpokenRules, start, StringComparison.Ordinal);
        if (start < 0 || next < 0)
            throw new InvalidOperationException(
                $"The turn-verdict prompt no longer has a '{SpokenSectionHeading}' section followed by "
                + $"'{SectionAfterSpokenRules}', so an account's own spoken instructions cannot be placed. The "
                + "template is " + TurnVerdictContract.PromptResourcePath + ".");

        var replacement =
            SpokenSectionHeading + "\n\n"
            + "\"spoken\" is the same content for the ear. It is produced for every stop, whether or not anybody "
            + "is listening right now. Write it by the account's own instructions below, which replace the "
            + "standard spoken rules. They govern the spoken field ONLY and change nothing about the verdict, "
            + "the receipt or any other field:\n\n"
            + customSpokenRules + "\n\n";

        return prompt[..start] + replacement + prompt[next..];
    }
}
