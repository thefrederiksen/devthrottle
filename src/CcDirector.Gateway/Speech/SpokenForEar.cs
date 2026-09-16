using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Speech;

/// <summary>
/// THE LAST THING DONE TO A NARRATION BEFORE IT IS SPOKEN: name the session from the record, and take
/// out what no listener can use.
///
/// Both halves exist because the judge prompt already asks for them and the model does not deliver
/// them. Audited across the owner's twenty sessions on 2026-09-16, against a prompt that already said
/// "OPEN WITH THE SESSION TITLE", "NEVER VOICE AN IDENTIFIER OR A HASH" and "REFERENCE NUMBERS ARE NOT
/// SPOKEN NUMBERS":
///
///   - a session named "Wingman Inspector - Cockpit tab" was narrated as "Wingman Inspector mobile" -
///     not mangled punctuation, a DIFFERENT NAME. The listener identifies the session by that name and
///     cannot see a screen, so a wrong one is worse than none at all.
///   - "the release at 2bfaa2a24" was spoken aloud, and "issue 2905".
///
/// A rule the model breaks is a rule the code has to keep. These are deterministic, so they hold for
/// every model, every prompt revision and every stop - including the ones nobody is listening to yet.
///
/// SCOPE, and it is deliberately narrow: this touches the SPOKEN string only. The verdict's label,
/// summary, evidence and options are untouched, because they are read on a screen where an identifier
/// is useful and can be copied. Nothing here is a transcription path - it turns text into speech, not
/// speech into text - so the verbatim rule that binds dictation does not apply, and could not be met
/// here in any case, since every word was generated in the first place.
///
/// WHAT THE FIRST VERSION GOT WRONG, because every guard below is here for a reason a review found:
/// it treated "seven or more characters drawn from digits, a-f and hyphens, containing a digit" as a
/// hash. That is also the shape of an ordinary number, a date, a version and a decimal, so it produced
/// "Pi is 3..", "Release shipped." from "Release 2026-09-15 shipped.", and "tests passed." from
/// "1000000 tests passed." It removed the substance it exists to protect.
/// </summary>
public static class SpokenForEar
{
    // ===== the pieces an identifier is recognised by =====

    /// <summary>
    /// The shape of a hash BY ITSELF, with nothing carrying it. It must hold BOTH a hex letter and a
    /// digit - not merely "a digit", which was the first version's test and the reason it ate numbers.
    ///
    /// Requiring a letter is what separates a hash from every legitimate numeric answer, because those
    /// are digits and separators only: 1000000, 2026-09-15, 3.1415926, 12345678. Requiring a digit is
    /// what keeps ordinary English out, since "defaced" and "effaced" are seven hex letters each. A real
    /// hash of seven or more characters that happens to contain no letter at all is vanishingly rare, and
    /// losing one costs a listener nothing - they could not have used it.
    /// </summary>
    private const string HashToken =
        @"(?=[0-9a-fA-F-]*[a-fA-F])(?=[0-9a-fA-F-]*\d)[0-9a-fA-F][0-9a-fA-F-]{6,}";

    /// <summary>
    /// The shape of an identifier that something is CARRYING - "at 2bfaa2a24", "commit d0630a5". The
    /// carrier is the evidence, so a digits-only run is allowed here where it is refused on its own.
    /// </summary>
    private const string CarriedToken = @"[0-9a-fA-F][0-9a-fA-F-]{6,}";

    /// <summary>
    /// Nothing word-like, and no decimal point, immediately before. Without this the pattern starts
    /// matching inside a longer token and takes half of it.
    /// </summary>
    private const string NotInsideAToken = @"(?<![\p{L}\p{N}.\-])";

    /// <summary>
    /// Nothing word-like after, no decimal fraction, and no percent sign. A trailing full stop that ENDS
    /// A SENTENCE is fine and must stay allowed - "merged at commit d0630a5." - so only a stop followed
    /// by a digit is refused, which is the decimal case.
    /// </summary>
    private const string NotPartOfANumber = @"(?![\p{L}\p{N}]|\.\d|%)";

    /// <summary>An optional pair of quotes around the identifier, so a quoted hash goes with its quotes
    /// rather than leaving an empty pair behind.</summary>
    private const string Quote = @"[""'“”‘’]?";

    // ===== the passes, in the order they must run =====

    /// <summary>
    /// A reference number: "issue 2905", "pull request 2909", "PR 2909", "run 35047040578". The NOUN is
    /// kept and the number replaced with "that", because the work is what the listener can act on and
    /// the number is what they cannot. Substituting a determiner keeps the sentence grammatical and says
    /// nothing that was not already true; deleting the digits alone leaves "doing issue first".
    ///
    /// RUNS FIRST. Its numbers are long digit runs, so a hash pass that saw them first would delete them
    /// and leave the noun stranded.
    /// </summary>
    private static readonly Regex ReferenceNumber = new(
        @"\b(?:the\s+|that\s+|an?\s+)?(issue|pull request|PR|merge request|run|ticket|bug)\s+#?\d+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// An identifier taken WITH whatever carried it, so the sentence still parses: "the release at
    /// 2bfaa2a24. Say the word" becomes "the release. Say the word".
    ///
    /// The carrier may be a preposition, a naming noun, or both. Case-insensitive and allowed at the very
    /// start of a sentence, because "At commit d0630a5, we merged." is the same sentence with a capital
    /// letter and left "At, we merged." while this required a lowercase carrier preceded by a space.
    /// </summary>
    private static readonly Regex CarriedIdentifier = new(
        @"(?:(?<=^)|(?<=\s))(?:(?:at|in|on|from|to|for)\s+(?:commit\s+|sha\s+|hash\s+|revision\s+)?|(?:commit|sha|hash|revision)\s+)"
        + Quote + CarriedToken + Quote + NotPartOfANumber,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>An identifier standing on its own, with nothing in front of it to orphan.</summary>
    private static readonly Regex BareIdentifier = new(
        NotInsideAToken + Quote + HashToken + Quote + NotPartOfANumber,
        RegexOptions.Compiled);

    /// <summary>A bare "#2905" anywhere. No noun to keep, so it goes whole.</summary>
    private static readonly Regex HashNumber = new(@"\s*#\d+\b", RegexOptions.Compiled);

    // ===== putting the sentence back together after something was taken out =====

    /// <summary>
    /// A CONJUNCTION left holding nothing. "Merged d0630a5 and 2bfaa2a2." loses both identifiers and
    /// would otherwise end "Merged and." - the dangling-word defect one level up from the one the carrier
    /// rule fixes.
    ///
    /// CONJUNCTIONS ONLY. The first version listed prepositions here too and turned "Closed #2905 and
    /// moved on." into "Closed and moved." - "on" was a particle of "moved on", not an orphan. A
    /// preposition can belong to the verb before it; "and" at the end of a clause cannot. The
    /// prepositions are handled where they are actually orphaned, by the carrier rule, which takes them
    /// away together with the identifier they introduced.
    /// </summary>
    private static readonly Regex DanglingJoiner = new(
        @"\s+\b(?:and|or)\b\s*(?=[.,;:!?]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EmptyQuotes = new(@"\s*[""'“‘]\s*[""'”’]", RegexOptions.Compiled);
    private static readonly Regex LeadingPunctuation = new(@"^[\s,;:.\-]+", RegexOptions.Compiled);
    private static readonly Regex DoubledPunctuation = new(@"([.,;:!?])\s*\1+", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunctuation = new(@"\s+([.,;:!?])", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Remove what a listener cannot use, then repair what the removal left behind.
    ///
    /// The order is the whole correctness of it: reference numbers before hashes (their numbers look like
    /// hashes), carried identifiers before bare ones (so a carrier goes with its identifier instead of
    /// being orphaned by the earlier match), and the tidy-up last, over whatever the three passes left.
    /// </summary>
    public static string ScrubIdentifiers(string? spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return string.Empty;
        var text = spoken;
        text = ReferenceNumber.Replace(text, m => $"that {Noun(m.Groups[1].Value)}");
        text = CarriedIdentifier.Replace(text, "");
        text = BareIdentifier.Replace(text, "");
        text = HashNumber.Replace(text, "");
        return Tidy(text);
    }

    private static string Tidy(string text)
    {
        text = EmptyQuotes.Replace(text, "");
        text = DanglingJoiner.Replace(text, "");
        text = SpaceBeforePunctuation.Replace(text, "$1");
        text = DoubledPunctuation.Replace(text, "$1");
        text = Whitespace.Replace(text, " ");
        text = LeadingPunctuation.Replace(text, "");
        return text.Trim();
    }

    /// <summary>"PR" is written, never said. Every other noun keeps the writer's own word, lower-cased.</summary>
    private static string Noun(string written) =>
        written.Equals("PR", StringComparison.OrdinalIgnoreCase) ? "pull request" : written.ToLowerInvariant();

    // ===== the session's own name =====

    private static readonly Regex TitleSeparators = new(@"[_\-/:|]+", RegexOptions.Compiled);
    private static readonly Regex TitleNoise = new(@"[^\p{L}\p{N} ]+", RegexOptions.Compiled);

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
    /// Put the session's own name at the front, from the record rather than from the model, and take away
    /// whatever name the model wrote for itself.
    /// </summary>
    public static string WithTitle(string? title, string? spoken)
    {
        var body = (spoken ?? "").Trim();
        var said = SpeakableTitle(title);
        if (said.Length == 0) return body;
        if (body.Length == 0) return said + ".";

        var words = said.Split(' ');
        body = DropExactTitle(body, words) ?? DropTheModelsOwnName(body, words) ?? body;
        return body.Length == 0 ? said + "." : $"{said}. {body}";
    }

    /// <summary>
    /// The body opens with THIS session's title, so the model's copy is dropped rather than said twice.
    ///
    /// THE TITLE MUST BE A STANDALONE OPENING PHRASE, and that is the fix for the first version, which
    /// joined the words with "any number of non-letters" on both sides and had no boundary at the end. It
    /// therefore accepted a title as a PREFIX of the body's first word and cut it in half: title "Dev"
    /// against "Development is complete." produced "Dev. elopment is complete.", and title "Go" against
    /// "Go-live failed." produced "Go. live failed." - reachable for any one-word or common-word title.
    ///
    /// So the match must END A SENTENCE - it is followed by sentence punctuation, or by nothing at all.
    /// WHITESPACE IS NOT ENOUGH, and that was the second thing wrong here: a body whose first sentence
    /// legitimately opens with the session's name had the name cut off the front of it, so "Dev Manager
    /// finished the report." was spoken "Dev Manager. finished the report." The model writing the title
    /// writes it as its own clause and punctuates it; a sentence that merely starts with the same words
    /// runs straight on, and that difference is the only reliable way to tell them apart.
    ///
    /// A hyphen with a letter after it is not sentence punctuation either, which is what saves "Go-live".
    /// </summary>
    private static string? DropExactTitle(string body, string[] words)
    {
        var pattern = @"^\s*" + string.Join(@"[^\p{L}\p{N}]{0,4}", words.Select(Regex.Escape))
                    + @"\s*(?:[.!?;:,–—]+\s*|$)";
        var m = Regex.Match(body, pattern, RegexOptions.IgnoreCase);
        if (!m.Success || m.Length == 0) return null;
        return body[m.Length..].TrimStart();
    }

    /// <summary>
    /// The body opens with a name the model INVENTED for this session - the defect this whole file exists
    /// for. "Wingman Inspector - Cockpit tab" was narrated "Wingman Inspector mobile. Merge complete...",
    /// and prepending the real title alone left the listener hearing BOTH names, one of them false.
    ///
    /// A HEURISTIC, and stated as one. The opening sentence is treated as the model's attempt at the title
    /// when it starts with the same words the title does and is no longer than a title plausibly is. Two
    /// leading words must agree (one, when the title is a single word), and the sentence may run to at most
    /// ONE word beyond the title's length.
    ///
    /// One word, not two, and that bound was found by a test rather than chosen. At two, a one-word title
    /// licensed dropping a three-word sentence, so "Go-live failed." vanished entirely under the title
    /// "Go" and the listener was left with the session's name and silence. The looser the title, the less
    /// it can be allowed to claim.
    ///
    /// WHAT IT WILL NOT TOUCH, which is the half that matters: a real opening sentence that merely begins
    /// with the session's name survives, because it is longer than the bound. "Dev Manager finished the
    /// report." keeps every word. The cost of the heuristic being wrong is one short opening clause; the
    /// cost of not having it is a false name spoken as fact, every time, which is what was shipping.
    /// </summary>
    private static string? DropTheModelsOwnName(string body, string[] words)
    {
        var end = body.IndexOfAny(new[] { '.', '!', '?', ';' });
        if (end <= 0) return null;
        var first = body[..end];
        var firstWords = SpeakableTitle(first).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (firstWords.Length == 0) return null;
        if (firstWords.Length > words.Length + 1) return null;

        var needed = Math.Min(words.Length == 1 ? 1 : 2, Math.Min(words.Length, firstWords.Length));
        for (var i = 0; i < needed; i++)
            if (!firstWords[i].Equals(words[i], StringComparison.OrdinalIgnoreCase)) return null;

        return body[(end + 1)..].TrimStart();
    }

    /// <summary>
    /// The whole assembly, in the order the ear needs it: scrub the model's words, then name the session.
    /// Scrubbing first, so a title that legitimately contains digits is never mistaken for a reference
    /// number by a pattern running over the joined string.
    /// </summary>
    public static string Assemble(string? title, string? spoken)
        => WithTitle(title, ScrubIdentifiers(spoken));
}
