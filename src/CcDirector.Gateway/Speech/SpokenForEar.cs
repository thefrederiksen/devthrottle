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
/// </summary>
public static class SpokenForEar
{
    /// <summary>
    /// The identifier itself. It must contain at least one DIGIT: without that guard the pattern eats
    /// ordinary English spelled from the letters a-f, and "defaced" and "effaced" are seven hex
    /// characters each. A real hash of seven or more characters with no digit at all is vanishingly
    /// rare, and losing one costs a listener nothing - they could not have used it.
    /// </summary>
    private const string IdBody = @"(?=[0-9a-fA-F-]{7,}\b)(?=[0-9a-fA-F-]*\d)[0-9a-fA-F][0-9a-fA-F-]{6,}\b";

    /// <summary>
    /// An identifier with something carrying it, taken WITH its carrier so the sentence still parses:
    /// "the release at 2bfaa2a24. Say the word" becomes "the release. Say the word".
    ///
    /// The carrier may be a preposition, a naming noun, or BOTH - "at 2bfaa2a2", "commit d0630a5",
    /// "at commit d0630a5". The first version allowed only one of the two and left "Merged at." on the
    /// third shape: the same dangling-word defect it exists to prevent, one word further along.
    /// </summary>
    private static readonly Regex CarriedIdentifier = new(
        @"\s+(?:(?:at|in|on|from|to|for)\s+(?:commit\s+|sha\s+|hash\s+|revision\s+)?|(?:commit|sha|hash|revision)\s+)" + IdBody,
        RegexOptions.Compiled);

    /// <summary>An identifier standing on its own, with nothing in front of it to orphan.</summary>
    private static readonly Regex BareIdentifier = new(@"\b" + IdBody, RegexOptions.Compiled);

    /// <summary>
    /// A reference number: "issue 2905", "pull request 2909", "PR 2909", "run 35047040578". The NOUN is
    /// kept and the number replaced with "that", because the work is what the listener can act on and
    /// the number is what they cannot. Substituting a determiner keeps the sentence grammatical and says
    /// nothing that was not already true; deleting the digits alone leaves "doing issue first".
    /// </summary>
    private static readonly Regex ReferenceNumber = new(
        @"\b(?:the\s+|that\s+|an?\s+)?(issue|pull request|PR|merge request|run|ticket|bug)\s+#?\d+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A bare "#2905" anywhere. No noun to keep, so it goes whole.</summary>
    private static readonly Regex HashNumber = new(@"\s*#\d+\b", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunctuation = new(@"\s+([.,;:!?])", RegexOptions.Compiled);

    /// <summary>
    /// Remove what a listener cannot use. Order matters: the carried form runs before the bare one, so a
    /// carrier goes with its identifier instead of being orphaned by the earlier match.
    /// </summary>
    public static string ScrubIdentifiers(string? spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return string.Empty;
        var text = spoken;
        text = CarriedIdentifier.Replace(text, "");
        text = BareIdentifier.Replace(text, "");
        text = ReferenceNumber.Replace(text, m => $"that {Noun(m.Groups[1].Value)}");
        text = HashNumber.Replace(text, "");
        text = SpaceBeforePunctuation.Replace(text, "$1");
        text = Whitespace.Replace(text, " ");
        return text.Trim();
    }

    /// <summary>"PR" is written, never said. Every other noun keeps the writer's own word, lower-cased.</summary>
    private static string Noun(string written) =>
        written.Equals("PR", StringComparison.OrdinalIgnoreCase) ? "pull request" : written.ToLowerInvariant();

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
    /// Put the session's own name at the front, from the record rather than from the model.
    ///
    /// If the narration ALREADY opens with that name - an older verdict judged while the prompt still
    /// asked for it, or a model that writes one anyway - the model's copy is dropped rather than said
    /// twice. A model that opened with a DIFFERENT name is not matched, and its wrong name survives in
    /// the body; the listener still hears the right one first, which is the failure this was built for.
    /// </summary>
    public static string WithTitle(string? title, string? spoken)
    {
        var body = (spoken ?? "").Trim();
        var said = SpeakableTitle(title);
        if (said.Length == 0) return body;
        if (body.Length == 0) return said + ".";

        // MATCHED ON WORDS, NOT ON LENGTH. The body carries the title in its WRITTEN form while the spoken
        // form has had its separators dropped, so the two differ in length whenever one was - and slicing
        // the body by the spoken length cut mid-word, leaving "mindzieWeb bpm video. eo. Send the video...".
        // Found by the test that quotes the real narration rather than an invented one.
        var words = said.Split(' ').Select(Regex.Escape);
        var opening = new Regex(@"^\s*" + string.Join(@"[^\p{L}\p{N}]*", words) + @"[^\p{L}\p{N}]*",
            RegexOptions.IgnoreCase);
        var m = opening.Match(body);
        if (m.Success && m.Length > 0)
        {
            body = body[m.Length..].TrimStart();
            if (body.Length == 0) return said + ".";
        }
        return $"{said}. {body}";
    }

    /// <summary>
    /// The whole assembly, in the order the ear needs it: scrub the model's words, then name the session.
    /// Scrubbing first, so a title that legitimately contains digits is never mistaken for a reference
    /// number by a pattern running over the joined string.
    /// </summary>
    public static string Assemble(string? title, string? spoken)
        => WithTitle(title, ScrubIdentifiers(spoken));
}
