using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Mentor;

/// <summary>A written slot is refused; <see cref="Slot"/> is its dotted path and <see cref="Reason"/> says why.</summary>
public sealed class SlotError : Exception
{
    public string Slot { get; }
    public string Reason { get; }

    public SlotError(string slot, string reason) : base(slot + ": " + reason)
    {
        Slot = slot;
        Reason = reason;
    }
}

/// <summary>One resolved citation inside a field: the span it occupies (session name to the end of the fragment's
/// brackets), the name form <c>cite</c> returned, the minute, the id8 it resolved to and the verified fragment
/// (null when the citation carries none).</summary>
public sealed class Citation
{
    public int Start { get; }
    public int End { get; }
    public string Name { get; }
    public string At { get; }
    public string Id8 { get; }
    public string Text { get; }
    public string? Fragment { get; }

    public Citation(int start, int end, string name, string at, string id8, string citation, string? fragment)
    {
        Start = start;
        End = end;
        Name = name;
        At = at;
        Id8 = id8;
        Text = citation;
        Fragment = fragment;
    }
}

/// <summary>
/// The port of <c>mentor_tools/slots.py</c>: the WRITTEN slots of the report - their schema and their validators.
///
/// The report is a fixed structure of slots. A slot is RENDERED by <see cref="Render"/> from the tools' own answers,
/// or WRITTEN by the agent as a small structured answer that this class validates on arrival. The written slots are
/// one JSON object of exactly this shape:
///
/// <code>
///     {
///       "recommendations": [ {"title", "saw", "cost", "try"}, x3 ],
///       "went_well": {"text"},
///       "prompting": {
///         "specific_target":        {"level", "observation", "step"},
///         "check_agent_can_run":    {...}, "one_task_per_prompt": {...}, "corrections_carry_reason": {...},
///         "not_re_explaining":      {...}, "session_hygiene": {...}
///       }
///     }
/// </code>
///
/// Every refusal is a <see cref="SlotError"/> whose slot is the dotted path of the field that failed
/// (recommendations[2].saw, prompting.session_hygiene.level, went_well.text): a refusal always names the slot, so a
/// writer retries that one slot alone. Every refusal message is the reference's, character for character.
///
/// The rules are the report's own rules, applied per field so the refusal names the field: every string is ASCII
/// with no heading mark and no line break; a double quotation mark sits only inside a citation of the checker's
/// form (the spans come from <see cref="ReportCheck.QuotedSpans"/>, the same extraction the checker runs); every
/// citation RESOLVES THROUGH THE TOOLS - the session name is looked up in the run's session_index and <c>cite</c> is
/// called with the FULL session id of every index row carrying that name, the citation being valid when exactly one
/// of them has a prompt at that minute, and every quoted fragment is passed to <c>verify_quote</c> and must be
/// answered true; the calls land in the run's tool log like any other call. The three recommendations obey the
/// figures rule per field; no field names a provider or a model; the level word is one of the judged words when the
/// week's human prompt count is ten or more, else exactly the too-few phrase - the count comes from week_overview,
/// never from the slot.
///
/// Which fields are BOUND to the log: <c>saw</c>, <c>went_well.text</c> and every <c>observation</c> carry citations
/// and quotations, resolved and verified through the tools; <c>cost</c> carries at least one resolved citation.
/// <c>try</c> and <c>step</c> are advice and <c>level</c> is a judgement: they are NOT bound to the log, and the
/// report says so in one fixed sentence of "How this was made" (<see cref="Render.BoundAndJudged"/>).
///
/// The validator runs over a PARSED object (dictionaries, lists, strings, numbers, booleans, null - the value model
/// <see cref="JsonValues"/> reads); the file is read in one place, <see cref="ReadFile"/>, so the loop can hand the
/// same shape in memory.
/// </summary>
public static class Slots
{
    public const string SlotsFile = "slots.json";
    public const int RecommendationCount = 3;
    public static readonly string[] RecommendationKeys = { "title", "saw", "cost", "try" };
    public const int TitleMinWords = 2;
    public const int TitleMaxWords = 8;
    public const int SawMinCitations = 2;
    public const int SawMaxWords = 60;
    public const int CostMaxWords = 40;
    public const int CostMaxSentences = 2;
    public const int CostMinCitations = 1;
    public const int TryMaxWords = 30;
    public const int WentWellMaxWords = 60;
    public const string NothingQualified = "Nothing in this week's prompts qualified.";
    public const int ObservationMinCitations = 2;
    public const int ObservationMaxWords = 60;
    public const int StepMaxWords = 25;
    public static readonly string[] PromptingKeys = { "level", "observation", "step" };
    public static readonly string[] TopKeys = { "recommendations", "went_well", "prompting" };

    /// <summary>The six dimensions in the rubric's order, each with the heading the contract fixes for it.</summary>
    public static readonly (string Key, string Heading)[] Dimensions =
    {
        ("specific_target", "### Specific target"),
        ("check_agent_can_run", "### A check the agent can run"),
        ("one_task_per_prompt", "### One task per prompt"),
        ("corrections_carry_reason", "### Corrections that carry the reason"),
        ("not_re_explaining", "### Not re-explaining"),
        ("session_hygiene", "### Session hygiene"),
    };

    public static readonly string[] DimensionKeys = Dimensions.Select(d => d.Key).ToArray();

    static Slots()
    {
        if (!Dimensions.Select(d => d.Heading).SequenceEqual(Contract.PromptingHeadings, StringComparer.Ordinal))
            throw new InvalidOperationException("Slots.Dimensions no longer matches Contract.PromptingHeadings; fix the table.");
        if (!DimensionKeys.SequenceEqual(ToolSurface.DimensionKeys, StringComparer.Ordinal))
            throw new InvalidOperationException("Slots.Dimensions no longer matches ToolSurface.DimensionKeys; fix the two tables.");
    }

    private static readonly Regex SentenceSplitRe = new(@"(?<=[.!?])" + PyText.SpaceClass + "+", RegexOptions.CultureInvariant);
    public const string SentenceEnd = ".?!";
    public const string CitationPlaceholder = "CITATION";
    private static readonly Regex DigitRe = new(@"\d", RegexOptions.CultureInvariant);

    // ------------------------------------------------------------------ reading the file

    /// <summary>The written slots from <c>&lt;run&gt;/slots.json</c>, ASCII JSON, as a parsed value; a missing or
    /// unreadable file is a <see cref="SlotError"/> on the slot "slots" carrying the reference's reason.</summary>
    public static object? ReadFile(string runDir)
    {
        var path = Path.Combine(runDir, SlotsFile);
        if (!File.Exists(path))
            throw new SlotError("slots", "no " + SlotsFile + " at " + path);
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] > 127)
                throw new SlotError("slots", SlotsFile + " is not ASCII JSON: 'ascii' codec can't decode byte 0x"
                    + bytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture) + " in position " + i + ": ordinal not in range(128)");
        }
        try
        {
            return JsonValues.Parse(Encoding.ASCII.GetString(bytes));
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new SlotError("slots", SlotsFile + " is not ASCII JSON: " + error.Message);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Python's <c>type(value).__name__</c> over the parsed value model.</summary>
    public static string TypeName(object? value) => value switch
    {
        null => "NoneType",
        bool => "bool",
        long or int => "int",
        double => "float",
        string => "str",
        List<object?> => "list",
        Dictionary<string, object?> => "dict",
        _ => value.GetType().Name,
    };

    public static int CountWords(string text) => PyText.CountWords(text);

    /// <summary>The sentences of a field once every citation is replaced by one plain word, so a full stop inside a
    /// quoted fragment or a session name is not a sentence end. Also whether the field ends with a sentence mark.</summary>
    public static (int Count, bool EndsWell) CountSentences(string text, IReadOnlyList<Citation> citations)
    {
        var masked = text;
        foreach (var citation in citations.OrderByDescending(c => c.Start))
            masked = masked.Substring(0, citation.Start) + CitationPlaceholder + masked.Substring(citation.End);
        var stripped = PyText.Strip(masked);
        var pieces = SentenceSplitRe.Split(stripped).Where(piece => PyText.Strip(piece).Length > 0).ToList();
        var endsWell = stripped.Length > 0 && SentenceEnd.Contains(stripped[^1]);
        return (pieces.Count, endsWell);
    }

    /// <summary>Resolves the citations of a field through the surface, using the run's session_index.</summary>
    public sealed class Resolver
    {
        public ToolSurface Surface { get; }
        public Dictionary<string, List<string>> IdsByName { get; } = new(StringComparer.Ordinal);

        public Resolver(ToolSurface surface, IReadOnlyList<Dictionary<string, object?>> index)
        {
            Surface = surface;
            foreach (var row in index)
            {
                var name = (string)row["name"]!;
                var id = (string)row["id"]!;
                if (!IdsByName.TryGetValue(name, out var ids)) IdsByName[name] = ids = new List<string>();
                ids.Add(id);
            }
        }

        /// <summary>The known session names <paramref name="before"/> ends with, longest first, a match starting at the
        /// text's start or after a non-alphanumeric character - the checker's session_reference rule, over the index's names.</summary>
        public List<string> Candidates(string before)
        {
            var found = new List<string>();
            foreach (var name in IdsByName.Keys)
            {
                if (name.Length == 0 || !before.EndsWith(name, StringComparison.Ordinal)) continue;
                var start = before.Length - name.Length;
                if (start > 0 && PyText.IsAlnum(before[start - 1])) continue;
                found.Add(name);
            }
            return found.OrderByDescending(n => n.Length).ToList();
        }

        /// <summary>(reference, id8, citation string) for a name at a minute, or null when no session of that name has a
        /// prompt there; <see cref="SlotError"/> when two of them do. <c>cite</c> is addressed with the FULL session id of
        /// each index row carrying the name, never with the name and never with the id8: session_index lists the
        /// sessions with a human prompt in the week - the checker's universe, in which a name can be unique - while
        /// the surface resolves a name over the wider universe that includes row-only sessions, and an id8 can collide
        /// too. The full id is the one address that is never ambiguous, and the citation string still comes back as
        /// the name form the checker reads; the slot's text must equal it.</summary>
        public (string Reference, string Id8, string Citation)? ResolveName(string slot, string name, string at)
        {
            var hits = new List<(string Reference, string Id8, string Citation)>();
            foreach (var sessionId in IdsByName[name])
            {
                Dictionary<string, object?> answer;
                try
                {
                    answer = Surface.Cite(sessionId, at);
                }
                catch (ToolError)
                {
                    continue;
                }
                hits.Add((sessionId, (string)answer["session_id8"]!, (string)answer["citation"]!));
            }
            if (hits.Count > 1)
            {
                var ids = IdsByName[name];
                throw new SlotError(slot, "the session name '" + name + "' is shared by " + ids.Count
                    + " sessions in the week (" + string.Join(", ", ids.Select(PromptsFile.Id8)) + ") and " + hits.Count
                    + " of them have a prompt at " + at + ": " + string.Join(", ", hits.Select(h => h.Id8))
                    + "; the checker cannot tell them apart, cite another minute");
            }
            return hits.Count > 0 ? hits[0] : null;
        }

        /// <summary>Every citation in the field, resolved and verified; <see cref="SlotError"/> on the first that is not.</summary>
        public List<Citation> Resolve(string slot, string text)
        {
            var citations = new List<Citation>();
            foreach (Match match in ReportCheck.MinuteRe.Matches(text))
            {
                var start = match.Index;
                var at = match.Value;
                if (start < 2 || text.Substring(start - 2, 2) != ", ")
                    throw new SlotError(slot, "the minute " + at + " is not preceded by ', <session name>'; the citation form is "
                        + ReportCheck.CitationForm);
                var before = text.Substring(0, start - 2);
                var names = Candidates(before);
                if (names.Count == 0)
                    throw new SlotError(slot, "no session name of the week precedes the minute " + at
                        + "; a citation ends with the session name exactly as session_index prints it, "
                        + "then ', ' and the minute");
                (string Reference, string Id8, string Citation)? resolved = null;
                string? name = null;
                foreach (var candidate in names)
                {
                    resolved = ResolveName(slot, candidate, at);
                    if (resolved is not null)
                    {
                        name = candidate;
                        break;
                    }
                }
                if (resolved is null)
                {
                    var ids = IdsByName[names[0]];
                    throw new SlotError(slot, "no human prompt at " + at + " in the session named '" + names[0] + "' ("
                        + string.Join(", ", ids.Select(PromptsFile.Id8)) + "); cite answers the nearest minutes");
                }
                var (reference, id8, citation) = resolved.Value;
                if (citation != name + ", " + at)
                    throw new SlotError(slot, "cite answered '" + citation + "' for the citation written as '" + name + ", "
                        + at + "'; the slot must carry the citation string cite returns");
                var spanStart = before.Length - name!.Length;
                var spanEnd = match.Index + match.Length;
                string? fragment = null;
                var tail = text.Substring(match.Index + match.Length);
                var fragmentMatch = ReportCheck.FragmentRe.Match(tail);
                if (!fragmentMatch.Success) fragmentMatch = ReportCheck.EmptyFragmentRe.Match(tail);
                if (fragmentMatch.Success)
                {
                    fragment = fragmentMatch.Groups["fragment"].Value;
                    var answer = Surface.VerifyQuote(reference, at, fragment);
                    if (answer["ok"] is not true)
                        throw new SlotError(slot, "the quoted fragment is not verified at " + citation + ": " + (string)answer["reason"]!);
                    spanEnd = match.Index + match.Length + fragmentMatch.Length;
                }
                citations.Add(new Citation(spanStart, spanEnd, name, at, id8, citation, fragment));
            }
            return citations;
        }
    }

    // ------------------------------------------------------------------ the per-field rules

    /// <summary>The field is a string, ASCII, with no heading mark and no line break.</summary>
    public static void CheckPlain(string slot, object? value)
    {
        if (value is not string text)
            throw new SlotError(slot, "must be a string, got " + TypeName(value));
        var position = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            position++;
            if (rune.Value > 127)
                throw new SlotError(slot, "is not ASCII at character " + position + " (code " + rune.Value + ")");
        }
        if (text.Contains('\n') || text.Contains('\r'))
            throw new SlotError(slot, "holds a line break; a slot is one line");
        if (text.Contains('#'))
            throw new SlotError(slot, "holds a heading mark (#); a slot carries no heading and no #");
    }

    /// <summary>No double quotation mark outside a citation's fragment; no provider or model name outside the
    /// developer's own quoted words and the cited session names.</summary>
    public static void CheckQuotesAndProviders(string slot, string text, IReadOnlyList<Citation> citations)
    {
        var masked = new StringBuilder(text);
        foreach (var (begin, end) in ReportCheck.QuotedSpans(text))
            for (var position = begin; position < end; position++) masked[position] = ' ';
        if (masked.ToString().Contains('"'))
            throw new SlotError(slot, "holds a double quotation mark outside a citation; a quotation sits only in "
                + ReportCheck.QuotedCitationForm + ", paraphrase is written without marks");
        var names = CheckCall.ProviderNamesIn(ReportCheck.MaskedQuotations(text), citations.Select(c => c.Name).ToList());
        if (names.Count > 0)
            throw new SlotError(slot, "names a provider or a model: " + string.Join(", ", names) + "; " + CheckCall.ProviderRuling);
    }

    /// <summary>The recommendation figures rule (<see cref="Contract.RecommendationFigures"/>), applied to one field.</summary>
    public static void CheckFigures(string slot, string text)
    {
        foreach (Match found in Contract.RecommendationFigureRe.Matches(text))
        {
            var token = found.Value;
            var digits = token.Replace(",", "").Replace(".", "");
            if (token.Contains('.'))
                throw new SlotError(slot, "states " + token + ", a decimal fraction; inside the three recommendations a "
                    + "number appears only if a person would say it aloud - say it in words");
            if (digits.Length > Contract.RecommendationMaxDigits)
                throw new SlotError(slot, "states " + token + ", more than " + Contract.RecommendationMaxDigits
                    + " digits; inside the three recommendations a number appears only if a person "
                    + "would say it aloud - say it in words");
        }
    }

    /// <summary>The whole rule for a prose field: plain, (for a recommendation field) the figure rule, resolved through
    /// the tools, quotes and providers, then the field's own word cap, citation floor, quoted floor and sentence count
    /// (an exact count, or a low and a high). Returns the resolved citations.</summary>
    public static List<Citation> CheckText(string slot, object? value, Resolver resolver, int? maxWords = null, int minCitations = 0,
        int minQuoted = 0, (int Low, int High)? sentences = null, bool figures = false)
    {
        CheckPlain(slot, value);
        var text = (string)value!;
        if (PyText.Strip(text).Length == 0)
            throw new SlotError(slot, "is empty");
        if (figures) CheckFigures(slot, text);
        var citations = resolver.Resolve(slot, text);
        CheckQuotesAndProviders(slot, text, citations);
        var words = CountWords(text);
        if (maxWords is not null && words > maxWords)
            throw new SlotError(slot, "has " + words + " words, over the " + maxWords + "-word limit");
        if (citations.Count < minCitations)
            throw new SlotError(slot, "carries " + citations.Count + " citation(s); at least " + minCitations
                + " of the form " + ReportCheck.CitationForm + " are required");
        var quoted = citations.Count(c => c.Fragment is not null);
        if (quoted < minQuoted)
            throw new SlotError(slot, "carries " + quoted + " quoted citation(s); at least " + minQuoted
                + " of the form " + ReportCheck.QuotedCitationForm + " are required");
        if (sentences is not null)
        {
            var (low, high) = sentences.Value;
            var (count, endsWell) = CountSentences(text, citations);
            if (!endsWell)
                throw new SlotError(slot, "does not end with a sentence mark (one of " + SentenceEnd + ")");
            if (count < low || count > high)
            {
                var wanted = low == high ? low.ToString(System.Globalization.CultureInfo.InvariantCulture) : low + " or " + high;
                throw new SlotError(slot, "has " + count + " sentences; " + wanted + " required");
            }
        }
        return citations;
    }

    /// <summary>The value is an object with exactly these keys; a missing or unknown key is named.</summary>
    public static Dictionary<string, object?> CheckKeys(string slot, object? value, IReadOnlyList<string> keys)
    {
        if (value is not Dictionary<string, object?> dictionary)
            throw new SlotError(slot, "must be an object with the keys " + string.Join(", ", keys) + ", got " + TypeName(value));
        foreach (var key in keys)
            if (!dictionary.ContainsKey(key))
                throw new SlotError(slot + "." + key, "is missing");
        foreach (var key in dictionary.Keys)
            if (!keys.Contains(key))
                throw new SlotError(slot + "." + key, "is not a slot; the keys are " + string.Join(", ", keys));
        return dictionary;
    }

    // ------------------------------------------------------------------ one validator per slot kind

    public static void ValidateRecommendations(object? value, Resolver resolver)
    {
        const string slot = "recommendations";
        if (value is not List<object?> list)
            throw new SlotError(slot, "must be a list of exactly " + RecommendationCount + " recommendations");
        if (list.Count != RecommendationCount)
            throw new SlotError(slot, "holds " + list.Count + " recommendations; exactly " + RecommendationCount + " are required");
        for (var position = 0; position < list.Count; position++)
        {
            var path = slot + "[" + position + "]";
            var item = CheckKeys(path, list[position], RecommendationKeys);
            var title = path + ".title";
            CheckPlain(title, item["title"]);
            var titleText = (string)item["title"]!;
            if (ReportCheck.MinuteRe.IsMatch(titleText))
                throw new SlotError(title, "carries a citation; a title is words only");
            if (DigitRe.IsMatch(titleText))
                throw new SlotError(title, "carries a figure; a title is words only");
            if (titleText.Contains('"'))
                throw new SlotError(title, "carries a double quotation mark; a title is words only");
            var words = CountWords(titleText);
            if (words < TitleMinWords || words > TitleMaxWords)
                throw new SlotError(title, "has " + words + " words; " + TitleMinWords + " to " + TitleMaxWords + " required");
            CheckFigures(title, titleText);
            var names = CheckCall.ProviderNamesIn(titleText);
            if (names.Count > 0)
                throw new SlotError(title, "names a provider or a model: " + string.Join(", ", names) + "; " + CheckCall.ProviderRuling);
            CheckText(path + ".saw", item["saw"], resolver, maxWords: SawMaxWords, minCitations: SawMinCitations, minQuoted: 1, figures: true);
            CheckText(path + ".cost", item["cost"], resolver, maxWords: CostMaxWords, minCitations: CostMinCitations, sentences: (1, CostMaxSentences), figures: true);
            CheckText(path + ".try", item["try"], resolver, maxWords: TryMaxWords, sentences: (1, 1), figures: true);
        }
    }

    public static void ValidateWentWell(object? value, Resolver resolver)
    {
        const string slot = "went_well";
        var entry = CheckKeys(slot, value, new[] { "text" });
        var path = slot + ".text";
        CheckPlain(path, entry["text"]);
        if ((string)entry["text"]! == NothingQualified) return;
        CheckText(path, entry["text"], resolver, maxWords: WentWellMaxWords, minCitations: 1, minQuoted: 1);
    }

    public static void ValidatePrompting(object? value, Resolver resolver, long humanCount)
    {
        const string slot = "prompting";
        var entries = CheckKeys(slot, value, DimensionKeys);
        foreach (var key in DimensionKeys)
        {
            var path = slot + "." + key;
            var entry = CheckKeys(path, entries[key], PromptingKeys);
            var level = path + ".level";
            CheckPlain(level, entry["level"]);
            var word = (string)entry["level"]!;
            if (humanCount < Contract.TooFewBelow)
            {
                if (word != Contract.TooFew)
                    throw new SlotError(level, "is '" + word + "' with " + humanCount + " prompts, under "
                        + Contract.TooFewBelow + "; the level must be exactly '" + Contract.TooFew + "'");
            }
            else if (!Contract.JudgedWords.Contains(word))
            {
                throw new SlotError(level, "is '" + word + "'; with " + humanCount + " prompts the level is one of "
                    + string.Join(", ", Contract.JudgedWords));
            }
            CheckText(path + ".observation", entry["observation"], resolver, maxWords: ObservationMaxWords, minCitations: ObservationMinCitations, minQuoted: 1);
            CheckText(path + ".step", entry["step"], resolver, maxWords: StepMaxWords, sentences: (1, 1));
        }
    }

    /// <summary>The slot STRUCTURE, and nothing about the words: one object with exactly the top keys; exactly three
    /// recommendations, each with exactly its keys, each a string; went_well with its text; prompting with exactly the
    /// six dimensions, each with exactly its keys, each a string. The first missing or unknown slot is named.
    /// <see cref="Validate"/> runs this first, and the standalone log check runs it before it enumerates a single
    /// field: an enumeration over whatever fields are present enumerates nothing over nothing.</summary>
    public static void CheckShape(object? slots)
    {
        if (slots is not Dictionary<string, object?> top)
            throw new SlotError("slots", "slots.json must hold one object with the keys " + string.Join(", ", TopKeys));
        foreach (var key in TopKeys)
            if (!top.ContainsKey(key))
                throw new SlotError(key, "is missing");
        foreach (var key in top.Keys)
            if (!TopKeys.Contains(key))
                throw new SlotError(key, "is not a slot; the slots are " + string.Join(", ", TopKeys));
        if (top["recommendations"] is not List<object?> recommendations)
            throw new SlotError("recommendations", "must be a list of exactly " + RecommendationCount + " recommendations");
        if (recommendations.Count != RecommendationCount)
            throw new SlotError("recommendations", "holds " + recommendations.Count + " recommendations; exactly "
                + RecommendationCount + " are required");
        for (var position = 0; position < recommendations.Count; position++)
        {
            var path = "recommendations[" + position + "]";
            var item = CheckKeys(path, recommendations[position], RecommendationKeys);
            foreach (var key in RecommendationKeys)
                CheckPlain(path + "." + key, item[key]);
        }
        var wentWell = CheckKeys("went_well", top["went_well"], new[] { "text" });
        CheckPlain("went_well.text", wentWell["text"]);
        var prompting = CheckKeys("prompting", top["prompting"], DimensionKeys);
        foreach (var key in DimensionKeys)
        {
            var path = "prompting." + key;
            var entry = CheckKeys(path, prompting[key], PromptingKeys);
            foreach (var field in PromptingKeys)
                CheckPlain(path + "." + field, entry[field]);
        }
    }

    /// <summary>Validate every written slot; the first <see cref="SlotError"/> is thrown. <paramref name="index"/> is the
    /// session_index answer and <paramref name="humanCount"/> the week_overview's human prompt count - both from the tools.</summary>
    public static Resolver Validate(object? slots, ToolSurface surface, IReadOnlyList<Dictionary<string, object?>> index, long humanCount)
    {
        CheckShape(slots);
        var top = (Dictionary<string, object?>)slots!;
        var resolver = new Resolver(surface, index);
        ValidateRecommendations(top["recommendations"], resolver);
        ValidateWentWell(top["went_well"], resolver);
        ValidatePrompting(top["prompting"], resolver, humanCount);
        return resolver;
    }
}
