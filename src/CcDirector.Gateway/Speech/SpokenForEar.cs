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

    private const string Start = @"(?:(?<=^)|(?<=\s))";
    private const string Preposition = @"(?:at|in|on|from|to|for)\s+";
    private const string NamingNoun = @"(?:commit|sha|hash|revision)\s+";

    /// <summary>
    /// A value something NAMES as an identifier - "commit 1234567", "at revision d0630a5". The naming noun
    /// is strong evidence, so a digits-only run is taken here: a thing called a commit is a commit.
    /// </summary>
    private static readonly Regex NamedIdentifier = new(
        Start + @"(?:" + Preposition + @")?" + NamingNoun + Quote + CarriedToken + Quote + NotPartOfANumber,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// An identifier introduced by a bare preposition - "at 2bfaa2a24". The preposition is taken along so
    /// the sentence still parses, but it is WEAK EVIDENCE and the token must therefore look like a hash on
    /// its own.
    ///
    /// A GENERAL PREPOSITION IS NOT EVIDENCE THAT A NUMBER IS AN IDENTIFIER, and treating it as one
    /// reopened the very defect the letter-and-digit rule had just closed. "on", "from", "to" and "for"
    /// introduce dates, durations and quantities constantly, so allowing a digits-only run behind them
    /// produced "Deployed." from "Deployed on 2026-09-15.", "It ran seconds." from "It ran for 12345678
    /// seconds.", and "The population grew." from "The population grew from 1000000 to 2000000."
    /// </summary>
    private static readonly Regex PrepositionedIdentifier = new(
        Start + Preposition + Quote + HashToken + Quote + NotPartOfANumber,
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
    /// <remarks>
    /// SENTENCE-FINAL ONLY, and NOT before a comma. A conjunction followed by a comma is routinely a real
    /// one introducing a parenthetical - "The list is long and, frankly, unread." lost its "and" while a
    /// comma counted. An "and" with nothing at all after it but the end of a sentence cannot be doing that
    /// job, and is the only shape a removal actually orphans.
    /// </remarks>
    private static readonly Regex DanglingJoiner = new(
        @"\s+\b(?:and|or)\b\s*(?=[.;:!?]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A comma or semicolon left leaning against a full stop by a removal in between.</summary>
    private static readonly Regex StrandedComma = new(@"\s*[,;:]+\s*(?=[.!?])", RegexOptions.Compiled);

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
        text = ReferenceNumber.Replace(text, m => Determiner(text, m.Index) + " " + Noun(m.Groups[1].Value));

        // Whether anything was actually CUT OUT, which is what licenses the repair pass below. The
        // reference-number rule is deliberately not counted: it substitutes rather than removes, so it
        // leaves no hole and orphans nothing.
        var before = text;
        text = NamedIdentifier.Replace(text, "");
        text = PrepositionedIdentifier.Replace(text, "");
        text = BareIdentifier.Replace(text, "");
        text = HashNumber.Replace(text, "");
        var removedSomething = !ReferenceEquals(before, text) && before != text;

        return Tidy(text, removedSomething);
    }

    /// <summary>
    /// Put the sentence back together after something was taken out of it.
    ///
    /// THE REPAIRS ONLY RUN IF THERE WAS A REMOVAL, and that gate is the fix for a whole class of damage
    /// rather than for one case of it. The joiner sweep ran unconditionally over every narration, so
    /// "The condition uses AND, not XOR." became "The condition uses, not XOR." and "The operator is OR."
    /// became "The operator is." - words that are the SUBJECT of the sentence, deleted from text that
    /// contained no identifier at all. Position alone is not evidence that a word is an orphan; a removal
    /// next to it is.
    ///
    /// Whitespace and punctuation spacing are always tidied - those are cosmetic and cannot lose a word.
    /// </summary>
    private static string Tidy(string text, bool removedSomething)
    {
        if (removedSomething)
        {
            text = EmptyQuotes.Replace(text, "");
            text = DanglingJoiner.Replace(text, "");
            text = StrandedComma.Replace(text, "");
            text = DoubledPunctuation.Replace(text, "$1");
        }
        text = SpaceBeforePunctuation.Replace(text, "$1");
        text = Whitespace.Replace(text, " ");
        if (removedSomething) text = LeadingPunctuation.Replace(text, "");
        return text.Trim();
    }

    /// <summary>
    /// "That", capitalised when it begins a sentence. The substitution replaces the FIRST word of a
    /// sentence whenever the reference opened one - "Run 35047040578 failed." - and a narration that
    /// starts in lower case reads as a fragment to anyone seeing it written and is a small wrongness in
    /// the mouth of a speech engine.
    /// </summary>
    private static string Determiner(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            return text[i] is '.' or '!' or '?' or ':' or ';' ? "That" : "that";
        }
        return "That";
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
    /// A HEURISTIC, and stated as one. The opening is treated as the model's attempt at the title when it
    /// agrees with the title on at least two words AND IS STRICTLY SHORTER THAN THE TITLE IN WORDS.
    ///
    /// SHORTER IS THE WHOLE TEST, and it replaced two successive bounds that were both wrong. A model
    /// getting the title wrong DROPS or SUBSTITUTES words from a name it was given - "Wingman Inspector
    /// Cockpit tab" came back as "Wingman Inspector mobile", three words for four. A real sentence ADDS
    /// them: it needs a verb, and an object, and it is therefore longer than the name it opens with.
    ///
    /// The earlier bounds counted upward from the title's length and so licensed exactly the sentences a
    /// verb makes. At two words over, "Go-live failed." vanished under the title "Go". At one word over,
    /// the inspector found "Go now! The deploy needs approval." losing its instruction, "Dev Manager
    /// failed. Retry queued." losing its failure, and the same shape in French. Counting downward cannot
    /// reach any of them.
    ///
    /// It follows that a ONE-WORD title never fires this rule - nothing is strictly shorter than one word
    /// - and that is correct rather than a gap: the only title attempt a one-word title can produce is
    /// that word by itself, which <see cref="DropExactTitle"/> already removes.
    ///
    /// WHAT IT WILL NOT TOUCH, which is the half that matters: a real opening sentence that merely begins
    /// with the session's name survives, because it is longer than the bound. "Dev Manager finished the
    /// report." keeps every word. The cost of the heuristic being wrong is one short opening clause; the
    /// cost of not having it is a false name spoken as fact, every time, which is what was shipping.
    /// </summary>
    private static string? DropTheModelsOwnName(string body, string[] words)
    {
        // A colon ends it too. A title written as a LABEL - "Wingman Inspector mobile: Merge complete" -
        // is a natural thing for a model to produce, and without the colon here the matcher read the whole
        // line as one over-long name and kept the false one.
        var end = body.IndexOfAny(new[] { '.', '!', '?', ';', ':' });
        if (end <= 0) return null;
        var firstWords = SpeakableTitle(body[..end]).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Strictly shorter than the title, and agreeing on two words. See the summary for why the
        // comparison runs this way round.
        if (firstWords.Length < 2 || firstWords.Length >= words.Length) return null;
        for (var i = 0; i < 2; i++)
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
