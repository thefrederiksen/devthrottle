using System.Globalization;
using System.Numerics;

namespace CcDirector.Gateway.Mentor;

/// <summary>The overview or the index lacks what a section needs; the message names it.</summary>
public sealed class RenderError : Exception
{
    public RenderError(string message) : base(message) { }
}

/// <summary>
/// The port of <c>mentor_tools/render.py</c>: the RENDERED slots of the report, each a function
/// (overview, index, logEntries) -> section body, using ONLY those three inputs.
///
/// <c>overview</c> is the week_overview answer (the metrics document), <c>index</c> the session_index answer (one row
/// per session with a human prompt in the week, index order) and <c>logEntries</c> the run's tool log
/// (<see cref="ToolLog.Entries"/>). A rendered section never sees the agent's text; the numbers in it are the
/// tools' own answers, formatted by one helper, and the wording is fixed in the constants below.
/// prompts/mentor.md is the authority for what each section says; the renderer obeys it by construction, so a
/// reader can check the sentence against the rule without a second model pass.
///
/// Sections rendered here, in the report's order (<see cref="Contract.Headings"/>):
/// Your week (five human-only facts with their baselines, counts and whole numbers only); What you worked on (one
/// topic line per repository, largest first by human prompts, every index session exactly once; sessions with no
/// repository name or no session row under "Not classified", last); How you drive (the nudge: a feature is nudged
/// when its count is zero or its share of human prompts is under <see cref="NudgeShare"/>, one line per nudged
/// feature, lowest share first, the count and the total in the same sentence as the feature name); Your fleet
/// (sessions by origin, standing sessions, turns by driver and quiet hours); Measured but not judged (what the
/// report leaves unjudged and why, in a fixed order); How this was made (counted FROM THE TOOL LOG, by the tool
/// NAMES the log holds: a tool never called is left out, a tool this class has no sentence for is written by its
/// name and its count, and refused calls are counted; then the coverage sentences the document carries, the
/// bound-and-judged sentence and the owner-only sentence last).
///
/// Numbers: <see cref="Figure"/> writes a value as the document holds it (an integer as is, a fraction with its
/// digits); <see cref="Whole"/> rounds a fraction to the nearest whole number, half up, and is used only in Your
/// week, whose form is counts and whole numbers. Nowhere is a share written as a decimal. Every sentence and every
/// constant is the reference's, character for character: the proof is a byte-identical report.md.
/// </summary>
public static class Render
{
    public const double NudgeShare = 0.05;
    public const string NotClassified = "Not classified";
    public const string NoPriorWeeks = "no prior weeks to compare yet";
    public const string AllInUse = "Voice, the phone and the Cockpit were all in regular use this week.";
    public const string NoOtherSessions = "No sessions were started by other sessions or by automation this week.";
    public const string NothingUnjudged = "Nothing measured this week was set aside unjudged.";
    /// <summary>The guarantee, stated exactly as wide as it is: what is bound to the log and what is the mentor's
    /// judgement. The reference's SKILL.md and the design document carry the same sentence.</summary>
    public const string BoundAndJudged = "Every quotation and citation in this report was fetched and verified by the tools it lists; "
        + "the advice lines and the level words are the mentor's judgement over the cited evidence.";
    public const string OwnerOnly = "This report reaches nobody but the account owner.";
    public const string QuietWords = "quiet (no terminal output, awaiting input or finished)";
    public const string AutomationWords = "started by automation";

    /// <summary>The nudge: the feature word as <see cref="CheckCall.NudgeFeatures"/> spells it, and the fixed action
    /// sentence. The voice sentence is the one prompts/mentor.md prescribes for voice at zero; it is the voice
    /// feature's only sentence, so the zero case holds by construction.</summary>
    public static readonly (string Feature, string Action)[] NudgeActions =
    {
        ("voice", "Hit the green speak button and talk some of your prompts, because they can be longer."),
        ("phone", "Open the phone app tomorrow and send a session its next prompt from there."),
        ("Cockpit", "Drive a session from the Cockpit tomorrow, not from the desktop."),
    };

    /// <summary>The heuristic candidate counts, in the order they are written, each with its words.</summary>
    public static readonly (string Key, string Words)[] HeuristicShares =
    {
        ("correction_candidates_share", "of your prompts opened with a correction marker"),
        ("specificity_markers_share", "of your prompts carried a file path, an issue number, a link or a quoted string"),
        ("done_criteria_share", "of your prompts of 20 words or more named a way to check the work"),
    };
    public const string CandidateNotFact = "that is a word-list candidate count, not a fact";

    /// <summary>The tools this renderer has a sentence for. This is NOT the list of tools that are counted: every tool
    /// NAME found in the log is counted (<see cref="ToolCounts"/>), and one this table does not know is written by its
    /// name and its call count.</summary>
    public static readonly string[] SentencedTools =
    {
        "week_overview", "session_index", "session_prompts", "dimension_candidates", "prompt_search",
        "prior_weeks", "turn_record", "session_outcomes", "cite", "verify_quote", "note",
    };
    public const string NoCallAnswered = "No tool answered a call in this run.";

    static Render()
    {
        if (!NudgeActions.Select(n => n.Feature).SequenceEqual(CheckCall.NudgeFeatures, StringComparer.Ordinal))
            throw new InvalidOperationException("Render.NudgeActions no longer matches CheckCall.NudgeFeatures; fix the table.");
    }

    public delegate string Section(Dictionary<string, object?> overview, IReadOnlyList<Dictionary<string, object?>> index, IReadOnlyList<Dictionary<string, object?>> logEntries);

    /// <summary>The rendered headings and their functions, in the report's order.</summary>
    public static readonly (string Heading, Section Render)[] Rendered =
    {
        ("# Your week", YourWeek),
        ("## What you worked on", WhatYouWorkedOn),
        ("## How you drive DevThrottle", HowYouDrive),
        ("## Your fleet", YourFleet),
        ("## Measured but not judged", MeasuredNotJudged),
        ("## How this was made", HowThisWasMade),
    };

    public static bool IsRendered(string heading) => Rendered.Any(r => r.Heading == heading);

    // ------------------------------------------------------------------ helpers

    private static bool IsNumber(object? value) => value is long or int or double;

    /// <summary>A number as the document holds it: an integer as is, a fraction with its own digits.</summary>
    public static string Figure(object? value)
    {
        switch (value)
        {
            case long l:
                return l.ToString(CultureInfo.InvariantCulture);
            case int i:
                return i.ToString(CultureInfo.InvariantCulture);
            case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                if (Math.Floor(d) == d) return new BigInteger(d).ToString(CultureInfo.InvariantCulture);
                return PlainDecimal(PythonFloat.Repr(d));
            default:
                throw new RenderError("not a number: " + Repr(value));
        }
    }

    /// <summary>A number rounded to the nearest whole number, half up (on the decimal digits of the number's repr,
    /// as <c>Decimal(repr(value)).quantize(Decimal(1), ROUND_HALF_UP)</c> does).</summary>
    public static string Whole(object? value)
    {
        switch (value)
        {
            case long l:
                return l.ToString(CultureInfo.InvariantCulture);
            case int i:
                return i.ToString(CultureInfo.InvariantCulture);
            case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                var exact = decimal.Parse(PythonFloat.Repr(d), NumberStyles.Float, CultureInfo.InvariantCulture);
                var rounded = Math.Round(exact, 0, MidpointRounding.AwayFromZero);
                return new BigInteger(rounded).ToString(CultureInfo.InvariantCulture);
            default:
                throw new RenderError("not a number: " + Repr(value));
        }
    }

    /// <summary>Python's <c>repr</c> of the value a refusal names.</summary>
    private static string Repr(object? value) => value switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        string s => PyText.Repr(s),
        _ => ParityJson.Compact(value),
    };

    /// <summary><c>format(Decimal(repr).normalize(), "f")</c>: the repr's digits as a plain decimal, no exponent, no
    /// trailing zeros in the fraction.</summary>
    private static string PlainDecimal(string repr)
    {
        var negative = repr.StartsWith('-');
        if (negative) repr = repr.Substring(1);
        var exponent = 0;
        var e = repr.IndexOf('e');
        if (e >= 0)
        {
            exponent = int.Parse(repr.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            repr = repr.Substring(0, e);
        }
        var point = repr.IndexOf('.');
        var digits = point < 0 ? repr : repr.Remove(point, 1);
        var decpt = (point < 0 ? repr.Length : point) + exponent;   // value = 0.<digits> x 10^decpt
        string whole, fraction;
        if (decpt <= 0)
        {
            whole = "0";
            fraction = new string('0', -decpt) + digits;
        }
        else if (decpt >= digits.Length)
        {
            whole = digits + new string('0', decpt - digits.Length);
            fraction = "";
        }
        else
        {
            whole = digits.Substring(0, decpt);
            fraction = digits.Substring(decpt);
        }
        whole = whole.TrimStart('0');
        if (whole.Length == 0) whole = "0";
        fraction = fraction.TrimEnd('0');
        var text = fraction.Length > 0 ? whole + "." + fraction : whole;
        return negative ? "-" + text : text;
    }

    private static bool IsOne(object? count) => count is long l ? l == 1 : count is int i ? i == 1 : count is double d && d == 1.0;

    private static bool IsZero(object? count) => count is long l ? l == 0 : count is int i ? i == 0 : count is double d && d == 0.0;

    public static string Plural(object? count, string singular, string? pluralForm = null)
        => Figure(count) + " " + (IsOne(count) ? singular : (pluralForm ?? singular + "s"));

    /// <summary>Python's <c>a + b</c> on two document numbers: int + int is an int, anything with a float is a float.</summary>
    private static object Add(object? a, object? b)
    {
        if (a is long la && b is long lb) return la + lb;
        return ToDouble(a) + ToDouble(b);
    }

    private static double ToDouble(object? value) => value switch
    {
        long l => l,
        int i => i,
        double d => d,
        _ => throw new RenderError("not a number: " + Repr(value)),
    };

    private static Dictionary<string, object?> Obj(object? value, string what)
        => value as Dictionary<string, object?> ?? throw new MentorDataException("The " + what + " is not an object.");

    private static List<object?> Arr(object? value, string what)
        => value as List<object?> ?? throw new MentorDataException("The " + what + " is not a list.");

    /// <summary>Python's <c>dictionary[key]</c>: a KeyError names the key.</summary>
    private static object? Key(Dictionary<string, object?> dictionary, string key)
        => dictionary.TryGetValue(key, out var value) ? value : throw new MentorDataException("KeyError: '" + key + "'");

    public static Dictionary<string, object?> Metric(Dictionary<string, object?> overview, string group, string key)
    {
        if (overview.TryGetValue(group, out var groupValue) && groupValue is Dictionary<string, object?> groupDict
            && groupDict.TryGetValue(key, out var entry) && entry is Dictionary<string, object?> metric)
            return metric;
        throw new RenderError("the overview has no " + group + "." + key);
    }

    private static int WeeksOf(Dictionary<string, object?> entry) => Arr(Key(entry, "baseline_weeks"), "baseline_weeks").Count;

    /// <summary><c>- &lt;fact&gt;, against &lt;baseline&gt; a week over your prior &lt;n&gt; weeks.</c> or, without a baseline,
    /// <c>- &lt;fact&gt;; no prior weeks to compare yet.</c></summary>
    public static string Against(string fact, string? baselineText, int weeks)
    {
        if (baselineText is null) return "- " + fact + "; " + NoPriorWeeks + ".";
        return "- " + fact + ", against " + baselineText + " a week over your prior " + Plural((long)weeks, "week") + ".";
    }

    /// <summary>The same pair for a fleet sentence, without the list dash.</summary>
    public static string ClauseSentence(string fact, string? baselineText, int weeks)
    {
        if (baselineText is null) return fact + "; " + NoPriorWeeks + ".";
        return fact + ", against " + baselineText + " a week over your prior " + Plural((long)weeks, "week") + ".";
    }

    public static string JoinClauses(IReadOnlyList<string> clauses)
    {
        if (clauses.Count == 1) return clauses[0];
        return string.Join(", ", clauses.Take(clauses.Count - 1)) + " and " + clauses[^1];
    }

    // ------------------------------------------------------------------ Your week

    public static string YourWeek(Dictionary<string, object?> overview, IReadOnlyList<Dictionary<string, object?>> index, IReadOnlyList<Dictionary<string, object?>> logEntries)
    {
        var origin = Metric(overview, "origin", "prompts_by_origin");
        var words = Metric(overview, "prompt_shape", "prompt_words");
        var hours = Metric(overview, "rhythm", "business_hours_share");
        var human = Obj(Key(Obj(Key(origin, "value"), "value"), "human"), "human");
        var baseline = Key(origin, "baseline");
        var baseHuman = baseline is null ? null : Obj(Key(Obj(baseline, "baseline"), "human"), "baseline human");
        var n = WeeksOf(origin);
        var count = Key(human, "count");
        var modality = Obj(Key(human, "by_modality"), "by_modality");
        var surface = Obj(Key(human, "by_surface"), "by_surface");
        var voice = Key(Obj(Key(modality, "voice"), "voice"), "count");
        var typed = Key(Obj(Key(modality, "typed"), "typed"), "count");
        var phone = Key(Obj(Key(surface, "phone"), "phone"), "count");
        var lines = new List<string>
        {
            Against(Figure(count) + " prompts of your own this week", baseHuman is null ? null : Whole(Key(baseHuman, "count")), n),
            Against(Figure(voice) + " spoken and " + Figure(typed) + " typed this week",
                baseHuman is null ? null
                    : Whole(Key(Obj(Key(Obj(Key(baseHuman, "by_modality"), "by_modality"), "voice"), "voice"), "count")) + " spoken and "
                        + Whole(Key(Obj(Key(Obj(Key(baseHuman, "by_modality"), "by_modality"), "typed"), "typed"), "count")) + " typed", n),
            Against(Figure(phone) + " sent from your phone this week",
                baseHuman is null ? null : Whole(Key(Obj(Key(Obj(Key(baseHuman, "by_surface"), "by_surface"), "phone"), "phone"), "count")), n),
        };
        var median = Key(Obj(Key(words, "value"), "value"), "median");
        var wordsBaseline = Key(words, "baseline");
        var medianBase = wordsBaseline is null ? null : Key(Obj(wordsBaseline, "baseline"), "median");
        lines.Add(Against("A middle prompt of " + Plural(median, "word") + " this week",
            medianBase is null ? null : Whole(medianBase) + " words", WeeksOf(words)));
        var value = Obj(Key(hours, "value"), "value");
        var window = TwoDigits(Key(value, "start")) + ":00 to " + TwoDigits(Key(value, "end")) + ":00";
        var hoursBaseline = Key(hours, "baseline");
        var outsideBase = hoursBaseline is null ? null : Key(Obj(hoursBaseline, "baseline"), "outside_count");
        lines.Add(Against(Figure(Key(value, "outside_count")) + " of " + Figure(count) + " sent outside " + window
            + " on weekdays this week", outsideBase is null ? null : Whole(outsideBase), WeeksOf(hours)));
        return string.Join("\n", lines);
    }

    /// <summary>Python's <c>format(value, "02d")</c> on an int.</summary>
    private static string TwoDigits(object? value) => value switch
    {
        long l => l.ToString("D2", CultureInfo.InvariantCulture),
        int i => i.ToString("D2", CultureInfo.InvariantCulture),
        _ => throw new MentorDataException("A business hour in the document is not an integer: " + Repr(value)),
    };

    // ------------------------------------------------------------------ What you worked on

    /// <summary><c>- &lt;repository name&gt;: &lt;id8&gt;, ...</c> largest first by human prompts summed from the index rows, ties
    /// by name; sessions with no repository name or no session row last under <see cref="NotClassified"/>. Every
    /// index session appears exactly once, in index order within its line.</summary>
    public static string WhatYouWorkedOn(Dictionary<string, object?> overview, IReadOnlyList<Dictionary<string, object?>> index, IReadOnlyList<Dictionary<string, object?>> logEntries)
    {
        if (index.Count == 0)
            throw new RenderError("the session index is empty; a week with no human prompt has no topics to render");
        var ids = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var totals = new Dictionary<string, object?>(StringComparer.Ordinal);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in index)
        {
            var id8 = (string)Key(row, "id8")!;
            var id = (string)Key(row, "id")!;
            if (seen.TryGetValue(id8, out var earlier))
                throw new RenderError("two sessions of the week share the id8 " + id8 + " (" + earlier
                    + " and " + id + "); a topic line names a session by its id8 and cannot "
                    + "name both (contract.topic_lines)");
            seen[id8] = id;
            var repo = (string)Key(row, "repo")!;
            if (repo == PromptsFile.NoRepoName || repo == PromptsFile.NoSessionRow || repo == "")
                repo = NotClassified;
            if (!ids.TryGetValue(repo, out var list)) ids[repo] = list = new List<string>();
            list.Add(id8);
            totals[repo] = Add(totals.TryGetValue(repo, out var total) ? total : 0L, Key(row, "human_prompts"));
        }
        var ordered = ids.Keys.Where(repo => repo != NotClassified)
            .OrderByDescending(repo => ToDouble(totals[repo]))
            .ThenBy(repo => repo, StringComparer.Ordinal)
            .ToList();
        if (ids.ContainsKey(NotClassified)) ordered.Add(NotClassified);
        return string.Join("\n", ordered.Select(repo => "- " + repo + ": " + string.Join(", ", ids[repo])));
    }

    // ------------------------------------------------------------------ How you drive DevThrottle

    /// <summary>{feature: count} for the nudge features from prompts_by_origin, and the human total.</summary>
    public static (Dictionary<string, object?> Counts, object? Total) FeatureCounts(Dictionary<string, object?> overview)
    {
        var human = Obj(Key(Obj(Key(Metric(overview, "origin", "prompts_by_origin"), "value"), "value"), "human"), "human");
        var modality = Obj(Key(human, "by_modality"), "by_modality");
        var surface = Obj(Key(human, "by_surface"), "by_surface");
        var counts = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["voice"] = Key(Obj(Key(modality, "voice"), "voice"), "count"),
            ["phone"] = Key(Obj(Key(surface, "phone"), "phone"), "count"),
            ["Cockpit"] = Key(Obj(Key(surface, "cockpit"), "cockpit"), "count"),
        };
        return (counts, Key(human, "count"));
    }

    /// <summary>[(feature, count, share)] for every nudged feature, lowest share first, ties in
    /// <see cref="CheckCall.NudgeFeatures"/> order.</summary>
    public static List<(string Feature, object? Count, double Share)> NudgedFeatures(Dictionary<string, object?> overview)
    {
        var (counts, total) = FeatureCounts(overview);
        var nudged = new List<(double Share, int Position, string Feature, object? Count)>();
        for (var position = 0; position < CheckCall.NudgeFeatures.Length; position++)
        {
            var feature = CheckCall.NudgeFeatures[position];
            var count = counts[feature];
            var share = IsZero(total) ? 0.0 : ToDouble(count) / ToDouble(total);
            if (IsZero(count) || share < NudgeShare)
                nudged.Add((share, position, feature, count));
        }
        return nudged.OrderBy(n => n.Share).ThenBy(n => n.Position).Select(n => (n.Feature, n.Count, n.Share)).ToList();
    }

    public static string HowYouDrive(Dictionary<string, object?> overview, IReadOnlyList<Dictionary<string, object?>> index, IReadOnlyList<Dictionary<string, object?>> logEntries)
    {
        var (_, total) = FeatureCounts(overview);
        var nudged = NudgedFeatures(overview);
        if (nudged.Count == 0) return AllInUse;
        var lines = new List<string>();
        foreach (var (feature, count, _) in nudged)
        {
            var action = NudgeActions.First(n => n.Feature == feature).Action;
            lines.Add("The " + feature + " took " + Figure(count) + " of your " + Figure(total) + " prompts. " + action);
        }
        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------------ Your fleet

    public static string YourFleet(Dictionary<string, object?> overview, IReadOnlyList<Dictionary<string, object?>> index, IReadOnlyList<Dictionary<string, object?>> logEntries)
    {
        var byOrigin = Metric(overview, "origin", "sessions_by_origin");
        var standing = Metric(overview, "origin", "standing_sessions");
        var turns = Metric(overview, "origin", "turns_by_driver");
        var quiet = Metric(overview, "waiting", "agent_quiet_hours");
        var value = Obj(Key(byOrigin, "value"), "value");
        var agent = Key(value, "agent");
        var schedule = Key(value, "schedule");
        var unknown = Key(value, "unknown");
        var human = Key(Obj(Key(value, "human"), "human"), "count");
        var standingValue = Obj(Key(standing, "value"), "value");
        var turnsValue = Obj(Key(turns, "value"), "value");
        if (IsZero(agent) && IsZero(schedule) && IsZero(Key(standingValue, "count")) && IsZero(Key(turnsValue, "agent")))
            return NoOtherSessions;
        var lines = new List<string>();
        var total = Add(Add(Add(human, agent), schedule), unknown);
        var baseline = Key(byOrigin, "baseline");
        var baseOrigin = baseline is null ? null : Obj(baseline, "baseline");
        lines.Add(ClauseSentence(
            "Of " + Plural(total, "session") + ", " + Figure(human) + " were yours; " + Figure(agent)
            + " were started by another session, " + Figure(schedule) + " " + AutomationWords + " and "
            + Figure(unknown) + " by an unknown origin",
            baseOrigin is null ? null
                : Figure(Key(baseOrigin, "agent")) + ", " + Figure(Key(baseOrigin, "schedule")) + " and " + Figure(Key(baseOrigin, "unknown")),
            WeeksOf(byOrigin)));
        var count = Key(standingValue, "count");
        var standingBaseline = Key(standing, "baseline");
        var baseStanding = standingBaseline is null ? null : Obj(standingBaseline, "baseline");
        lines.Add(ClauseSentence(
            (IsZero(count) ? "None of the sessions" : Figure(count) + " of the sessions")
            + " started by another session or by automation lived over a day",
            baseStanding is not null && Key(baseStanding, "count") is not null ? Figure(Key(baseStanding, "count")) : null,
            WeeksOf(standing)));
        var turnsBaseline = Key(turns, "baseline");
        var baseTurns = turnsBaseline is null ? null : Obj(turnsBaseline, "baseline");
        lines.Add(ClauseSentence(
            "Beside your " + Plural(Key(turnsValue, "human"), "turn") + ", " + Figure(Key(turnsValue, "agent"))
            + " came from messaging by other sessions and " + Figure(Key(turnsValue, "framework")) + " from the product's own text",
            baseTurns is null ? null : Figure(Key(baseTurns, "agent")) + " and " + Figure(Key(baseTurns, "framework")),
            WeeksOf(turns)));
        var quietBaseline = Key(quiet, "baseline");
        lines.Add(ClauseSentence(
            "Sessions were " + QuietWords + " for " + Plural(Key(quiet, "value"), "hour"),
            quietBaseline is null ? null : Figure(quietBaseline), WeeksOf(quiet)));
        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------------ Measured but not judged

    /// <summary>Python's <c>key.replace("_", " ").capitalize()</c> over an ASCII metric key.</summary>
    public static string MetricWords(string key) => key.Replace("_", " ");

    private static string Capitalize(string text)
        => text.Length == 0 ? text : text.Substring(0, 1).ToUpperInvariant() + text.Substring(1).ToLowerInvariant();

    public static string MeasuredNotJudged(Dictionary<string, object?> overview, IReadOnlyList<Dictionary<string, object?>> index, IReadOnlyList<Dictionary<string, object?>> logEntries)
    {
        var sentences = new List<string>();
        foreach (var group in Metrics.GroupOrder)
        {
            foreach (var key in Metrics.GroupIds[group])
            {
                var coverage = Obj(Key(Metric(overview, group, key), "coverage"), "coverage");
                var note = coverage.TryGetValue("baseline_note", out var noteValue) ? noteValue as string : null;
                if (note is not null && note.StartsWith(Metrics.BaselineNotePrefix, StringComparison.Ordinal))
                    sentences.Add(Capitalize(MetricWords(key)) + " has no baseline yet ("
                        + note.Substring(Metrics.BaselineNotePrefix.Length) + ").");
            }
        }
        foreach (var group in Metrics.GroupOrder)
        {
            foreach (var key in Metrics.GroupIds[group])
            {
                var coverage = Obj(Key(Metric(overview, group, key), "coverage"), "coverage");
                if (coverage.TryGetValue("claude_only", out var only) && only is true)
                    sentences.Add(Capitalize(MetricWords(key)) + " is reported by one agent only, over "
                        + Figure(Key(coverage, "sessions_covered")) + " of " + Plural(Key(coverage, "sessions_in_week"), "session") + ".");
            }
        }
        foreach (var (key, words) in HeuristicShares)
        {
            var entry = Metric(overview, "prompt_shape", key);
            var value = Key(entry, "value");
            if (value is null) continue;
            sentences.Add("About " + Whole(Times100(value)) + " percent " + words + "; " + CandidateNotFact + ".");
        }
        var clusters = Arr(Key(Metric(overview, "prompt_shape", "repeated_instruction_clusters"), "value"), "repeated_instruction_clusters value");
        if (clusters.Count > 0)
        {
            var largest = Obj(clusters[0], "largest cluster");
            sentences.Add(Plural((long)clusters.Count, "repeated-instruction cluster") + " came out of the word-shingle rule, "
                + "the largest holding " + Plural(Key(largest, "size"), "prompt") + " across "
                + Plural((long)Arr(Key(largest, "sessions"), "cluster sessions").Count, "session") + "; these are candidates the agent may or may "
                + "not have acted on.");
        }
        if (sentences.Count == 0) return NothingUnjudged;
        return string.Join("\n", sentences);
    }

    /// <summary>Python's <c>value * 100</c>: an int stays an int, a float stays a float.</summary>
    private static object Times100(object? value) => value switch
    {
        long l => l * 100,
        int i => (long)i * 100,
        double d => d * 100,
        _ => throw new RenderError("not a number: " + Repr(value)),
    };

    // ------------------------------------------------------------------ How this was made

    public sealed class Counts
    {
        public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);
        public int Opened { get; init; }
        public int Dimensions { get; init; }
        public int Searched { get; init; }
        public int Measures { get; init; }
        public int Moments { get; init; }
        public int Outcomes { get; init; }
        public int Citations { get; init; }
        public int Quotations { get; init; }
        public int Notes { get; init; }
        public int Refused { get; init; }
    }

    /// <summary>What the calls in the log amount to, derived from the TOOL NAMES the log holds. <c>Calls</c> maps every
    /// tool name with at least one answered call (ok true) to its count - every name, known to this class or not. The
    /// other counts are the distinct things the answered calls of a known tool reached: sessions opened, dimensions
    /// scanned, phrases searched, measures looked back over, moments found, outcomes read, citations fetched,
    /// quotations verified true (distinct (citation, fragment) pairs - a check made twice is one quotation), notes
    /// kept. <c>Refused</c> is the number of calls with ok false.</summary>
    public static Counts ToolCounts(IReadOnlyList<Dictionary<string, object?>> logEntries)
    {
        var ok = logEntries.Where(e => e.TryGetValue("ok", out var value) && value is true).ToList();
        var counts = new Counts
        {
            Opened = ok.Where(e => Tool(e) == "session_prompts").Select(e => Key(e, "session_id8")).Distinct().Count(),
            Dimensions = ok.Where(e => Tool(e) == "dimension_candidates").Select(e => Key(Obj(Key(e, "args"), "args"), "dimension")).Distinct().Count(),
            Searched = ok.Where(e => Tool(e) == "prompt_search").Select(e => Key(Obj(Key(e, "args"), "args"), "query")).Distinct().Count(),
            Measures = ok.Where(e => Tool(e) == "prior_weeks").Select(e => Key(Obj(Key(e, "args"), "args"), "metric")).Distinct().Count(),
            Moments = ok.Count(e => Tool(e) == "turn_record" && ((e.TryGetValue("summary", out var s) ? s as string : null) ?? "").StartsWith("available=true", StringComparison.Ordinal)),
            Outcomes = ok.Where(e => Tool(e) == "session_outcomes").Select(e => Key(e, "session_id8")).Distinct().Count(),
            Citations = ok.Where(e => Tool(e) == "cite").Select(e => Key(e, "citation")).Distinct().Count(),
            Quotations = ok.Where(e => Tool(e) == "verify_quote" && e.TryGetValue("verified", out var v) && v is true)
                .Select(e => (Key(e, "citation"), Key(e, "fragment"))).Distinct().Count(),
            Notes = ok.Count(e => Tool(e) == "note"),
            Refused = logEntries.Count(e => e.TryGetValue("ok", out var value) && value is false),
        };
        foreach (var e in ok)
        {
            var tool = Tool(e);
            counts.Calls[tool] = counts.Calls.TryGetValue(tool, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    private static string Tool(Dictionary<string, object?> entry) => (string)Key(entry, "tool")!;

    public static string HowThisWasMade(Dictionary<string, object?> overview, IReadOnlyList<Dictionary<string, object?>> index, IReadOnlyList<Dictionary<string, object?>> logEntries)
    {
        var counts = ToolCounts(logEntries);
        var calls = counts.Calls;
        var clauses = new List<string>();
        if (calls.ContainsKey("week_overview") && calls.ContainsKey("session_index"))
            clauses.Add("read the week's numbers and the index of your " + Plural((long)index.Count, "session"));
        else if (calls.ContainsKey("week_overview"))
            clauses.Add("read the week's numbers");
        else if (calls.ContainsKey("session_index"))
            clauses.Add("read the index of your " + Plural((long)index.Count, "session"));
        if (counts.Opened > 0) clauses.Add("opened " + Figure((long)counts.Opened) + " of them in full");
        if (counts.Dimensions > 0) clauses.Add("scanned every prompt for candidates on " + Plural((long)counts.Dimensions, "dimension"));
        if (counts.Searched > 0) clauses.Add("searched your prompts for " + Plural((long)counts.Searched, "phrase"));
        if (counts.Measures > 0) clauses.Add("looked back over " + Plural((long)counts.Measures, "measure") + " across your prior weeks");
        if (counts.Moments > 0) clauses.Add("looked at " + Plural((long)counts.Moments, "moment") + " in the terminal record");
        if (counts.Outcomes > 0) clauses.Add("read the outcomes of " + Plural((long)counts.Outcomes, "session"));
        if (counts.Citations > 0) clauses.Add("fetched " + Plural((long)counts.Citations, "citation"));
        if (counts.Quotations > 0) clauses.Add("checked " + Plural((long)counts.Quotations, "quotation") + " against your prompts");
        if (counts.Notes > 0) clauses.Add("kept " + Plural((long)counts.Notes, "note"));
        foreach (var name in calls.Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (!SentencedTools.Contains(name))
                clauses.Add("called " + name + " " + Plural((long)calls[name], "time"));
        var made = clauses.Count > 0 ? "I " + JoinClauses(clauses) + "." : NoCallAnswered;
        if (counts.Refused > 0)
            made += " The tools refused " + Plural((long)counts.Refused, "call") + ", which answered nothing.";
        var coverage = Obj(Key(overview, "coverage"), "coverage");
        foreach (var key in new[] { "unresolved_sentence", "chat_relay_sentence", "hours_caution" })
            if (!coverage.ContainsKey(key))
                throw new RenderError("the overview's coverage has no " + key);
        return string.Join("\n", new[]
        {
            made, (string)coverage["unresolved_sentence"]!, (string)coverage["chat_relay_sentence"]!,
            (string)coverage["hours_caution"]!, BoundAndJudged, OwnerOnly,
        });
    }
}
