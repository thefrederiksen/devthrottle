using System.Reflection;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Teams.Mentor;

/// <summary>The three tones a block may carry, and the words a page shows for each. Decided here, once (rule 7).</summary>
public static class MentorTones
{
    public const string Good = "good";
    public const string Mixed = "mixed";
    public const string Hard = "hard";

    /// <summary>Whether <paramref name="tone"/> is one of the three.</summary>
    public static bool IsTone(string? tone) => tone is Good or Mixed or Hard;

    /// <summary>The words shown for a tone: "a good week", "a mixed week", "a hard week".</summary>
    public static string Label(string tone) => IsTone(tone)
        ? $"a {tone} week"
        : throw new ArgumentOutOfRangeException(nameof(tone), tone, "Not a Mentor tone.");
}

/// <summary>What the model answered, once the Gateway has accepted it. The quotes are still labels; the Gateway turns
/// them into the person's own prompts itself.</summary>
public sealed record MentorAnswer(
    string Tone,
    string WorkedOn,
    string? HowItWent,
    string? WentBadlyAndWhy,
    IReadOnlyList<string> QuoteLabels,
    string OneThingToTry);

/// <summary>The Gateway's verdict on a model's answer: accepted (<see cref="Answer"/>), or refused with the reason a
/// person reads (<see cref="Refusal"/>). Exactly one is set.</summary>
public sealed record MentorAnswerCheck(MentorAnswer? Answer, string? Refusal)
{
    public static MentorAnswerCheck Accept(MentorAnswer answer) => new(answer, null);
    /// <summary>Refuse. The reason names the KIND of refusal and counts only - never text the model wrote - because it
    /// is logged and stored (devthrottle_internal#2305, review G2).</summary>
    public static MentorAnswerCheck Refuse(string reason) => new(null, reason);
}

/// <summary>The request to the model: the instruction, then the person's own prompts, each under a label.</summary>
public sealed record MentorRequest(string Text, IReadOnlyDictionary<string, PromptRecord> PromptsByLabel);

/// <summary>
/// WHAT THE MENTOR'S MODEL IS ASKED, AND WHAT IS ACCEPTED BACK (devthrottle_internal#2305).
///
/// The model is handed ONE person's own prompts from ONE week, each under a label (<c>P1</c>, <c>P2</c>, ...), and the
/// instruction in <c>Content/mentor-weekly.instructions.md</c>. It answers the block's fields and the LABELS of one or
/// two prompts to quote - never a prompt's text. The answer is then checked here, strictly: anything that is not the
/// exact shape asked for, or a quote that is not one of the labels this request handed out, is refused. There is no
/// second, looser reading of a refused answer (rule 3) - the caller records the refusal and writes no block.
/// </summary>
public static class MentorBrief
{
    /// <summary>The most prompts one request shows the model: the most recent ones. Bounds the cost of a busy week.</summary>
    public const int MaxPromptsShown = 150;

    /// <summary>How much of one prompt the model is shown. A quoted prompt is still stored whole.</summary>
    public const int MaxPromptCharsShown = 600;

    /// <summary>The longest any one text field of an accepted answer may be.</summary>
    public const int MaxFieldChars = 600;

    /// <summary>The cap on the model's answer, in tokens: a block is six short fields.</summary>
    public const int MaxOutputTokens = 800;

    /// <summary>The most prompts a block may quote.</summary>
    public const int MaxQuotes = 2;

    /// <summary>A free-text field holding this many consecutive words of a prompt the model was shown is refused: a
    /// prompt reaches the page only as a quote the Gateway copies (devthrottle_internal#2305, review G1).</summary>
    public const int MaxEchoedWords = 8;

    private static readonly string[] FreeTextFields = { "workedOn", "howItWent", "wentBadlyAndWhy", "oneThingToTry" };

    private static readonly string[] Fields = { "tone", "workedOn", "howItWent", "wentBadlyAndWhy", "quotes", "oneThingToTry" };

    /// <summary>The instruction text, from the one file it lives in.</summary>
    public static string Instruction { get; } = LoadInstruction();

    /// <summary>
    /// Build the request for one person's week. <paramref name="prompts"/> must already be that person's own prompts
    /// of that week; the most recent <see cref="MaxPromptsShown"/> are shown, oldest first, labelled from <c>P1</c>. The
    /// model is not told who the person is: the block never names them, it says "they".
    /// </summary>
    public static MentorRequest Build(MentorWeek week, IReadOnlyList<PromptRecord> prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        if (prompts.Count == 0)
            throw new ArgumentException("A Mentor request needs at least one prompt; a person with none gets no model call.", nameof(prompts));

        var shown = prompts
            .OrderBy(p => p.TsUtc)
            .TakeLast(MaxPromptsShown)
            .ToList();

        var byLabel = new Dictionary<string, PromptRecord>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine(Instruction.TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"The week: {week} ({week.Start:dddd d MMMM yyyy} to {week.End:dddd d MMMM yyyy})");
        sb.AppendLine($"Their prompts this week, oldest first ({shown.Count} shown):");
        for (var i = 0; i < shown.Count; i++)
        {
            var label = $"P{i + 1}";
            byLabel[label] = shown[i];
            sb.AppendLine($"[{label}] {shown[i].TsUtc:ddd HH:mm} UTC: {ForDisplay(shown[i].Text)}");
        }
        return new MentorRequest(sb.ToString(), byLabel);
    }

    /// <summary>
    /// Check the model's answer against the request that produced it. Accepted only when the text is one JSON object
    /// (optionally inside one code fence) with exactly the six fields, each of the right type and length, a tone of
    /// the three, at least one of "how it went" and "where it went badly", and quotes that are one or two DISTINCT
    /// labels of this request when "where it went badly" is given and none otherwise.
    /// </summary>
    public static MentorAnswerCheck Check(string? answerText, MentorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = Unfence(answerText);
        if (json is null)
            return MentorAnswerCheck.Refuse("The answer was not one JSON object.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return MentorAnswerCheck.Refuse("The answer was not valid JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return MentorAnswerCheck.Refuse("The answer was not one JSON object.");

            var names = root.EnumerateObject().Select(p => p.Name).ToList();
            var extra = names.Where(n => !Fields.Contains(n, StringComparer.Ordinal)).ToList();
            if (extra.Count > 0)
                return MentorAnswerCheck.Refuse($"The answer carried {extra.Count} field(s) that were not asked for.");
            if (names.Count != names.Distinct(StringComparer.Ordinal).Count())
                return MentorAnswerCheck.Refuse("The answer named a field twice.");
            var missing = Fields.Where(f => !names.Contains(f, StringComparer.Ordinal)).ToList();
            if (missing.Count > 0)
                return MentorAnswerCheck.Refuse($"The answer left out fields: {string.Join(", ", missing)}.");

            if (!TryText(root, "tone", required: true, out var tone, out var refusal)) return MentorAnswerCheck.Refuse(refusal!);
            if (!MentorTones.IsTone(tone))
                return MentorAnswerCheck.Refuse("The answer's tone was not one of good, mixed or hard.");
            if (!TryText(root, "workedOn", required: true, out var workedOn, out refusal)) return MentorAnswerCheck.Refuse(refusal!);
            if (!TryText(root, "howItWent", required: false, out var howItWent, out refusal)) return MentorAnswerCheck.Refuse(refusal!);
            if (!TryText(root, "wentBadlyAndWhy", required: false, out var wentBadly, out refusal)) return MentorAnswerCheck.Refuse(refusal!);
            if (!TryText(root, "oneThingToTry", required: true, out var oneThing, out refusal)) return MentorAnswerCheck.Refuse(refusal!);
            if (howItWent is null && wentBadly is null)
                return MentorAnswerCheck.Refuse("The answer said neither how the week went nor where it went badly.");

            var echoRefusal = RefuseEchoedPrompts(
                new[] { workedOn, howItWent, wentBadly, oneThing }, request.PromptsByLabel.Values);
            if (echoRefusal is not null)
                return MentorAnswerCheck.Refuse(echoRefusal);

            var quotes = root.GetProperty("quotes");
            if (quotes.ValueKind != JsonValueKind.Array)
                return MentorAnswerCheck.Refuse("The answer's quotes were not a list.");
            var labels = new List<string>();
            foreach (var q in quotes.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.String)
                    return MentorAnswerCheck.Refuse("The answer's quotes held something other than prompt ids.");
                var label = q.GetString()!.Trim();
                if (!request.PromptsByLabel.ContainsKey(label))
                    return MentorAnswerCheck.Refuse("The answer quoted something that is not one of this person's prompts of this week.");
                if (labels.Contains(label, StringComparer.Ordinal))
                    return MentorAnswerCheck.Refuse($"The answer quoted {label} twice.");
                labels.Add(label);
            }

            if (wentBadly is null && labels.Count > 0)
                return MentorAnswerCheck.Refuse("The answer quoted prompts without saying where the week went badly.");
            if (wentBadly is not null && labels.Count == 0)
                return MentorAnswerCheck.Refuse("The answer said where the week went badly without quoting a prompt.");
            if (labels.Count > MaxQuotes)
                return MentorAnswerCheck.Refuse($"The answer quoted {labels.Count} prompts; at most {MaxQuotes} may be quoted.");

            return MentorAnswerCheck.Accept(new MentorAnswer(tone!, workedOn!, howItWent, wentBadly, labels, oneThing!));
        }
    }

    /// <summary>
    /// The one rule that keeps a prompt's words out of the summary: a free-text field may not contain a double quotation
    /// mark, nor <see cref="MaxEchoedWords"/> or more consecutive words of any prompt the model was shown (compared
    /// without case or punctuation). Null when the fields are clean; otherwise the refusal, naming the field only.
    /// </summary>
    private static string? RefuseEchoedPrompts(IReadOnlyList<string?> fieldValues, IEnumerable<PromptRecord> shown)
    {
        var promptRuns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prompt in shown)
            foreach (var run in WordRuns(prompt.Text))
                promptRuns.Add(run);

        for (var i = 0; i < FreeTextFields.Length; i++)
        {
            var value = fieldValues[i];
            if (value is null)
                continue;
            if (value.IndexOfAny(DoubleQuotationMarks) >= 0)
                return $"The answer's {FreeTextFields[i]} contained a quotation mark; a prompt may be quoted only through quotes.";
            if (WordRuns(value).Any(promptRuns.Contains))
                return $"The answer's {FreeTextFields[i]} repeated {MaxEchoedWords} or more consecutive words of a prompt; a prompt may be quoted only through quotes.";
        }
        return null;
    }

    private static readonly char[] DoubleQuotationMarks = { '"', '\u201C', '\u201D', '\u201E', '\u201F', '\uFF02' };

    /// <summary>Every run of <see cref="MaxEchoedWords"/> consecutive words in <paramref name="text"/>, lower-cased.
    /// A word is a run of letters and digits; an apostrophe inside it is dropped (so "doesn't" and "doesnt" are one
    /// word), and every other character - a space, a hyphen, a slash, an underscore, any punctuation - ends it, so words
    /// glued together by a hyphen are still seen (review H4).</summary>
    private static IEnumerable<string> WordRuns(string? text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text ?? "")
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (c is '\'' or '\u2019')
            {
                // An apostrophe stays inside its word and is dropped from it.
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            words.Add(current.ToString());

        for (var i = 0; i + MaxEchoedWords <= words.Count; i++)
            yield return string.Join(' ', words.GetRange(i, MaxEchoedWords));
    }

    /// <summary>One string field: present as a string (or null when not required), trimmed, not empty, not too long.</summary>
    private static bool TryText(JsonElement root, string field, bool required, out string? value, out string? refusal)
    {
        value = null;
        refusal = null;
        var el = root.GetProperty(field);
        if (el.ValueKind == JsonValueKind.Null && !required)
            return true;
        if (el.ValueKind != JsonValueKind.String)
        {
            refusal = required
                ? $"The answer's {field} was not text."
                : $"The answer's {field} was neither text nor null.";
            return false;
        }
        var text = el.GetString()!.Trim();
        if (text.Length == 0)
        {
            refusal = $"The answer's {field} was empty.";
            return false;
        }
        if (text.Length > MaxFieldChars)
        {
            refusal = $"The answer's {field} was {text.Length} characters; at most {MaxFieldChars} are kept.";
            return false;
        }
        value = text;
        return true;
    }

    /// <summary>The JSON object in the answer: the whole trimmed text, or the inside of ONE code fence around it.
    /// Null when there is no object.</summary>
    private static string? Unfence(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = t.IndexOf('\n');
            if (firstNewline < 0 || !t.EndsWith("```", StringComparison.Ordinal) || t.Length < firstNewline + 4)
                return null;
            t = t[(firstNewline + 1)..^3].Trim();
        }
        return t.StartsWith('{') && t.EndsWith('}') ? t : null;
    }

    private static string ForDisplay(string text)
    {
        var oneLine = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= MaxPromptCharsShown ? oneLine : oneLine[..MaxPromptCharsShown] + " [cut]";
    }

    private static string LoadInstruction()
    {
        const string name = "CcDirector.Gateway.Teams.Mentor.mentor-weekly.instructions.md";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The Mentor's instruction file is not embedded in the Gateway ({name}).");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
