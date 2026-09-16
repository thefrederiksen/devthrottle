using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Speech;

/// <summary>
/// SAY WHICH SESSION IS TALKING. NOTHING ELSE.
///
/// The listener usually cannot see a screen, so the first thing a narration must tell them is which
/// session it is. The judge prompt asked the model for that and the model got it wrong: on 2026-09-16 a
/// session named "Wingman Inspector - Cockpit tab" was narrated as "Wingman Inspector mobile" - not
/// mangled punctuation, a different name. A name is a fact on a record, so it is read off the record.
///
/// THIS STEP NEVER EDITS THE NARRATION. Not a word removed, not a word changed. That is the whole
/// design, and it is what four review rounds cost to arrive at.
///
/// IT IS NOT THE ONLY STEP, AND SAYING OTHERWISE WAS AN OVERCLAIM. Both callers run the narration
/// through <see cref="SpeechContract.Finish"/> first, which strips Markdown for the ear and drops fenced
/// code blocks WHOLE - so a narration whose answer sits in a fenced block reaches this method already
/// missing it. That pass is older than this file and is not changed here; what is corrected is the claim,
/// which twice said "nothing else is changed" about a pipeline that changes something else immediately
/// upstream. A comment promising a guarantee the code does not hold is how a reader comes to trust the
/// wrong thing. The fenced-block loss is filed separately as its own defect.
///
/// This file began by also stripping what the model should not have said - hashes and issue numbers
/// read aloud, and names it invented - because the prompt forbids those and the model does them anyway.
/// Rounds one to three found fourteen defects in that stripping, every one real content deleted for
/// sitting where an identifier might sit ("1000000 tests passed." losing its number, "Deployed on
/// 2026-09-15." losing its date, "The condition uses AND, not XOR." losing its subject). The stripping
/// was deleted.
///
/// Round four then found that the ONE remaining edit - dropping a copy of the title the model had
/// written itself - was still eating body text: "Dev, Manager found four defects." lost a word,
/// "Dev-short for development-is complete." lost a clause. So that went too. A model that writes the
/// title now simply says it twice, which is mildly clumsy and takes nothing away from the listener.
///
/// The pattern across all four rounds is one finding: a rule sharp enough to catch the bad case cuts
/// good ones, because prose does not carry the evidence to tell them apart. Everything that needs that
/// evidence lives in the prompt, which is where a judgement belongs. What is left here needs none: a
/// name, from a record, in front.
/// </summary>
public static class SpokenForEar
{
    /// <summary>The characters that JOIN words in a session name and are read as pauses, not voiced.</summary>
    private static readonly Regex Separators = new(@"[_\-/:|]+", RegexOptions.Compiled);

    /// <summary>Any run of whitespace, INCLUDING A SINGLE ONE. It matched only runs of two or more, so a
    /// lone tab inside a name survived into the speech string as a tab.</summary>
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// The session's name as it should be SAID: the separators that join its words become spaces, so
    /// "devthrottle_internal - parallel" is spoken "devthrottle internal parallel" instead of voicing the
    /// punctuation.
    ///
    /// NOTHING ELSE IS REMOVED, and the first version's attempt to remove more is why that is spelled out.
    /// It kept only letters, numbers and spaces, which silently mangled real names: combining marks are
    /// neither letters nor numbers, so a Devanagari name lost its vowel signs and a decomposed "equipe"
    /// split in half. A session called "C++" became "C", and one called "+++" became nothing at all, which
    /// dropped the name this method exists to say. A name is the user's word; it is repunctuated for the
    /// ear and never rewritten.
    /// </summary>
    public static string SpeakableTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var said = Whitespace.Replace(Separators.Replace(title, " "), " ").Trim();
        // A NAME MADE ENTIRELY OF SEPARATORS SURVIVES AS ITSELF. Turning "_-/:|" into pauses leaves
        // nothing, and this method would then return empty and the session would go unnamed - the exact
        // failure it exists to prevent, for a name the product's own validation accepts. When the
        // repunctuation would erase the name, the name is said as written instead.
        return said.Length == 0 ? Whitespace.Replace(title, " ").Trim() : said;
    }

    /// <summary>
    /// The session's name, then the narration exactly as the model wrote it.
    ///
    /// The narration's WORDS are never touched. Leading and trailing whitespace is normalised, which is
    /// the only difference between the body handed in and the body spoken - said precisely here because
    /// an earlier version of this comment claimed byte-identity that the trim made false.
    ///
    /// A blank title yields the narration alone rather than a stray full stop: an unnamed session is a
    /// smaller problem than a narration that opens on a noise.
    /// </summary>
    public static string WithTitle(string? title, string? spoken)
    {
        var body = (spoken ?? "").Trim();
        var said = SpeakableTitle(title);
        if (said.Length == 0) return body;
        return body.Length == 0 ? said + "." : $"{said}. {body}";
    }

    /// <summary>The narration as it goes to synthesis. A named seam, so a caller never has to know
    /// whether naming the session is one step or several.</summary>
    public static string Assemble(string? title, string? spoken) => WithTitle(title, spoken);
}
