using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The pieces of the reference's <c>check_call.py</c> the report framework reads: the provider rule (charter
/// ruling 11 - no provider and no model is named in the report's own prose, the developer's quoted words and the
/// cited session names exempt by span) and the nudge feature words the rendered "How you drive DevThrottle"
/// section writes. The spoken call's own checks stay in Python (PHASE-B-PLAN decision 1).
///
/// THE LIMIT, stated here as the reference states it over its list: A WORD LIST CAN NEVER BE COMPLETE. A model
/// released next month is not in it and this check will pass over that name in silence. It covers the vendors
/// and the model families known on 2026-09-02. It turns the common case from unguarded into refused; it is not a
/// proof that the class is closed, and nobody may read a green run as one.
/// </summary>
public static class CheckCall
{
    /// <summary>The product features a nudge can be about. The report writes these words and so does the call.</summary>
    public static readonly string[] NudgeFeatures = { "voice", "phone", "Cockpit" };

    public static readonly string[] ProviderNames =
    {
        "openai", "anthropic", "deepmind", "mistral", "cohere", "deepinfra", "xai", "huggingface",
        "chatgpt", "gpt", "claude", "gemini", "bard", "llama", "whisper", "sonnet", "opus", "haiku",
        "grok", "codex", "copilot", "cursor", "perplexity", "bedrock",
    };

    public const string ProviderRuling = "no provider or model name reaches the developer: say the agent or the "
        + "assistant (charter ruling 11, prompts/mentor.md)";

    private static readonly Regex ReportStampRe = new(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", RegexOptions.CultureInvariant);

    /// <summary>The (start, end) of every occurrence of each of <paramref name="names"/> in <paramref name="text"/>,
    /// ignoring letter case. The names are taken longest first so a longer name wins over a shorter one inside it,
    /// and a span already claimed is not claimed again.</summary>
    public static List<(int Start, int End)> OccurrenceSpans(string text, IEnumerable<string> names)
    {
        var lowered = PyText.Lower(text);
        var spans = new List<(int Start, int End)>();
        foreach (var name in names.OrderByDescending(n => n.Length))
        {
            var needle = PyText.Strip(PyText.Lower(name));
            if (needle.Length == 0) continue;
            var start = lowered.IndexOf(needle, StringComparison.Ordinal);
            while (start >= 0)
            {
                var end = start + needle.Length;
                if (!spans.Any(claimed => claimed.Start < end && start < claimed.End))
                    spans.Add((start, end));
                start = start + 1 <= lowered.Length ? lowered.IndexOf(needle, start + 1, StringComparison.Ordinal) : -1;
            }
        }
        return spans;
    }

    /// <summary>The provider or model names the text states, in <see cref="ProviderNames"/> order, each once. The
    /// spans of <paramref name="exemptNames"/> - the session names the report carries - are cut out before anything
    /// is read: a developer may name a session after the tool they were driving, and that name is the developer's
    /// own word for their own session, not the report claiming a vendor.</summary>
    public static List<string> ProviderNamesIn(string text, IEnumerable<string>? exemptNames = null)
    {
        var cut = text;
        foreach (var (start, end) in OccurrenceSpans(text, exemptNames ?? Array.Empty<string>()).OrderByDescending(s => s.Start).ThenByDescending(s => s.End))
            cut = cut.Substring(0, start) + " . " + cut.Substring(end);
        return ProviderNames
            .Where(name => Regex.IsMatch(cut, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
    }

    /// <summary>The session names the report carries, longest first, resolved against the week's known sessions
    /// through <see cref="ReportCheck.SessionReference"/> - one mechanism on two surfaces, so this cannot disagree
    /// with the citation check about what a session name is.</summary>
    public static List<string> ReportSessionNames(string text, ReportCheck.Prompts prompts)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in PyText.SplitLines(text))
        {
            foreach (Match match in ReportStampRe.Matches(line))
            {
                var before = line.Substring(0, match.Index);
                if (!before.EndsWith(", ", StringComparison.Ordinal)) continue;
                var found = ReportCheck.SessionReference(before.Substring(0, before.Length - 2), prompts);
                if (found.Count > 0) names.Add(found[0]);
            }
        }
        return names.OrderByDescending(n => n.Length).ToList();
    }

    /// <summary>Whether one feature word is named in the text, on a word boundary, ignoring letter case.</summary>
    public static bool FeaturesNamed(string text, string feature)
        => Regex.IsMatch(PyText.Lower(text), @"\b" + Regex.Escape(PyText.Lower(feature)) + @"\b", RegexOptions.CultureInvariant);
}
