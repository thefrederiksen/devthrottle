using CcDirector.Core.Wingman;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The whole question the Gateway asks the turn-verdict judge. From contract v3 (owner ruling, 2026-09-18) that is
/// the contract's own prompt and nothing else, and this class is the one place that says why. From contract v4 it is
/// asked only about a stop no code step decided (<see cref="CallACodeSteps"/>), and the answer is one word.
///
/// WHAT IT USED TO ADD, AND WHY IT NO LONGER DOES. The judge used to answer a "spoken" field - the words a person
/// heard in the car - so this builder carried two things Core cannot know: the account's spoken language, and the
/// account's own narration instructions when it had replaced the shipped ones (issue #537). Contract v3 cut the
/// spoken field. The judge now answers a state, a label, what the agent recommends, and a menu; every word a person
/// HEARS is written by the narration call, which takes the language and the account's instructions itself and is
/// registered in <see cref="Speech.SpokenPaths"/> as a whole-output spoken path.
///
/// SO NEITHER BELONGS HERE ANY MORE, and leaving them would be worse than useless. The spoken-rules replacement cut
/// the prompt between two headings, and the v3 template has neither, so it threw - an account that had typed its own
/// narration instructions could not be judged at all. The language rule named "spoken" as the one field it governed,
/// so against v3 it pointed at nothing. Both were removed rather than re-aimed: the fields the judge still answers
/// were English before this change and are English after it, so nothing a person reads changed.
///
/// IT IS REGISTERED UNDER <see cref="Speech.SpokenPaths.NotSpokenOutput"/> for the same reason - its output is read
/// by code from end to end.
/// </summary>
public static class TurnVerdictPrompt
{
    /// <summary>The line that closes the untrusted screen block. Kept because the tests that prove a screen cannot
    /// forge the end of the screen block read it from here.</summary>
    internal const string EndOfScreenMarker = "=== END OF THE SCREEN ===";

    /// <summary>The prompt for one stop.</summary>
    /// <param name="package">The stop being judged.</param>
    /// <exception cref="InvalidOperationException">The contract's prompt no longer closes the untrusted screen
    /// block. A prompt whose screen block does not end is one a drawn screen can write instructions into.</exception>
    public static string BuildVerdictPrompt(TurnVerdictPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var prompt = TurnVerdictContract.BuildPrompt(package);
        if (!prompt.Contains(EndOfScreenMarker, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The turn-verdict prompt has no '{EndOfScreenMarker}' line, so the untrusted screen block is not "
                + "closed and text drawn on a screen could read as instructions. The template is "
                + TurnVerdictContract.PromptResourcePath + ".");

        return prompt;
    }
}
