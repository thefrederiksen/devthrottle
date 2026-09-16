using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Speech;

/// <summary>
/// NAME THE SESSION, FROM THE RECORD, AND CHANGE NOTHING ELSE.
///
/// The listener usually cannot see a screen, so the first thing a narration must tell them is WHICH
/// session is talking. The judge prompt asked the model for that and the model got it wrong: on
/// 2026-09-16 a session named "Wingman Inspector - Cockpit tab" was narrated as "Wingman Inspector
/// mobile" - not mangled punctuation, a different name. A name is a fact on a record, so it is read
/// off the record.
///
/// WHAT THIS DELIBERATELY DOES NOT DO, and why the file is this small.
///
/// It was written to also STRIP what the model should not have said - commit hashes and issue numbers
/// read aloud, and any wrong name the model wrote for itself - because the prompt forbids those too and
/// the model does them anyway. Three independent review rounds found fourteen defects in that stripping,
/// every one of them the same mistake in a new place: real content deleted because it sat where an
/// identifier might sit.
///
///   "1000000 tests passed."             -> "tests passed."
///   "Deployed on 2026-09-15."           -> "Deployed."
///   "The condition uses AND, not XOR."  -> "The condition uses, not XOR."
///   "Go now! The deploy needs approval."-> "Go."
///   "Dev Manager found four defects."   -> "" (the whole result, under a long session title)
///
/// Each was fixed and the next round found more, including a valid removal in one sentence licensing
/// damage in a different one. The lesson is not that the patterns needed another pass: it is that
/// free-form prose does not carry the evidence needed to tell an identifier from a number, or an
/// invented name from a sentence, and every rule sharp enough to catch the bad case cuts good ones.
///
/// THE TRADE, stated plainly, because it is the reason for the deletion. Hearing a commit hash read
/// aloud is irritating. Losing the number that was the answer, or a whole sentence of a result, is
/// being told something false about your own work. The first is a cost worth paying to avoid the
/// second, and the two are not close.
///
/// So the stripping lives in the prompt, where it belongs - the SPOKEN section forbids voicing an
/// identifier or a reference number - and the code does only what a record can prove.
/// </summary>
public static class SpokenForEar
{
    private static readonly Regex TitleSeparators = new(@"[_\-/:|]+", RegexOptions.Compiled);
    private static readonly Regex TitleNoise = new(@"[^\p{L}\p{N} ]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// The session's name as it should be SAID: separators become spaces, so "devthrottle_internal -
    /// parallel" is spoken "devthrottle internal parallel" rather than voicing the punctuation. The words
    /// themselves are never changed - that is the whole point of taking this away from the model.
    /// </summary>
    public static string SpeakableTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var t = TitleSeparators.Replace(title, " ");
        t = TitleNoise.Replace(t, " ");
        return Whitespace.Replace(t, " ").Trim();
    }

    /// <summary>
    /// Put the session's own name at the front.
    ///
    /// The ONLY thing ever taken out of the narration is a copy of THIS session's title that the model
    /// wrote as its own opening clause - an older verdict judged while the prompt still asked for one, or
    /// a model that writes one anyway. Without that, such a narration says the name twice.
    ///
    /// IT MUST BE THE WHOLE TITLE AND IT MUST END A CLAUSE. Both conditions were learned from a review
    /// that found this rule cutting words in half. Matching the title as a mere PREFIX turned
    /// "Development is complete." into "elopment is complete." under the title "Dev", and accepting
    /// whitespace as the boundary took the name off the front of the real sentence "Dev Manager finished
    /// the report." A model writing the title punctuates it as its own clause; a sentence that merely
    /// begins with the same words runs straight on, and that is the only reliable difference between them.
    ///
    /// A name the model INVENTED is left where it is. It is wrong and it is not ours to guess at: every
    /// rule tried for spotting one also deleted real opening sentences. The listener hears the correct
    /// name first, from the record, which is what they needed.
    /// </summary>
    public static string WithTitle(string? title, string? spoken)
    {
        var body = (spoken ?? "").Trim();
        var said = SpeakableTitle(title);
        if (said.Length == 0) return body;
        if (body.Length == 0) return said + ".";

        var pattern = @"^\s*"
                    + string.Join(@"[^\p{L}\p{N}]{0,4}", said.Split(' ').Select(Regex.Escape))
                    + @"\s*(?:[.!?;:,–—]+\s*|$)";
        var m = Regex.Match(body, pattern, RegexOptions.IgnoreCase);
        if (m.Success && m.Length > 0)
        {
            body = body[m.Length..].TrimStart();
            if (body.Length == 0) return said + ".";
        }
        return $"{said}. {body}";
    }

    /// <summary>The narration as it goes to synthesis. One step, kept as a named seam so a caller never
    /// has to know whether naming the session is one operation or several.</summary>
    public static string Assemble(string? title, string? spoken) => WithTitle(title, spoken);
}
